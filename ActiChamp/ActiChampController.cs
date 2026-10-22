using System;
using System.Threading;

namespace ActiChamp
{
    public enum ActiChampRunState
    {
        Idle = -1,
        Normal = 0,
        ActiveShield = 1,
        Impedance = 2,
        Test = 3,
        LedTest = 99
    }

    public sealed class ActiChampStateChangedEventArgs : EventArgs
    {
        public bool IsInitialized { get; private set; }
        public ActiChampRunState RunState { get; private set; }

        public bool IsRunning
        {
            get { return RunState != ActiChampRunState.Idle; }
        }

        public ActiChampStateChangedEventArgs(bool isInitialized, ActiChampRunState runState)
        {
            IsInitialized = isInitialized;
            RunState = runState;
        }
    }

    public sealed class ActiChampLogEventArgs : EventArgs
    {
        public string Message { get; private set; }
        public DateTime Timestamp { get; private set; }

        public ActiChampLogEventArgs(string message)
        {
            Message = message;
            Timestamp = DateTime.Now;
        }
    }

    public sealed class ActiChampDataBlockEventArgs : EventArgs
    {
        public ActiChampDataBlock DataBlock { get; private set; }

        public ActiChampDataBlockEventArgs(ActiChampDataBlock dataBlock)
        {
            DataBlock = dataBlock;
        }
    }

    public sealed class ActiChampImpedanceEventArgs : EventArgs
    {
        public uint[] ValuesOhm { get; private set; }

        public ActiChampImpedanceEventArgs(uint[] valuesOhm)
        {
            ValuesOhm = valuesOhm;
        }
    }

    public sealed class ActiChampController : IDisposable
    {
        public const int EogHeogLeftIndex = 0;
        public const int EogHeogRightIndex = 1;
        public const int EogVeogUpIndex = 2;
        public const int EogVeogDownIndex = 3;
        public const int EogPhysicalRoleCount = 4;

        private readonly object _sync = new object();
        private ActiChampDevice _device;
        private Thread _workerThread;
        private volatile bool _stopRequested;
        private bool _disposed;
        private bool _runtimePrepared;
        private DateTime _lastStatusLogUtc = DateTime.MinValue;
        private int _readBlockCounter;
        private int _warmupBlocksToSkip = 5;
        private int _loggedEegChannelOneBased = 1;
        private ChampRate _defaultRate = ChampRate.Rate500Hz;
        private int _blockSizeSamples = 32;
        private int _activeShieldGain = 5;
        private int[] _selectedPhysicalChannelIndices;
        private int[] _referencePhysicalChannelIndices;
        private int[] _eogPhysicalChannelIndices = new int[] { -1, -1, -1, -1 };
        private double _lowCutoffHz;
        private double _highCutoffHz;
        private double _eogLowCutoffHz = 0.1;
        private double _eogHighCutoffHz = 30.0;
        private bool _notch50Hz;
        private readonly ActiChampSignalFilter _eegSignalFilter = new ActiChampSignalFilter();
        private readonly ActiChampSignalFilter _eogSignalFilter = new ActiChampSignalFilter();
        private ActiChampRunState _runState = ActiChampRunState.Idle;
        private uint _impedanceGoodOhm = 5000;
        private uint _impedanceBadOhm = 10000;
        private ChampLedTestAction _defaultLedAction = ChampLedTestAction.AllGreen;

        public event EventHandler<ActiChampLogEventArgs> LogMessage;
        public event EventHandler<ActiChampStateChangedEventArgs> StateChanged;
        public event EventHandler<ActiChampDataBlockEventArgs> DataBlockReceived;
        public event EventHandler<ActiChampImpedanceEventArgs> ImpedanceReceived;

        public bool IsInitialized
        {
            get
            {
                lock (_sync)
                {
                    return _device != null && _device.IsOpen;
                }
            }
        }

        public bool IsRunning
        {
            get
            {
                lock (_sync)
                {
                    return _runState != ActiChampRunState.Idle;
                }
            }
        }

        public ActiChampRunState RunState
        {
            get
            {
                lock (_sync)
                {
                    return _runState;
                }
            }
        }

        public ChampProperties Properties
        {
            get
            {
                lock (_sync)
                {
                    if (_device == null || !_device.IsOpen)
                        return default(ChampProperties);
                    return _device.Properties;
                }
            }
        }

        public ChampRate DefaultRate
        {
            get { return _defaultRate; }
            set { _defaultRate = value; }
        }

        public int BlockSizeSamples
        {
            get { return _blockSizeSamples; }
            set
            {
                if (value <= 0)
                    throw new ArgumentOutOfRangeException("value");
                _blockSizeSamples = value;
            }
        }

        public int ActiveShieldGain
        {
            get { return _activeShieldGain; }
            set
            {
                if (value < 1 || value > 100)
                    throw new ArgumentOutOfRangeException("value");
                _activeShieldGain = value;
            }
        }

        public int[] SelectedPhysicalChannelIndices
        {
            get
            {
                lock (_sync)
                {
                    return _selectedPhysicalChannelIndices == null
                        ? null
                        : (int[])_selectedPhysicalChannelIndices.Clone();
                }
            }
            set
            {
                lock (_sync)
                {
                    if (_runState != ActiChampRunState.Idle)
                        throw new InvalidOperationException("Нельзя менять набор каналов во время съёма.");

                    _selectedPhysicalChannelIndices = value == null
                        ? null
                        : (int[])value.Clone();
                }
            }
        }

        public int[] ReferencePhysicalChannelIndices
        {
            get
            {
                lock (_sync)
                {
                    return _referencePhysicalChannelIndices == null
                        ? null
                        : (int[])_referencePhysicalChannelIndices.Clone();
                }
            }
            set
            {
                lock (_sync)
                {
                    if (_runState != ActiChampRunState.Idle)
                        throw new InvalidOperationException("Нельзя менять референтные каналы во время съёма.");

                    _referencePhysicalChannelIndices = value == null
                        ? null
                        : (int[])value.Clone();
                }
            }
        }

        public int[] EogPhysicalChannelIndices
        {
            get
            {
                lock (_sync)
                {
                    return (int[])_eogPhysicalChannelIndices.Clone();
                }
            }
            set
            {
                lock (_sync)
                {
                    if (_runState != ActiChampRunState.Idle)
                        throw new InvalidOperationException("Нельзя менять EOG-каналы во время съёма.");

                    int[] indices = value == null
                        ? new int[] { -1, -1, -1, -1 }
                        : (int[])value.Clone();

                    if (indices.Length != EogPhysicalRoleCount)
                        throw new ArgumentException("EOG configuration must contain four physical channel indices.", "value");

                    ValidateEogPair(indices[EogHeogLeftIndex], indices[EogHeogRightIndex], "HEOG");
                    ValidateEogPair(indices[EogVeogUpIndex], indices[EogVeogDownIndex], "VEOG");

                    for (int i = 0; i < indices.Length; i++)
                    {
                        if (indices[i] < -1)
                            throw new ArgumentOutOfRangeException("value");

                        if (indices[i] < 0)
                            continue;

                        for (int j = i + 1; j < indices.Length; j++)
                        {
                            if (indices[j] == indices[i])
                                throw new InvalidOperationException("Один физический EEG-канал не может использоваться в двух EOG-ролях.");
                        }
                    }

                    _eogPhysicalChannelIndices = indices;
                }
            }
        }

        public double EogLowCutoffHz
        {
            get { return _eogLowCutoffHz; }
            set
            {
                lock (_sync)
                {
                    if (_runState != ActiChampRunState.Idle)
                        throw new InvalidOperationException("Нельзя менять фильтры во время съёма.");
                    if (value < 0.0)
                        throw new ArgumentOutOfRangeException("value");
                    _eogLowCutoffHz = value;
                }
            }
        }

        public double EogHighCutoffHz
        {
            get { return _eogHighCutoffHz; }
            set
            {
                lock (_sync)
                {
                    if (_runState != ActiChampRunState.Idle)
                        throw new InvalidOperationException("Нельзя менять фильтры во время съёма.");
                    if (value < 0.0)
                        throw new ArgumentOutOfRangeException("value");
                    _eogHighCutoffHz = value;
                }
            }
        }

        public double LowCutoffHz
        {
            get { return _lowCutoffHz; }
            set
            {
                lock (_sync)
                {
                    if (_runState != ActiChampRunState.Idle)
                        throw new InvalidOperationException("Нельзя менять фильтры во время съёма.");
                    if (value < 0.0)
                        throw new ArgumentOutOfRangeException("value");
                    _lowCutoffHz = value;
                }
            }
        }

        public double HighCutoffHz
        {
            get { return _highCutoffHz; }
            set
            {
                lock (_sync)
                {
                    if (_runState != ActiChampRunState.Idle)
                        throw new InvalidOperationException("Нельзя менять фильтры во время съёма.");
                    if (value < 0.0)
                        throw new ArgumentOutOfRangeException("value");
                    _highCutoffHz = value;
                }
            }
        }

        public bool Notch50Hz
        {
            get { return _notch50Hz; }
            set
            {
                lock (_sync)
                {
                    if (_runState != ActiChampRunState.Idle)
                        throw new InvalidOperationException("Нельзя менять фильтры во время съёма.");
                    _notch50Hz = value;
                }
            }
        }

        public int WarmupBlocksToSkip
        {
            get { return _warmupBlocksToSkip; }
            set { _warmupBlocksToSkip = value < 0 ? 0 : value; }
        }

        public int LoggedEegChannelOneBased
        {
            get { return _loggedEegChannelOneBased; }
            set { _loggedEegChannelOneBased = value < 1 ? 1 : value; }
        }

        public uint ImpedanceGoodOhm
        {
            get { return _impedanceGoodOhm; }
            set { _impedanceGoodOhm = value; }
        }

        public uint ImpedanceBadOhm
        {
            get { return _impedanceBadOhm; }
            set { _impedanceBadOhm = value; }
        }

        public ChampLedTestAction DefaultLedAction
        {
            get { return _defaultLedAction; }
            set { _defaultLedAction = value; }
        }

        public void Initialize()
        {
            Initialize(_defaultRate);
        }

        public void Initialize(ChampRate rate)
        {
            ChampVoltages voltages;
            ChampProperties properties;

            ThrowIfDisposed();
            PrepareRuntimeOnce();

            lock (_sync)
            {
                if (_runState != ActiChampRunState.Idle)
                    throw new InvalidOperationException("Нельзя переинициализировать устройство во время активного режима.");
            }

            ShutdownDeviceOnly();
            _defaultRate = rate;

            RaiseLog("=== Инициализация actiCHamp (x86) ===");
            RaiseLog("Открытие устройства...");

            lock (_sync)
            {
                _device = new ActiChampDevice(RaiseLog);
                _device.ActiveShieldGain = _activeShieldGain;
                _device.Open(RaiseLog);

                RaiseLog("DLL version: 0x" + _device.DllVersion.ToString("X8"));
                RaiseLog("Маска модулей present: 0x" + _device.Modules.Present.ToString("X8"));
                RaiseLog("Маска модулей enabled: 0x" + _device.Modules.Enabled.ToString("X8"));
                RaiseLog("Ревизия платы: " + _device.BoardRevision.ToString());

                voltages = _device.GetVoltages();
                RaiseLog("После open: VDC=" + voltages.VDC.ToString("0.00") + " V, TEMP=" + voltages.TEMP.ToString("0.0") + " °C");

                RaiseLog("Преднастройка Normal, частота " + ActiChampDevice.RateHzMap[rate].ToString("0") + " Гц...");
                _device.Setup(ChampMode.Normal, rate);
                properties = _device.Properties;
                voltages = _device.GetVoltages();
                _runState = ActiChampRunState.Idle;
            }

            RaiseLog("EEG каналов: " + properties.CountEeg.ToString());
            RaiseLog("AUX каналов: " + properties.CountAux.ToString());
            RaiseLog("Фактическая частота: " + properties.Rate.ToString("0.###") + " Гц");
            RaiseLog("Разрешение EEG: " + properties.ResolutionEeg.ToString("E6") + " V/bit");
            RaiseLog("Разрешение AUX: " + properties.ResolutionAux.ToString("E6") + " V/bit");
            RaiseLog("Размер блока: " + _blockSizeSamples.ToString() + " samples");
            RaiseLog("После setup: VDC=" + voltages.VDC.ToString("0.00") + " V, TEMP=" + voltages.TEMP.ToString("0.0") + " °C");
            RaiseLog("Инициализация завершена.");
            RaiseLog(string.Empty);
            RaiseStateChanged();
        }

        public void StartNormal()
        {
            StartMode(ChampMode.Normal, _defaultRate);
        }

        public void StartImpedance()
        {
            StartMode(ChampMode.Impedance, _defaultRate);
        }

        public void StartTest()
        {
            StartMode(ChampMode.Test, _defaultRate);
        }

        public void StartLedTest()
        {
            StartMode(ChampMode.LedTest, _defaultRate, _defaultLedAction);
        }

        public void StartMode(ChampMode mode, ChampRate rate)
        {
            StartMode(mode, rate, _defaultLedAction);
        }

        public void StartMode(ChampMode mode, ChampRate rate, ChampLedTestAction ledAction)
        {
            ThrowIfDisposed();
            PrepareRuntimeOnce();

            lock (_sync)
            {
                EnsureInitializedInternal();
                if (_runState != ActiChampRunState.Idle)
                    throw new InvalidOperationException("Сначала остановите текущий режим.");

                _defaultRate = rate;
                _device.ActiveShieldGain = _activeShieldGain;

                RaiseLog("=== Запуск режима " + ActiChampDevice.GetModeDisplayName(mode) + " ===");
                RaiseLog("Настройка режима " + ActiChampDevice.GetModeDisplayName(mode) + ", частота " + ActiChampDevice.RateHzMap[rate].ToString("0") + " Гц...");
                _device.Setup(mode, rate);

                if (mode == ChampMode.Impedance)
                {
                    _device.SetImpedanceRange(_impedanceGoodOhm, _impedanceBadOhm, 5);
                    RaiseLog("Порог LED импеданса: good=" + _impedanceGoodOhm.ToString() + " Ω, bad=" + _impedanceBadOhm.ToString() + " Ω");
                }
                else if (ActiChampDevice.IsContinuousDataMode(mode))
                {
                    ValidateEogPhysicalChannelsInternal();

                    int eegChannelCount = GetSelectedEegChannelCountInternal();
                    int eogChannelCount = GetConfiguredEogOutputChannelCountInternal();
                    double sampleRateHz = _device.Properties.Rate;

                    _eegSignalFilter.Configure(
                        sampleRateHz,
                        eegChannelCount,
                        _lowCutoffHz,
                        _highCutoffHz,
                        _notch50Hz);

                    if (eogChannelCount > 0)
                    {
                        _eogSignalFilter.Configure(
                            sampleRateHz,
                            eogChannelCount,
                            _eogLowCutoffHz,
                            _eogHighCutoffHz,
                            _notch50Hz);
                    }
                    else
                    {
                        _eogSignalFilter.Configure(
                            sampleRateHz,
                            0,
                            0.0,
                            0.0,
                            false);
                    }

                    RaiseLog("EEG filters: " + ActiChampSignalFilter.Describe(
                        _lowCutoffHz,
                        _highCutoffHz,
                        _notch50Hz));

                    if (eogChannelCount > 0)
                    {
                        RaiseLog("EOG filters: " + ActiChampSignalFilter.Describe(
                            _eogLowCutoffHz,
                            _eogHighCutoffHz,
                            _notch50Hz));
                    }
                }

                RaiseLog("Вызов champStart...");
                _device.Start();
                RaiseLog("champStart выполнен успешно.");

                _stopRequested = false;
                _readBlockCounter = 0;
                _lastStatusLogUtc = DateTime.MinValue;
                _runState = ToRunState(mode);

                if (mode == ChampMode.Impedance)
                {
                    _workerThread = new Thread(ImpedanceWorker);
                    _workerThread.IsBackground = true;
                    _workerThread.Name = "ActiChampImpedance";
                    _workerThread.Start();
                }
                else if (mode == ChampMode.LedTest)
                {
                    ApplyLedTestActionInternal(ledAction, true);
                    _workerThread = null;
                }
                else
                {
                    _workerThread = new Thread(ReadWorker);
                    _workerThread.IsBackground = true;
                    _workerThread.Name = "ActiChampReader";
                    _workerThread.Priority = ThreadPriority.AboveNormal;
                    _workerThread.Start();
                }
            }

            RaiseLog(string.Empty);
            RaiseStateChanged();
        }

        public void StopCurrentMode()
        {
            Thread workerThread;
            ActiChampDevice localDevice;
            ActiChampRunState oldState;

            ThrowIfDisposed();

            lock (_sync)
            {
                oldState = _runState;
                if (oldState == ActiChampRunState.Idle)
                    return;

                workerThread = _workerThread;
                _stopRequested = true;
            }

            if (workerThread != null && workerThread.IsAlive)
                workerThread.Join(3000);

            lock (_sync)
            {
                localDevice = _device;
                _workerThread = null;
            }

            if (localDevice != null && localDevice.IsRunning)
                localDevice.Stop();

            // Impedance mode internally forces the amplifier to 10 kHz.
            // Restore the normal acquisition configuration immediately after
            // stopping impedance so Properties.Rate and the channel setup do
            // not remain in the impedance configuration.
            if (oldState == ActiChampRunState.Impedance &&
                localDevice != null &&
                localDevice.IsOpen)
            {
                localDevice.ActiveShieldGain = _activeShieldGain;
                localDevice.Setup(ChampMode.Normal, _defaultRate);
                RaiseLog("После Impedance восстановлена конфигурация Normal, " +
                    ActiChampDevice.RateHzMap[_defaultRate].ToString("0") + " Гц.");
            }

            lock (_sync)
            {
                _runState = ActiChampRunState.Idle;
            }

            RaiseLog("=== Остановка режима " + RunStateDisplayName(oldState) + " ===");
            RaiseLog("Съём остановлен.");
            RaiseLog(string.Empty);
            RaiseStateChanged();
        }

        public bool ApplyLedTestAction(ChampLedTestAction action)
        {
            ThrowIfDisposed();
            lock (_sync)
            {
                if (_device == null || !_device.IsOpen || !_device.IsRunning || _runState != ActiChampRunState.LedTest)
                    throw new InvalidOperationException("LED test action доступно только при активном режиме Electrode LED Test.");
                return ApplyLedTestActionInternal(action, false);
            }
        }

        public void Shutdown()
        {
            if (_disposed)
                return;

            try
            {
                if (IsRunning)
                    StopCurrentMode();
            }
            finally
            {
                ShutdownDeviceOnly();
                RaiseStateChanged();
            }
        }

        private void ReadWorker()
        {
            try
            {
                RaiseLog("Пауза 200 мс перед первым чтением...");
                Thread.Sleep(200);

                for (int i = 0; i < _warmupBlocksToSkip; i++)
                {
                    if (_stopRequested)
                        return;

                    ActiChampDataBlock ignored = ReadOneBlock();
                    if (ignored == null)
                        i--;
                }

                if (_warmupBlocksToSkip > 0)
                    RaiseLog("Прогревочные блоки пропущены. Идёт рабочее чтение.");

                while (!_stopRequested)
                {
                    ActiChampDataBlock block = ReadOneBlock();
                    if (block == null)
                        continue;

                    _readBlockCounter++;
                    RaiseDataBlock(block);
                    MaybeRaiseStatusLog(block);
                }
            }
            catch (ThreadAbortException)
            {
            }
            catch (Exception ex)
            {
                RaiseLog("Ошибка в потоке чтения: " + ex.ToString());
            }
            finally
            {
                RaiseStateChanged();
            }
        }

        private void ImpedanceWorker()
        {
            try
            {
                RaiseLog("Пауза 300 мс перед первым чтением импеданса...");
                Thread.Sleep(300);

                while (!_stopRequested)
                {
                    uint[] values = ReadImpedances();
                    if (values != null)
                        RaiseImpedances(values);

                    if (_stopRequested)
                        return;

                    Thread.Sleep(1000);
                }
            }
            catch (ThreadAbortException)
            {
            }
            catch (Exception ex)
            {
                RaiseLog("Ошибка в потоке импеданса: " + ex.ToString());
            }
            finally
            {
                RaiseStateChanged();
            }
        }

        private ActiChampDataBlock ReadOneBlock()
        {
            lock (_sync)
            {
                if (_device == null || !_device.IsRunning)
                    return null;

                ActiChampDataBlock sourceBlock = _device.ReadSamples(
                    _blockSizeSamples,
                    _selectedPhysicalChannelIndices,
                    _referencePhysicalChannelIndices,
                    _eogPhysicalChannelIndices);

                if (sourceBlock == null)
                    return null;

                ActiChampDataBlock block = BuildFinalOutputBlock(sourceBlock);

                _eegSignalFilter.ProcessInPlace(
                    block,
                    0,
                    block.EegChannelCount);

                _eogSignalFilter.ProcessInPlace(
                    block,
                    block.EegChannelCount,
                    block.EogChannelCount);

                return block;
            }
        }

        private int GetSelectedEegChannelCountInternal()
        {
            if (_device == null || !_device.IsOpen)
                return 0;

            int physicalEegCount = checked((int)_device.Properties.CountEeg);

            if (_selectedPhysicalChannelIndices == null || _selectedPhysicalChannelIndices.Length == 0)
                return physicalEegCount - GetConfiguredEogSourceChannelCountInternal();

            int count = 0;
            for (int i = 0; i < _selectedPhysicalChannelIndices.Length; i++)
            {
                int physicalIndex = _selectedPhysicalChannelIndices[i];
                if (physicalIndex >= 0 &&
                    physicalIndex < physicalEegCount &&
                    !IsEogPhysicalChannelInternal(physicalIndex))
                {
                    count++;
                }
            }

            return count;
        }

        private int GetConfiguredEogOutputChannelCountInternal()
        {
            int count = 0;

            if (_eogPhysicalChannelIndices[EogHeogLeftIndex] >= 0)
                count++;

            if (_eogPhysicalChannelIndices[EogVeogUpIndex] >= 0)
                count++;

            return count;
        }

        private int GetConfiguredEogSourceChannelCountInternal()
        {
            int count = 0;

            for (int i = 0; i < _eogPhysicalChannelIndices.Length; i++)
            {
                if (_eogPhysicalChannelIndices[i] >= 0)
                    count++;
            }

            return count;
        }

        private bool IsEogPhysicalChannelInternal(int physicalIndex)
        {
            return Array.IndexOf(_eogPhysicalChannelIndices, physicalIndex) >= 0;
        }

        private void ValidateEogPhysicalChannelsInternal()
        {
            if (_device == null || !_device.IsOpen)
                return;

            int physicalEegCount = checked((int)_device.Properties.CountEeg);

            for (int i = 0; i < _eogPhysicalChannelIndices.Length; i++)
            {
                int physicalIndex = _eogPhysicalChannelIndices[i];
                if (physicalIndex < 0)
                    continue;

                if (physicalIndex >= physicalEegCount)
                {
                    throw new InvalidOperationException(
                        "EOG может использовать только физические EEG-каналы actiCHamp.");
                }

                if (_selectedPhysicalChannelIndices == null ||
                    Array.IndexOf(_selectedPhysicalChannelIndices, physicalIndex) < 0)
                {
                    throw new InvalidOperationException(
                        "EOG-канал должен входить в набор считываемых физических каналов.");
                }
            }
        }

        private static void ValidateEogPair(int firstPhysicalIndex, int secondPhysicalIndex, string pairName)
        {
            bool hasFirst = firstPhysicalIndex >= 0;
            bool hasSecond = secondPhysicalIndex >= 0;

            if (hasFirst != hasSecond)
            {
                throw new InvalidOperationException(
                    pairName + " должен быть задан полной парой каналов.");
            }
        }

        private ActiChampDataBlock BuildFinalOutputBlock(ActiChampDataBlock sourceBlock)
        {
            int eogOutputCount = GetConfiguredEogOutputChannelCountInternal();
            if (eogOutputCount <= 0)
                return sourceBlock;

            int sampleCount = sourceBlock.SampleCount;
            int physicalEegCount = checked((int)_device.Properties.CountEeg);
            int eegOutputCount = 0;
            int auxOutputCount = 0;

            int heogLeftSource = -1;
            int heogRightSource = -1;
            int veogUpSource = -1;
            int veogDownSource = -1;

            for (int sourceCh = 0; sourceCh < sourceBlock.ChannelCount; sourceCh++)
            {
                int physicalIndex = sourceBlock.PhysicalChannelIndices[sourceCh];

                if (physicalIndex == _eogPhysicalChannelIndices[EogHeogLeftIndex])
                {
                    heogLeftSource = sourceCh;
                    continue;
                }
                if (physicalIndex == _eogPhysicalChannelIndices[EogHeogRightIndex])
                {
                    heogRightSource = sourceCh;
                    continue;
                }
                if (physicalIndex == _eogPhysicalChannelIndices[EogVeogUpIndex])
                {
                    veogUpSource = sourceCh;
                    continue;
                }
                if (physicalIndex == _eogPhysicalChannelIndices[EogVeogDownIndex])
                {
                    veogDownSource = sourceCh;
                    continue;
                }

                if (physicalIndex < physicalEegCount)
                    eegOutputCount++;
                else
                    auxOutputCount++;
            }

            int outputChannelCount = eegOutputCount + eogOutputCount + auxOutputCount;
            float[] output = new float[outputChannelCount * sampleCount];
            int[] outputPhysicalIndices = new int[outputChannelCount];
            int outputCh = 0;

            for (int sourceCh = 0; sourceCh < sourceBlock.ChannelCount; sourceCh++)
            {
                int physicalIndex = sourceBlock.PhysicalChannelIndices[sourceCh];

                if (physicalIndex >= physicalEegCount || IsEogPhysicalChannelInternal(physicalIndex))
                    continue;

                Buffer.BlockCopy(
                    sourceBlock.SignalsVoltsChannelMajor,
                    sourceCh * sampleCount * sizeof(float),
                    output,
                    outputCh * sampleCount * sizeof(float),
                    sampleCount * sizeof(float));

                outputPhysicalIndices[outputCh] = physicalIndex;
                outputCh++;
            }

            if (_eogPhysicalChannelIndices[EogHeogLeftIndex] >= 0)
            {
                if (heogLeftSource < 0 || heogRightSource < 0)
                    throw new InvalidOperationException("HEOG source channels are missing from the acquisition block.");

                int leftBase = heogLeftSource * sampleCount;
                int rightBase = heogRightSource * sampleCount;
                int outputBase = outputCh * sampleCount;

                for (int s = 0; s < sampleCount; s++)
                {
                    output[outputBase + s] =
                        sourceBlock.SignalsVoltsChannelMajor[leftBase + s] -
                        sourceBlock.SignalsVoltsChannelMajor[rightBase + s];
                }

                outputPhysicalIndices[outputCh] = -1; // HEOG = Left - Right
                outputCh++;
            }

            if (_eogPhysicalChannelIndices[EogVeogUpIndex] >= 0)
            {
                if (veogUpSource < 0 || veogDownSource < 0)
                    throw new InvalidOperationException("VEOG source channels are missing from the acquisition block.");

                int upBase = veogUpSource * sampleCount;
                int downBase = veogDownSource * sampleCount;
                int outputBase = outputCh * sampleCount;

                for (int s = 0; s < sampleCount; s++)
                {
                    output[outputBase + s] =
                        sourceBlock.SignalsVoltsChannelMajor[upBase + s] -
                        sourceBlock.SignalsVoltsChannelMajor[downBase + s];
                }

                outputPhysicalIndices[outputCh] = -2; // VEOG = Up - Down
                outputCh++;
            }

            for (int sourceCh = 0; sourceCh < sourceBlock.ChannelCount; sourceCh++)
            {
                int physicalIndex = sourceBlock.PhysicalChannelIndices[sourceCh];
                if (physicalIndex < physicalEegCount)
                    continue;

                Buffer.BlockCopy(
                    sourceBlock.SignalsVoltsChannelMajor,
                    sourceCh * sampleCount * sizeof(float),
                    output,
                    outputCh * sampleCount * sizeof(float),
                    sampleCount * sizeof(float));

                outputPhysicalIndices[outputCh] = physicalIndex;
                outputCh++;
            }

            ActiChampDataBlock result = new ActiChampDataBlock();
            result.SignalsVoltsChannelMajor = output;
            result.Trigger = sourceBlock.Trigger;
            result.SampleCounter = sourceBlock.SampleCounter;
            result.EegChannelCount = eegOutputCount;
            result.EogChannelCount = eogOutputCount;
            result.AuxChannelCount = auxOutputCount;
            result.PhysicalChannelIndices = outputPhysicalIndices;
            return result;
        }

        private uint[] ReadImpedances()
        {
            lock (_sync)
            {
                if (_device == null || !_device.IsRunning)
                    return null;
                return _device.ReadImpedances();
            }
        }

        private void MaybeRaiseStatusLog(ActiChampDataBlock block)
        {
            if (block == null)
                return;

            DateTime nowUtc = DateTime.UtcNow;
            if ((nowUtc - _lastStatusLogUtc).TotalSeconds < 1.0)
                return;

            _lastStatusLogUtc = nowUtc;

            int signalIndex;
            string signalLabel;
            GetLoggedSignal(block, out signalIndex, out signalLabel);

            double signalUv = 0.0;
            uint trg = 0;
            ulong sc0 = 0;
            ulong scN = 0;

            if (block.SampleCount > 0)
            {
                trg = block.Trigger[0];
                sc0 = block.SampleCounter[0];
                scN = block.SampleCounter[block.SampleCount - 1];

                if (block.ChannelCount > 0 && signalIndex >= 0)
                    signalUv = block.GetSignalVolts(signalIndex, 0) * 1000000.0;
            }

            RaiseLog(
                "Режим " + RunStateDisplayName(_runState) +
                ": блок " + _readBlockCounter.ToString() +
                ", samples=" + block.SampleCount.ToString() +
                ", channels=" + block.ChannelCount.ToString() +
                ", trigger0=" + trg.ToString() +
                ", sampleCounter=" + sc0.ToString() + ".." + scN.ToString() +
                ", " + signalLabel + "[0]=" + signalUv.ToString("F3") + " µV");
        }

        private void GetLoggedSignal(ActiChampDataBlock block, out int signalIndex, out string signalLabel)
        {
            signalIndex = -1;
            signalLabel = "Ch";

            if (block == null || block.ChannelCount <= 0)
                return;

            ChampProperties props = Properties;
            int physicalEegCount = checked((int)props.CountEeg);
            int requestedPhysicalEeg = _loggedEegChannelOneBased - 1;

            if (block.PhysicalChannelIndices != null)
            {
                for (int i = 0; i < block.PhysicalChannelIndices.Length; i++)
                {
                    if (block.PhysicalChannelIndices[i] == requestedPhysicalEeg)
                    {
                        signalIndex = i;
                        break;
                    }
                }

                if (signalIndex < 0)
                {
                    for (int i = 0; i < block.PhysicalChannelIndices.Length; i++)
                    {
                        if (block.PhysicalChannelIndices[i] < physicalEegCount)
                        {
                            signalIndex = i;
                            break;
                        }
                    }
                }

                if (signalIndex < 0)
                    signalIndex = 0;

                int physicalIndex = block.PhysicalChannelIndices[signalIndex];

                if (physicalIndex == -1)
                    signalLabel = "HEOG";
                else if (physicalIndex == -2)
                    signalLabel = "VEOG";
                else if (physicalIndex >= 0 && physicalIndex < physicalEegCount)
                    signalLabel = "EEG" + (physicalIndex + 1).ToString();
                else
                    signalLabel = "AUX" + (physicalIndex - physicalEegCount + 1).ToString();
            }
            else
            {
                signalIndex = 0;
                signalLabel = "Ch1";
            }
        }

        private void EnsureInitializedInternal()
        {
            if (_device == null || !_device.IsOpen)
                throw new InvalidOperationException("Сначала инициализируйте actiCHamp.");
        }

        private bool ApplyLedTestActionInternal(ChampLedTestAction action, bool log)
        {
            bool cycleDone = _device.ApplyLedTestAction(action);
            if (log)
                RaiseLog("LED test action: " + action.ToString() + "." + (cycleDone ? " Полный цикл завершён." : string.Empty));
            return cycleDone;
        }

        private void ShutdownDeviceOnly()
        {
            ActiChampDevice deviceToDispose = null;

            lock (_sync)
            {
                if (_device == null)
                    return;

                deviceToDispose = _device;
                _device = null;
                _workerThread = null;
                _stopRequested = true;
                _runState = ActiChampRunState.Idle;
            }

            try
            {
                deviceToDispose.Dispose();
            }
            catch (Exception ex)
            {
                RaiseLog("Ошибка при освобождении устройства: " + ex.Message);
            }
        }

        private void PrepareRuntimeOnce()
        {
            if (_runtimePrepared)
                return;

            _runtimePrepared = true;
            ActiChampRuntimeCompat.ApplyPyCorderLikeProcessSettings(delegate(string message)
            {
                RaiseLog(message);
            });
        }

        private void RaiseLog(string message)
        {
            EventHandler<ActiChampLogEventArgs> handler = LogMessage;
            if (handler != null)
                handler(this, new ActiChampLogEventArgs(message));
        }

        private void RaiseStateChanged()
        {
            EventHandler<ActiChampStateChangedEventArgs> handler = StateChanged;
            if (handler != null)
                handler(this, new ActiChampStateChangedEventArgs(IsInitialized, GetRunState()));
        }

        private void RaiseDataBlock(ActiChampDataBlock dataBlock)
        {
            EventHandler<ActiChampDataBlockEventArgs> handler = DataBlockReceived;
            if (handler != null)
                handler(this, new ActiChampDataBlockEventArgs(dataBlock));
        }

        private void RaiseImpedances(uint[] values)
        {
            EventHandler<ActiChampImpedanceEventArgs> handler = ImpedanceReceived;
            if (handler != null)
                handler(this, new ActiChampImpedanceEventArgs(values));
        }

        private ActiChampRunState GetRunState()
        {
            lock (_sync)
            {
                return _runState;
            }
        }

        private static ActiChampRunState ToRunState(ChampMode mode)
        {
            switch (mode)
            {
                case ChampMode.Normal: return ActiChampRunState.Normal;
                case ChampMode.ActiveShield: return ActiChampRunState.ActiveShield;
                case ChampMode.Impedance: return ActiChampRunState.Impedance;
                case ChampMode.Test: return ActiChampRunState.Test;
                case ChampMode.LedTest: return ActiChampRunState.LedTest;
                default: return ActiChampRunState.Idle;
            }
        }

        private static string RunStateDisplayName(ActiChampRunState state)
        {
            switch (state)
            {
                case ActiChampRunState.Normal: return "Normal";
                case ActiChampRunState.ActiveShield: return "ActiveShield";
                case ActiChampRunState.Impedance: return "Impedance";
                case ActiChampRunState.Test: return "Test";
                case ActiChampRunState.LedTest: return "Electrode LED Test";
                default: return "Idle";
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException("ActiChampController");
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            Shutdown();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
