using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ActiChamp
{
    public enum ChampError : int
    {
        Ok = 0,
        InvalidHandle = -1,
        InvalidParameter = -2,
        Fail = -3,
        Monitoring = -4,
        NotSupported = -5
    }

    public enum ChampMode : int
    {
        Normal = 0,
        ActiveShield = 1,
        Impedance = 2,
        Test = 3,
        LedTest = 99
    }

    public enum ChampRate : int
    {
        Rate10kHz = 0,
        Rate50kHz = 1,
        Rate100kHz = 2,
        Rate25kHz = 10,
        Rate5kHz = 11,
        Rate2kHz = 12,
        Rate1kHz = 13,
        Rate500Hz = 14,
        Rate200Hz = 15
    }

    public enum ChampAdcFilter : int
    {
        Native = 0,
        Averaging2 = 1
    }

    public enum ChampDecimation : int
    {
        None = 0,
        By2 = 2,
        By5 = 5,
        By10 = 10,
        By20 = 20,
        By50 = 50
    }

    public enum ChampLedColor : int
    {
        Off = 0,
        Green = 1,
        Red = 2
    }

    public enum ChampLedTestAction : int
    {
        OffReset = 0,
        NextGreen = 1,
        NextRed = 2,
        AllGreen = 11,
        AllRed = 12
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ChampSettingsEx
    {
        public int Mode;
        public int Rate;
        public int AdcFilter;
        public int Decimation;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ChampProperties
    {
        public uint CountEeg;
        public uint CountAux;
        public uint TriggersIn;
        public uint TriggersOut;
        public float Rate;
        public float ResolutionEeg;
        public float ResolutionAux;
        public float RangeEeg;
        public float RangeAux;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ChampModules
    {
        public uint Present;
        public uint Enabled;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ChampVoltages
    {
        public float VDC;
        public float V3;
        public float TEMP;
        public float DVDD3;
        public float AVDD3;
        public float AVDD5;
        public float REF;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ChampVersionInfo
    {
        public uint DLL;
        public uint USBDRV;
        public uint USBCTRL;
        public uint FPGA;
        public uint DSP;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ChampVersionInfoExt
    {
        public uint DLL;
        public uint USBDRV;
        public uint USBCTRL;
        public uint FPGAM;
        public uint DSP;
        public uint FPGAC;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ChampDataStatus
    {
        public uint Samples;
        public uint Errors;
        public float Rate;
        public float Speed;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ChampImpedanceSetup
    {
        public uint Good;
        public uint Bad;
        public uint LedsDisable;
        public uint TimeOut;
    }

    public sealed class ActiChampException : Exception
    {
        private readonly ChampError _error;

        public ChampError Error
        {
            get { return _error; }
        }

        public ActiChampException(string message)
            : base(message)
        {
            _error = ChampError.Ok;
        }

        public ActiChampException(string message, ChampError error)
            : base(message + " (DLL error: " + ((int)error).ToString() + " / " + error.ToString() + ")")
        {
            _error = error;
        }
    }

    public sealed class ActiChampDataBlock
    {
        // Основной формат сервиса PolyHub:
        // [ch0:s0..sN-1][ch1:s0..sN-1]... в вольтах.
        public float[] SignalsVoltsChannelMajor;
        public uint[] Trigger;
        public ulong[] SampleCounter;
        public int EegChannelCount;
        public int EogChannelCount;
        public int AuxChannelCount;
        public int[] PhysicalChannelIndices;

        public int SampleCount
        {
            get { return Trigger == null ? 0 : Trigger.Length; }
        }

        public int ChannelCount
        {
            get { return EegChannelCount + EogChannelCount + AuxChannelCount; }
        }

        public float GetSignalVolts(int channelIndex, int sampleIndex)
        {
            if (SignalsVoltsChannelMajor == null)
                return 0.0f;
            if (channelIndex < 0 || channelIndex >= ChannelCount)
                throw new ArgumentOutOfRangeException("channelIndex");
            if (sampleIndex < 0 || sampleIndex >= SampleCount)
                throw new ArgumentOutOfRangeException("sampleIndex");

            return SignalsVoltsChannelMajor[channelIndex * SampleCount + sampleIndex];
        }
    }

    public sealed class ActiChampDevice : IDisposable
    {
        public const uint RequiredDllVersion = 0x190E0804;
        public const uint ImpedanceInvalid = 999900;

        private const string DllName = "ActiChamp_x86.dll";
        private const ulong UInt32Wrap = (ulong)uint.MaxValue + 1UL;
        private const int SafeReadBufferBytes = 10000 * 1024;

        public static readonly Dictionary<ChampRate, double> RateHzMap = new Dictionary<ChampRate, double>
        {
            { ChampRate.Rate200Hz, 200.0 },
            { ChampRate.Rate500Hz, 500.0 },
            { ChampRate.Rate1kHz, 1000.0 },
            { ChampRate.Rate2kHz, 2000.0 },
            { ChampRate.Rate5kHz, 5000.0 },
            { ChampRate.Rate10kHz, 10000.0 },
            { ChampRate.Rate25kHz, 25000.0 },
            { ChampRate.Rate50kHz, 50000.0 },
            { ChampRate.Rate100kHz, 100000.0 }
        };

        private int _handle;
        private bool _disposed;
        private bool _running;
        private IntPtr _readBuffer;
        private int _readBufferBytes;
        private IntPtr _impedanceBuffer;
        private int _impedanceBufferBytes;
        private ulong _sampleCounterAdjust;
        private uint _dllVersion;
        private int _ledIndex;

        public int ActiveShieldGain { get; set; }
        public ChampMode CurrentMode { get; private set; }
        public ChampRate CurrentRate { get; private set; }
        public ChampProperties Properties { get; private set; }
        public ChampModules Modules { get; private set; }
        public ChampVersionInfo Version { get; private set; }
        public ChampVersionInfoExt VersionExt { get; private set; }

        public ActiChampDevice()
            : this(null)
        {
        }

        public ActiChampDevice(Action<string> log)
        {
            ActiveShieldGain = 5;
            CurrentMode = ChampMode.Normal;
            CurrentRate = ChampRate.Rate500Hz;
            _handle = 0;
            _readBuffer = IntPtr.Zero;
            _impedanceBuffer = IntPtr.Zero;
            TryReadDllVersion(log);
        }

        public bool IsOpen
        {
            get { return _handle != 0; }
        }

        public bool IsRunning
        {
            get { return _running; }
        }

        public uint DllVersion
        {
            get { return _dllVersion != 0U ? _dllVersion : Version.DLL; }
        }

        public int BoardRevision
        {
            get { return GetBoardRevision(Version); }
        }

        public void Open()
        {
            Open(null);
        }

        public void Open(Action<string> log)
        {
            ChampError err;
            ChampVersionInfo version;
            ChampModules modules;
            ChampProperties props;
            int count;
            int retry;

            ThrowIfDisposed();
            if (IsOpen)
                return;

            if (log == null)
                log = delegate(string message) { };

            TryReadDllVersion(log);

            log("champGetCount: поиск устройств...");
            count = Native.champGetCount();
            log("champGetCount -> " + count.ToString());
            if (count == 0)
                throw new ActiChampException("actiCHamp hardware not available.");

            _handle = 0;
            for (retry = 0; retry < 3; retry++)
            {
                log("Попытка открытия " + (retry + 1).ToString() + "/3: champOpen(index=0)...");
                Stopwatch timer = Stopwatch.StartNew();
                _handle = Native.champOpen(0);
                timer.Stop();
                log("champOpen -> handle=0x" + _handle.ToString("X8") +
                    ", время " + timer.ElapsedMilliseconds.ToString() + " мс.");
                if (_handle == 0)
                {
                    log("Попытка неудачна: champOpen вернул нулевой дескриптор.");
                    continue;
                }

                log("champGetVersion: проверка открытого устройства...");
                err = (ChampError)Native.champGetVersion(_handle, out version);
                log("champGetVersion -> " + ((int)err).ToString() + " / " + err.ToString());
                if (err == ChampError.Ok)
                    log("Версии: DLL=0x" + version.DLL.ToString("X8") +
                        ", USBDRV=0x" + version.USBDRV.ToString("X8") +
                        ", USBCTRL=0x" + version.USBCTRL.ToString("X8") +
                        ", FPGA=0x" + version.FPGA.ToString("X8") +
                        ", DSP=0x" + version.DSP.ToString("X8"));
                if (err == ChampError.Ok && version.FPGA != 0U)
                {
                    Version = version;
                    log("Открытие и проверка версии устройства выполнены успешно.");
                    break;
                }

                log(err != ChampError.Ok
                    ? "Попытка отклонена: ошибка champGetVersion."
                    : "Попытка отклонена: версия FPGA равна 0.");
                log("champClose: закрытие устройства после неудачной проверки...");
                err = (ChampError)Native.champClose(_handle);
                log("champClose -> " + ((int)err).ToString() + " / " + err.ToString());
                _handle = 0;
                log("Пауза 500 мс после неудачной проверки устройства.");
                System.Threading.Thread.Sleep(500);
            }

            if (_handle == 0)
            {
                log("Все 3 попытки открытия устройства завершились неудачей.");
                throw new ActiChampException("Failed to open actiCHamp device.");
            }

            if (DllVersion != 0U && DllVersion != RequiredDllVersion)
                throw new ActiChampException("Unexpected DLL version 0x" + DllVersion.ToString("X8") + ", expected 0x" + RequiredDllVersion.ToString("X8") + ".");

            log("champGetModules: чтение карты модулей...");
            err = (ChampError)Native.champGetModules(_handle, out modules);
            log("champGetModules -> " + ((int)err).ToString() + " / " + err.ToString());
            ThrowIfError(err, "Failed to read module map.");
            Modules = modules;

            log("champGetProperty: чтение свойств устройства...");
            err = (ChampError)Native.champGetProperty(_handle, out props);
            log("champGetProperty -> " + ((int)err).ToString() + " / " + err.ToString());
            ThrowIfError(err, "Failed to read device properties.");
            Properties = props;
        }

        public void Close()
        {
            if (!IsOpen)
                return;

            try
            {
                if (_running)
                    Stop();
            }
            catch
            {
            }

            Native.champClose(_handle);
            _handle = 0;
            _running = false;
            _ledIndex = 0;
        }

        public void Setup(ChampMode mode, ChampRate requestedRate)
        {
            ChampSettingsEx settingsEx;
            uint enabledModules;
            ChampError err;
            ChampProperties props;
            ChampModules modules;

            ThrowIfDisposed();
            EnsureOpen();

            CurrentMode = mode;
            CurrentRate = requestedRate;

            settingsEx = BuildExtendedSettings(mode, requestedRate);
            enabledModules = GetEnabledModuleMask(Modules.Present, requestedRate);

            modules = Modules;
            modules.Enabled = enabledModules;

            err = (ChampError)Native.champSetModules(_handle, ref modules);
            ThrowIfError(err, "Failed to configure enabled modules.");
            Modules = modules;

            err = (ChampError)Native.champSetSettingsEx(_handle, ref settingsEx);
            ThrowIfError(err, "Failed to configure acquisition settings.");

            err = (ChampError)Native.champSetActiveShieldGain(_handle, ClampGain(ActiveShieldGain));
            ThrowIfError(err, "Failed to set active shield gain.");

            err = (ChampError)Native.champGetProperty(_handle, out props);
            ThrowIfError(err, "Failed to refresh properties after setup.");
            Properties = props;

            EnsureReadBuffer(SafeReadBufferBytes);
            _sampleCounterAdjust = 0UL;
            _ledIndex = 0;
        }

        public void Start()
        {
            ChampError err;
            ChampVersionInfoExt versionExt;

            ThrowIfDisposed();
            EnsureOpen();
            if (_running)
                return;

            err = (ChampError)Native.champStart(_handle);
            ThrowIfError(err, "Failed to start device.");

            if (BoardRevision >= 6)
            {
                err = (ChampError)Native.champGetVersionExt(_handle, out versionExt);
                if (err != ChampError.NotSupported)
                    ThrowIfError(err, "Failed to read extended version info.");
                VersionExt = versionExt;
            }

            _running = true;
            _sampleCounterAdjust = 0UL;
        }

        public void Stop()
        {
            ChampError err;

            if (!_running)
                return;

            if (CurrentMode == ChampMode.LedTest)
            {
                try
                {
                    ClearElectrodeLeds();
                }
                catch
                {
                }
            }

            err = (ChampError)Native.champStop(_handle);
            ThrowIfError(err, "Failed to stop device.");
            _running = false;
        }

        public unsafe ActiChampDataBlock ReadSamples(
            int requestedSampleCount,
            int[] selectedPhysicalChannelIndices,
            int[] referencePhysicalChannelIndices,
            int[] unreferencedEegPhysicalChannelIndices)
        {
            int eegCount;
            int auxCount;
            int totalSignalChannels;
            int intsPerSample;
            int bytesPerSample;
            int requestedBytes;
            int bytesRead;
            int itemCount;
            int sampleCount;
            float[] signals;
            uint[] trigger;
            ulong[] sampleCounter;
            uint[] rawCounters;
            int[] selectedIndices;
            int[] referenceIndices;
            int[] unreferencedEegIndices;
            int selectedEegCount;
            int selectedAuxCount;
            int s;
            int outCh;
            int physicalIndex;
            int baseIndex;
            float scale;
            bool applyReference;
            float[] referenceVolts;
            float referenceSum;
            ulong baseAdjust;
            int wrapIndex;
            bool wrappedInsideThisBlock;
            ActiChampDataBlock result;

            ThrowIfDisposed();
            EnsureRunning();

            if (!IsContinuousDataMode(CurrentMode))
                throw new InvalidOperationException("Текущий режим не предназначен для чтения EEG/AUX блоков.");
            if (requestedSampleCount <= 0)
                throw new ArgumentOutOfRangeException("requestedSampleCount");

            eegCount = checked((int)Properties.CountEeg);
            auxCount = checked((int)Properties.CountAux);
            totalSignalChannels = eegCount + auxCount;
            intsPerSample = totalSignalChannels + 2;
            bytesPerSample = intsPerSample * sizeof(int);
            requestedBytes = checked(bytesPerSample * requestedSampleCount);

            if (selectedPhysicalChannelIndices == null || selectedPhysicalChannelIndices.Length == 0)
            {
                selectedIndices = new int[totalSignalChannels];
                for (outCh = 0; outCh < totalSignalChannels; outCh++)
                    selectedIndices[outCh] = outCh;
            }
            else
            {
                selectedIndices = selectedPhysicalChannelIndices;
            }

            selectedEegCount = 0;
            selectedAuxCount = 0;
            for (outCh = 0; outCh < selectedIndices.Length; outCh++)
            {
                physicalIndex = selectedIndices[outCh];
                if (physicalIndex < 0 || physicalIndex >= totalSignalChannels)
                    throw new ArgumentOutOfRangeException("selectedPhysicalChannelIndices");

                if (physicalIndex < eegCount)
                    selectedEegCount++;
                else
                    selectedAuxCount++;
            }

            referenceIndices = referencePhysicalChannelIndices == null
                ? new int[0]
                : referencePhysicalChannelIndices;

            for (outCh = 0; outCh < referenceIndices.Length; outCh++)
            {
                physicalIndex = referenceIndices[outCh];
                if (physicalIndex < 0 || physicalIndex >= eegCount)
                    throw new ArgumentOutOfRangeException(
                        "referencePhysicalChannelIndices",
                        "Референтом может быть только физический EEG-канал.");
            }

            unreferencedEegIndices = unreferencedEegPhysicalChannelIndices == null
                ? new int[0]
                : unreferencedEegPhysicalChannelIndices;

            for (outCh = 0; outCh < unreferencedEegIndices.Length; outCh++)
            {
                physicalIndex = unreferencedEegIndices[outCh];
                if (physicalIndex < 0)
                    continue;

                if (physicalIndex >= eegCount)
                    throw new ArgumentOutOfRangeException(
                        "unreferencedEegPhysicalChannelIndices",
                        "Без программного REF может читаться только физический EEG-канал.");
            }

            EnsureReadBuffer(Math.Max(requestedBytes, SafeReadBufferBytes));
            bytesRead = Native.champGetDataBlocking(_handle, _readBuffer, requestedBytes);

            if (bytesRead < 0)
            {
                ChampError error = (ChampError)bytesRead;
                if (error == ChampError.Monitoring)
                    return null;
                ThrowIfError(error, "Failed to read data block.");
            }

            if (bytesRead == 0)
                return null;

            itemCount = bytesRead / sizeof(int);
            sampleCount = itemCount / intsPerSample;
            if (sampleCount <= 0)
                return null;

            // Не создаём управляемый массив всех физических каналов.
            // Из нативного sample-major DLL-буфера сразу формируем конечный
            // компактный channel-major float[] в вольтах. Если выбраны REF-каналы,
            // для каждого sample сначала вычисляется их среднее, после чего этот
            // reference вычитается из EEG до публикации в MMF и до визуализации.
            signals = new float[selectedIndices.Length * sampleCount];
            trigger = new uint[sampleCount];
            sampleCounter = new ulong[sampleCount];
            rawCounters = new uint[sampleCount];
            referenceVolts = referenceIndices.Length > 0
                ? new float[sampleCount]
                : null;

            int* rawPtr = (int*)_readBuffer.ToPointer();

            if (referenceVolts != null)
            {
                for (s = 0; s < sampleCount; s++)
                {
                    referenceSum = 0.0f;
                    for (outCh = 0; outCh < referenceIndices.Length; outCh++)
                    {
                        physicalIndex = referenceIndices[outCh];
                        referenceSum += rawPtr[s * intsPerSample + physicalIndex] * Properties.ResolutionEeg;
                    }

                    referenceVolts[s] = referenceSum / referenceIndices.Length;
                }
            }

            for (outCh = 0; outCh < selectedIndices.Length; outCh++)
            {
                physicalIndex = selectedIndices[outCh];
                scale = physicalIndex < eegCount
                    ? Properties.ResolutionEeg
                    : Properties.ResolutionAux;

                applyReference = physicalIndex < eegCount &&
                    referenceVolts != null &&
                    Array.IndexOf(unreferencedEegIndices, physicalIndex) < 0;

                for (s = 0; s < sampleCount; s++)
                {
                    float value = rawPtr[s * intsPerSample + physicalIndex] * scale;

                    if (applyReference)
                        value -= referenceVolts[s];

                    signals[outCh * sampleCount + s] = value;
                }
            }

            for (s = 0; s < sampleCount; s++)
            {
                baseIndex = s * intsPerSample;
                trigger[s] = unchecked((uint)rawPtr[baseIndex + totalSignalChannels]);
                rawCounters[s] = unchecked((uint)rawPtr[baseIndex + totalSignalChannels + 1]);
            }

            baseAdjust = _sampleCounterAdjust;
            wrapIndex = Array.IndexOf(rawCounters, 0u);
            wrappedInsideThisBlock = wrapIndex >= 0 && (rawCounters[0] + baseAdjust) != 0UL;

            for (s = 0; s < sampleCount; s++)
            {
                ulong adjust = baseAdjust;
                if (wrappedInsideThisBlock && s >= wrapIndex)
                    adjust += UInt32Wrap;
                sampleCounter[s] = rawCounters[s] + adjust;
            }

            if (wrappedInsideThisBlock)
                _sampleCounterAdjust += UInt32Wrap;

            result = new ActiChampDataBlock();
            result.SignalsVoltsChannelMajor = signals;
            result.Trigger = trigger;
            result.SampleCounter = sampleCounter;
            result.EegChannelCount = selectedEegCount;
            result.EogChannelCount = 0;
            result.AuxChannelCount = selectedAuxCount;
            result.PhysicalChannelIndices = selectedIndices;
            return result;
        }

        public uint[] ReadImpedances()
        {
            int itemCount;
            int bytes;
            ChampError err;
            uint[] result;
            byte[] tmp;
            int dummyRead;

            ThrowIfDisposed();
            EnsureRunning();

            if (CurrentMode != ChampMode.Impedance)
                throw new InvalidOperationException("ReadImpedances() доступен только в режиме Impedance.");

            itemCount = checked((int)Properties.CountEeg) + 1;
            bytes = itemCount * sizeof(uint);
            EnsureImpedanceBuffer(bytes);
            EnsureReadBuffer(SafeReadBufferBytes);

            err = (ChampError)Native.champImpedanceGetData(_handle, _impedanceBuffer, bytes);
            dummyRead = Native.champGetData(_handle, _readBuffer, _readBufferBytes);
            if (dummyRead < 0 && (ChampError)dummyRead != ChampError.Monitoring)
                ThrowIfError((ChampError)dummyRead, "Dummy read after impedance query failed.");

            if (err == ChampError.Fail)
                return null;
            ThrowIfError(err, "Failed to read impedances.");

            result = new uint[itemCount];
            tmp = new byte[bytes];
            Marshal.Copy(_impedanceBuffer, tmp, 0, bytes);
            Buffer.BlockCopy(tmp, 0, result, 0, bytes);
            return result;
        }

        public void SetImpedanceRange(uint goodOhm, uint badOhm, uint timeoutSeconds)
        {
            ChampImpedanceSetup setup = new ChampImpedanceSetup();
            setup.Good = goodOhm;
            setup.Bad = badOhm;
            setup.LedsDisable = 0U;
            setup.TimeOut = timeoutSeconds;

            ChampError err = (ChampError)Native.champImpedanceSetSetup(_handle, ref setup);
            ThrowIfError(err, "Failed to set impedance range.");
        }

        public void ClearElectrodeLeds()
        {
            ApplyLedTestAction(ChampLedTestAction.OffReset);
        }

        public bool ApplyLedTestAction(ChampLedTestAction action)
        {
            int ledCount;
            int[] leds;
            IntPtr ledPtr;
            int sizeBytes;
            int err;
            bool wrapped;

            ThrowIfDisposed();
            EnsureOpen();

            if (CurrentMode != ChampMode.LedTest)
                throw new InvalidOperationException("Electrode LED commands доступны только в режиме LED Test.");

            ledCount = checked((int)Properties.CountEeg) + 1;
            if (ledCount <= 0)
                return true;

            if (action == ChampLedTestAction.OffReset)
            {
                _ledIndex = 0;
                err = Native.champSetElectrodes(_handle, IntPtr.Zero, 0);
                ThrowIfError((ChampError)err, "Failed to clear electrode LEDs.");
                return true;
            }

            leds = new int[ledCount];
            switch (action)
            {
                case ChampLedTestAction.NextGreen:
                    leds[_ledIndex] = (int)ChampLedColor.Green;
                    _ledIndex++;
                    break;
                case ChampLedTestAction.NextRed:
                    leds[_ledIndex] = (int)ChampLedColor.Red;
                    _ledIndex++;
                    break;
                case ChampLedTestAction.AllGreen:
                    FillArray(leds, (int)ChampLedColor.Green);
                    _ledIndex = 0;
                    break;
                case ChampLedTestAction.AllRed:
                    FillArray(leds, (int)ChampLedColor.Red);
                    _ledIndex = 0;
                    break;
                default:
                    throw new ArgumentOutOfRangeException("action");
            }

            sizeBytes = ledCount * sizeof(int);
            ledPtr = Marshal.AllocHGlobal(sizeBytes);
            try
            {
                Marshal.Copy(leds, 0, ledPtr, ledCount);
                err = Native.champSetElectrodes(_handle, ledPtr, sizeBytes);
                ThrowIfError((ChampError)err, "Failed to set electrode LEDs.");
            }
            finally
            {
                Marshal.FreeHGlobal(ledPtr);
            }

            wrapped = false;
            if (_ledIndex >= ledCount)
            {
                _ledIndex = 0;
                wrapped = true;
            }

            return wrapped || _ledIndex == 0;
        }

        public ChampVoltages GetVoltages()
        {
            ChampVoltages voltages;
            ChampError err = (ChampError)Native.champGetVoltages(_handle, out voltages);
            ThrowIfError(err, "Failed to read voltages.");
            return voltages;
        }

        public ChampDataStatus GetDataStatus()
        {
            ChampDataStatus status;
            ChampError err = (ChampError)Native.champGetDataStatus(_handle, out status);
            ThrowIfError(err, "Failed to read data status.");
            return status;
        }

        public static bool IsContinuousDataMode(ChampMode mode)
        {
            return mode == ChampMode.Normal || mode == ChampMode.ActiveShield || mode == ChampMode.Test;
        }

        public static string GetModeDisplayName(ChampMode mode)
        {
            switch (mode)
            {
                case ChampMode.Normal: return "Normal";
                case ChampMode.ActiveShield: return "ActiveShield";
                case ChampMode.Impedance: return "Impedance";
                case ChampMode.Test: return "Test";
                case ChampMode.LedTest: return "Electrode LED Test";
                default: return mode.ToString();
            }
        }

        private void TryReadDllVersion(Action<string> log = null)
        {
            ChampVersionInfo version;
            if (log != null)
                log("champGetVersion(handle=0): запрос версии " + DllName + "...");
            try
            {
                ChampError err = (ChampError)Native.champGetVersion(0, out version);
                if (log != null)
                    log("champGetVersion(handle=0) -> " + ((int)err).ToString() + " / " + err.ToString() +
                        (err == ChampError.Ok ? "; DLL=0x" + version.DLL.ToString("X8") : string.Empty) +
                        "; ожидается DLL=0x" + RequiredDllVersion.ToString("X8"));
                if (err == ChampError.Ok)
                    _dllVersion = version.DLL;
            }
            catch (Exception ex)
            {
                if (log != null)
                    log("champGetVersion(handle=0): " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static ChampSettingsEx BuildExtendedSettings(ChampMode mode, ChampRate requestedRate)
        {
            ChampMode hardwareMode = mode == ChampMode.LedTest ? ChampMode.Normal : mode;
            ChampSettingsEx result = new ChampSettingsEx();
            result.Mode = (int)hardwareMode;
            result.Rate = MapPhysicalRate(requestedRate);
            result.Decimation = MapDecimation(requestedRate);
            result.AdcFilter = (int)ChampAdcFilter.Averaging2;

            if (hardwareMode == ChampMode.Impedance)
            {
                result.Rate = (int)ChampRate.Rate10kHz;
                result.Decimation = (int)ChampDecimation.None;
            }

            return result;
        }

        private static int MapPhysicalRate(ChampRate requestedRate)
        {
            switch (requestedRate)
            {
                case ChampRate.Rate200Hz:
                case ChampRate.Rate500Hz:
                case ChampRate.Rate1kHz:
                case ChampRate.Rate2kHz:
                case ChampRate.Rate5kHz:
                case ChampRate.Rate10kHz:
                    return 0;
                case ChampRate.Rate25kHz:
                case ChampRate.Rate50kHz:
                    return 1;
                case ChampRate.Rate100kHz:
                    return 2;
                default:
                    throw new ArgumentOutOfRangeException("requestedRate");
            }
        }

        private static int MapDecimation(ChampRate requestedRate)
        {
            switch (requestedRate)
            {
                case ChampRate.Rate200Hz: return (int)ChampDecimation.By50;
                case ChampRate.Rate500Hz: return (int)ChampDecimation.By20;
                case ChampRate.Rate1kHz: return (int)ChampDecimation.By10;
                case ChampRate.Rate2kHz: return (int)ChampDecimation.By5;
                case ChampRate.Rate5kHz: return (int)ChampDecimation.By2;
                case ChampRate.Rate10kHz: return (int)ChampDecimation.None;
                case ChampRate.Rate25kHz: return (int)ChampDecimation.By2;
                case ChampRate.Rate50kHz:
                case ChampRate.Rate100kHz:
                    return (int)ChampDecimation.None;
                default:
                    throw new ArgumentOutOfRangeException("requestedRate");
            }
        }

        private static uint GetEnabledModuleMask(uint presentMask, ChampRate rate)
        {
            switch (rate)
            {
                case ChampRate.Rate100kHz: return presentMask & 0x03u;
                case ChampRate.Rate50kHz: return presentMask & 0x07u;
                case ChampRate.Rate25kHz: return presentMask & 0x1Fu;
                default: return presentMask;
            }
        }

        private static int GetBoardRevision(ChampVersionInfo version)
        {
            uint dspMajor = version.DSP >> 24;
            if (dspMajor >= 100U)
                return 6;
            if (dspMajor > 0U)
                return 4;
            return 0;
        }

        private static uint ClampGain(int gain)
        {
            if (gain < 1)
                return 1U;
            if (gain > 100)
                return 100U;
            return (uint)gain;
        }

        private static void ThrowIfError(ChampError error, string message)
        {
            if (error != ChampError.Ok)
                throw new ActiChampException(message, error);
        }

        private void EnsureOpen()
        {
            if (!IsOpen)
                throw new InvalidOperationException("Device is not open.");
        }

        private void EnsureRunning()
        {
            EnsureOpen();
            if (!_running)
                throw new InvalidOperationException("Device is not running.");
        }

        private void EnsureReadBuffer(int bytes)
        {
            if (_readBufferBytes >= bytes)
                return;

            if (_readBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(_readBuffer);

            _readBuffer = Marshal.AllocHGlobal(bytes);
            _readBufferBytes = bytes;
        }

        private void EnsureImpedanceBuffer(int bytes)
        {
            if (_impedanceBufferBytes >= bytes)
                return;

            if (_impedanceBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(_impedanceBuffer);

            _impedanceBuffer = Marshal.AllocHGlobal(bytes);
            _impedanceBufferBytes = bytes;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException("ActiChampDevice");
        }

        private static void FillArray(int[] array, int value)
        {
            for (int i = 0; i < array.Length; i++)
                array[i] = value;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            try
            {
                Close();
            }
            catch
            {
            }

            if (_readBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(_readBuffer);
            if (_impedanceBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(_impedanceBuffer);

            _readBuffer = IntPtr.Zero;
            _impedanceBuffer = IntPtr.Zero;
            _readBufferBytes = 0;
            _impedanceBufferBytes = 0;
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        ~ActiChampDevice()
        {
            Dispose();
        }

        private static class Native
        {
            [DllImport(DllName, CallingConvention = CallingConvention.Winapi)]
            public static extern int champGetCount();

            [DllImport(DllName, CallingConvention = CallingConvention.Winapi)]
            public static extern int champOpen(int index);

            [DllImport(DllName, CallingConvention = CallingConvention.Winapi)]
            public static extern int champClose(int handle);

            [DllImport(DllName, CallingConvention = CallingConvention.Winapi)]
            public static extern int champGetVersion(int handle, out ChampVersionInfo version);

            [DllImport(DllName, CallingConvention = CallingConvention.Winapi)]
            public static extern int champGetVersionExt(int handle, out ChampVersionInfoExt version);

            [DllImport(DllName, CallingConvention = CallingConvention.Winapi)]
            public static extern int champGetModules(int handle, out ChampModules modules);

            [DllImport(DllName, CallingConvention = CallingConvention.Winapi)]
            public static extern int champSetModules(int handle, ref ChampModules modules);

            [DllImport(DllName, CallingConvention = CallingConvention.Winapi)]
            public static extern int champGetProperty(int handle, out ChampProperties properties);

            [DllImport(DllName, CallingConvention = CallingConvention.Winapi)]
            public static extern int champSetSettingsEx(int handle, ref ChampSettingsEx settings);

            [DllImport(DllName, CallingConvention = CallingConvention.Winapi)]
            public static extern int champSetActiveShieldGain(int handle, uint gain);

            [DllImport(DllName, CallingConvention = CallingConvention.Winapi)]
            public static extern int champStart(int handle);

            [DllImport(DllName, CallingConvention = CallingConvention.Winapi)]
            public static extern int champStop(int handle);

            [DllImport(DllName, CallingConvention = CallingConvention.Winapi)]
            public static extern int champGetDataBlocking(int handle, IntPtr buffer, int requestedBytes);

            [DllImport(DllName, CallingConvention = CallingConvention.Winapi)]
            public static extern int champGetData(int handle, IntPtr buffer, int bufferBytes);

            [DllImport(DllName, CallingConvention = CallingConvention.Winapi)]
            public static extern int champImpedanceGetData(int handle, IntPtr buffer, int bufferBytes);

            [DllImport(DllName, CallingConvention = CallingConvention.Winapi)]
            public static extern int champImpedanceSetSetup(int handle, ref ChampImpedanceSetup setup);

            [DllImport(DllName, CallingConvention = CallingConvention.Winapi)]
            public static extern int champGetDataStatus(int handle, out ChampDataStatus status);

            [DllImport(DllName, CallingConvention = CallingConvention.Winapi)]
            public static extern int champGetVoltages(int handle, out ChampVoltages voltages);

            [DllImport(DllName, CallingConvention = CallingConvention.Winapi)]
            public static extern int champSetElectrodes(int handle, IntPtr values, int sizeBytes);
        }
    }
}
