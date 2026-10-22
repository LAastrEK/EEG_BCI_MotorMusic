using OpenTK.Graphics.OpenGL;
using PolyHubIpc;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using static SignalGenerator.SignalGeneratorEngine;

namespace SignalGenerator
{
    public partial class fmMain : Form
    {
        private ServiceEventLink _serviceEventLink;
        private ProcessingBlockRing _processingBlockRing;

        private const string ServiceSettingsFileName = "SignalGenerator.json";
        private const string ServiceManifestFileName = "SignalGenerator.service.json";
        private const int ProcessingRingBlockCount = 256;

        private SignalGeneratorSettings _serviceProgramSettings;
        private ServiceManifest _serviceManifest;

        // Если false, локальную картинку не строим вообще
        private bool _enableLocalDisplayCache = true;

        private int _lastShownDroppedBlocks = -1;

        // -------------------------------------------------
        // MM timer
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

        // -------------------------------------------------
        // QPC
        // -------------------------------------------------
        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceCounter(out long value);

        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceFrequency(out long freq);

        private static long _QpcFreq;

        // -------------------------------------------------
        // UI delegate exactly in old style
        // -------------------------------------------------
        public delegate void FmNotifyDrawFrameHandler();
        public FmNotifyDrawFrameHandler fmNotifyDrawFrame;

        // -------------------------------------------------
        // Runtime
        // -------------------------------------------------
        private readonly object _ringSync = new object();

        private long _qpcStart;
        private long _generatedSamplesCount;
        private int _timerBusy;
        private bool _timePeriodActive;

        private bool _glInitialized;
        private bool _isRunning;

        private SignalGeneratorEngine _generator;
        private readonly List<double> _sineFrequenciesHz = new List<double>();
        private readonly List<NumericUpDown> _sineFrequencyEditors = new List<NumericUpDown>();
        private bool _updatingSineFrequencyEditors;

        private int _bufferChannelsCount;
        private int _viewSamplesPerChannel;

        // -------------------------------------------------
        // Visual phase
        // -------------------------------------------------
        // Сдвиг текущей картинки по X, в пикселях
        private float _pageShiftPx;

        // Номер текущей входящей картонки
        private long _visualPageSeq;

        // Скорость сдвига картинки по X, пикселей за 1 тик таймера
        private float _pageDxPx;

        // -------------------------------------------------
        // Pending blocks for direct drawing
        // -------------------------------------------------
        private struct PendingBlock
        {
            public float[] Samples;
            public int SamplesCount;
            public long FirstAbsoluteSample;
            public int ChannelsCount;
        }

        private readonly List<PendingBlock> _pendingBlocks = new List<PendingBlock>(8);

        // -------------------------------------------------
        // Page drawing state
        // -------------------------------------------------
        private struct PrevPoint
        {
            public bool HasValue;
            public float X;
            public float Y;
            public long AbsoluteSample;
        }

        private PrevPoint[] _prevPoints;

        private int[] _waveTex = new int[2];
        private int[] _waveFbo = new int[2];
        private long[] _waveSeq = new long[] { long.MinValue, long.MinValue };

        private int _pageW;
        private int _pageH;

        // -------------------------------------------------
        // Overlay
        // -------------------------------------------------
        private int _overlayTextureId;
        private Bitmap _overlayBitmap;
        private bool _overlayDirty = true;

        private const int DefaultTimeBaseSeconds = 5;
        private int _timeBaseSeconds = DefaultTimeBaseSeconds;

        private float _displayScaleVolts = 100e-6f; // по умолчанию ±100 µV

        private const float PlotLeft = 70.0f;
        private const float PlotTop = 10.0f;
        private const float PlotRight = 10.0f;
        private const float PlotBottom = 24.0f;

        public fmMain(bool hosted)
        {
            InitializeComponent();
            QueryPerformanceFrequency(out _QpcFreq);

            if (hosted)
            {
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
            }

            fmNotifyDrawFrame = new FmNotifyDrawFrameHandler(DrawFrameFromTimer);
        }

        // -------------------------------------------------
        // Form lifetime
        // -------------------------------------------------
        private void fmMain_Load(object sender, EventArgs e)
        {
            LoadLocalJsonFiles();
            ApplyServiceProgramSettingsToUi();

            cbWaveForm.Items.Clear();
            cbWaveForm.Items.Add("Sine");
            cbWaveForm.Items.Add("AsEEG");
            cbWaveForm.SelectedItem = string.Equals(_serviceProgramSettings.WaveForm, "Sine",
                StringComparison.OrdinalIgnoreCase) ? "Sine" : "AsEEG";

            if (cmbBxScaleAmpl.Items.Count > 0)
            {
                int defaultIndex = cmbBxScaleAmpl.Items.IndexOf("100 µV");
                cmbBxScaleAmpl.SelectedIndex = defaultIndex >= 0 ? defaultIndex : 7;
            }

            if (numUpDwnChnlsCnt.Value <= 0) numUpDwnChnlsCnt.Value = 4;
            if (numUpDwnSamplingRate.Value < MinimumSamplingRate) numUpDwnSamplingRate.Value = MinimumSamplingRate;
            if (numUpDwnBlockSize.Value <= 0) numUpDwnBlockSize.Value = 32;
            if (numUpDwnTimeBaseInSec.Value <= 0) numUpDwnTimeBaseInSec.Value = DefaultTimeBaseSeconds;

            _timeBaseSeconds = (int)numUpDwnTimeBaseInSec.Value;
            _displayScaleVolts = ParseAmplitudeScaleVolts(cmbBxScaleAmpl.Text);

            cmbBxScaleAmpl.SelectedIndexChanged += cmbBxScaleAmpl_SelectedIndexChanged;
            numUpDwnTimeBaseInSec.ValueChanged += numUpDwnTimeBaseInSec_ValueChanged;

            _serviceEventLink = new ServiceEventLink(
                _serviceManifest.ServiceEvtLinkManifest.ServiceEvtLinkMapName,
                _serviceManifest.ServiceEvtLinkManifest.HubToServiceEvtName,
                _serviceManifest.ServiceEvtLinkManifest.ServiceToHubEvtName,
                ServiceEventSide.Service,
                _serviceManifest.ServiceEvtLinkManifest.ServiceEvtLinkCapacity);

            _serviceEventLink.EventReceived += ServiceEventLink_EventReceived;

            PublishServiceState();

            _generator = new SignalGeneratorEngine();
            RebuildSineFrequencyEditors();
            numUpDwnChnlsCnt.ValueChanged += numUpDwnChnlsCnt_ValueChanged;
            numUpDwnSamplingRate.ValueChanged += numUpDwnSamplingRate_ValueChanged;
            cbWaveForm.SelectedIndexChanged += cbWaveForm_SelectedIndexChanged;

            EnsureGlInitialized();
            ResetDisplayState((int)numUpDwnChnlsCnt.Value, (int)numUpDwnSamplingRate.Value);

            if (_enableLocalDisplayCache)
                RecreateWavePages();

            InvalidateOverlay();

            _lastShownDroppedBlocks = 0;
            UpdateDroppedBlocksStatusSafe(0);
        }

        private void fmMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            StopGeneratorInternal();

            CaptureServiceProgramSettingsFromUi();
            SaveLocalJsonFiles();

            if (_serviceEventLink != null)
            {
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
                            if (!_isRunning)
                                btnStartGenerator_Click(this, EventArgs.Empty);
                            break;

                        case ServiceEventCode.Stop:
                            if (_isRunning)
                                btnStopGenerator_Click(this, EventArgs.Empty);
                            break;

                        case ServiceEventCode.Reset:
                            if (_isRunning)
                                btnStopGenerator_Click(this, EventArgs.Empty);

                            btnStartGenerator_Click(this, EventArgs.Empty);
                            break;

                        case ServiceEventCode.Ping:
                            break;
                    }

                    _serviceEventLink.SendEvent(
                        ServiceEventCode.Ack,
                        _isRunning ? 1 : 0,
                        (int)code,
                        0,
                        "OK");
                }
                catch (Exception ex)
                {
                    _serviceEventLink.SendEvent(
                        ServiceEventCode.Error,
                        _isRunning ? 1 : 0,
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

        private SignalGeneratorSettings CreateDefaultServiceProgramSettings()
        {
            return new SignalGeneratorSettings
            {
                WindowLeft = 100,
                WindowTop = 100,
                WindowWidth = 1000,
                WindowHeight = 700,
            };
        }

        private ServiceManifest CreateDefaultServiceManifest()
        {
            return new ServiceManifest
            {
                ServiceId = "SignalGenerator",
                ServiceDisplayName = "Signal Generator",

                ServiceProgramFolderName = "SignalGenerator",
                ServiceExeFileName = "SignalGenerator.exe",
                ServiceExeSubFolder = @"bin\Debug",
                ServiceHostArguments = "--hosted",
                ServiceSupportsHosted = true,

                ServiceEvtLinkManifest = new ServiceEvtLinkManifest
                {
                    ServiceEvtLinkMapName = @"Local\PolyHub.SignalGenerator.1.ServiceEventLink",
                    HubToServiceEvtName = @"Local\PolyHub.SignalGenerator.1.ServiceEventLink.HubToServiceEvent",
                    ServiceToHubEvtName = @"Local\PolyHub.SignalGenerator.1.ServiceEventLink.ServiceToHubEvent",
                    ServiceEvtLinkCapacity = 256
                },

                ServiceDataOutputManifestList = new List<ServiceDataPortManifest>
                {
                    new ServiceDataPortManifest
                    {
                        ServiceDataPortId = "RawSignal",
                        ServiceDataPortDisplayName = "Raw Signal",
                        ServiceDataPortMapName = @"Local\PolyHub.SignalGenerator.1.ProcessingBlockRing",
                        ServiceDataPortReadyEventName = @"Local\PolyHub.SignalGenerator.1.ProcessingBlockRing.DataReady"
                    }
                },

                ServiceDataInputManifestList = new List<ServiceDataPortManifest>()
            };
        }

        private void LoadLocalJsonFiles()
        {
            _serviceProgramSettings = JsonFileStore.LoadOrCreate(
                GetServiceSettingsFilePath(),
                CreateDefaultServiceProgramSettings);

            _serviceManifest = JsonFileStore.LoadOrCreate(
                GetServiceManifestFilePath(),
                CreateDefaultServiceManifest);
        }

        private void ApplyServiceProgramSettingsToUi()
        {
            StartPosition = FormStartPosition.Manual;
            Left = _serviceProgramSettings.WindowLeft;
            Top = _serviceProgramSettings.WindowTop;
            Width = _serviceProgramSettings.WindowWidth;
            Height = _serviceProgramSettings.WindowHeight;

            if (_serviceProgramSettings.ChannelsCount > 0)
                numUpDwnChnlsCnt.Value = Math.Min(numUpDwnChnlsCnt.Maximum,
                    Math.Max(numUpDwnChnlsCnt.Minimum, _serviceProgramSettings.ChannelsCount));
            if (_serviceProgramSettings.SamplingRate > 0)
                numUpDwnSamplingRate.Value = Math.Min(numUpDwnSamplingRate.Maximum,
                    Math.Max(numUpDwnSamplingRate.Minimum, _serviceProgramSettings.SamplingRate));

            if (_serviceProgramSettings.SineFrequenciesHz != null)
            {
                int count = Math.Min(_serviceProgramSettings.SineFrequenciesHz.Length,
                    (int)numUpDwnChnlsCnt.Maximum);
                for (int ch = 0; ch < count; ch++)
                {
                    double frequency = _serviceProgramSettings.SineFrequenciesHz[ch];
                    _sineFrequenciesHz.Add(double.IsNaN(frequency) || double.IsInfinity(frequency)
                        ? 8.0 + ch : frequency);
                }
            }
        }

        private void CaptureServiceProgramSettingsFromUi()
        {
            _serviceProgramSettings.WindowLeft = Left;
            _serviceProgramSettings.WindowTop = Top;
            _serviceProgramSettings.WindowWidth = Width;
            _serviceProgramSettings.WindowHeight = Height;
            _serviceProgramSettings.ChannelsCount = (int)numUpDwnChnlsCnt.Value;
            _serviceProgramSettings.SamplingRate = (int)numUpDwnSamplingRate.Value;
            _serviceProgramSettings.WaveForm = cbWaveForm.Text;
            _serviceProgramSettings.SineFrequenciesHz = _sineFrequenciesHz.ToArray();
        }

        private void SaveLocalJsonFiles()
        {
            JsonFileStore.Save(GetServiceSettingsFilePath(), _serviceProgramSettings);
            JsonFileStore.Save(GetServiceManifestFilePath(), _serviceManifest);
        }

        private void PublishServiceState()
        {
            if (_serviceEventLink == null)
                return;

            _serviceEventLink.SendEvent(
                ServiceEventCode.State,
                _isRunning ? 1 : 0,
                0,
                0,
                _isRunning ? "Running" : "Stopped");
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
        // Sine frequency editors
        // -------------------------------------------------
        private void RebuildSineFrequencyEditors()
        {
            int count = (int)numUpDwnChnlsCnt.Value;
            while (_sineFrequenciesHz.Count < count)
                _sineFrequenciesHz.Add(8.0 + _sineFrequenciesHz.Count);

            UpdateSineFrequencyBounds();
            pnlSineFrequencies.SuspendLayout();
            try
            {
                while (pnlSineFrequencies.Controls.Count > 0)
                    pnlSineFrequencies.Controls[0].Dispose();
                _sineFrequencyEditors.Clear();
                pnlSineFrequencies.AutoScrollPosition = Point.Empty;

                decimal maximum = (decimal)GetMaximumSineFrequencyHz((int)numUpDwnSamplingRate.Value);
                for (int ch = 0; ch < count; ch++)
                {
                    var label = new Label
                    {
                        AutoSize = true,
                        Location = new Point(8, 7 + ch * 24),
                        Text = "Ch " + (ch + 1).ToString(CultureInfo.InvariantCulture)
                    };
                    var editor = new NumericUpDown
                    {
                        Name = "numUpDwnSineFrequency" + (ch + 1).ToString(CultureInfo.InvariantCulture),
                        Location = new Point(86, 4 + ch * 24),
                        Size = new Size(100, 20),
                        Minimum = (decimal)MinimumSineFrequencyHz,
                        Maximum = maximum,
                        DecimalPlaces = 1,
                        Increment = 0.1m,
                        Value = (decimal)_sineFrequenciesHz[ch],
                        Tag = ch,
                        TabIndex = ch
                    };
                    editor.ValueChanged += SineFrequency_ValueChanged;
                    _sineFrequencyEditors.Add(editor);
                    pnlSineFrequencies.Controls.Add(label);
                    pnlSineFrequencies.Controls.Add(editor);
                }
            }
            finally
            {
                pnlSineFrequencies.ResumeLayout();
            }
        }

        private void UpdateSineFrequencyBounds()
        {
            double maximum = GetMaximumSineFrequencyHz((int)numUpDwnSamplingRate.Value);
            _updatingSineFrequencyEditors = true;
            try
            {
                for (int ch = 0; ch < _sineFrequenciesHz.Count; ch++)
                {
                    _sineFrequenciesHz[ch] = Math.Round(Math.Max(MinimumSineFrequencyHz,
                        Math.Min(maximum, _sineFrequenciesHz[ch])), 1, MidpointRounding.AwayFromZero);
                    if (ch < _sineFrequencyEditors.Count)
                    {
                        _sineFrequencyEditors[ch].Maximum = (decimal)maximum;
                        _sineFrequencyEditors[ch].Value = (decimal)_sineFrequenciesHz[ch];
                    }
                }
            }
            finally
            {
                _updatingSineFrequencyEditors = false;
            }

            lblSineFrequencies.Text = string.Format(CultureInfo.CurrentCulture,
                "Sine frequencies, Hz (0.1 - {0:F1})", maximum);
            bool enabled = GetSelectedWaveForm() == WaveFormKind.Sine;
            pnlSineFrequencies.Enabled = enabled;
            lblSineFrequencies.Enabled = enabled;
        }

        private double[] GetSineFrequencies()
        {
            return _sineFrequenciesHz.GetRange(0, (int)numUpDwnChnlsCnt.Value).ToArray();
        }

        private void SineFrequency_ValueChanged(object sender, EventArgs e)
        {
            if (_updatingSineFrequencyEditors)
                return;

            var editor = (NumericUpDown)sender;
            decimal frequency = Math.Round(editor.Value, 1, MidpointRounding.AwayFromZero);
            if (editor.Value != frequency)
            {
                editor.Value = frequency;
                return;
            }

            _sineFrequenciesHz[(int)editor.Tag] = (double)frequency;
            if (_isRunning && GetSelectedWaveForm() == WaveFormKind.Sine)
                _generator.SetSineFrequencies(GetSineFrequencies());
        }

        private void numUpDwnChnlsCnt_ValueChanged(object sender, EventArgs e)
        {
            RebuildSineFrequencyEditors();
        }

        private void numUpDwnSamplingRate_ValueChanged(object sender, EventArgs e)
        {
            UpdateSineFrequencyBounds();
        }

        private void cbWaveForm_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateSineFrequencyBounds();
        }

        private void SetGeneratorConfigurationEnabled(bool enabled)
        {
            numUpDwnChnlsCnt.Enabled = enabled;
            numUpDwnSamplingRate.Enabled = enabled;
            numUpDwnBlockSize.Enabled = enabled;
            cbWaveForm.Enabled = enabled;
        }

        // -------------------------------------------------
        // Start / stop
        // -------------------------------------------------
        private void btnStartGenerator_Click(object sender, EventArgs e)
        {
            StopGeneratorInternal();

            int channels = (int)numUpDwnChnlsCnt.Value;
            int samplingRate = (int)numUpDwnSamplingRate.Value;
            int blockSize = (int)numUpDwnBlockSize.Value;
            WaveFormKind waveForm = GetSelectedWaveForm();

            _generator.Configure(channels, samplingRate, blockSize, waveForm, GetSineFrequencies());

            string[] channelNames = new string[_generator.ChannelsCount];
            for (int ch = 0; ch < _generator.ChannelsCount; ch++)
            {
                channelNames[ch] =
                    _generator.Channels[ch].Name;
            }

            lock (_ringSync)
            {
                if (_processingBlockRing != null)
                {
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

            ServiceDataPortManifest outputManifest = GetPrimaryServiceDataOutputManifest();

            var newRing = new ProcessingBlockRing(
                outputManifest.ServiceDataPortMapName,
                outputManifest.ServiceDataPortReadyEventName,
                channels,
                samplingRate,
                blockSize,
                ProcessingRingBlockCount,
                channelNames);

            newRing.UpdateRunning(true);

            lock (_ringSync)
            {
                _processingBlockRing = newRing;
            }

            ResetDisplayState(channels, samplingRate);
            ClearPendingBlocks();

            if (_enableLocalDisplayCache && _glInitialized)
            {
                glCtrl.MakeCurrent();
                RecreateWavePages();
            }

            _generatedSamplesCount = 0;
            _timerBusy = 0;
            _lastShownDroppedBlocks = 0;

            _pageShiftPx = 0.0f;
            _visualPageSeq = 0;

            QueryPerformanceCounter(out _qpcStart);

            InvalidateOverlay();
            UpdateDroppedBlocksStatusSafe(0);

            if (_enableLocalDisplayCache && _glInitialized)
            {
                glCtrl.MakeCurrent();
                EnsureWavePages();
                UpdatePageDxPx();
            }

            if (!_timePeriodActive)
            {
                timeBeginPeriod(1);
                _timePeriodActive = true;
            }

            mHandler = new TimerEventHandler(TimerCallback);

            if (mTimerId != 0)
                timeKillEvent(mTimerId);

            mTimerId = timeSetEvent(UiTimerPeriodMs, 2, mHandler, IntPtr.Zero, EVENT_TYPE);

            _isRunning = true;
            SetGeneratorConfigurationEnabled(false);
            PublishServiceState();

            if (_enableLocalDisplayCache)
                RenderFrame();
        }

        private void btnStopGenerator_Click(object sender, EventArgs e)
        {
            StopGeneratorInternal();
            PublishServiceState();
            glCtrl.Invalidate();
        }

        private void StopGeneratorInternal()
        {
            _isRunning = false;
            SetGeneratorConfigurationEnabled(true);

            if (mTimerId != 0)
            {
                int timerId = mTimerId;
                mTimerId = 0;
                timeKillEvent(timerId);
            }

            lock (_ringSync)
            {
                if (_processingBlockRing != null)
                {
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

            if (_timePeriodActive)
            {
                timeEndPeriod(1);
                _timePeriodActive = false;
            }
        }

        // -------------------------------------------------
        // MM timer callback
        // -------------------------------------------------
        private void TimerCallback(int id, int msg, IntPtr user, int dw1, int dw2)
        {
            if (!_isRunning || _generator == null)
                return;

            if (Interlocked.Exchange(ref _timerBusy, 1) != 0)
                return;

            try
            {
                long qpcNow;
                QueryPerformanceCounter(out qpcNow);

                double elapsedSec = (double)(qpcNow - _qpcStart) / _QpcFreq;
                long expectedSamples = (long)Math.Floor(elapsedSec * _generator.SamplingRate);
                long missingSamples = expectedSamples - _generatedSamplesCount;

                ClearPendingBlocks();

                if (missingSamples > 0)
                {
                    while (missingSamples > 0)
                    {
                        int samplesToGenerate = (int)Math.Min(missingSamples, _generator.BlockSize);
                        long firstAbsoluteSample = _generatedSamplesCount;

                        float[] blockChannelMajor = _generator.GenerateSamples(samplesToGenerate);

                        int droppedBlocks = 0;

                        lock (_ringSync)
                        {
                            if (_processingBlockRing != null)
                            {
                                _processingBlockRing.PublishBlock(
                                    blockChannelMajor,
                                    samplesToGenerate,
                                    qpcNow);

                                droppedBlocks = _processingBlockRing.ReadHeader().DroppedBlockCount;
                            }
                        }

                        if (_enableLocalDisplayCache)
                        {
                            _pendingBlocks.Add(new PendingBlock
                            {
                                Samples = blockChannelMajor,
                                SamplesCount = samplesToGenerate,
                                FirstAbsoluteSample = firstAbsoluteSample,
                                ChannelsCount = _generator.ChannelsCount
                            });
                        }

                        if (droppedBlocks != _lastShownDroppedBlocks)
                        {
                            _lastShownDroppedBlocks = droppedBlocks;
                            UpdateDroppedBlocksStatusSafe(droppedBlocks);
                        }

                        _generatedSamplesCount += samplesToGenerate;
                        missingSamples -= samplesToGenerate;
                    }
                }

                if (_enableLocalDisplayCache && glCtrl.IsHandleCreated && !glCtrl.IsDisposed)
                {
                    this.Invoke(fmNotifyDrawFrame);
                }
            }
            catch
            {
            }
            finally
            {
                Interlocked.Exchange(ref _timerBusy, 0);
            }
        }

        // -------------------------------------------------
        // UI draw delegate target
        // -------------------------------------------------
        private void DrawFrameFromTimer()
        {
            if (!_isRunning)
                return;

            if (_enableLocalDisplayCache)
            {
                EnsureWavePages();
                RenderPendingBlocksToPages();
                AdvanceVisualPhase();
            }

            RenderFrame();
        }

        private void RenderPendingBlocksToPages()
        {
            if (_pendingBlocks.Count == 0)
                return;

            for (int i = 0; i < _pendingBlocks.Count; i++)
            {
                PendingBlock block = _pendingBlocks[i];

                RenderBlockToPages(
                    block.Samples,
                    block.ChannelsCount,
                    block.SamplesCount,
                    block.FirstAbsoluteSample);
            }

            _pendingBlocks.Clear();
        }

        private void RenderBlockToPages(float[] blockChannelMajor, int channelsCount, int sampleCount, long firstAbsoluteSample)
        {
            if (blockChannelMajor == null || blockChannelMajor.Length == 0)
                return;

            if (channelsCount <= 0 || sampleCount <= 0 || _viewSamplesPerChannel <= 1)
                return;

            int boundSlot = -1;

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

        /*        private void AdvanceVisualPhase()
                {
                    _pageShiftPx += _pageDxPx;

                    if (_pageW > 0)
                    {
                        while (_pageShiftPx >= _pageW)
                        {
                            _pageShiftPx -= _pageW;
                            _visualPageSeq++;
                        }
                    }
                }*/
        private void AdvanceVisualPhase()
        {
            float pageSpanPx = _pageW > 1 ? (_pageW - 1.0f) : 1.0f;

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

            _bufferChannelsCount = channelsCount;

            _viewSamplesPerChannel = samplingRate * _timeBaseSeconds;
            if (_viewSamplesPerChannel < 100)
                _viewSamplesPerChannel = 100;

            EnsurePrevPoints();
            ResetPrevPoints();
        }

        private void ClearPendingBlocks()
        {
            _pendingBlocks.Clear();
        }

        /*        private void UpdatePageDxPx()
                {
                    float pageWidthPx = _pageW > 1
                        ? _pageW
                        : Math.Max(1.0f, GetPlotRectangle().Width);

                    float ticksPerScreen = (_timeBaseSeconds * 1000.0f) / UiTimerPeriodMs;
                    if (ticksPerScreen < 1.0f)
                        ticksPerScreen = 1.0f;

                    _pageDxPx = pageWidthPx / ticksPerScreen;
                }*/
        private void UpdatePageDxPx()
        {
            float pageSpanPx = _pageW > 1 ? (_pageW - 1.0f) : 1.0f;

            float ticksPerScreen = (_timeBaseSeconds * 1000.0f) / UiTimerPeriodMs;
            if (ticksPerScreen < 1.0f)
                ticksPerScreen = 1.0f;

            _pageDxPx = pageSpanPx / ticksPerScreen;
        }

        // -------------------------------------------------
        // Generator / UI parameter helpers
        // -------------------------------------------------
        private WaveFormKind GetSelectedWaveForm()
        {
            string text = cbWaveForm.SelectedItem != null
                ? cbWaveForm.SelectedItem.ToString()
                : "AsEEG";

            if (string.Equals(text, "Sine", StringComparison.OrdinalIgnoreCase))
                return WaveFormKind.Sine;

            if (string.Equals(text, "AsEEG", StringComparison.OrdinalIgnoreCase))
                return WaveFormKind.AsEEG;

            return WaveFormKind.AsEEG;
        }

        private float ParseAmplitudeScaleVolts(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 100e-6f;

            string s = text.Trim()
                .Replace("μ", "µ")
                .Replace(",", ".");

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

            EnsureWavePages();

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            SetupViewport();
            GL.Clear(ClearBufferMask.ColorBufferBit);

            DrawWavePages();
            DrawOverlayTexture();

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

                int channelsCount = _bufferChannelsCount > 0
                    ? _bufferChannelsCount
                    : Math.Max(1, (int)numUpDwnChnlsCnt.Value);

                SignalGeneratorEngine.ChannelInfo[] channels = _generator != null ? _generator.Channels : null;
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
                    string channelName = "Ch " + (ch + 1).ToString();

                    if (channels != null &&
                        ch < channels.Length &&
                        !string.IsNullOrEmpty(channels[ch].Name))
                    {
                        channelName = channels[ch].Name;
                    }

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

            var data = _overlayBitmap.LockBits(
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
        // UI events
        // -------------------------------------------------
        private void cmbBxScaleAmpl_SelectedIndexChanged(object sender, EventArgs e)
        {
            _displayScaleVolts = ParseAmplitudeScaleVolts(cmbBxScaleAmpl.Text);

            if (_enableLocalDisplayCache && _glInitialized)
            {
                glCtrl.MakeCurrent();
                RecreateWavePages();

                _pageShiftPx = 0.0f;
                _visualPageSeq = 0;
                _generatedSamplesCount = 0;
                QueryPerformanceCounter(out _qpcStart);

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

            int samplingRate = 250;
            if (_generator != null && _generator.SamplingRate > 0)
                samplingRate = _generator.SamplingRate;
            else if (numUpDwnSamplingRate.Value > 0)
                samplingRate = (int)numUpDwnSamplingRate.Value;

            int channelsCount = 1;
            if (_bufferChannelsCount > 0)
                channelsCount = _bufferChannelsCount;
            else if (numUpDwnChnlsCnt.Value > 0)
                channelsCount = (int)numUpDwnChnlsCnt.Value;

            ResetDisplayState(channelsCount, samplingRate);
            ClearPendingBlocks();

            _generatedSamplesCount = 0;
            _pageShiftPx = 0.0f;
            _visualPageSeq = 0;

            QueryPerformanceCounter(out _qpcStart);

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

            RecreateWavePages();
            ClearPendingBlocks();

            InvalidateOverlay();
            RenderFrame();
        }

        // -------------------------------------------------
        // Status
        // -------------------------------------------------
        private void UpdateDroppedBlocksStatusSafe(int droppedBlocks)
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            BeginInvoke((Action)(() =>
            {
                string text = "Dropped blocks: " + droppedBlocks.ToString();

                if (statusStrpMain != null)
                {
                    if (statusStrpMain.Items.Count > 0)
                        statusStrpMain.Items[0].Text = text;
                    else
                        statusStrpMain.Text = text;
                }
            }));
        }
    }
}