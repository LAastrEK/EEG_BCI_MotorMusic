using OpenTK.Graphics.OpenGL;
using PolyHubIpc;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ActiChamp
{
    public partial class fmMain : Form
    {
        private ServiceEventLink _serviceEventLink;
        private ActiChampProcessingBlockRing _processingBlockRing;

        private const string ServiceSettingsFileName = "ActiChamp.json";
        private const string ServiceManifestFileName = "ActiChamp.service.json";
        private const int ProcessingRingBlockCount = 256;
        private const int MaxPendingDisplayBlocks = 64;

        // PyCorder default impedance display range: 0..50 kOhm,
        // fixed green / yellow / red thirds.
        private const uint ImpedanceRangeMaxOhm = 50000U;
        private const uint ImpedanceGoodOhm = 16667U;
        private const uint ImpedanceBadOhm = 33333U;

        private ActiChampSettings _actiCHampSettings;
        private ServiceManifest _serviceManifest;

        private readonly ActiChampController _actiChampController = new ActiChampController();

        private bool _enableLocalDisplayCache = true;
        private int _lastShownDroppedBlocks = -1;

        // -------------------------------------------------
        // Multimedia timer: только локальная визуализация.
        // Съём данных тактируется самим actiCHamp через champGetDataBlocking.
        // -------------------------------------------------
        public int mTimerId;
        private TimerEventHandler mHandler;

        private delegate void TimerEventHandler(int id, int msg, IntPtr user, int dw1, int dw2);

        private const int TIME_PERIODIC = 1;
        private const int EVENT_TYPE = TIME_PERIODIC;
        private const int UiTimerPeriodMs = 5;

        [DllImport("winmm.dll")]
        private static extern int timeSetEvent(int delay, int resolution, TimerEventHandler handler, IntPtr user, int eventType);

        [DllImport("winmm.dll")]
        private static extern int timeKillEvent(int id);

        [DllImport("winmm.dll")]
        private static extern int timeBeginPeriod(int msec);

        [DllImport("winmm.dll")]
        private static extern int timeEndPeriod(int msec);

        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceCounter(out long value);

        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceFrequency(out long freq);

        private static long _qpcFreq;

        public delegate void FmNotifyDrawFrameHandler();
        public FmNotifyDrawFrameHandler fmNotifyDrawFrame;

        // -------------------------------------------------
        // Runtime
        // -------------------------------------------------
        private readonly object _ringSync = new object();
        private readonly object _displaySync = new object();

        private int _timerBusy;
        private bool _timePeriodActive;
        private bool _glInitialized;
        private bool _isRunning;
        private bool _visualClockStarted;

        private enum DisplayRenderMode
        {
            Waveform = 0,
            Impedance = 1
        }

        private DisplayRenderMode _displayRenderMode = DisplayRenderMode.Waveform;

        private uint[] _impedanceValuesOhm;
        private int[] _impedancePhysicalChannelIndices = new int[0];
        private string[] _impedanceChannelNames = new string[0];

        private long _receivedSamplesCount;

        private int _bufferChannelsCount;
        private int _eegChannelsCount;
        private int _eogChannelsCount;
        private int _auxChannelsCount;
        private int _physicalEegChannelsCount;
        private int _physicalAuxChannelsCount;
        private int _samplingRate;
        private int _viewSamplesPerChannel;
        private int[] _selectedPhysicalChannelIndices = new int[0];
        private int[] _referencePhysicalChannelIndices = new int[0];
        private string[] _selectedChannelNames = new string[0];
        private int _channelGridEegCount = -1;
        private int _channelGridAuxCount = -1;
        private bool _updatingChannelGrid;

        private const string ChannelTypeEeg = "EEG";
        private const string ChannelTypeHeogLeft = "HEOG-L";
        private const string ChannelTypeHeogRight = "HEOG-R";
        private const string ChannelTypeVeogUp = "VEOG-U";
        private const string ChannelTypeVeogDown = "VEOG-D";
        private const string ChannelTypeAux = "AUX";

        private const int ChannelColumnUseIndex = 0;
        private const int ChannelColumnRefIndex = 1;
        private const int ChannelColumnPhysicalIndex = 2;
        private const int ChannelColumnNameIndex = 3;
        private const int ChannelColumnTypeIndex = 4;

        // -------------------------------------------------
        // Visual phase
        // -------------------------------------------------
        private float _pageShiftPx;
        private long _visualPageSeq;
        private float _pageDxPx;

        private struct PendingBlock
        {
            public float[] Samples;
            public int SamplesCount;
            public long FirstAbsoluteSample;
            public int ChannelsCount;
        }

        private readonly List<PendingBlock> _pendingBlocks = new List<PendingBlock>(16);

        private struct PrevPoint
        {
            public bool HasValue;
            public float X;
            public float Y;
            public long AbsoluteSample;
        }

        private PrevPoint[] _prevPoints;

        private readonly int[] _waveTex = new int[2];
        private readonly int[] _waveFbo = new int[2];
        private readonly long[] _waveSeq = new long[] { long.MinValue, long.MinValue };

        private int _pageW;
        private int _pageH;

        // -------------------------------------------------
        // Overlay
        // -------------------------------------------------
        // Waveform overlay resources. These are never reused by Impedance mode.
        private int _overlayTextureId;
        private Bitmap _overlayBitmap;
        private bool _overlayDirty = true;

        // Impedance display has its own texture/bitmap. Keeping these resources
        // separate prevents a mode switch from contaminating waveform rendering.
        private int _impedanceTextureId;
        private Bitmap _impedanceBitmap;
        private bool _impedanceDirty = true;

        private const int DefaultTimeBaseSeconds = 5;
        private int _timeBaseSeconds = DefaultTimeBaseSeconds;
        private float _displayScaleVolts = 100e-6f;
        private float[] _displayBaselineVolts;
        private bool[] _displayBaselineInitialized;

        private const float PlotLeft = 70.0f;
        private const float PlotTop = 10.0f;
        private const float PlotRight = 10.0f;
        private const float PlotBottom = 24.0f;

        public fmMain(bool hosted)
        {
            InitializeComponent();
            QueryPerformanceFrequency(out _qpcFreq);

            if (hosted)
            {
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
            }

            fmNotifyDrawFrame = new FmNotifyDrawFrameHandler(DrawFrameFromTimer);
        }

        private bool IsImpedanceDisplayMode
        {
            get { return _displayRenderMode == DisplayRenderMode.Impedance; }
        }

        private void EnterWaveformDisplayMode()
        {
            _displayRenderMode = DisplayRenderMode.Waveform;
            InvalidateOverlay();
        }

        private void EnterImpedanceDisplayMode()
        {
            _displayRenderMode = DisplayRenderMode.Impedance;
            InvalidateImpedanceDisplay();
        }

        // -------------------------------------------------
        // Form lifetime
        // -------------------------------------------------
        private void fmMain_Load(object sender, EventArgs e)
        {
            LoadLocalJsonFiles();
            PopulateSamplingRates();
            ApplyServiceProgramSettingsToUi();

            _timeBaseSeconds = (int)numUpDwnTimeBaseInSec.Value;
            _displayScaleVolts = ParseAmplitudeScaleVolts(cmbBxScaleAmpl.Text);

            _actiChampController.LogMessage += ActiChampController_LogMessage;
            _actiChampController.StateChanged += ActiChampController_StateChanged;
            _actiChampController.DataBlockReceived += ActiChampController_DataBlockReceived;
            _actiChampController.ImpedanceReceived += ActiChampController_ImpedanceReceived;

            _serviceEventLink = new ServiceEventLink(
                _serviceManifest.ServiceEvtLinkManifest.ServiceEvtLinkMapName,
                _serviceManifest.ServiceEvtLinkManifest.HubToServiceEvtName,
                _serviceManifest.ServiceEvtLinkManifest.ServiceToHubEvtName,
                ServiceEventSide.Service,
                _serviceManifest.ServiceEvtLinkManifest.ServiceEvtLinkCapacity);

            _serviceEventLink.EventReceived += ServiceEventLink_EventReceived;

            EnsureGlInitialized();
            ResetDisplayState(1, 500);

            if (_enableLocalDisplayCache)
                RecreateWavePages();

            InvalidateOverlay();
            UpdateActiChampUiState(false, false);
            UpdateDroppedBlocksStatusSafe(0);
            PublishServiceState();

            AppendLog("Приложение actiCHamp service запущено.");
        }

        private void fmMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                StopAcquisitionInternal(false);
            }
            catch
            {
            }

            CaptureServiceProgramSettingsFromUi();
            SaveLocalJsonFiles();

            _actiChampController.LogMessage -= ActiChampController_LogMessage;
            _actiChampController.StateChanged -= ActiChampController_StateChanged;
            _actiChampController.DataBlockReceived -= ActiChampController_DataBlockReceived;
            _actiChampController.ImpedanceReceived -= ActiChampController_ImpedanceReceived;
            _actiChampController.Dispose();

            if (_serviceEventLink != null)
            {
                _serviceEventLink.EventReceived -= ServiceEventLink_EventReceived;
                _serviceEventLink.Dispose();
                _serviceEventLink = null;
            }

            if (_glInitialized)
            {
                try
                {
                    glCtrl.MakeCurrent();

                    if (_overlayTextureId != 0)
                    {
                        GL.DeleteTexture(_overlayTextureId);
                        _overlayTextureId = 0;
                    }

                    if (_impedanceTextureId != 0)
                    {
                        GL.DeleteTexture(_impedanceTextureId);
                        _impedanceTextureId = 0;
                    }

                    DestroyWavePages();
                }
                catch
                {
                }
            }

            if (_overlayBitmap != null)
            {
                _overlayBitmap.Dispose();
                _overlayBitmap = null;
            }

            if (_impedanceBitmap != null)
            {
                _impedanceBitmap.Dispose();
                _impedanceBitmap = null;
            }
        }

        // -------------------------------------------------
        // Service events / config
        // -------------------------------------------------
        private void ServiceEventLink_EventReceived(ServiceEventPacket evt)
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            BeginInvoke((Action)(() =>
            {
                ServiceEventCode code = (ServiceEventCode)evt.Code;

                try
                {
                    switch (code)
                    {
                        case ServiceEventCode.Start:
                            if (_actiChampController.RunState == ActiChampRunState.Impedance)
                                StopImpedanceInternal(false);
                            if (!_isRunning)
                                StartAcquisitionInternal();
                            break;

                        case ServiceEventCode.Stop:
                            if (_actiChampController.IsRunning || _isRunning)
                                StopAcquisitionInternal(true);
                            break;

                        case ServiceEventCode.Reset:
                            StopAcquisitionInternal(false);
                            StartAcquisitionInternal();
                            break;

                        case ServiceEventCode.Ping:
                            break;
                    }

                    _serviceEventLink.SendEvent(
                        ServiceEventCode.Ack,
                        _actiChampController.IsRunning ? 1 : 0,
                        (int)code,
                        0,
                        "OK");
                }
                catch (Exception ex)
                {
                    _serviceEventLink.SendEvent(
                        ServiceEventCode.Error,
                        _actiChampController.IsRunning ? 1 : 0,
                        (int)code,
                        0,
                        ex.Message);
                }
            }));
        }

        private string GetServiceSettingsFilePath()
        {
            return System.IO.Path.Combine(Application.StartupPath, ServiceSettingsFileName);
        }

        private string GetServiceManifestFilePath()
        {
            return System.IO.Path.Combine(Application.StartupPath, ServiceManifestFileName);
        }

        private ActiChampSettings CreateDefaultServiceProgramSettings()
        {
            return new ActiChampSettings
            {
                WindowLeft = 100,
                WindowTop = 100,
                WindowWidth = 1100,
                WindowHeight = 720,
                SamplingRateHz = 500,
                BlockSizeSamples = 32,
                TimeBaseSeconds = 5,
                DisplayScale = "100 µV",
                ActiveShieldGain = 5,
                Channels = new List<ActiChampChannelSettings>(),
                LowCutoffHz = 0.0,
                HighCutoffHz = 0.0,
                Notch50Hz = false,
                EogLowCutoffHz = 0.1,
                EogHighCutoffHz = 30.0
            };
        }

        private ServiceManifest CreateDefaultServiceManifest()
        {
            return new ServiceManifest
            {
                ServiceId = "ActiChamp",
                ServiceDisplayName = "actiCHamp",

                ServiceProgramFolderName = "ActiChamp",
                ServiceExeFileName = "ActiChamp.exe",
                ServiceExeSubFolder = @"bin\Debug",
                ServiceHostArguments = "--hosted",
                ServiceSupportsHosted = true,

                ServiceEvtLinkManifest = new ServiceEvtLinkManifest
                {
                    ServiceEvtLinkMapName = @"Local\PolyHub.ActiChamp.1.ServiceEventLink",
                    HubToServiceEvtName = @"Local\PolyHub.ActiChamp.1.ServiceEventLink.HubToServiceEvent",
                    ServiceToHubEvtName = @"Local\PolyHub.ActiChamp.1.ServiceEventLink.ServiceToHubEvent",
                    ServiceEvtLinkCapacity = 256
                },

                ServiceDataOutputManifestList = new List<ServiceDataPortManifest>
                {
                    new ServiceDataPortManifest
                    {
                        ServiceDataPortId = "RawSignal",
                        ServiceDataPortDisplayName = "Raw Signal",
                        ServiceDataPortMapName = @"Local\PolyHub.ActiChamp.1.ProcessingBlockRing",
                        ServiceDataPortReadyEventName = @"Local\PolyHub.ActiChamp.1.ProcessingBlockRing.DataReady"
                    }
                },

                ServiceDataInputManifestList = new List<ServiceDataPortManifest>()
            };
        }

        private void LoadLocalJsonFiles()
        {
            _actiCHampSettings = JsonFileStore.LoadOrCreate(
                GetServiceSettingsFilePath(),
                CreateDefaultServiceProgramSettings);

            _serviceManifest = JsonFileStore.LoadOrCreate(
                GetServiceManifestFilePath(),
                CreateDefaultServiceManifest);
        }

        private void ApplyServiceProgramSettingsToUi()
        {
            StartPosition = FormStartPosition.Manual;
            Left = _actiCHampSettings.WindowLeft;
            Top = _actiCHampSettings.WindowTop;
            Width = _actiCHampSettings.WindowWidth;
            Height = _actiCHampSettings.WindowHeight;

            SelectSamplingRate(_actiCHampSettings.SamplingRateHz);

            decimal blockSize = _actiCHampSettings.BlockSizeSamples;
            if (blockSize < numUpDwnBlockSize.Minimum)
                blockSize = numUpDwnBlockSize.Minimum;
            if (blockSize > numUpDwnBlockSize.Maximum)
                blockSize = numUpDwnBlockSize.Maximum;
            numUpDwnBlockSize.Value = blockSize;

            decimal timeBase = _actiCHampSettings.TimeBaseSeconds;
            if (timeBase < numUpDwnTimeBaseInSec.Minimum)
                timeBase = numUpDwnTimeBaseInSec.Minimum;
            if (timeBase > numUpDwnTimeBaseInSec.Maximum)
                timeBase = numUpDwnTimeBaseInSec.Maximum;
            numUpDwnTimeBaseInSec.Value = timeBase;

            decimal gain = _actiCHampSettings.ActiveShieldGain;
            if (gain < numUpDwnActiveShieldGain.Minimum)
                gain = numUpDwnActiveShieldGain.Minimum;
            if (gain > numUpDwnActiveShieldGain.Maximum)
                gain = numUpDwnActiveShieldGain.Maximum;
            numUpDwnActiveShieldGain.Value = gain;

            int scaleIndex = cmbBxScaleAmpl.Items.IndexOf(_actiCHampSettings.DisplayScale);
            cmbBxScaleAmpl.SelectedIndex = scaleIndex >= 0 ? scaleIndex : cmbBxScaleAmpl.Items.IndexOf("100 µV");

            SelectFilterFrequency(cmbLowCutoff, _actiCHampSettings.LowCutoffHz);
            SelectFilterFrequency(cmbHighCutoff, _actiCHampSettings.HighCutoffHz);
            SelectFilterFrequency(cmbEogLowCutoff, _actiCHampSettings.EogLowCutoffHz);
            SelectFilterFrequency(cmbEogHighCutoff, _actiCHampSettings.EogHighCutoffHz);
            chkNotch50Hz.Checked = _actiCHampSettings.Notch50Hz;
        }

        private void CaptureServiceProgramSettingsFromUi()
        {
            _actiCHampSettings.WindowLeft = Left;
            _actiCHampSettings.WindowTop = Top;
            _actiCHampSettings.WindowWidth = Width;
            _actiCHampSettings.WindowHeight = Height;
            _actiCHampSettings.SamplingRateHz = GetSelectedSamplingRateHz();
            _actiCHampSettings.BlockSizeSamples = (int)numUpDwnBlockSize.Value;
            _actiCHampSettings.TimeBaseSeconds = (int)numUpDwnTimeBaseInSec.Value;
            _actiCHampSettings.DisplayScale = cmbBxScaleAmpl.Text;
            _actiCHampSettings.ActiveShieldGain = (int)numUpDwnActiveShieldGain.Value;
            _actiCHampSettings.LowCutoffHz = ParseFilterFrequency(cmbLowCutoff.Text);
            _actiCHampSettings.HighCutoffHz = ParseFilterFrequency(cmbHighCutoff.Text);
            _actiCHampSettings.EogLowCutoffHz = ParseFilterFrequency(cmbEogLowCutoff.Text);
            _actiCHampSettings.EogHighCutoffHz = ParseFilterFrequency(cmbEogHighCutoff.Text);
            _actiCHampSettings.Notch50Hz = chkNotch50Hz.Checked;

            if (dgvChannels.Rows.Count > 0)
                _actiCHampSettings.Channels = CaptureChannelSettingsFromGrid();
        }

        private void SaveLocalJsonFiles()
        {
            JsonFileStore.Save(GetServiceSettingsFilePath(), _actiCHampSettings);
            JsonFileStore.Save(GetServiceManifestFilePath(), _serviceManifest);
        }

        private void PublishServiceState()
        {
            if (_serviceEventLink == null)
                return;

            ActiChampRunState runState = _actiChampController.RunState;
            string stateText;

            if (runState == ActiChampRunState.Impedance)
                stateText = "Impedance";
            else if (runState != ActiChampRunState.Idle)
                stateText = "Running";
            else
                stateText = _actiChampController.IsInitialized ? "Initialized" : "Stopped";

            _serviceEventLink.SendEvent(
                ServiceEventCode.State,
                runState != ActiChampRunState.Idle ? 1 : 0,
                _actiChampController.IsInitialized ? 1 : 0,
                (int)runState,
                stateText);
        }

        private ServiceDataPortManifest GetPrimaryServiceDataOutputManifest()
        {
            if (_serviceManifest == null)
                throw new InvalidOperationException("ServiceManifest is null.");

            if (_serviceManifest.ServiceDataOutputManifestList == null ||
                _serviceManifest.ServiceDataOutputManifestList.Count == 0)
            {
                throw new InvalidOperationException("ServiceDataOutputManifestList is empty.");
            }

            return _serviceManifest.ServiceDataOutputManifestList[0];
        }

        // -------------------------------------------------
        // Device / acquisition
        // -------------------------------------------------
        private void btnInitialize_Click(object sender, EventArgs e)
        {
            try
            {
                InitializeDeviceFromUi();
            }
            catch (Exception ex)
            {
                AppendLog("Ошибка инициализации: " + ex.Message);
                MessageBox.Show(this, ex.Message, "actiCHamp", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnStartAcquisition_Click(object sender, EventArgs e)
        {
            try
            {
                StartAcquisitionInternal();
            }
            catch (Exception ex)
            {
                AppendLog("Ошибка запуска: " + ex.Message);
                MessageBox.Show(this, ex.Message, "actiCHamp", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnStopAcquisition_Click(object sender, EventArgs e)
        {
            try
            {
                StopAcquisitionInternal(true);
            }
            catch (Exception ex)
            {
                AppendLog("Ошибка остановки: " + ex.Message);
                MessageBox.Show(this, ex.Message, "actiCHamp", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnImpedance_Click(object sender, EventArgs e)
        {
            try
            {
                if (_actiChampController.RunState == ActiChampRunState.Impedance)
                    StopImpedanceInternal(true);
                else
                    StartImpedanceInternal();
            }
            catch (Exception ex)
            {
                AppendLog("Ошибка режима импеданса: " + ex.Message);
                MessageBox.Show(this, ex.Message, "actiCHamp impedance", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void InitializeDeviceFromUi()
        {
            if (_isRunning)
                throw new InvalidOperationException("Сначала остановите съём.");

            ChampRate rate = GetSelectedChampRate();

            _actiChampController.BlockSizeSamples = (int)numUpDwnBlockSize.Value;
            _actiChampController.ActiveShieldGain = (int)numUpDwnActiveShieldGain.Value;
            _actiChampController.Initialize(rate);

            ChampProperties props = _actiChampController.Properties;
            ApplyDeviceProperties(props);
            PublishServiceState();
        }

        private void StartImpedanceInternal()
        {
            if (_isRunning)
                throw new InvalidOperationException("Сначала остановите съём данных.");

            if (_actiChampController.RunState == ActiChampRunState.Impedance)
                return;

            if (_actiChampController.IsRunning)
                throw new InvalidOperationException("Сначала остановите текущий режим actiCHamp.");

            ChampRate selectedRate = GetSelectedChampRate();

            _actiChampController.ActiveShieldGain = (int)numUpDwnActiveShieldGain.Value;
            if (!_actiChampController.IsInitialized || _actiChampController.DefaultRate != selectedRate)
                _actiChampController.Initialize(selectedRate);

            ChampProperties props = _actiChampController.Properties;
            ApplyDeviceProperties(props);

            GetImpedanceDisplayChannels(out _impedancePhysicalChannelIndices, out _impedanceChannelNames);
            if (_impedancePhysicalChannelIndices.Length == 0)
                throw new InvalidOperationException("Для контроля импеданса не выбран ни один EEG-канал.");

            DisposeProcessingRing();
            StopVisualTimer();
            ClearPendingBlocks();

            _isRunning = false;
            _impedanceValuesOhm = null;
            EnterImpedanceDisplayMode();

            _actiChampController.ImpedanceGoodOhm = ImpedanceGoodOhm;
            _actiChampController.ImpedanceBadOhm = ImpedanceBadOhm;

            RenderFrame();

            try
            {
                _actiChampController.StartImpedance();
            }
            catch
            {
                _impedanceValuesOhm = null;
                EnterWaveformDisplayMode();
                RenderFrame();
                throw;
            }

            AppendLog("Impedance display: 0.." + (ImpedanceRangeMaxOhm / 1000U).ToString() + " kΩ; green < 16.7 kΩ, yellow < 33.3 kΩ, red >= 33.3 kΩ.");
            UpdateActiChampUiState(true, true);
            PublishServiceState();
        }

        private void StopImpedanceInternal(bool publishState)
        {
            if (_actiChampController.RunState == ActiChampRunState.Impedance)
            {
                _actiChampController.StopCurrentMode();

                // StopCurrentMode restores the device to the configured Normal
                // sampling rate after Impedance. Refresh the UI immediately so
                // it cannot keep showing the impedance-mode 10 kHz properties.
                ApplyDeviceProperties(_actiChampController.Properties);
            }

            _impedanceValuesOhm = null;
            EnterWaveformDisplayMode();

            // Deliberately do not recreate or reset waveform FBOs here.
            // Impedance mode owns separate graphics resources and therefore has
            // no reason to touch waveform rendering state. StartAcquisitionInternal()
            // performs the normal waveform reset when acquisition is started again.

            UpdateActiChampUiState(_actiChampController.IsInitialized, false);

            if (publishState)
                PublishServiceState();

            RenderFrame();
        }

        private void GetImpedanceDisplayChannels(out int[] physicalIndices, out string[] channelNames)
        {
            CommitChannelGridEdit();

            List<int> indices = new List<int>();
            List<string> names = new List<string>();

            for (int i = 0; i < dgvChannels.Rows.Count; i++)
            {
                DataGridViewRow row = dgvChannels.Rows[i];
                int rawIndex = row.Tag is int ? (int)row.Tag : -1;
                if (rawIndex < 0 || rawIndex >= _physicalEegChannelsCount)
                    continue;

                bool enabled = Convert.ToBoolean(
                    row.Cells[ChannelColumnUseIndex].Value ?? false,
                    CultureInfo.InvariantCulture);

                bool isReference = Convert.ToBoolean(
                    row.Cells[ChannelColumnRefIndex].Value ?? false,
                    CultureInfo.InvariantCulture);

                // PyCorder shows EEG electrodes that are enabled OR used as reference.
                if (!enabled && !isReference)
                    continue;

                indices.Add(rawIndex);
                names.Add(GetChannelNameFromRow(row));
            }

            physicalIndices = indices.ToArray();
            channelNames = names.ToArray();
        }

        private void StartAcquisitionInternal()
        {
            if (_isRunning)
                return;

            ChampRate selectedRate = GetSelectedChampRate();
            int blockSize = (int)numUpDwnBlockSize.Value;
            int activeShieldGain = (int)numUpDwnActiveShieldGain.Value;
            double lowCutoffHz = ParseFilterFrequency(cmbLowCutoff.Text);
            double highCutoffHz = ParseFilterFrequency(cmbHighCutoff.Text);
            double eogLowCutoffHz = ParseFilterFrequency(cmbEogLowCutoff.Text);
            double eogHighCutoffHz = ParseFilterFrequency(cmbEogHighCutoff.Text);
            bool notch50Hz = chkNotch50Hz.Checked;

            _actiChampController.BlockSizeSamples = blockSize;
            _actiChampController.ActiveShieldGain = activeShieldGain;

            if (!_actiChampController.IsInitialized || _actiChampController.DefaultRate != selectedRate)
                _actiChampController.Initialize(selectedRate);

            ChampProperties props = _actiChampController.Properties;
            ApplyDeviceProperties(props);

            int selectedEegCount;
            int selectedEogCount;
            int selectedAuxCount;
            string[] selectedNames;
            int[] referencePhysicalIndices;
            int[] eogPhysicalIndices;
            int[] selectedPhysicalIndices = GetSelectedPhysicalChannelIndices(
                props,
                out selectedEegCount,
                out selectedEogCount,
                out selectedAuxCount,
                out selectedNames,
                out referencePhysicalIndices,
                out eogPhysicalIndices);

            int channels = selectedNames.Length;
            int samplingRate = checked((int)Math.Round(props.Rate));

            if (channels <= 0)
                throw new InvalidOperationException("Не выбран ни один канал.");
            if (samplingRate <= 0)
                throw new InvalidOperationException("Некорректная частота дискретизации actiCHamp.");

            ActiChampSignalFilter.ValidateConfiguration(
                samplingRate,
                lowCutoffHz,
                highCutoffHz,
                notch50Hz);

            if (selectedEogCount > 0)
            {
                ActiChampSignalFilter.ValidateConfiguration(
                    samplingRate,
                    eogLowCutoffHz,
                    eogHighCutoffHz,
                    notch50Hz);
            }

            _actiChampController.SelectedPhysicalChannelIndices = selectedPhysicalIndices;
            _actiChampController.ReferencePhysicalChannelIndices = referencePhysicalIndices;
            _actiChampController.EogPhysicalChannelIndices = eogPhysicalIndices;
            _actiChampController.LowCutoffHz = lowCutoffHz;
            _actiChampController.HighCutoffHz = highCutoffHz;
            _actiChampController.EogLowCutoffHz = eogLowCutoffHz;
            _actiChampController.EogHighCutoffHz = eogHighCutoffHz;
            _actiChampController.Notch50Hz = notch50Hz;

            AppendLog(referencePhysicalIndices.Length > 0
                ? "Software REF: " + referencePhysicalIndices.Length.ToString() + " EEG channel(s)"
                : "Software REF: none");

            AppendLog("EEG filters: " + ActiChampSignalFilter.Describe(
                lowCutoffHz,
                highCutoffHz,
                notch50Hz));

            if (selectedEogCount > 0)
            {
                AppendLog("EOG filters: " + ActiChampSignalFilter.Describe(
                    eogLowCutoffHz,
                    eogHighCutoffHz,
                    notch50Hz));
            }

            DisposeProcessingRing();

            ServiceDataPortManifest outputManifest = GetPrimaryServiceDataOutputManifest();

            ActiChampProcessingBlockRing newRing = new ActiChampProcessingBlockRing(
                    outputManifest.ServiceDataPortMapName,
                    outputManifest.ServiceDataPortReadyEventName,
                    channels,
                    samplingRate,
                    blockSize,
                    ProcessingRingBlockCount,
                    selectedNames);

            newRing.UpdateRunning(true);

            lock (_ringSync)
            {
                _processingBlockRing = newRing;
            }

            _selectedPhysicalChannelIndices = selectedPhysicalIndices;
            _referencePhysicalChannelIndices = referencePhysicalIndices;
            _selectedChannelNames = selectedNames;
            _eegChannelsCount = selectedEegCount;
            _eogChannelsCount = selectedEogCount;
            _auxChannelsCount = selectedAuxCount;
            _bufferChannelsCount = channels;
            _samplingRate = samplingRate;
            _receivedSamplesCount = 0;
            _visualClockStarted = false;
            _lastShownDroppedBlocks = 0;

            ResetDisplayState(channels, samplingRate);
            ClearPendingBlocks();

            _pageShiftPx = 0.0f;
            _visualPageSeq = 0;

            if (_enableLocalDisplayCache && _glInitialized)
            {
                glCtrl.MakeCurrent();
                RecreateWavePages();
            }

            InvalidateOverlay();
            UpdateDroppedBlocksStatusSafe(0);
            UpdateChannelStatusText();

            _isRunning = true;
            StartVisualTimer();

            try
            {
                _actiChampController.StartNormal();
            }
            catch
            {
                _isRunning = false;
                StopVisualTimer();
                DisposeProcessingRing();
                throw;
            }

            UpdateActiChampUiState(true, true);
            PublishServiceState();
            RenderFrame();
        }

        private void StopAcquisitionInternal(bool publishState)
        {
            _isRunning = false;
            StopVisualTimer();

            if (_actiChampController.IsRunning)
                _actiChampController.StopCurrentMode();

            _impedanceValuesOhm = null;
            EnterWaveformDisplayMode();

            DisposeProcessingRing();
            ClearPendingBlocks();
            _visualClockStarted = false;

            UpdateActiChampUiState(_actiChampController.IsInitialized, false);

            if (publishState)
                PublishServiceState();

            if (_glInitialized && glCtrl.IsHandleCreated && !glCtrl.IsDisposed)
                glCtrl.Invalidate();
        }

        private void DisposeProcessingRing()
        {
            lock (_ringSync)
            {
                if (_processingBlockRing == null)
                    return;

                try
                {
                    _processingBlockRing.UpdateRunning(false);
                }
                catch
                {
                }

                try
                {
                    _processingBlockRing.Dispose();
                }
                catch
                {
                }

                _processingBlockRing = null;
            }
        }

        private void ActiChampController_DataBlockReceived(object sender, ActiChampDataBlockEventArgs e)
        {
            ActiChampDataBlock block = e.DataBlock;
            if (!_isRunning || block == null || block.SampleCount <= 0 || block.SignalsVoltsChannelMajor == null)
                return;

            long qpcNow;
            QueryPerformanceCounter(out qpcNow);

            int droppedBlocks = 0;

            lock (_ringSync)
            {
                if (_processingBlockRing != null)
                {
                    _processingBlockRing.PublishBlock(
                        block.SignalsVoltsChannelMajor,
                        block.SampleCount,
                        qpcNow);

                    droppedBlocks = _processingBlockRing.ReadHeader().DroppedBlockCount;
                }
            }

            long firstAbsoluteSample = Interlocked.Add(ref _receivedSamplesCount, block.SampleCount) - block.SampleCount;

            if (_enableLocalDisplayCache)
            {
                lock (_displaySync)
                {
                    // Локальная картинка не имеет права тормозить аппаратный поток.
                    // Если UI сильно отстал, выбрасываем только накопившийся display backlog;
                    // публикация в MMF уже выполнена выше и не затрагивается.
                    if (_pendingBlocks.Count >= MaxPendingDisplayBlocks)
                        _pendingBlocks.Clear();

                    _pendingBlocks.Add(new PendingBlock
                    {
                        Samples = block.SignalsVoltsChannelMajor,
                        SamplesCount = block.SampleCount,
                        FirstAbsoluteSample = firstAbsoluteSample,
                        ChannelsCount = block.ChannelCount
                    });
                }
            }

            _visualClockStarted = true;

            if (droppedBlocks != _lastShownDroppedBlocks)
            {
                _lastShownDroppedBlocks = droppedBlocks;
                UpdateDroppedBlocksStatusSafe(droppedBlocks);
            }
        }

        private void ActiChampController_ImpedanceReceived(object sender, ActiChampImpedanceEventArgs e)
        {
            if (e == null || e.ValuesOhm == null)
                return;

            uint[] values = (uint[])e.ValuesOhm.Clone();

            if (IsDisposed || !IsHandleCreated)
                return;

            BeginInvoke((Action)(() =>
            {
                if (!IsImpedanceDisplayMode || _actiChampController.RunState != ActiChampRunState.Impedance)
                    return;

                _impedanceValuesOhm = values;
                InvalidateImpedanceDisplay();
                RenderFrame();
            }));
        }

        private void ActiChampController_LogMessage(object sender, ActiChampLogEventArgs e)
        {
            AppendLog(e.Message, e.Timestamp);
        }

        private void ActiChampController_StateChanged(object sender, ActiChampStateChangedEventArgs e)
        {
            UpdateActiChampUiState(e.IsInitialized, e.IsRunning);
            PublishServiceState();
        }

        private void ApplyDeviceProperties(ChampProperties props)
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            if (InvokeRequired)
            {
                BeginInvoke(new Action<ChampProperties>(ApplyDeviceProperties), props);
                return;
            }

            _physicalEegChannelsCount = (int)props.CountEeg;
            _physicalAuxChannelsCount = (int)props.CountAux;
            _samplingRate = (int)Math.Round(props.Rate);

            EnsureChannelGrid(_physicalEegChannelsCount, _physicalAuxChannelsCount);
            RefreshSelectedChannelStateFromGrid();

            tlStrpStatusSamplingRate.Text = _samplingRate.ToString() + " Hz";
            UpdateChannelStatusText();

            InvalidateOverlay();
            UpdateActiChampUiState(_actiChampController.IsInitialized, _actiChampController.IsRunning);
        }

        private void UpdateActiChampUiState(bool isInitialized, bool isRunning)
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            if (InvokeRequired)
            {
                BeginInvoke(new Action<bool, bool>(UpdateActiChampUiState), isInitialized, isRunning);
                return;
            }

            ActiChampRunState runState = _actiChampController.RunState;
            bool normalRunning = runState == ActiChampRunState.Normal && _isRunning;
            bool impedanceRunning = runState == ActiChampRunState.Impedance;
            bool anyModeRunning = runState != ActiChampRunState.Idle;

            btnInitialize.Enabled = !anyModeRunning;
            btnStartAcquisition.Enabled = !anyModeRunning;
            btnStopAcquisition.Enabled = normalRunning;
            btnImpedance.Enabled = isInitialized && (!anyModeRunning || impedanceRunning);
            btnImpedance.Text = impedanceRunning ? "Stop Imp." : "Impedance";

            cmbSamplingRate.Enabled = !anyModeRunning;
            numUpDwnBlockSize.Enabled = !anyModeRunning;
            numUpDwnActiveShieldGain.Enabled = !anyModeRunning;
            cmbLowCutoff.Enabled = !anyModeRunning;
            cmbHighCutoff.Enabled = !anyModeRunning;
            cmbEogLowCutoff.Enabled = !anyModeRunning;
            cmbEogHighCutoff.Enabled = !anyModeRunning;
            chkNotch50Hz.Enabled = !anyModeRunning;
            dgvChannels.Enabled = !anyModeRunning && isInitialized;
            btnChannelsAll.Enabled = !anyModeRunning && isInitialized && dgvChannels.Rows.Count > 0;
            btnChannelsNone.Enabled = !anyModeRunning && isInitialized && dgvChannels.Rows.Count > 0;

            numUpDwnTimeBaseInSec.Enabled = !impedanceRunning;
            cmbBxScaleAmpl.Enabled = !impedanceRunning;
            btnBaselineNow.Enabled = normalRunning;

            if (impedanceRunning)
                lblDeviceState.Text = "Impedance";
            else if (normalRunning)
                lblDeviceState.Text = "Running";
            else
                lblDeviceState.Text = isInitialized ? "Initialized" : "Not initialized";
        }

        // -------------------------------------------------
        // Channel selection / software reference / EOG
        // -------------------------------------------------
        private void EnsureChannelGrid(int eegCount, int auxCount)
        {
            if (eegCount < 0) eegCount = 0;
            if (auxCount < 0) auxCount = 0;

            if (dgvChannels.Rows.Count == eegCount + auxCount &&
                _channelGridEegCount == eegCount &&
                _channelGridAuxCount == auxCount)
            {
                return;
            }

            Dictionary<string, bool> savedEnabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, bool> savedReference = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> savedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> savedTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (_actiCHampSettings != null && _actiCHampSettings.Channels != null)
            {
                for (int i = 0; i < _actiCHampSettings.Channels.Count; i++)
                {
                    ActiChampChannelSettings c = _actiCHampSettings.Channels[i];
                    if (c == null || string.IsNullOrEmpty(c.Type))
                        continue;

                    string key = BuildChannelKey(c.Type, c.PhysicalIndex);
                    savedEnabled[key] = c.Enabled;
                    savedReference[key] = c.IsReference;
                    savedTypes[key] = c.Type;

                    if (!string.IsNullOrWhiteSpace(c.Name))
                        savedNames[key] = c.Name.Trim();
                }
            }

            if (dgvChannels.Rows.Count > 0)
            {
                List<ActiChampChannelSettings> current = CaptureChannelSettingsFromGrid();
                for (int i = 0; i < current.Count; i++)
                {
                    string key = BuildChannelKey(current[i].Type, current[i].PhysicalIndex);
                    savedEnabled[key] = current[i].Enabled;
                    savedReference[key] = current[i].IsReference;
                    savedTypes[key] = current[i].Type;

                    if (!string.IsNullOrWhiteSpace(current[i].Name))
                        savedNames[key] = current[i].Name.Trim();
                }
            }

            _updatingChannelGrid = true;
            try
            {
                dgvChannels.Rows.Clear();

                for (int i = 0; i < eegCount; i++)
                {
                    AddChannelGridRow(
                        ChannelTypeEeg,
                        i,
                        "EEG" + (i + 1).ToString(),
                        i,
                        savedEnabled,
                        savedReference,
                        savedNames,
                        savedTypes);
                }

                for (int i = 0; i < auxCount; i++)
                {
                    AddChannelGridRow(
                        ChannelTypeAux,
                        i,
                        "AUX" + (i + 1).ToString(),
                        eegCount + i,
                        savedEnabled,
                        savedReference,
                        savedNames,
                        savedTypes);
                }

                _channelGridEegCount = eegCount;
                _channelGridAuxCount = auxCount;
                dgvChannels.ClearSelection();
            }
            finally
            {
                _updatingChannelGrid = false;
            }

            RefreshSelectedChannelStateFromGrid();
        }

        private void AddChannelGridRow(
            string physicalType,
            int typePhysicalIndex,
            string physicalName,
            int rawPhysicalIndex,
            Dictionary<string, bool> savedEnabled,
            Dictionary<string, bool> savedReference,
            Dictionary<string, string> savedNames,
            Dictionary<string, string> savedTypes)
        {
            string key = BuildChannelKey(physicalType, typePhysicalIndex);

            string type = physicalType;
            string savedType;
            if (string.Equals(physicalType, ChannelTypeEeg, StringComparison.OrdinalIgnoreCase) &&
                savedTypes.TryGetValue(key, out savedType) &&
                IsValidEegChannelType(savedType))
            {
                type = savedType;

                if (IsEogChannelType(type) && IsEogRoleAssigned(type, -1))
                    type = ChannelTypeEeg;
            }

            bool enabled;
            if (!savedEnabled.TryGetValue(key, out enabled))
                enabled = true;

            if (IsEogChannelType(type))
                enabled = true;

            bool isReference = false;
            if (string.Equals(type, ChannelTypeEeg, StringComparison.OrdinalIgnoreCase))
                savedReference.TryGetValue(key, out isReference);

            string customName;
            if (!savedNames.TryGetValue(key, out customName) || string.IsNullOrWhiteSpace(customName))
                customName = physicalName;

            int rowIndex = dgvChannels.Rows.Add(enabled, isReference, physicalName, customName, type);
            DataGridViewRow row = dgvChannels.Rows[rowIndex];
            row.Tag = rawPhysicalIndex;
            row.Cells[ChannelColumnPhysicalIndex].Tag = typePhysicalIndex;

            DataGridViewComboBoxCell typeCell =
                row.Cells[ChannelColumnTypeIndex] as DataGridViewComboBoxCell;

            if (typeCell != null)
            {
                typeCell.Value = null;
                typeCell.Items.Clear();

                if (string.Equals(physicalType, ChannelTypeAux, StringComparison.OrdinalIgnoreCase))
                {
                    typeCell.Items.Add(ChannelTypeAux);
                }
                else
                {
                    typeCell.Items.Add(ChannelTypeEeg);
                    typeCell.Items.Add(ChannelTypeHeogLeft);
                    typeCell.Items.Add(ChannelTypeHeogRight);
                    typeCell.Items.Add(ChannelTypeVeogUp);
                    typeCell.Items.Add(ChannelTypeVeogDown);
                }

                typeCell.Value = type;
            }

            ApplyChannelRoleCellState(row);
        }

        private static string BuildChannelKey(string type, int physicalIndex)
        {
            string physicalType = string.Equals(type, ChannelTypeAux, StringComparison.OrdinalIgnoreCase)
                ? ChannelTypeAux
                : ChannelTypeEeg;

            return physicalType + ":" + physicalIndex.ToString(CultureInfo.InvariantCulture);
        }

        private static bool IsValidEegChannelType(string type)
        {
            return string.Equals(type, ChannelTypeEeg, StringComparison.OrdinalIgnoreCase) ||
                IsEogChannelType(type);
        }

        private static bool IsEogChannelType(string type)
        {
            return string.Equals(type, ChannelTypeHeogLeft, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, ChannelTypeHeogRight, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, ChannelTypeVeogUp, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, ChannelTypeVeogDown, StringComparison.OrdinalIgnoreCase);
        }

        private static int GetEogRoleIndex(string type)
        {
            if (string.Equals(type, ChannelTypeHeogLeft, StringComparison.OrdinalIgnoreCase))
                return ActiChampController.EogHeogLeftIndex;
            if (string.Equals(type, ChannelTypeHeogRight, StringComparison.OrdinalIgnoreCase))
                return ActiChampController.EogHeogRightIndex;
            if (string.Equals(type, ChannelTypeVeogUp, StringComparison.OrdinalIgnoreCase))
                return ActiChampController.EogVeogUpIndex;
            if (string.Equals(type, ChannelTypeVeogDown, StringComparison.OrdinalIgnoreCase))
                return ActiChampController.EogVeogDownIndex;

            return -1;
        }

        private bool IsEogRoleAssigned(string role, int exceptRowIndex)
        {
            if (!IsEogChannelType(role))
                return false;

            for (int i = 0; i < dgvChannels.Rows.Count; i++)
            {
                if (i == exceptRowIndex)
                    continue;

                DataGridViewRow row = dgvChannels.Rows[i];

                string type = Convert.ToString(
                    row.Cells[ChannelColumnTypeIndex].Value,
                    CultureInfo.InvariantCulture);

                if (string.Equals(type, role, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private void ConfigureChannelTypeCellItems(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= dgvChannels.Rows.Count)
                return;

            DataGridViewRow row = dgvChannels.Rows[rowIndex];
            DataGridViewComboBoxCell typeCell =
                row.Cells[ChannelColumnTypeIndex] as DataGridViewComboBoxCell;

            if (typeCell == null || typeCell.ReadOnly)
                return;

            string currentType = Convert.ToString(
                typeCell.Value,
                CultureInfo.InvariantCulture);

            typeCell.Items.Clear();
            typeCell.Items.Add(ChannelTypeEeg);

            AddAvailableEogRole(typeCell, ChannelTypeHeogLeft, currentType, rowIndex);
            AddAvailableEogRole(typeCell, ChannelTypeHeogRight, currentType, rowIndex);
            AddAvailableEogRole(typeCell, ChannelTypeVeogUp, currentType, rowIndex);
            AddAvailableEogRole(typeCell, ChannelTypeVeogDown, currentType, rowIndex);
        }

        private void AddAvailableEogRole(
            DataGridViewComboBoxCell typeCell,
            string role,
            string currentType,
            int rowIndex)
        {
            if (string.Equals(currentType, role, StringComparison.OrdinalIgnoreCase) ||
                !IsEogRoleAssigned(role, rowIndex))
            {
                typeCell.Items.Add(role);
            }
        }

        private void ApplyChannelRoleCellState(DataGridViewRow row)
        {
            if (row == null)
                return;

            string type = Convert.ToString(
                row.Cells[ChannelColumnTypeIndex].Value,
                CultureInfo.InvariantCulture);

            bool isAux = string.Equals(type, ChannelTypeAux, StringComparison.OrdinalIgnoreCase);
            bool isEog = IsEogChannelType(type);

            DataGridViewCell useCell = row.Cells[ChannelColumnUseIndex];
            DataGridViewCell refCell = row.Cells[ChannelColumnRefIndex];
            DataGridViewCell typeCell = row.Cells[ChannelColumnTypeIndex];

            typeCell.ReadOnly = isAux;

            if (isEog)
            {
                useCell.Value = true;
                useCell.ReadOnly = true;
                useCell.Style.BackColor = SystemColors.Control;
                useCell.ToolTipText = "EOG source channels are always used.";

                refCell.Value = false;
                refCell.ReadOnly = true;
                refCell.Style.BackColor = SystemColors.Control;
                refCell.Style.ForeColor = SystemColors.GrayText;
                refCell.ToolTipText = "Software EEG REF is not applied to EOG source channels.";
            }
            else if (isAux)
            {
                useCell.ReadOnly = false;
                useCell.Style.BackColor = SystemColors.Window;
                useCell.ToolTipText = string.Empty;

                refCell.Value = false;
                refCell.ReadOnly = true;
                refCell.Style.BackColor = SystemColors.Control;
                refCell.Style.ForeColor = SystemColors.GrayText;
                refCell.ToolTipText = "REF is available only for EEG channels.";
            }
            else
            {
                useCell.ReadOnly = false;
                useCell.Style.BackColor = SystemColors.Window;
                useCell.ToolTipText = string.Empty;

                refCell.ReadOnly = false;
                refCell.Style.BackColor = SystemColors.Window;
                refCell.Style.ForeColor = SystemColors.ControlText;
                refCell.ToolTipText = string.Empty;
            }
        }

        private List<ActiChampChannelSettings> CaptureChannelSettingsFromGrid()
        {
            CommitChannelGridEdit();

            List<ActiChampChannelSettings> result = new List<ActiChampChannelSettings>(dgvChannels.Rows.Count);

            for (int i = 0; i < dgvChannels.Rows.Count; i++)
            {
                DataGridViewRow row = dgvChannels.Rows[i];
                string name = Convert.ToString(row.Cells[ChannelColumnNameIndex].Value, CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(name))
                    name = Convert.ToString(row.Cells[ChannelColumnPhysicalIndex].Value, CultureInfo.InvariantCulture);
                name = (name ?? string.Empty).Trim();

                string type = Convert.ToString(row.Cells[ChannelColumnTypeIndex].Value, CultureInfo.InvariantCulture);
                int typePhysicalIndex = row.Cells[ChannelColumnPhysicalIndex].Tag is int
                    ? (int)row.Cells[ChannelColumnPhysicalIndex].Tag
                    : i;

                bool enabled = Convert.ToBoolean(
                    row.Cells[ChannelColumnUseIndex].Value ?? false,
                    CultureInfo.InvariantCulture);

                if (IsEogChannelType(type))
                    enabled = true;

                bool isReference = string.Equals(type, ChannelTypeEeg, StringComparison.OrdinalIgnoreCase) &&
                    Convert.ToBoolean(
                        row.Cells[ChannelColumnRefIndex].Value ?? false,
                        CultureInfo.InvariantCulture);

                result.Add(new ActiChampChannelSettings
                {
                    PhysicalIndex = typePhysicalIndex,
                    Name = name,
                    Type = type,
                    Enabled = enabled,
                    IsReference = isReference
                });
            }

            return result;
        }

        private int[] GetSelectedPhysicalChannelIndices(
            ChampProperties props,
            out int selectedEegCount,
            out int selectedEogCount,
            out int selectedAuxCount,
            out string[] selectedNames,
            out int[] referencePhysicalIndices,
            out int[] eogPhysicalIndices)
        {
            CommitChannelGridEdit();

            int eegCount = checked((int)props.CountEeg);
            int auxCount = checked((int)props.CountAux);
            EnsureChannelGrid(eegCount, auxCount);

            List<int> acquisitionIndices = new List<int>();
            List<int> referenceIndices = new List<int>();
            List<string> eegNames = new List<string>();
            List<string> auxNames = new List<string>();
            int[] eogIndices = new int[] { -1, -1, -1, -1 };

            selectedEegCount = 0;
            selectedEogCount = 0;
            selectedAuxCount = 0;

            for (int i = 0; i < dgvChannels.Rows.Count; i++)
            {
                DataGridViewRow row = dgvChannels.Rows[i];
                int rawIndex = row.Tag is int ? (int)row.Tag : -1;
                if (rawIndex < 0 || rawIndex >= eegCount + auxCount)
                    continue;

                string type = Convert.ToString(
                    row.Cells[ChannelColumnTypeIndex].Value,
                    CultureInfo.InvariantCulture);

                int eogRoleIndex = GetEogRoleIndex(type);
                if (eogRoleIndex >= 0)
                {
                    if (rawIndex >= eegCount)
                        throw new InvalidOperationException("EOG role can be assigned only to a physical EEG channel.");

                    eogIndices[eogRoleIndex] = rawIndex;
                    acquisitionIndices.Add(rawIndex);
                    continue;
                }

                bool enabled = Convert.ToBoolean(
                    row.Cells[ChannelColumnUseIndex].Value ?? false,
                    CultureInfo.InvariantCulture);

                bool isReference = rawIndex < eegCount &&
                    string.Equals(type, ChannelTypeEeg, StringComparison.OrdinalIgnoreCase) &&
                    Convert.ToBoolean(
                        row.Cells[ChannelColumnRefIndex].Value ?? false,
                        CultureInfo.InvariantCulture);

                if (isReference)
                    referenceIndices.Add(rawIndex);

                if (!enabled)
                    continue;

                acquisitionIndices.Add(rawIndex);

                if (rawIndex < eegCount)
                {
                    selectedEegCount++;
                    eegNames.Add(GetChannelNameFromRow(row));
                }
                else
                {
                    selectedAuxCount++;
                    auxNames.Add(GetChannelNameFromRow(row));
                }
            }

            ValidateEogPairs(eogIndices);

            List<string> names = new List<string>(selectedEegCount + 2 + selectedAuxCount);
            names.AddRange(eegNames);

            if (eogIndices[ActiChampController.EogHeogLeftIndex] >= 0)
            {
                names.Add("HEOG");
                selectedEogCount++;
            }

            if (eogIndices[ActiChampController.EogVeogUpIndex] >= 0)
            {
                names.Add("VEOG");
                selectedEogCount++;
            }

            names.AddRange(auxNames);

            selectedNames = names.ToArray();
            referencePhysicalIndices = referenceIndices.ToArray();
            eogPhysicalIndices = eogIndices;
            return acquisitionIndices.ToArray();
        }

        private static void ValidateEogPairs(int[] eogIndices)
        {
            bool hasHeogLeft = eogIndices[ActiChampController.EogHeogLeftIndex] >= 0;
            bool hasHeogRight = eogIndices[ActiChampController.EogHeogRightIndex] >= 0;
            bool hasVeogUp = eogIndices[ActiChampController.EogVeogUpIndex] >= 0;
            bool hasVeogDown = eogIndices[ActiChampController.EogVeogDownIndex] >= 0;

            if (hasHeogLeft != hasHeogRight)
                throw new InvalidOperationException("HEOG requires both HEOG-L and HEOG-R channels.");

            if (hasVeogUp != hasVeogDown)
                throw new InvalidOperationException("VEOG requires both VEOG-U and VEOG-D channels.");
        }

        private void RefreshSelectedChannelStateFromGrid()
        {
            if (dgvChannels == null)
                return;

            CommitChannelGridEdit();

            List<int> acquisitionIndices = new List<int>();
            List<int> referenceIndices = new List<int>();
            List<string> eegNames = new List<string>();
            List<string> auxNames = new List<string>();
            int[] eogIndices = new int[] { -1, -1, -1, -1 };
            int selectedEegCount = 0;
            int selectedAuxCount = 0;

            for (int i = 0; i < dgvChannels.Rows.Count; i++)
            {
                DataGridViewRow row = dgvChannels.Rows[i];
                int rawIndex = row.Tag is int ? (int)row.Tag : -1;
                if (rawIndex < 0)
                    continue;

                string type = Convert.ToString(
                    row.Cells[ChannelColumnTypeIndex].Value,
                    CultureInfo.InvariantCulture);

                int eogRoleIndex = GetEogRoleIndex(type);
                if (eogRoleIndex >= 0)
                {
                    eogIndices[eogRoleIndex] = rawIndex;
                    acquisitionIndices.Add(rawIndex);
                    continue;
                }

                bool enabled = Convert.ToBoolean(
                    row.Cells[ChannelColumnUseIndex].Value ?? false,
                    CultureInfo.InvariantCulture);

                bool isReference = rawIndex < _physicalEegChannelsCount &&
                    string.Equals(type, ChannelTypeEeg, StringComparison.OrdinalIgnoreCase) &&
                    Convert.ToBoolean(
                        row.Cells[ChannelColumnRefIndex].Value ?? false,
                        CultureInfo.InvariantCulture);

                if (isReference)
                    referenceIndices.Add(rawIndex);

                if (!enabled)
                    continue;

                acquisitionIndices.Add(rawIndex);

                if (rawIndex < _physicalEegChannelsCount)
                {
                    selectedEegCount++;
                    eegNames.Add(GetChannelNameFromRow(row));
                }
                else
                {
                    selectedAuxCount++;
                    auxNames.Add(GetChannelNameFromRow(row));
                }
            }

            List<string> names = new List<string>(selectedEegCount + 2 + selectedAuxCount);
            names.AddRange(eegNames);

            int selectedEogCount = 0;
            if (eogIndices[ActiChampController.EogHeogLeftIndex] >= 0 &&
                eogIndices[ActiChampController.EogHeogRightIndex] >= 0)
            {
                names.Add("HEOG");
                selectedEogCount++;
            }

            if (eogIndices[ActiChampController.EogVeogUpIndex] >= 0 &&
                eogIndices[ActiChampController.EogVeogDownIndex] >= 0)
            {
                names.Add("VEOG");
                selectedEogCount++;
            }

            names.AddRange(auxNames);

            _selectedPhysicalChannelIndices = acquisitionIndices.ToArray();
            _referencePhysicalChannelIndices = referenceIndices.ToArray();
            _selectedChannelNames = names.ToArray();
            _eegChannelsCount = selectedEegCount;
            _eogChannelsCount = selectedEogCount;
            _auxChannelsCount = selectedAuxCount;
            _bufferChannelsCount = names.Count > 0 ? names.Count : 1;

            if (lblChannelsSelected != null)
            {
                lblChannelsSelected.Text =
                    names.Count.ToString() + " output / " +
                    referenceIndices.Count.ToString() + " REF";
            }

            UpdateChannelStatusText();
            InvalidateOverlay();
        }

        private void UpdateChannelStatusText()
        {
            if (toolStripStatusLabelChannels == null)
                return;

            int outputCount = _selectedChannelNames != null
                ? _selectedChannelNames.Length
                : 0;

            int referenceCount = _referencePhysicalChannelIndices != null
                ? _referencePhysicalChannelIndices.Length
                : 0;

            toolStripStatusLabelChannels.Text =
                outputCount.ToString() + " output; REF " + referenceCount.ToString() +
                "; EOG " + _eogChannelsCount.ToString() + " / " +
                _physicalEegChannelsCount.ToString() + " EEG + " +
                _physicalAuxChannelsCount.ToString() + " AUX";
        }

        private void CommitChannelGridEdit()
        {
            if (dgvChannels == null)
                return;

            if (dgvChannels.IsCurrentCellDirty)
                dgvChannels.CommitEdit(DataGridViewDataErrorContexts.Commit);

            dgvChannels.EndEdit();
        }

        private void btnChannelsAll_Click(object sender, EventArgs e)
        {
            SetAllChannelRowsEnabled(true);
        }

        private void btnChannelsNone_Click(object sender, EventArgs e)
        {
            SetAllChannelRowsEnabled(false);
        }

        private void SetAllChannelRowsEnabled(bool enabled)
        {
            _updatingChannelGrid = true;
            try
            {
                for (int i = 0; i < dgvChannels.Rows.Count; i++)
                {
                    DataGridViewRow row = dgvChannels.Rows[i];
                    string type = Convert.ToString(
                        row.Cells[ChannelColumnTypeIndex].Value,
                        CultureInfo.InvariantCulture);

                    row.Cells[ChannelColumnUseIndex].Value =
                        IsEogChannelType(type) ? true : enabled;
                }
            }
            finally
            {
                _updatingChannelGrid = false;
            }

            RefreshSelectedChannelStateFromGrid();
        }

        private void dgvChannels_CurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            if (!dgvChannels.IsCurrentCellDirty)
                return;

            if (dgvChannels.CurrentCell is DataGridViewCheckBoxCell ||
                dgvChannels.CurrentCell is DataGridViewComboBoxCell)
            {
                dgvChannels.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        }

        private void dgvChannels_CellBeginEdit(object sender, DataGridViewCellCancelEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != ChannelColumnTypeIndex)
                return;

            ConfigureChannelTypeCellItems(e.RowIndex);
        }

        private void dgvChannels_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_updatingChannelGrid || e.RowIndex < 0)
                return;

            if (e.ColumnIndex == ChannelColumnTypeIndex)
            {
                DataGridViewRow row = dgvChannels.Rows[e.RowIndex];

                _updatingChannelGrid = true;
                try
                {
                    ApplyChannelRoleCellState(row);
                }
                finally
                {
                    _updatingChannelGrid = false;
                }

                RefreshSelectedChannelStateFromGrid();
                return;
            }

            if (e.ColumnIndex == ChannelColumnUseIndex ||
                e.ColumnIndex == ChannelColumnRefIndex ||
                e.ColumnIndex == ChannelColumnNameIndex)
            {
                NormalizeChannelNameCell(e.RowIndex);
                RefreshSelectedChannelStateFromGrid();
            }
        }

        private string GetChannelNameFromRow(DataGridViewRow row)
        {
            if (row == null)
                return string.Empty;

            string name = Convert.ToString(
                row.Cells[ChannelColumnNameIndex].Value,
                CultureInfo.InvariantCulture);

            if (string.IsNullOrWhiteSpace(name))
            {
                name = Convert.ToString(
                    row.Cells[ChannelColumnPhysicalIndex].Value,
                    CultureInfo.InvariantCulture);
            }

            return (name ?? string.Empty).Trim();
        }

        private void NormalizeChannelNameCell(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= dgvChannels.Rows.Count)
                return;

            DataGridViewRow row = dgvChannels.Rows[rowIndex];
            string normalized = GetChannelNameFromRow(row);
            string current = Convert.ToString(
                row.Cells[ChannelColumnNameIndex].Value,
                CultureInfo.InvariantCulture);

            if (!string.Equals(current, normalized, StringComparison.Ordinal))
            {
                _updatingChannelGrid = true;
                try
                {
                    row.Cells[ChannelColumnNameIndex].Value = normalized;
                }
                finally
                {
                    _updatingChannelGrid = false;
                }
            }
        }

        // -------------------------------------------------
        // Sampling rate helpers
        // -------------------------------------------------
        private static double ParseFilterFrequency(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0.0;

            string valueText = text.Trim();
            if (string.Equals(valueText, "Off", StringComparison.OrdinalIgnoreCase))
                return 0.0;

            valueText = valueText.Replace("Hz", string.Empty).Trim().Replace(',', '.');

            double value;
            if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                throw new InvalidOperationException("Некорректная частота фильтра: " + text);

            return value;
        }

        private static void SelectFilterFrequency(ComboBox comboBox, double frequencyHz)
        {
            if (comboBox == null || comboBox.Items.Count == 0)
                return;

            int bestIndex = 0;
            double bestError = double.MaxValue;

            for (int i = 0; i < comboBox.Items.Count; i++)
            {
                string itemText = Convert.ToString(comboBox.Items[i], CultureInfo.InvariantCulture);
                double itemValue;

                try
                {
                    itemValue = ParseFilterFrequency(itemText);
                }
                catch
                {
                    continue;
                }

                double error = Math.Abs(itemValue - frequencyHz);
                if (error < bestError)
                {
                    bestError = error;
                    bestIndex = i;
                }
            }

            comboBox.SelectedIndex = bestIndex;
        }

        private void PopulateSamplingRates()
        {
            cmbSamplingRate.Items.Clear();
            cmbSamplingRate.Items.Add("200 Hz");
            cmbSamplingRate.Items.Add("500 Hz");
            cmbSamplingRate.Items.Add("1000 Hz");
            cmbSamplingRate.Items.Add("2000 Hz");
            cmbSamplingRate.Items.Add("5000 Hz");
            cmbSamplingRate.Items.Add("10000 Hz");
            cmbSamplingRate.Items.Add("25000 Hz");
            cmbSamplingRate.Items.Add("50000 Hz");
            cmbSamplingRate.Items.Add("100000 Hz");
        }

        private void SelectSamplingRate(int samplingRateHz)
        {
            string text = samplingRateHz.ToString(CultureInfo.InvariantCulture) + " Hz";
            int index = cmbSamplingRate.Items.IndexOf(text);
            cmbSamplingRate.SelectedIndex = index >= 0 ? index : cmbSamplingRate.Items.IndexOf("500 Hz");
        }

        private int GetSelectedSamplingRateHz()
        {
            return (int)ActiChampDevice.RateHzMap[GetSelectedChampRate()];
        }

        private ChampRate GetSelectedChampRate()
        {
            string text = cmbSamplingRate.SelectedItem != null
                ? cmbSamplingRate.SelectedItem.ToString()
                : "500 Hz";

            int space = text.IndexOf(' ');
            string numberText = space > 0 ? text.Substring(0, space) : text;
            int hz;
            if (!int.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out hz))
                hz = 500;

            switch (hz)
            {
                case 200: return ChampRate.Rate200Hz;
                case 500: return ChampRate.Rate500Hz;
                case 1000: return ChampRate.Rate1kHz;
                case 2000: return ChampRate.Rate2kHz;
                case 5000: return ChampRate.Rate5kHz;
                case 10000: return ChampRate.Rate10kHz;
                case 25000: return ChampRate.Rate25kHz;
                case 50000: return ChampRate.Rate50kHz;
                case 100000: return ChampRate.Rate100kHz;
                default: return ChampRate.Rate500Hz;
            }
        }

        // -------------------------------------------------
        // Visual timer
        // -------------------------------------------------
        private void StartVisualTimer()
        {
            if (!_enableLocalDisplayCache)
                return;

            if (!_timePeriodActive)
            {
                timeBeginPeriod(1);
                _timePeriodActive = true;
            }

            mHandler = new TimerEventHandler(TimerCallback);

            if (mTimerId != 0)
                timeKillEvent(mTimerId);

            mTimerId = timeSetEvent(UiTimerPeriodMs, 2, mHandler, IntPtr.Zero, EVENT_TYPE);
        }

        private void StopVisualTimer()
        {
            if (mTimerId != 0)
            {
                int timerId = mTimerId;
                mTimerId = 0;
                timeKillEvent(timerId);
            }

            if (_timePeriodActive)
            {
                timeEndPeriod(1);
                _timePeriodActive = false;
            }
        }

        private void TimerCallback(int id, int msg, IntPtr user, int dw1, int dw2)
        {
            if (!_isRunning || !_enableLocalDisplayCache)
                return;

            if (Interlocked.Exchange(ref _timerBusy, 1) != 0)
                return;

            try
            {
                if (glCtrl.IsHandleCreated && !glCtrl.IsDisposed)
                    Invoke(fmNotifyDrawFrame);
            }
            catch
            {
            }
            finally
            {
                Interlocked.Exchange(ref _timerBusy, 0);
            }
        }

        private void DrawFrameFromTimer()
        {
            if (!_isRunning)
                return;

            if (_enableLocalDisplayCache)
            {
                EnsureWavePages();
                RenderPendingBlocksToPages();

                if (_visualClockStarted)
                    AdvanceVisualPhase();
            }

            RenderFrame();
        }

        private void RenderPendingBlocksToPages()
        {
            PendingBlock[] blocks;

            lock (_displaySync)
            {
                if (_pendingBlocks.Count == 0)
                    return;

                blocks = _pendingBlocks.ToArray();
                _pendingBlocks.Clear();
            }

            for (int i = 0; i < blocks.Length; i++)
            {
                PendingBlock block = blocks[i];
                RenderBlockToPages(
                    block.Samples,
                    block.ChannelsCount,
                    block.SamplesCount,
                    block.FirstAbsoluteSample);
            }
        }

        private void RenderBlockToPages(float[] blockChannelMajor, int channelsCount, int sampleCount, long firstAbsoluteSample)
        {
            if (blockChannelMajor == null || blockChannelMajor.Length == 0)
                return;
            if (channelsCount <= 0 || sampleCount <= 0 || _viewSamplesPerChannel <= 1)
                return;

            int boundSlot = -1;
            EnsureDisplayBaselineState(channelsCount);

            try
            {
                for (int s = 0; s < sampleCount; s++)
                {
                    long abs = firstAbsoluteSample + s;
                    long seq = abs / _viewSamplesPerChannel;
                    int slot = (int)(seq & 1L);

                    if (_waveSeq[slot] != seq)
                    {
                        ClearWavePage(slot);
                        _waveSeq[slot] = seq;
                    }

                    if (boundSlot != slot)
                    {
                        if (boundSlot != -1)
                            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

                        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _waveFbo[slot]);
                        SetupPageViewport();
                        GL.Disable(EnableCap.Texture2D);
                        boundSlot = slot;
                    }

                    float x2 = GetPageX(abs);

                    for (int ch = 0; ch < channelsCount; ch++)
                    {
                        int srcIndex = ch * sampleCount + s;
                        float value = blockChannelMajor[srcIndex];

                        value = ApplyDisplayBaseline(ch, value);

                        float y2 = GetPageY(ch, channelsCount, value);

                        PrevPoint prev = _prevPoints[ch];

                        if (prev.HasValue && prev.AbsoluteSample == abs - 1)
                        {
                            long prevSeq = prev.AbsoluteSample / _viewSamplesPerChannel;

                            if (prevSeq == seq)
                            {
                                GL.Color3(GetChannelColor(ch));
                                GL.Begin(PrimitiveType.Lines);
                                GL.Vertex2(prev.X, prev.Y);
                                GL.Vertex2(x2, y2);
                                GL.End();
                            }
                            else if (prevSeq == seq - 1)
                            {
                                int prevSlot = (int)(prevSeq & 1L);

                                if (_waveSeq[prevSlot] == prevSeq)
                                {
                                    GL.BindFramebuffer(FramebufferTarget.Framebuffer, _waveFbo[prevSlot]);
                                    SetupPageViewport();
                                    GL.Disable(EnableCap.Texture2D);

                                    GL.Color3(GetChannelColor(ch));
                                    GL.Begin(PrimitiveType.Lines);
                                    GL.Vertex2(prev.X, prev.Y);
                                    GL.Vertex2(_pageW - 1.0f, y2);
                                    GL.End();

                                    GL.BindFramebuffer(FramebufferTarget.Framebuffer, _waveFbo[slot]);
                                    SetupPageViewport();
                                    GL.Disable(EnableCap.Texture2D);
                                    boundSlot = slot;
                                }
                            }
                        }

                        prev.HasValue = true;
                        prev.X = x2;
                        prev.Y = y2;
                        prev.AbsoluteSample = abs;
                        _prevPoints[ch] = prev;
                    }
                }
            }
            finally
            {
                if (boundSlot != -1)
                    GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

                SetupViewport();
                GL.Enable(EnableCap.Texture2D);
            }
        }

        private void AdvanceVisualPhase()
        {
            float pageSpanPx = _pageW > 1 ? _pageW - 1.0f : 1.0f;

            _pageShiftPx += _pageDxPx;

            while (_pageShiftPx >= pageSpanPx)
            {
                _pageShiftPx -= pageSpanPx;
                _visualPageSeq++;
            }
        }

        // -------------------------------------------------
        // Display state
        // -------------------------------------------------
        private void ResetDisplayState(int channelsCount, int samplingRate)
        {
            if (channelsCount <= 0)
                channelsCount = 1;
            if (samplingRate <= 0)
                samplingRate = 500;

            _bufferChannelsCount = channelsCount;
            _samplingRate = samplingRate;

            _viewSamplesPerChannel = samplingRate * _timeBaseSeconds;
            if (_viewSamplesPerChannel < 100)
                _viewSamplesPerChannel = 100;

            EnsurePrevPoints();
            ResetPrevPoints();
            EnsureDisplayBaselineState(channelsCount);
            ResetDisplayBaselineState();
        }

        private void EnsureDisplayBaselineState(int channelsCount)
        {
            if (channelsCount <= 0)
                channelsCount = 1;

            if (_displayBaselineVolts != null &&
                _displayBaselineInitialized != null &&
                _displayBaselineVolts.Length == channelsCount &&
                _displayBaselineInitialized.Length == channelsCount)
            {
                return;
            }

            _displayBaselineVolts = new float[channelsCount];
            _displayBaselineInitialized = new bool[channelsCount];
        }

        private void ResetDisplayBaselineState()
        {
            if (_displayBaselineVolts != null)
                Array.Clear(_displayBaselineVolts, 0, _displayBaselineVolts.Length);

            if (_displayBaselineInitialized != null)
                Array.Clear(_displayBaselineInitialized, 0, _displayBaselineInitialized.Length);
        }

        private float ApplyDisplayBaseline(int channelIndex, float valueVolts)
        {
            if (_displayBaselineVolts == null ||
                _displayBaselineInitialized == null ||
                channelIndex < 0 ||
                channelIndex >= _displayBaselineVolts.Length)
            {
                return valueVolts;
            }

            if (!_displayBaselineInitialized[channelIndex])
            {
                _displayBaselineVolts[channelIndex] = valueVolts;
                _displayBaselineInitialized[channelIndex] = true;
                return 0.0f;
            }

            return valueVolts - _displayBaselineVolts[channelIndex];
        }

        private void ClearPendingBlocks()
        {
            lock (_displaySync)
            {
                _pendingBlocks.Clear();
            }
        }

        private void UpdatePageDxPx()
        {
            float pageSpanPx = _pageW > 1 ? _pageW - 1.0f : 1.0f;

            float ticksPerScreen = (_timeBaseSeconds * 1000.0f) / UiTimerPeriodMs;
            if (ticksPerScreen < 1.0f)
                ticksPerScreen = 1.0f;

            _pageDxPx = pageSpanPx / ticksPerScreen;
        }

        private float ParseAmplitudeScaleVolts(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 100e-6f;

            string s = text.Trim().Replace("μ", "µ").Replace(",", ".");
            string[] parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return 100e-6f;

            float value;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return 100e-6f;

            string unit = parts[1];

            if (string.Equals(unit, "µV", StringComparison.OrdinalIgnoreCase))
                return value * 1e-6f;
            if (string.Equals(unit, "mV", StringComparison.OrdinalIgnoreCase))
                return value * 1e-3f;
            if (string.Equals(unit, "V", StringComparison.OrdinalIgnoreCase))
                return value;

            return 100e-6f;
        }

        // -------------------------------------------------
        // GL init / viewport
        // -------------------------------------------------
        private void EnsureGlInitialized()
        {
            if (_glInitialized)
                return;
            if (!glCtrl.IsHandleCreated)
                return;

            glCtrl.MakeCurrent();

            GL.ClearColor(Color.Black);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Lighting);
            GL.Disable(EnableCap.CullFace);

            GL.Enable(EnableCap.Texture2D);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            GL.Disable(EnableCap.LineSmooth);
            GL.Disable(EnableCap.PointSmooth);
            GL.LineWidth(1.0f);
            GL.PointSize(2.0f);

            SetupViewport();
            _glInitialized = true;
        }

        private void SetupViewport()
        {
            int w = glCtrl.Width > 0 ? glCtrl.Width : 1;
            int h = glCtrl.Height > 0 ? glCtrl.Height : 1;

            GL.Viewport(0, 0, w, h);
            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadIdentity();
            GL.Ortho(0.0, w, h, 0.0, -1.0, 1.0);
            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadIdentity();
        }

        private void SetupPageViewport()
        {
            int w = _pageW > 0 ? _pageW : 1;
            int h = _pageH > 0 ? _pageH : 1;

            GL.Viewport(0, 0, w, h);
            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadIdentity();
            GL.Ortho(0.0, w, h, 0.0, -1.0, 1.0);
            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadIdentity();
        }

        // -------------------------------------------------
        // Wave pages
        // -------------------------------------------------
        private void DestroyWavePages()
        {
            if (!_glInitialized)
                return;

            for (int i = 0; i < 2; i++)
            {
                if (_waveFbo[i] != 0)
                {
                    GL.DeleteFramebuffer(_waveFbo[i]);
                    _waveFbo[i] = 0;
                }

                if (_waveTex[i] != 0)
                {
                    GL.DeleteTexture(_waveTex[i]);
                    _waveTex[i] = 0;
                }

                _waveSeq[i] = long.MinValue;
            }

            _pageW = 0;
            _pageH = 0;
            ResetPrevPoints();
        }

        private void ClearWavePage(int slot)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _waveFbo[slot]);
            SetupPageViewport();

            GL.Disable(EnableCap.Texture2D);
            GL.ClearColor(0f, 0f, 0f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            SetupViewport();
            GL.ClearColor(Color.Black);
            _waveSeq[slot] = long.MinValue;
        }

        private void CreateWavePage(int slot)
        {
            _waveTex[slot] = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _waveTex[slot]);

            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, 0);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);

            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.Rgba8,
                _pageW,
                _pageH,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                IntPtr.Zero);

            _waveFbo[slot] = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _waveFbo[slot]);

            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                _waveTex[slot],
                0);

            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);

            FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.BindTexture(TextureTarget.Texture2D, 0);

            if (status != FramebufferErrorCode.FramebufferComplete)
                throw new InvalidOperationException("Wave page FBO is incomplete: " + status.ToString());

            ClearWavePage(slot);
        }

        private void RecreateWavePages()
        {
            RectangleF rect = GetPlotRectangle();

            int newPageW = Math.Max(1, (int)Math.Ceiling(rect.Width));
            int newPageH = Math.Max(1, (int)Math.Ceiling(rect.Height));

            DestroyWavePages();

            _pageW = newPageW;
            _pageH = newPageH;

            for (int i = 0; i < 2; i++)
                CreateWavePage(i);

            EnsurePrevPoints();
            ResetPrevPoints();
            UpdatePageDxPx();
        }

        private void EnsureWavePages()
        {
            RectangleF rect = GetPlotRectangle();
            int w = Math.Max(1, (int)Math.Ceiling(rect.Width));
            int h = Math.Max(1, (int)Math.Ceiling(rect.Height));

            bool needRecreate =
                _waveTex[0] == 0 || _waveTex[1] == 0 ||
                _waveFbo[0] == 0 || _waveFbo[1] == 0 ||
                _pageW != w || _pageH != h;

            if (needRecreate)
                RecreateWavePages();
        }

        private void EnsurePrevPoints()
        {
            int channelsCount = _bufferChannelsCount;
            if (channelsCount <= 0)
                channelsCount = 1;

            if (_prevPoints != null && _prevPoints.Length == channelsCount)
                return;

            _prevPoints = new PrevPoint[channelsCount];
            ResetPrevPoints();
        }

        private void ResetPrevPoints()
        {
            if (_prevPoints == null)
                return;

            for (int i = 0; i < _prevPoints.Length; i++)
            {
                _prevPoints[i].HasValue = false;
                _prevPoints[i].X = 0.0f;
                _prevPoints[i].Y = 0.0f;
                _prevPoints[i].AbsoluteSample = long.MinValue;
            }
        }

        private float GetPageX(long absoluteSample)
        {
            if (_viewSamplesPerChannel <= 1 || _pageW <= 1)
                return 0.0f;

            long local = absoluteSample % _viewSamplesPerChannel;
            if (local < 0)
                local += _viewSamplesPerChannel;

            return (float)local * (_pageW - 1) / (float)(_viewSamplesPerChannel - 1);
        }

        private float GetPageY(int channelIndex, int channelsCount, float valueVolts)
        {
            if (channelsCount <= 0)
                channelsCount = 1;

            float bandHeight = (float)_pageH / channelsCount;
            float scaleVolts = _displayScaleVolts;
            if (scaleVolts <= 0.0f)
                scaleVolts = 100e-6f;

            float pixelsPerVolt = (bandHeight * 0.35f) / scaleVolts;
            float yBase = (channelIndex + 0.5f) * bandHeight;

            return yBase - valueVolts * pixelsPerVolt;
        }

        // -------------------------------------------------
        // Frame render
        // -------------------------------------------------
        private void RenderFrame()
        {
            if (!_glInitialized)
                EnsureGlInitialized();

            if (!_glInitialized || glCtrl.IsDisposed || !glCtrl.IsHandleCreated)
                return;

            glCtrl.MakeCurrent();

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            SetupViewport();
            GL.Clear(ClearBufferMask.ColorBufferBit);

            if (IsImpedanceDisplayMode)
            {
                DrawImpedanceTexture();
            }
            else
            {
                // This is the original pre-Impedance waveform render path.
                EnsureWavePages();
                DrawWavePages();
                DrawOverlayTexture();
            }

            glCtrl.SwapBuffers();
        }

        private void BeginPlotScissor(RectangleF rect)
        {
            int x = Math.Max((int)Math.Floor(rect.Left), 0);
            int yTop = Math.Max((int)Math.Floor(rect.Top), 0);
            int width = Math.Max((int)Math.Ceiling(rect.Width), 1);
            int height = Math.Max((int)Math.Ceiling(rect.Height), 1);

            int glY = glCtrl.Height - (yTop + height);
            if (glY < 0)
                glY = 0;

            GL.Enable(EnableCap.ScissorTest);
            GL.Scissor(x, glY, width, height);
        }

        private void EndPlotScissor()
        {
            GL.Disable(EnableCap.ScissorTest);
        }

        private void DrawPage(long pageSequence, float left, float top)
        {
            if (pageSequence < 0)
                return;

            int slot = (int)(pageSequence & 1L);
            if (_waveTex[slot] == 0 || _waveSeq[slot] != pageSequence)
                return;

            float right = left + _pageW;
            float bottom = top + _pageH;

            GL.Enable(EnableCap.Texture2D);
            GL.BindTexture(TextureTarget.Texture2D, _waveTex[slot]);
            GL.Color4(1f, 1f, 1f, 1f);

            GL.Begin(PrimitiveType.Quads);
            GL.TexCoord2(0f, 1f); GL.Vertex2(left, top);
            GL.TexCoord2(1f, 1f); GL.Vertex2(right, top);
            GL.TexCoord2(1f, 0f); GL.Vertex2(right, bottom);
            GL.TexCoord2(0f, 0f); GL.Vertex2(left, bottom);
            GL.End();

            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        private void DrawWavePages()
        {
            if (_waveTex[0] == 0 || _waveTex[1] == 0 || _pageW <= 1)
                return;

            RectangleF rect = GetPlotRectangle();
            float pageSpanPx = _pageW - 1.0f;

            float currentLeft = rect.Right - _pageShiftPx;
            float previousLeft = currentLeft - pageSpanPx;

            BeginPlotScissor(rect);
            DrawPage(_visualPageSeq - 1, previousLeft, rect.Top);
            DrawPage(_visualPageSeq, currentLeft, rect.Top);
            EndPlotScissor();
        }

        private RectangleF GetPlotRectangle()
        {
            float width = glCtrl.Width - PlotLeft - PlotRight;
            float height = glCtrl.Height - PlotTop - PlotBottom;

            if (width < 10.0f) width = 10.0f;
            if (height < 10.0f) height = 10.0f;

            return new RectangleF(PlotLeft, PlotTop, width, height);
        }

        // -------------------------------------------------
        // Overlay
        // -------------------------------------------------
        private void InvalidateOverlay()
        {
            _overlayDirty = true;
        }

        private Bitmap BuildOverlayBitmap()
        {
            int w = Math.Max(glCtrl.Width, 1);
            int h = Math.Max(glCtrl.Height, 1);

            Bitmap bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            using (Graphics g = Graphics.FromImage(bmp))
            using (Font font = new Font("Segoe UI", 8.0f))
            using (Pen borderPen = new Pen(Color.FromArgb(70, 70, 70), 1.0f))
            using (Pen gridPen = new Pen(Color.FromArgb(45, 45, 45), 1.0f))
            using (Pen baselinePen = new Pen(Color.FromArgb(35, 35, 35), 1.0f))
            using (Brush brushChannels = new SolidBrush(Color.LightGray))
            using (Brush brushTime = new SolidBrush(Color.Gray))
            {
                g.Clear(Color.Transparent);

                RectangleF rect = GetPlotRectangle();
                int channelsCount = _bufferChannelsCount > 0 ? _bufferChannelsCount : 1;
                float bandHeight = rect.Height / channelsCount;

                g.DrawRectangle(borderPen, rect.X, rect.Y, rect.Width, rect.Height);

                for (int sec = 0; sec <= _timeBaseSeconds; sec++)
                {
                    float x = rect.Left + rect.Width * sec / _timeBaseSeconds;
                    g.DrawLine(gridPen, x, rect.Top, x, rect.Bottom);
                }

                for (int ch = 0; ch < channelsCount; ch++)
                {
                    float y = rect.Top + bandHeight * (ch + 0.5f);
                    g.DrawLine(baselinePen, rect.Left, y, rect.Right, y);
                }

                for (int ch = 0; ch < channelsCount; ch++)
                {
                    string channelName = GetChannelDisplayName(ch);
                    float yBase = rect.Top + (ch + 0.5f) * bandHeight;
                    g.DrawString(channelName, font, brushChannels, 4.0f, yBase - font.Height * 0.5f);
                }

                for (int sec = 0; sec <= _timeBaseSeconds; sec++)
                {
                    float x = rect.Left + rect.Width * sec / _timeBaseSeconds;
                    int relSec = sec - _timeBaseSeconds;

                    string text = relSec.ToString(CultureInfo.InvariantCulture) + " s";
                    SizeF sz = g.MeasureString(text, font);
                    g.DrawString(text, font, brushTime, x - sz.Width * 0.5f, rect.Bottom + 2.0f);
                }

                string scaleText = "Scale: " + cmbBxScaleAmpl.Text;
                SizeF scaleSize = g.MeasureString(scaleText, font);
                g.DrawString(scaleText, font, brushTime, rect.Right - scaleSize.Width, 2.0f);
            }

            return bmp;
        }

        private Bitmap BuildImpedanceBitmap()
        {
            int w = Math.Max(glCtrl.Width, 1);
            int h = Math.Max(glCtrl.Height, 1);
            Bitmap bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            using (Graphics g = Graphics.FromImage(bmp))
            using (Font titleFont = new Font("Segoe UI", 10.0f, FontStyle.Regular))
            using (Font legendFont = new Font("Segoe UI", 8.0f, FontStyle.Regular))
            {
                g.Clear(Color.Black);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                const float margin = 14.0f;
                const float topArea = 42.0f;
                const float gap = 7.0f;

                using (Brush titleBrush = new SolidBrush(Color.Gainsboro))
                using (Brush secondaryBrush = new SolidBrush(Color.Gray))
                {
                    g.DrawString("Impedance", titleFont, titleBrush, margin, 11.0f);
                    g.DrawString((ImpedanceRangeMaxOhm / 1000U).ToString(CultureInfo.InvariantCulture) + " kΩ range", legendFont, secondaryBrush, margin + 76.0f, 14.0f);
                }

                DrawImpedanceLegend(g, w, margin, legendFont);

                int channelCount = _impedancePhysicalChannelIndices != null
                    ? _impedancePhysicalChannelIndices.Length
                    : 0;

                // One extra cell for GND, as in PyCorder.
                int itemCount = channelCount + 1;
                RectangleF area = new RectangleF(
                    margin,
                    topArea,
                    Math.Max(1.0f, w - margin * 2.0f),
                    Math.Max(1.0f, h - topArea - margin));

                int columns;
                int rows;
                CalculateImpedanceGrid(itemCount, area.Width, area.Height, gap, out columns, out rows);

                float cellW = (area.Width - gap * (columns - 1)) / columns;
                float cellH = (area.Height - gap * (rows - 1)) / rows;

                float fontSize = Math.Max(7.0f, Math.Min(11.0f, Math.Min(cellW / 9.0f, cellH / 5.5f)));
                using (Font channelFont = new Font("Segoe UI", fontSize, FontStyle.Bold))
                using (Font valueFont = new Font("Segoe UI", Math.Max(7.0f, fontSize - 0.5f), FontStyle.Regular))
                {
                    for (int i = 0; i < itemCount; i++)
                    {
                        int row = i / columns;
                        int col = i % columns;
                        RectangleF rect = new RectangleF(
                            area.Left + col * (cellW + gap),
                            area.Top + row * (cellH + gap),
                            cellW,
                            cellH);

                        bool isGnd = i == channelCount;
                        string name;
                        uint value;
                        bool valueAvailable;

                        if (isGnd)
                        {
                            name = "GND";
                            valueAvailable = _impedanceValuesOhm != null && _impedanceValuesOhm.Length > _physicalEegChannelsCount;
                            value = valueAvailable ? _impedanceValuesOhm[_impedanceValuesOhm.Length - 1] : 0U;
                        }
                        else
                        {
                            int physicalIndex = _impedancePhysicalChannelIndices[i];
                            name = _impedanceChannelNames != null && i < _impedanceChannelNames.Length
                                ? _impedanceChannelNames[i]
                                : "EEG" + (physicalIndex + 1).ToString(CultureInfo.InvariantCulture);

                            valueAvailable = _impedanceValuesOhm != null &&
                                physicalIndex >= 0 &&
                                physicalIndex < _impedanceValuesOhm.Length - 1;
                            value = valueAvailable ? _impedanceValuesOhm[physicalIndex] : 0U;
                        }

                        DrawImpedanceCell(g, rect, name, valueAvailable, value, channelFont, valueFont, isGnd);
                    }
                }
            }

            return bmp;
        }

        private static void CalculateImpedanceGrid(int itemCount, float width, float height, float gap, out int bestColumns, out int bestRows)
        {
            if (itemCount <= 0)
            {
                bestColumns = 1;
                bestRows = 1;
                return;
            }

            bestColumns = 1;
            bestRows = itemCount;
            float bestScore = 0.0f;

            for (int columns = 1; columns <= itemCount; columns++)
            {
                int rows = (itemCount + columns - 1) / columns;
                float cellW = (width - gap * (columns - 1)) / columns;
                float cellH = (height - gap * (rows - 1)) / rows;
                if (cellW <= 0.0f || cellH <= 0.0f)
                    continue;

                // Slightly landscape cards look cleaner for "EEG128 / 12 kΩ".
                float score = Math.Min(cellW / 1.45f, cellH);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestColumns = columns;
                    bestRows = rows;
                }
            }
        }

        private static void DrawImpedanceLegend(Graphics g, int width, float margin, Font font)
        {
            const float box = 10.0f;
            const float gap = 5.0f;
            string[] labels = new string[] { "<17", "17–33", ">=33 kΩ" };
            Color[] colors = new Color[] {
                Color.FromArgb(62, 153, 80),
                Color.FromArgb(205, 168, 55),
                Color.FromArgb(190, 66, 63)
            };

            float totalWidth = 0.0f;
            for (int i = 0; i < labels.Length; i++)
            {
                SizeF sz = g.MeasureString(labels[i], font);
                totalWidth += box + gap + sz.Width + 10.0f;
            }

            float x = Math.Max(margin, width - margin - totalWidth);
            float y = 14.0f;

            for (int i = 0; i < labels.Length; i++)
            {
                using (Brush b = new SolidBrush(colors[i]))
                    g.FillRectangle(b, x, y + 1.0f, box, box);

                x += box + gap;
                using (Brush tb = new SolidBrush(Color.Gray))
                    g.DrawString(labels[i], font, tb, x, y - 1.0f);

                x += g.MeasureString(labels[i], font).Width + 10.0f;
            }
        }

        private static void DrawImpedanceCell(
            Graphics g,
            RectangleF rect,
            string name,
            bool valueAvailable,
            uint valueOhm,
            Font channelFont,
            Font valueFont,
            bool isGnd)
        {
            Color fillColor;
            string valueText;

            if (!valueAvailable)
            {
                fillColor = Color.FromArgb(50, 50, 50);
                valueText = "...";
            }
            else if (valueOhm > ActiChampDevice.ImpedanceInvalid)
            {
                fillColor = Color.FromArgb(82, 82, 82);
                valueText = "Disconnected";
            }
            else if (valueOhm == ActiChampDevice.ImpedanceInvalid)
            {
                fillColor = Color.FromArgb(190, 66, 63);
                valueText = "Out of range";
            }
            else
            {
                if (valueOhm < ImpedanceGoodOhm)
                    fillColor = Color.FromArgb(62, 153, 80);
                else if (valueOhm < ImpedanceBadOhm)
                    fillColor = Color.FromArgb(205, 168, 55);
                else
                    fillColor = Color.FromArgb(190, 66, 63);

                valueText = (valueOhm / 1000.0).ToString("0", CultureInfo.InvariantCulture) + " kΩ";
            }

            using (Brush fill = new SolidBrush(fillColor))
            using (Pen border = new Pen(isGnd ? Color.Gainsboro : Color.FromArgb(110, 110, 110), isGnd ? 1.5f : 1.0f))
            using (Brush text = new SolidBrush(Color.White))
            using (Brush valueBrush = new SolidBrush(Color.FromArgb(235, 235, 235)))
            {
                g.FillRectangle(fill, rect);
                g.DrawRectangle(border, rect.X, rect.Y, rect.Width, rect.Height);

                SizeF nameSize = g.MeasureString(name, channelFont);
                SizeF valueSize = g.MeasureString(valueText, valueFont);
                float centerX = rect.Left + rect.Width * 0.5f;
                float centerY = rect.Top + rect.Height * 0.5f;

                g.DrawString(name, channelFont, text, centerX - nameSize.Width * 0.5f, centerY - nameSize.Height - 1.0f);
                g.DrawString(valueText, valueFont, valueBrush, centerX - valueSize.Width * 0.5f, centerY + 1.0f);
            }
        }

        private string GetChannelDisplayName(int channelIndex)
        {
            if (_selectedChannelNames != null &&
                channelIndex >= 0 &&
                channelIndex < _selectedChannelNames.Length &&
                !string.IsNullOrEmpty(_selectedChannelNames[channelIndex]))
            {
                return _selectedChannelNames[channelIndex];
            }

            return "Ch " + (channelIndex + 1).ToString();
        }

        private void InvalidateImpedanceDisplay()
        {
            _impedanceDirty = true;
        }

        private void RebuildImpedanceTexture()
        {
            if (!_glInitialized)
                return;

            if (_impedanceBitmap != null)
            {
                _impedanceBitmap.Dispose();
                _impedanceBitmap = null;
            }

            _impedanceBitmap = BuildImpedanceBitmap();

            if (_impedanceTextureId == 0)
                _impedanceTextureId = GL.GenTexture();

            GL.BindTexture(TextureTarget.Texture2D, _impedanceTextureId);

            System.Drawing.Imaging.BitmapData data = _impedanceBitmap.LockBits(
                new Rectangle(0, 0, _impedanceBitmap.Width, _impedanceBitmap.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            try
            {
                GL.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    PixelInternalFormat.Rgba,
                    _impedanceBitmap.Width,
                    _impedanceBitmap.Height,
                    0,
                    PixelFormat.Bgra,
                    PixelType.UnsignedByte,
                    data.Scan0);
            }
            finally
            {
                _impedanceBitmap.UnlockBits(data);
            }

            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Clamp);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Clamp);

            GL.BindTexture(TextureTarget.Texture2D, 0);
            _impedanceDirty = false;
        }

        private void DrawImpedanceTexture()
        {
            if (_impedanceDirty || _impedanceTextureId == 0)
                RebuildImpedanceTexture();

            if (_impedanceTextureId == 0)
                return;

            int w = Math.Max(glCtrl.Width, 1);
            int h = Math.Max(glCtrl.Height, 1);

            GL.Enable(EnableCap.Texture2D);
            GL.BindTexture(TextureTarget.Texture2D, _impedanceTextureId);
            GL.Color4(1.0f, 1.0f, 1.0f, 1.0f);

            GL.Begin(PrimitiveType.Quads);
            GL.TexCoord2(0.0f, 0.0f); GL.Vertex2(0.0f, 0.0f);
            GL.TexCoord2(1.0f, 0.0f); GL.Vertex2(w, 0.0f);
            GL.TexCoord2(1.0f, 1.0f); GL.Vertex2(w, h);
            GL.TexCoord2(0.0f, 1.0f); GL.Vertex2(0.0f, h);
            GL.End();

            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        private void RebuildOverlayTexture()
        {
            if (!_glInitialized)
                return;

            if (_overlayBitmap != null)
            {
                _overlayBitmap.Dispose();
                _overlayBitmap = null;
            }

            _overlayBitmap = BuildOverlayBitmap();

            if (_overlayTextureId == 0)
                _overlayTextureId = GL.GenTexture();

            GL.BindTexture(TextureTarget.Texture2D, _overlayTextureId);

            System.Drawing.Imaging.BitmapData data = _overlayBitmap.LockBits(
                new Rectangle(0, 0, _overlayBitmap.Width, _overlayBitmap.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            try
            {
                GL.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    PixelInternalFormat.Rgba,
                    _overlayBitmap.Width,
                    _overlayBitmap.Height,
                    0,
                    PixelFormat.Bgra,
                    PixelType.UnsignedByte,
                    data.Scan0);
            }
            finally
            {
                _overlayBitmap.UnlockBits(data);
            }

            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Clamp);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Clamp);

            GL.BindTexture(TextureTarget.Texture2D, 0);
            _overlayDirty = false;
        }

        private void DrawOverlayTexture()
        {
            if (_overlayDirty || _overlayTextureId == 0)
                RebuildOverlayTexture();

            if (_overlayTextureId == 0)
                return;

            int w = Math.Max(glCtrl.Width, 1);
            int h = Math.Max(glCtrl.Height, 1);

            GL.Enable(EnableCap.Texture2D);
            GL.BindTexture(TextureTarget.Texture2D, _overlayTextureId);
            GL.Color4(1.0f, 1.0f, 1.0f, 1.0f);

            GL.Begin(PrimitiveType.Quads);
            GL.TexCoord2(0.0f, 0.0f); GL.Vertex2(0.0f, 0.0f);
            GL.TexCoord2(1.0f, 0.0f); GL.Vertex2(w, 0.0f);
            GL.TexCoord2(1.0f, 1.0f); GL.Vertex2(w, h);
            GL.TexCoord2(0.0f, 1.0f); GL.Vertex2(0.0f, h);
            GL.End();

            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        private Color GetChannelColor(int ch)
        {
            Color[] colors = new Color[]
            {
                Color.Lime,
                Color.Cyan,
                Color.Yellow,
                Color.Orange,
                Color.Magenta,
                Color.DeepSkyBlue,
                Color.LightGreen,
                Color.White
            };

            return colors[ch % colors.Length];
        }

        // -------------------------------------------------
        // UI display events
        // -------------------------------------------------
        private void btnBaselineNow_Click(object sender, EventArgs e)
        {
            ResetDisplayBaselineState();
            ClearPendingBlocks();

            _receivedSamplesCount = 0;
            _pageShiftPx = 0.0f;
            _visualPageSeq = 0;
            _visualClockStarted = false;

            if (_enableLocalDisplayCache && _glInitialized)
            {
                glCtrl.MakeCurrent();
                RecreateWavePages();
            }

            RenderFrame();
        }

        private void cmbBxScaleAmpl_SelectedIndexChanged(object sender, EventArgs e)
        {
            _displayScaleVolts = ParseAmplitudeScaleVolts(cmbBxScaleAmpl.Text);

            if (_enableLocalDisplayCache && _glInitialized)
            {
                glCtrl.MakeCurrent();
                RecreateWavePages();

                _pageShiftPx = 0.0f;
                _visualPageSeq = 0;
                _receivedSamplesCount = 0;
                _visualClockStarted = false;
                ClearPendingBlocks();
            }

            InvalidateOverlay();
            RenderFrame();
        }

        private void numUpDwnTimeBaseInSec_ValueChanged(object sender, EventArgs e)
        {
            int newTimeBaseSeconds = (int)numUpDwnTimeBaseInSec.Value;
            if (newTimeBaseSeconds <= 0)
                newTimeBaseSeconds = DefaultTimeBaseSeconds;

            if (newTimeBaseSeconds == _timeBaseSeconds)
                return;

            _timeBaseSeconds = newTimeBaseSeconds;

            int samplingRate = _samplingRate > 0 ? _samplingRate : 500;
            int channelsCount = _bufferChannelsCount > 0 ? _bufferChannelsCount : 1;

            ResetDisplayState(channelsCount, samplingRate);
            ClearPendingBlocks();

            _receivedSamplesCount = 0;
            _pageShiftPx = 0.0f;
            _visualPageSeq = 0;
            _visualClockStarted = false;

            if (_enableLocalDisplayCache && _glInitialized)
            {
                glCtrl.MakeCurrent();
                RecreateWavePages();
            }

            InvalidateOverlay();
            RenderFrame();
        }

        private void glCtrl_Paint(object sender, PaintEventArgs e)
        {
            RenderFrame();
        }

        private void glCtrl_Resize(object sender, EventArgs e)
        {
            if (!_glInitialized)
                EnsureGlInitialized();

            if (!_glInitialized)
                return;

            glCtrl.MakeCurrent();
            SetupViewport();

            if (IsImpedanceDisplayMode)
            {
                // Impedance owns no waveform FBO state. Resize only its own texture.
                InvalidateImpedanceDisplay();
            }
            else
            {
                // Exact pre-Impedance waveform resize behavior.
                RecreateWavePages();
                ClearPendingBlocks();
                InvalidateOverlay();
            }

            RenderFrame();
        }

        // -------------------------------------------------
        // Log / status
        // -------------------------------------------------
        private void AppendLog(string message)
        {
            AppendLog(message, DateTime.Now);
        }

        private void AppendLog(string message, DateTime timestamp)
        {
            if (IsDisposed || rtbDeviceInfo == null)
                return;

            if (rtbDeviceInfo.InvokeRequired)
            {
                rtbDeviceInfo.BeginInvoke(new Action<string, DateTime>(AppendLog), message, timestamp);
                return;
            }

            if (string.IsNullOrEmpty(message))
                rtbDeviceInfo.AppendText(Environment.NewLine);
            else
                rtbDeviceInfo.AppendText(timestamp.ToString("HH:mm:ss.fff  ") + message + Environment.NewLine);

            rtbDeviceInfo.SelectionStart = rtbDeviceInfo.TextLength;
            rtbDeviceInfo.ScrollToCaret();
        }

        private void UpdateDroppedBlocksStatusSafe(int droppedBlocks)
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            BeginInvoke((Action)(() =>
            {
                toolStripStatusDropped.Text = "Dropped blocks: " + droppedBlocks.ToString();
            }));
        }
    }
}
