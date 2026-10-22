using PolyHubIpc;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PolyHubNet
{
    public partial class fmMain : Form
    {
        private sealed class RouteDragInfo
        {
            public string SourceNodeId { get; set; }
            public string SourcePinId { get; set; }
            public string RouteDataType { get; set; }
            public string DisplayText { get; set; }
        }

        private const string RouteDataTypeSignal = "Signal";

        private const string NodeIdGainProcessor = "Processor:GainProcessor";
        private const string NodeIdSsvepProcessor = "Processor:SsvepProcessor";
        private const string NodeIdSignalViewer = "Viewer:SignalViewer";

        private const string PinIdIn = "In";
        private const string PinIdOut = "Out";

        private SignalStream _gainOutputStream;
        private SignalStream _ssvepOutputStream;

        private GainProcessor _gainProcessor;
        private SsvepProcessor _ssvepProcessor;
        private GlScope _glScope;
        private TimeFrequencyGlView _timeFrequencyGlView;
        private AnalysisGlView _analysisGlView;
        private RessSpatialGlView _ressSpatialGlView;
        private EpochInfo _spectrumEpoch;
        private bool _bindingSpectrumClass;
        private bool _bindingEogRejectionUi;
        private CovertCalibrationEog _covertCalibrationEog;
        private ServiceSlot _covertCalibrationService;
        private long _covertCalibrationStartQpc, _covertCalibrationEndQpc;
        private int _neuroPianoCalibrationLevel;
        private int _spectrumRequest;

        private readonly List<ServiceSlot> _serviceSlotList = new List<ServiceSlot>();

        private ServiceSlot _selectedServiceSlot;
        private ServiceDataOutputSlot _selectedServiceDataOutputSlot;

        private PolyHubSettings _polyHubSettings;

        private string _polyHubRootFolder;

        private PolyHubSettingsStore _polyHubSettingsStore;
        private ServiceCatalog _serviceCatalog;
        private RouteStore _routeStore;
        private SignalProcessorCatalog _signalProcessorCatalog;

        private const int TIME_PERIODIC = 1;
        private const int UiTimerPeriodMs = 5;

        private int _uiTimerId;
        private TimerEventHandler _uiTimerHandler;
        private bool _timePeriodActive;

        private long _qpcFreq;
        private long _lastStatusUpdateQpc;

        private readonly object _statusSync = new object();
        private string _readerStatusText = "Reader: disconnected";
        private string _processorStatusText = "Processor dropped latest blocks: N/A";

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

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

        private delegate void TimerEventHandler(int id, int msg, IntPtr user, int dw1, int dw2);

        private SessionRecorder _sessionRecorder;
        private SessionReader _sessionReader;
        private SessionPlayback _sessionPlayback;
        private SignalStream _playbackStream;

        private ServiceRuntimeController _serviceRuntimeController;
        private RouteRuntimeController _routeRuntimeController;

        private bool _viewerPlaybackMode;
        private bool _allowMouseToggle = true;

        private bool _playbackTrackBarDragging;
        private bool _playbackTrackBarInternalUpdate;
        private long _openedPlaybackStartQpc;
        private long _openedPlaybackEndQpc;

        private bool _ctrlsLock;

        private int _previewPageFirstBlockIndex = -1;
        private int _previewPageLastBlockIndex = -1;
        private long _previewPageStartQpc = -1;
        private int _previewPageSampleCount = 0;

        private bool _recordingUiActive;
        private long _recordingUiStartQpc;

        private const int DefaultViewerTimeBaseSeconds = 5;
        private const float DefaultViewerScaleVolts = 100e-6f;
        private const int DefaultTimeFrequencyTimeBaseSeconds = 5;
        private const float DefaultTimeFrequencyScaleVolts = 10e-6f;

        private const string SsvepModelFileName = "SsvepModel.json";
        private static readonly string SsvepModelsFolderRelativePath = Path.Combine("Models", "SSVEP");
        private const int SsvepTrainingWindowMilliseconds = 1000;
        private const int SsvepTrainingStepMilliseconds = 250;
        private const int SsvepTrainingHarmonicCount = 2;

        private string _openedRecordingFilePath;
        private bool _ssvepTraining;
        private bool _ssvepControlEnabled;
        private string _activeSsvepModelPath;

        public fmMain()
        {
            InitializeComponent();
        }


        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F6)
            {
                SendNeuroPianoCommand(CovertCalibrationEog.StartCommand, 0);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Инициализирует конфигурацию, контроллеры, процессорный pipeline и UI-привязки формы.
        private void fmMain_Load(object sender, EventArgs e)
        {
            QueryPerformanceFrequency(out _qpcFreq);
            QueryPerformanceCounter(out _lastStatusUpdateQpc);

            _polyHubSettingsStore = new PolyHubSettingsStore(Application.StartupPath);
            _polyHubRootFolder = _polyHubSettingsStore.FindPolyHubRootFolder();

            _polyHubSettings = _polyHubSettingsStore.LoadOrCreate();
            EnsureViewerSettings();
            _routeStore = new RouteStore(_polyHubSettings);

            _signalProcessorCatalog = new SignalProcessorCatalog();

            _polyHubSettingsStore.ApplyWindowSettings(this, _polyHubSettings);

            _gainOutputStream = new SignalStream("GainProcessor.Out");
            _ssvepOutputStream = new SignalStream("SsvepProcessor.Out");

            _gainProcessor = new GainProcessor("Gain x2", 2.0f, 64);
            _gainProcessor.InputDroppedBlockCountChanged += SignalProcessor_InputDroppedBlockCountChanged;
            _gainProcessor.SetOutput(_gainOutputStream);

            _signalProcessorCatalog.Add(new SignalProcessorSlot
            {
                ProcessorNodeId = NodeIdGainProcessor,
                ProcessorDisplayName = "GainProcessor",
                InputPinId = PinIdIn,
                OutputPinId = PinIdOut,
                SignalProcessor = _gainProcessor,
                ProcessorOutputStream = _gainOutputStream
            });

            _ssvepProcessor = new SsvepProcessor("SSVEP", 64, _qpcFreq);
            _ssvepProcessor.InputDroppedBlockCountChanged += SignalProcessor_InputDroppedBlockCountChanged;
            _ssvepProcessor.SetOutput(_ssvepOutputStream);
            _ssvepProcessor.AnalysisReady += SsvepProcessor_AnalysisReady;

            _signalProcessorCatalog.Add(new SignalProcessorSlot
            {
                ProcessorNodeId = NodeIdSsvepProcessor,
                ProcessorDisplayName = "SsvepProcessor",
                InputPinId = PinIdIn,
                OutputPinId = PinIdOut,
                SignalProcessor = _ssvepProcessor,
                ProcessorOutputStream = _ssvepOutputStream
            });

            _glScope = new GlScope(glCtrlViewer);
            _glScope.SetUiTimerPeriodMs(UiTimerPeriodMs);
            _glScope.AttachInput(_gainOutputStream);
            _glScope.Configure(
                0,
                500,
                GetViewerTimeBaseSeconds(),
                GetViewerScaleVolts());

            _timeFrequencyGlView = new TimeFrequencyGlView(glCtrlTimeFreqViewer, _qpcFreq);
            _timeFrequencyGlView.SetUiTimerPeriodMs(UiTimerPeriodMs);
            _timeFrequencyGlView.AttachInput(_gainOutputStream);
            _timeFrequencyGlView.Configure(
                0,
                500,
                GetTimeFrequencyTimeBaseSeconds(),
                GetTimeFrequencyScaleVolts());

            _analysisGlView = new AnalysisGlView(glCtrlAnalysis);
            _ressSpatialGlView = new RessSpatialGlView(glCtrlAnalysis2);
            InitializeAnalysis2Ui();
            InitializeSsvepModelUi();
            InitializeEogRejectionUi();
            TryLoadSsvepModel();

            _sessionRecorder = new SessionRecorder(128, 64L * 1024L * 1024L, 64L * 1024L * 1024L);
            _sessionReader = new SessionReader();
            _sessionPlayback = new SessionPlayback(128, 512);
            _playbackStream = new SignalStream("Playback");

            _serviceCatalog = new ServiceCatalog(_polyHubRootFolder);
            _serviceSlotList.Clear();
            _serviceSlotList.AddRange(_serviceCatalog.LoadServiceSlots(_polyHubSettings));

            _serviceRuntimeController = new ServiceRuntimeController(
                _polyHubRootFolder,
                panelHost,
                _polyHubSettings,
                _serviceCatalog,
                _serviceSlotList,
                OnServiceEvtReceived);

            _routeRuntimeController = new RouteRuntimeController(
                _routeStore,
                _serviceCatalog,
                _signalProcessorCatalog,
                _serviceSlotList,
                _gainOutputStream,
                _glScope,
                _timeFrequencyGlView,
                GetViewerTimeBaseSeconds,
                GetViewerScaleVolts,
                GetTimeFrequencyTimeBaseSeconds,
                GetTimeFrequencyScaleVolts,
                SetReaderStatusText,
                SetProcessorStatusText,
                FormatSourceNodePinText,
                NodeIdSignalViewer,
                PinIdIn);

            BuildTreeViewServices();
            BuildTreeViewPipeline();
            RestoreSelectedServiceNode();
            ConfigureDragAndDropUi();
            UpdateViewerInputPinText();

            timeBeginPeriod(1);
            _timePeriodActive = true;

            _uiTimerHandler = new TimerEventHandler(UiTimerCallback);
            _uiTimerId = timeSetEvent(UiTimerPeriodMs, 2, _uiTimerHandler, IntPtr.Zero, TIME_PERIODIC);

            trackBarPlayback.Minimum = 0;
            trackBarPlayback.Maximum = 1000;
            trackBarPlayback.Value = 0;
            trackBarPlayback.Enabled = false;

            lblPlaybackCurrentTime.Text = "00:00.000";
            lblPlaybackTotalTime.Text = "00:00.000";

            _recordingUiActive = false;
            _recordingUiStartQpc = 0;

            trackBarPlayback.MouseDown -= trackBarPlayback_MouseDown;
            trackBarPlayback.MouseDown += trackBarPlayback_MouseDown;

            trackBarPlayback.MouseUp -= trackBarPlayback_MouseUp;
            trackBarPlayback.MouseUp += trackBarPlayback_MouseUp;

            trackBarPlayback.Scroll -= trackBarPlayback_Scroll;
            trackBarPlayback.Scroll += trackBarPlayback_Scroll;

            InitializeViewerScaleAndTimeUi();
            InitializeTimeFrequencyScaleAndTimeUi();
        }

        private void InitializeAnalysis2Ui()
        {
            cmbBxAnalysis2SensAmpl.SelectedIndex = 4; // Same scale as option 5 in TimeFreq.
            cmbBxAnalysis2Mode.SelectedIndex = 1;
            dataGridViewEpochs.CellClick += dataGridViewEpochs_SpectrumCellClick;
            listBoxEpochClasses.MouseClick += listBoxEpochClasses_SpectrumMouseClick;
            if (cmbBxAnalysis2View.Items.Count > 0)
                cmbBxAnalysis2View.SelectedIndex = 0;

            if (cmbBxAnalysis2Component.Items.Count > 0)
                cmbBxAnalysis2Component.SelectedIndex = 0;
        }

        private void cmbBxAnalysis2SensAmpl_SelectedIndexChanged(object sender, EventArgs e)
        {
            int index = cmbBxAnalysis2SensAmpl.SelectedIndex;
            if (index >= 0 && _ressSpatialGlView != null)
                _ressSpatialGlView.SetSpectrumScaleVolts(GetTimeFrequencyScale(index));
        }

        private void cmbBxAnalysis2Mode_SelectedIndexChanged(object sender, EventArgs e)
        {
            bool spectra = cmbBxAnalysis2Mode.SelectedIndex == 1;
            cmbBxAnalysis2View.Enabled = !spectra;
            cmbBxAnalysis2Component.Enabled = !spectra;
            lblAnalysis2View.Enabled = !spectra;
            lblAnalysis2Component.Enabled = !spectra;
            cmbBxAnalysis2SensAmpl.Enabled = spectra;
            lblAnalysis2SensAmpl.Enabled = spectra;
            if (_ressSpatialGlView == null) return;
            _ressSpatialGlView.SetSpectrumMode(spectra);
            RefreshSpatialSpectra();
        }

        private void listBoxEpochClasses_SpectrumMouseClick(object sender, MouseEventArgs e)
        {
            int index = listBoxEpochClasses.IndexFromPoint(e.Location);
            if (index < 0 || index != listBoxEpochClasses.SelectedIndex || _spectrumEpoch == null) return;
            _spectrumEpoch = null;
            RefreshSpatialSpectra();
        }

        private void dataGridViewEpochs_SpectrumCellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || _bindingSpectrumClass) return;
            var epoch = dataGridViewEpochs.Rows[e.RowIndex].DataBoundItem as EpochInfo;
            if (ReferenceEquals(_spectrumEpoch, epoch)) return;
            _spectrumEpoch = epoch;
            RefreshSpatialSpectra();
        }

        private async void RefreshSpatialSpectra()
        {
            int request = System.Threading.Interlocked.Increment(ref _spectrumRequest);
            if (_ressSpatialGlView == null) return;
            _ressSpatialGlView.SetSpectra(null, null, "Select a recorded class");
            var summary = listBoxEpochClasses.SelectedItem as EpochClassSummary;
            if (cmbBxAnalysis2Mode.SelectedIndex != 1 || _sessionReader == null ||
                summary == null || string.IsNullOrEmpty(_openedRecordingFilePath)) return;
            string file = _openedRecordingFilePath;
            int classId = summary.ClassId;
            EpochInfo selectedEpoch = _spectrumEpoch;
            EogRejection spectrumRejection = GetActiveEogRejection();
            _ressSpatialGlView.SetSpectra(null, null, "Calculating spectra...");
            try
            {
                var result = await Task.Run(() =>
                {
                    // Own reader: opening/closing playback cannot invalidate this job.
                    using (var reader = new SessionReader())
                    {
                        reader.Open(file);
                        var epochs = selectedEpoch != null
                            ? new List<EpochInfo> { selectedEpoch } : reader.GetEpochsByClass(classId);
                        double[] sum = new double[reader.Header.ChannelCount * TimeFrequencySpectrum.FrequencyRows];
                        int count = 0;
                        int minWindowSamples = int.MaxValue;
                        int maxWindowSamples = 0;
                        foreach (var epoch in epochs)
                        {
                            if (System.Threading.Volatile.Read(ref _spectrumRequest) != request) return null;
                            SignalBlock epochBlock = reader.ReadEpochAsSingleBlock(epoch);
                            if (selectedEpoch == null && CheckRecordedEpoch(reader, epoch, spectrumRejection, epochBlock).IsRejected) continue;
                            float[] amplitudes = TimeFrequencySpectrum.CalculateEpoch(epochBlock);
                            if (amplitudes == null) continue;
                            minWindowSamples = Math.Min(minWindowSamples, epochBlock.SampleCount);
                            maxWindowSamples = Math.Max(maxWindowSamples, epochBlock.SampleCount);
                            for (int i = 0; i < sum.Length; i++) sum[i] += amplitudes[i];
                            count++;
                        }
                        float[] average = count > 0 ? new float[sum.Length] : null;
                        if (average != null)
                            for (int i = 0; i < sum.Length; i++) average[i] = (float)(sum[i] / count);
                        string status = selectedEpoch != null ? "Epoch " + selectedEpoch.EpochId
                            : "Class " + classId + " · " + count + " epochs";
                        if (count == 0) status = "No samples available for spectrum calculation";
                        string windowText = "";
                        if (count > 0)
                        {
                            string minMs = (minWindowSamples * 1000.0 / reader.Header.SampleRate).ToString("0", CultureInfo.InvariantCulture);
                            string maxMs = (maxWindowSamples * 1000.0 / reader.Header.SampleRate).ToString("0", CultureInfo.InvariantCulture);
                            windowText = minMs == maxMs ? minMs + " ms" : minMs + "–" + maxMs + " ms";
                        }
                        return Tuple.Create(average, (string[])reader.ChannelNames.Clone(), status, reader.Header.SampleRate, windowText);
                    }
                });
                if (request != _spectrumRequest || IsDisposed || _ressSpatialGlView == null || result == null) return;
                _ressSpatialGlView.SetSpectra(result.Item1, result.Item2, result.Item3, result.Item4, result.Item5);
            }
            catch (Exception ex)
            {
                if (request == _spectrumRequest && !IsDisposed && _ressSpatialGlView != null)
                    _ressSpatialGlView.SetSpectra(null, null, "Spectrum: " + ex.Message);
            }
        }

        private void cmbBxAnalysis2View_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_ressSpatialGlView == null)
                return;

            RessSpatialViewMode viewMode =
                cmbBxAnalysis2View.SelectedIndex == 1
                    ? RessSpatialViewMode.Topography
                    : RessSpatialViewMode.Grid;

            _ressSpatialGlView.SetViewMode(viewMode);
        }

        private void cmbBxAnalysis2Component_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_ressSpatialGlView == null)
                return;

            RessSpatialComponent component =
                cmbBxAnalysis2Component.SelectedIndex == 1
                    ? RessSpatialComponent.SecondHarmonic
                    : RessSpatialComponent.BaseFrequency;

            _ressSpatialGlView.SetComponent(component);
        }

        private void InitializeSsvepModelUi()
        {
            prgSsvepTraining.Minimum = 0;
            prgSsvepTraining.Maximum = 100;
            prgSsvepTraining.Value = 0;

            lblSsvepActiveModel.Text = "Active: none";
            lblSsvepModelInfo.Text = "No model selected";

            Directory.CreateDirectory(GetSsvepModelsFolder());
            RefreshSsvepModelList();

            AppendSsvepStatus("Ready.");
            UpdateSsvepModelButtons();
            UpdateCalibrationButtons();
        }

        private void btnSsvepCalbr1_Click(object sender, EventArgs e)
        {
            SendNeuroPianoCommand(_neuroPianoCalibrationLevel == 1 ? 2002 : 2001, 0);
        }

        private void btnSsvepCalbr2_Click(object sender, EventArgs e)
        {
            SendNeuroPianoCommand(_neuroPianoCalibrationLevel == 2
                ? 2002 : CovertCalibrationEog.StartCommand, 0);
        }

        private void UpdateCalibrationButtons()
        {
            ServiceSlot piano = _serviceSlotList.Find(s => s.ServiceManifest != null &&
                string.Equals(s.ServiceManifest.ServiceId, "NeuroPiano", StringComparison.OrdinalIgnoreCase));
            bool available = piano != null && piano.ServiceIsRunning;
            btnSsvepCalbr1.Enabled = available;
            btnSsvepCalbr2.Enabled = available;
            btnSsvepCalbr1.Text = _neuroPianoCalibrationLevel == 1 ? "Stop1" : "Calbr1";
            btnSsvepCalbr2.Text = _neuroPianoCalibrationLevel == 2 ? "Stop2" : "Calbr2";
        }

        private void HandleNeuroPianoCalibrationState(ServiceSlot service, ServiceEventPacket evt)
        {
            if ((ServiceEventCode)evt.Code != ServiceEventCode.State ||
                service.ServiceManifest == null ||
                !string.Equals(service.ServiceManifest.ServiceId, "NeuroPiano", StringComparison.OrdinalIgnoreCase)) return;

            int level = evt.Value0 == 2001 ? 1
                : evt.Value0 == CovertCalibrationEog.StartCommand ? 2 : 0;
            if (level != 0 && evt.State != 0)
            {
                bool running = evt.Value1 != 0;
                if ((running && _neuroPianoCalibrationLevel != level) ||
                    (!running && _neuroPianoCalibrationLevel == level))
                {
                    _neuroPianoCalibrationLevel = running ? level : 0;
                    string text = string.IsNullOrWhiteSpace(evt.Text)
                        ? "Calibration " + level + (running ? " started." : " stopped.")
                        : evt.Text;
                    if (running && level == 2) text += " EOG profile: Covert Attention.";
                    AppendSsvepStatus(text);
                }
            }
            else if (evt.State == 0 && _neuroPianoCalibrationLevel != 0)
            {
                AppendSsvepStatus("Calibration " + _neuroPianoCalibrationLevel +
                    " stopped (NeuroPiano service stopped).");
                _neuroPianoCalibrationLevel = 0;
                StopCovertCalibrationMonitor();
            }
            UpdateCalibrationButtons();
        }

        private string GetSsvepModelsFolder()
        {
            return Path.Combine(Application.StartupPath, SsvepModelsFolderRelativePath);
        }

        private void RefreshSsvepModelList()
        {
            string selectedPath = GetSelectedSsvepModelPath();
            string folder = GetSsvepModelsFolder();
            Directory.CreateDirectory(folder);

            string[] files = Directory.GetFiles(folder, "*.json", SearchOption.TopDirectoryOnly);
            Array.Sort(files, delegate (string a, string b)
            {
                return File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a));
            });

            lstViewSsvepModels.BeginUpdate();
            try
            {
                lstViewSsvepModels.Items.Clear();

                for (int i = 0; i < files.Length; i++)
                {
                    string filePath = files[i];
                    ListViewItem item = new ListViewItem(Path.GetFileNameWithoutExtension(filePath));
                    item.SubItems.Add(File.GetLastWriteTime(filePath).ToString("yyyy-MM-dd HH:mm"));
                    item.Tag = filePath;
                    lstViewSsvepModels.Items.Add(item);

                    if (!string.IsNullOrWhiteSpace(selectedPath) &&
                        string.Equals(selectedPath, filePath, StringComparison.OrdinalIgnoreCase))
                    {
                        item.Selected = true;
                    }
                }
            }
            finally
            {
                lstViewSsvepModels.EndUpdate();
            }

            UpdateSelectedSsvepModelInfo();
            UpdateSsvepModelButtons();
        }

        private string GetSelectedSsvepModelPath()
        {
            if (lstViewSsvepModels.SelectedItems.Count != 1)
                return null;

            return lstViewSsvepModels.SelectedItems[0].Tag as string;
        }

        private void UpdateSelectedSsvepModelInfo()
        {
            string path = GetSelectedSsvepModelPath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                lblSsvepModelInfo.Text = "No model selected";
                return;
            }

            try
            {
                SsvepModel model = SsvepModel.Load(path);
                double windowSeconds = (double)model.WindowSamples / model.SampleRate;
                double stepSeconds = (double)model.StepSamples / model.SampleRate;

                lblSsvepModelInfo.Text =
                    model.ClassCount.ToString() + " classes | " +
                    model.ChannelNames.Length.ToString() + " ch | " +
                    model.SampleRate.ToString() + " Hz | " +
                    windowSeconds.ToString("0.##") + "/" + stepSeconds.ToString("0.##") + " s | " +
                    model.FeatureCount.ToString() + " features";
            }
            catch (Exception ex)
            {
                lblSsvepModelInfo.Text = "Invalid model: " + ex.Message;
            }
        }

        private void UpdateSsvepModelButtons()
        {
            bool selected = lstViewSsvepModels.SelectedItems.Count == 1;

            btnSsvepTrain.Enabled =
                !_ssvepTraining &&
                !string.IsNullOrWhiteSpace(_openedRecordingFilePath) &&
                File.Exists(_openedRecordingFilePath);

            btnSsvepUse.Enabled =
                !_ssvepTraining &&
                (_ssvepControlEnabled || selected);

            bool selectedIsActive = !selected || string.Equals(
                GetSelectedSsvepModelPath(), _activeSsvepModelPath, StringComparison.OrdinalIgnoreCase);
            btnSsvepUse.Text = _ssvepControlEnabled && selectedIsActive ? "Unuse" : "Use";

            btnSsvepDelete.Enabled = !_ssvepTraining && selected;
        }

        private void AppendSsvepStatus(string text)
        {
            if (rtbSsvepStatus == null || string.IsNullOrWhiteSpace(text))
                return;

            if (rtbSsvepStatus.TextLength > 0)
                rtbSsvepStatus.AppendText(Environment.NewLine);

            rtbSsvepStatus.AppendText(text);
            rtbSsvepStatus.SelectionStart = rtbSsvepStatus.TextLength;
            rtbSsvepStatus.ScrollToCaret();
        }

        private void ReportSsvepTrainingProgress(int percent, string text)
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            BeginInvoke((Action)(() =>
            {
                int value = Math.Max(prgSsvepTraining.Minimum, Math.Min(prgSsvepTraining.Maximum, percent));
                prgSsvepTraining.Value = value;

                if (!string.IsNullOrWhiteSpace(text))
                    AppendSsvepStatus(text);
            }));
        }

        private void SetActiveSsvepModel(SsvepModel model, string modelPath)
        {
            if (model == null)
                throw new ArgumentNullException("model");

            _ssvepProcessor.SetModel(model);
            _activeSsvepModelPath = modelPath;
            _analysisGlView.SetModel(model);
            _ressSpatialGlView.SetModel(model);
            string displayName = string.IsNullOrWhiteSpace(modelPath)
                ? "memory"
                : Path.GetFileNameWithoutExtension(modelPath);

            lblSsvepActiveModel.Text = "Active: " + displayName;
            AppendSsvepStatus("Active model: " + displayName);

            EpochClassSummary summary = listBoxEpochClasses.SelectedItem as EpochClassSummary;
            _analysisGlView.SetSelectedClassId(summary != null ? (int?)summary.ClassId : null);
            _ressSpatialGlView.SetSelectedClassId(summary != null ? (int?)summary.ClassId : null);

            UpdateSsvepModelButtons();
        }

        private void ClearActiveSsvepModel()
        {
            _ssvepControlEnabled = false;
            _ssvepProcessor.ClearModel();
            _activeSsvepModelPath = null;
            _analysisGlView.SetModel(null);
            _ressSpatialGlView.SetModel(null);
            lblSsvepActiveModel.Text = "Active: none";
            AppendSsvepStatus("SSVEP model cleared.");
            UpdateSsvepModelButtons();
        }

        private void TryLoadSsvepModel()
        {
            if (_ssvepProcessor == null || _analysisGlView == null)
                return;

            // Backward-compatible single-file model from the first implementation.
            string modelPath = Path.Combine(Application.StartupPath, SsvepModelFileName);
            if (!File.Exists(modelPath))
            {
                _analysisGlView.SetModel(null);
                _ressSpatialGlView.SetModel(null);
                return;
            }

            try
            {
                SsvepModel model = SsvepModel.Load(modelPath);
                SetActiveSsvepModel(model, modelPath);
            }
            catch (Exception ex)
            {
                _ssvepProcessor.ClearModel();
                _analysisGlView.SetModel(null);
                _ressSpatialGlView.SetModel(null);
                _analysisGlView.SetStatus("SSVEP model error: " + ex.Message);
                AppendSsvepStatus("Model load error: " + ex.Message);
            }
        }

        private async void btnSsvepTrain_Click(object sender, EventArgs e)
        {
            if (_ssvepTraining)
                return;

            if (string.IsNullOrWhiteSpace(_openedRecordingFilePath) ||
                !File.Exists(_openedRecordingFilePath))
            {
                MessageBox.Show(this, "Open a calibration recording first.", "SSVEP training");
                return;
            }

            EogRejection trainingRejection = GetActiveEogRejection();
            string sourceFile = _openedRecordingFilePath;
            string modelFolder = GetSsvepModelsFolder();
            Directory.CreateDirectory(modelFolder);

            _ssvepTraining = true;
            prgSsvepTraining.Value = 0;
            rtbSsvepStatus.Clear();
            AppendSsvepStatus("Training source: " + Path.GetFileName(sourceFile));
            UpdateSsvepModelButtons();

            try
            {
                Tuple<SsvepModel, string> trained = await Task.Run(() =>
                {
                    using (SessionReader reader = new SessionReader())
                    {
                        reader.Open(sourceFile);

                        List<EpochClassSummary> summaries = reader.GetClassSummaries();
                        // NeuroPiano notes only. Class 1200 supplies negative epochs;
                        // feedback MIDI events and other markers are not note classes.
                        summaries.RemoveAll(s => s.ClassId < 1201 || s.ClassId > 1208);
                        ValidateSsvepCalibrationClasses(summaries);

                        int[] classIds = new int[summaries.Count];
                        double[] frequencies = new double[summaries.Count];
                        int trialCount = 0;

                        for (int i = 0; i < summaries.Count; i++)
                        {
                            classIds[i] = summaries[i].ClassId;

                            List<EpochInfo> epochs =
                                reader.GetEpochsByClass(classIds[i]);

                            if (epochs.Count == 0)
                            {
                                throw new InvalidOperationException(
                                    "SSVEP class " +
                                    classIds[i].ToString() +
                                    " has no epochs.");
                            }

                            int frequencyMilliHz = epochs[0].Value1;

                            if (frequencyMilliHz <= 0)
                            {
                                throw new InvalidOperationException(
                                    "SSVEP class " +
                                    classIds[i].ToString() +
                                    " has no target frequency.");
                            }

                            for (int j = 1; j < epochs.Count; j++)
                            {
                                if (epochs[j].Value1 != frequencyMilliHz)
                                {
                                    throw new InvalidOperationException(
                                        "SSVEP class " +
                                        classIds[i].ToString() +
                                        " contains inconsistent target frequencies.");
                                }
                            }

                            frequencies[i] =
                                frequencyMilliHz / 1000.0;

                            trialCount += summaries[i].Count;
                        }

                        string[] channelNames = SsvepChannelSelection.SelectEegChannels(reader.ChannelNames);

                        ReportSsvepTrainingProgress(
                            3,
                            "Calibration: " + summaries.Count.ToString() + " classes, " +
                            trialCount.ToString() + " trials, " +
                            channelNames.Length.ToString() + " channels, " +
                            reader.Header.SampleRate.ToString() + " Hz");

                        if (channelNames.Length > 32)
                        {
                            ReportSsvepTrainingProgress(
                                4,
                                "Warning: many channels; RESS training may be slow.");
                        }

                        SsvepTrainer trainer = new SsvepTrainer();
                        SsvepModel model = trainer.Train(
                            reader,
                            channelNames,
                            classIds,
                            frequencies,
                            SsvepTrainingWindowMilliseconds,
                            SsvepTrainingStepMilliseconds,
                            SsvepTrainingHarmonicCount,
                            ReportSsvepTrainingProgress,
                            trainingRejection);

                        string baseName = Path.GetFileNameWithoutExtension(sourceFile);
                        string fileName =
                            baseName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json";
                        string modelPath = Path.Combine(modelFolder, fileName);

                        model.Save(modelPath);
                        ReportSsvepTrainingProgress(100, "Saved: " + fileName);

                        return Tuple.Create(model, modelPath);
                    }
                });

                SetActiveSsvepModel(trained.Item1, trained.Item2);
                RefreshSsvepModelList();
                SelectSsvepModelInList(trained.Item2);
                AppendSsvepStatus("Ready.");

                AnalyzeCurrentSelectedEpochForSsvep();
            }
            catch (Exception ex)
            {
                AppendSsvepStatus("ERROR: " + ex.Message);
                MessageBox.Show(this, ex.Message, "SSVEP training", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _ssvepTraining = false;
                UpdateSsvepModelButtons();
            }
        }

        private static void ValidateSsvepCalibrationClasses(List<EpochClassSummary> summaries)
        {
            if (summaries == null || summaries.Count == 0)
            {
                throw new InvalidOperationException(
                    "SSVEP calibration must contain a target class (1201..1208).");
            }

            for (int i = 0; i < summaries.Count; i++)
            {
                if (summaries[i].Count <= 0)
                {
                    throw new InvalidOperationException(
                        "SSVEP class " +
                        summaries[i].ClassId.ToString() +
                        " has no trials.");
                }
            }
        }

        private void btnSsvepUse_Click(object sender, EventArgs e)
        {
            string selectedPath = GetSelectedSsvepModelPath();
            bool selectedIsActive = string.IsNullOrWhiteSpace(selectedPath) || string.Equals(
                selectedPath, _activeSsvepModelPath, StringComparison.OrdinalIgnoreCase);
            if (_ssvepControlEnabled && selectedIsActive)
            {
                _ssvepControlEnabled = false;

                SendNeuroPianoCommand(
                    2004,
                    0);

                if (_analysisGlView != null)
                    _analysisGlView.Clear();

                AppendSsvepStatus("SSVEP control disabled.");
                UpdateSsvepModelButtons();
                return;
            }

            UseSelectedSsvepModel();
        }

        private void lstViewSsvepModels_DoubleClick(object sender, EventArgs e)
        {
            UseSelectedSsvepModel();
        }

        private void UseSelectedSsvepModel()
        {
            string path = GetSelectedSsvepModelPath();
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                SsvepModel model = SsvepModel.Load(path);
                SetActiveSsvepModel(model, path);
                _ssvepControlEnabled = true;
                AppendSsvepStatus("SSVEP control enabled.");
                AnalyzeCurrentSelectedEpochForSsvep();
                UpdateSsvepModelButtons();
            }
            catch (Exception ex)
            {
                AppendSsvepStatus("Model load error: " + ex.Message);
                MessageBox.Show(this, ex.Message, "SSVEP model", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnSsvepDelete_Click(object sender, EventArgs e)
        {
            if (_ssvepTraining)
                return;

            string path = GetSelectedSsvepModelPath();
            if (string.IsNullOrWhiteSpace(path))
                return;

            string name = Path.GetFileNameWithoutExtension(path);
            if (MessageBox.Show(this, "Delete model \"" + name + "\"?", "Delete model",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            try
            {
                File.Delete(path);
                if (string.Equals(_activeSsvepModelPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    bool wasEnabled = _ssvepControlEnabled;
                    ClearActiveSsvepModel();
                    if (wasEnabled)
                        SendNeuroPianoCommand(2004, 0);
                }
                RefreshSsvepModelList();
                AppendSsvepStatus("Deleted model: " + name);
            }
            catch (Exception ex)
            {
                AppendSsvepStatus("Model delete error: " + ex.Message);
                MessageBox.Show(this, ex.Message, "Delete model", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void lstViewSsvepModels_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateSelectedSsvepModelInfo();
            UpdateSsvepModelButtons();
        }

        private void SelectSsvepModelInList(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
                return;

            for (int i = 0; i < lstViewSsvepModels.Items.Count; i++)
            {
                string path = lstViewSsvepModels.Items[i].Tag as string;
                if (!string.Equals(path, modelPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                lstViewSsvepModels.Items[i].Selected = true;
                lstViewSsvepModels.Items[i].EnsureVisible();
                break;
            }
        }

        private void SendSsvepKeyToNeuroPiano(SsvepAnalysisResult result)
        {
            if (!_ssvepControlEnabled ||
                result == null ||
                !result.HasPrediction ||
                _ssvepProcessor == null)
            {
                return;
            }

            SsvepModel model = _ssvepProcessor.Model;
            if (model == null)
                return;

            int keyIndex = result.PredictedClassId.Value - 1201;

            if (keyIndex < 0 || keyIndex >= 8)
                return;

            SendNeuroPianoCommand(
                2003,
                keyIndex);
        }

        private void SendNeuroPianoCommand(int command, int value)
        {
            ServiceSlot neuroPiano =
                _serviceCatalog.FindServiceSlot(
                    _serviceSlotList,
                    "NeuroPiano");

            if (neuroPiano == null)
                return;

            _serviceRuntimeController.EnsureServiceEvtLinkCreated(
                neuroPiano);

            if (neuroPiano.ServiceEvtLink == null)
                return;

            neuroPiano.ServiceEvtLink.SendEvent(
                ServiceEventCode.UserEvent,
                1,
                command,
                value,
                "SSVEP");
        }

        private void SsvepProcessor_AnalysisReady(SsvepAnalysisResult result)
        {
            if (result == null || IsDisposed || !IsHandleCreated)
                return;

            BeginInvoke((Action)(() =>
            {
                if (_covertCalibrationEog != null ||
                    (_covertCalibrationStartQpc > 0 && result.QpcTimestamp >= _covertCalibrationStartQpc &&
                     result.QpcTimestamp <= _covertCalibrationEndQpc)) return;
                HandleEogRejection(result);
                if (!_ssvepControlEnabled && !result.IsRejected)
                    return;

                if (_analysisGlView != null)
                    _analysisGlView.SetResult(result);

                SendSsvepKeyToNeuroPiano(result);
            }));
        }

        private void trackBarPlayback_MouseDown(object sender, MouseEventArgs e)
        {
            _playbackTrackBarDragging = true;
        }

        private void trackBarPlayback_MouseUp(object sender, MouseEventArgs e)
        {
            if (_sessionReader == null || _sessionReader.BlockCount <= 0)
            {
                _playbackTrackBarDragging = false;
                return;
            }

            long targetSourceQpc = ConvertTrackBarValueToPlaybackQpc(trackBarPlayback.Value);

            bool wasPlaying =
                _sessionPlayback != null &&
                _sessionPlayback.State == SessionPlaybackState.Playing;

            _playbackTrackBarDragging = false;

            if (_sessionPlayback != null)
                _sessionPlayback.Stop();

            if (wasPlaying)
                StartPlaybackFromSourceQpc(targetSourceQpc);
            else
                PreviewRecordingAtSourceQpc(targetSourceQpc);

            UpdatePlaybackUi();
        }

        private void trackBarPlayback_Scroll(object sender, EventArgs e)
        {
            if (_ctrlsLock)
                return;

            if (_sessionReader == null || _sessionReader.BlockCount <= 0)
                return;

            long sourceQpc = ConvertTrackBarValueToPlaybackQpc(trackBarPlayback.Value);
            lblPlaybackCurrentTime.Text = FormatPlaybackTimeQpc(sourceQpc - _openedPlaybackStartQpc);

            if (_sessionPlayback != null && _sessionPlayback.State == SessionPlaybackState.Playing)
                return;

            PreviewRecordingAtSourceQpc(sourceQpc);
        }

        // Корректно останавливает таймеры, сохраняет настройки и освобождает runtime-ресурсы формы.
        private void fmMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            StopCovertCalibrationMonitor();
            try
            {
                if (_uiTimerId != 0)
                {
                    timeKillEvent(_uiTimerId);
                    _uiTimerId = 0;
                }

                if (_timePeriodActive)
                {
                    timeEndPeriod(1);
                    _timePeriodActive = false;
                }
            }
            catch
            {
            }

            _polyHubSettingsStore.CaptureWindowSettings(
                this,
                _polyHubSettings,
                _selectedServiceSlot != null && _selectedServiceSlot.ServiceManifest != null
                    ? _selectedServiceSlot.ServiceManifest.ServiceId
                    : null);

            EnsureViewerSettings();
            _polyHubSettingsStore.Save(_polyHubSettings);

            if (_routeRuntimeController != null)
                _routeRuntimeController.DisconnectProcessorInputRoute();
            if (_serviceRuntimeController != null)
                _serviceRuntimeController.ShutdownAllServiceSlots();

            if (_sessionPlayback != null)
                _sessionPlayback.Stop(); 

            if (_sessionRecorder != null)
            {
                _sessionRecorder.Dispose();
                _sessionRecorder = null;
            }

            if (_sessionReader != null)
            {
                _sessionReader.Dispose();
                _sessionReader = null;
            }

            if (_sessionPlayback != null)
            {
                _sessionPlayback.Dispose();
                _sessionPlayback = null;
            }

            if (_glScope != null)
            {
                _glScope.Dispose();
                _glScope = null;
            }

            if (_timeFrequencyGlView != null)
            {
                _timeFrequencyGlView.Dispose();
                _timeFrequencyGlView = null;
            }

            if (_analysisGlView != null)
            {
                _analysisGlView.Dispose();
                _analysisGlView = null;
            }

            if (_ressSpatialGlView != null)
            {
                _ressSpatialGlView.Dispose();
                _ressSpatialGlView = null;
            }

            if (_ssvepProcessor != null)
            {
                _ssvepProcessor.AnalysisReady -= SsvepProcessor_AnalysisReady;
                _ssvepProcessor.InputDroppedBlockCountChanged -= SignalProcessor_InputDroppedBlockCountChanged;
                _ssvepProcessor.Dispose();
                _ssvepProcessor = null;
            }

            if (_gainProcessor != null)
            {
                _gainProcessor.InputDroppedBlockCountChanged -= SignalProcessor_InputDroppedBlockCountChanged;
                _gainProcessor.Dispose();
                _gainProcessor = null;
            }
        }

        private string FormatPlaybackTimeQpc(long deltaQpc)
        {
            if (deltaQpc < 0)
                deltaQpc = 0;

            double seconds = (double)deltaQpc / _qpcFreq;
            TimeSpan ts = TimeSpan.FromSeconds(seconds);

            return ts.ToString(@"mm\:ss\.fff");
        }

        private int ConvertPlaybackQpcToTrackBarValue(long sourceQpc)
        {
            if (_openedPlaybackEndQpc <= _openedPlaybackStartQpc)
                return 0;

            if (sourceQpc < _openedPlaybackStartQpc)
                sourceQpc = _openedPlaybackStartQpc;
            if (sourceQpc > _openedPlaybackEndQpc)
                sourceQpc = _openedPlaybackEndQpc;

            long span = _openedPlaybackEndQpc - _openedPlaybackStartQpc;
            long pos = sourceQpc - _openedPlaybackStartQpc;

            if (span <= 0)
                return 0;

            double frac = (double)pos / span;
            int value = (int)Math.Round(frac * trackBarPlayback.Maximum);

            if (value < trackBarPlayback.Minimum)
                value = trackBarPlayback.Minimum;
            if (value > trackBarPlayback.Maximum)
                value = trackBarPlayback.Maximum;

            return value;
        }

        private long ConvertTrackBarValueToPlaybackQpc(int value)
        {
            if (_openedPlaybackEndQpc <= _openedPlaybackStartQpc)
                return _openedPlaybackStartQpc;

            if (value < trackBarPlayback.Minimum)
                value = trackBarPlayback.Minimum;
            if (value > trackBarPlayback.Maximum)
                value = trackBarPlayback.Maximum;

            double frac = 0.0;
            if (trackBarPlayback.Maximum > trackBarPlayback.Minimum)
            {
                frac = (double)(value - trackBarPlayback.Minimum) /
                       (double)(trackBarPlayback.Maximum - trackBarPlayback.Minimum);
            }

            long span = _openedPlaybackEndQpc - _openedPlaybackStartQpc;
            long qpc = _openedPlaybackStartQpc + (long)Math.Round(frac * span);

            if (qpc < _openedPlaybackStartQpc)
                qpc = _openedPlaybackStartQpc;
            if (qpc > _openedPlaybackEndQpc)
                qpc = _openedPlaybackEndQpc;

            return qpc;
        }

        private void UpdatePlaybackUi()
        {
            if (_recordingUiActive)
            {
                _playbackTrackBarInternalUpdate = true;
                try
                {
                    _ctrlsLock = true;
                    try
                    {
                        trackBarPlayback.Enabled = false;
                        trackBarPlayback.Value = trackBarPlayback.Minimum;
                    }
                    finally
                    {
                        _ctrlsLock = false;
                    }
                }
                finally
                {
                    _playbackTrackBarInternalUpdate = false;
                }

                lblPlaybackCurrentTime.Text = "00:00.000";
                // lblPlaybackTotalTime здесь НЕ трогаем:
                // пока идёт запись, его ведёт живой таймер в UiTimerCallback.
                return;
            }

            if (_sessionReader == null || _sessionReader.BlockCount <= 0)
            {
                _playbackTrackBarInternalUpdate = true;
                try
                {
                    _ctrlsLock = true;
                    try
                    {
                        trackBarPlayback.Enabled = false;
                        trackBarPlayback.Value = trackBarPlayback.Minimum;
                    }
                    finally
                    {
                        _ctrlsLock = false;
                    }
                }
                finally
                {
                    _playbackTrackBarInternalUpdate = false;
                }

                lblPlaybackCurrentTime.Text = "00:00.000";
                lblPlaybackTotalTime.Text = "00:00.000";
                return;
            }

            if (_openedPlaybackStartQpc == 0 || _openedPlaybackEndQpc == 0)
            {
                _openedPlaybackStartQpc = _sessionReader.GetFirstSourceQpc();
                _openedPlaybackEndQpc = _sessionReader.GetLastSourceQpc();
            }

            long totalQpc = _openedPlaybackEndQpc - _openedPlaybackStartQpc;
            if (totalQpc < 0)
                totalQpc = 0;

            long currentSourceQpc = _openedPlaybackStartQpc;

            if (_sessionPlayback != null &&
                (_sessionPlayback.State == SessionPlaybackState.Playing ||
                 _sessionPlayback.State == SessionPlaybackState.Paused))
            {
                currentSourceQpc = _sessionPlayback.CurrentSourceQpc;
            }
            else
            {
                currentSourceQpc = ConvertTrackBarValueToPlaybackQpc(trackBarPlayback.Value);
            }

            if (currentSourceQpc < _openedPlaybackStartQpc)
                currentSourceQpc = _openedPlaybackStartQpc;
            if (currentSourceQpc > _openedPlaybackEndQpc)
                currentSourceQpc = _openedPlaybackEndQpc;

            lblPlaybackCurrentTime.Text = FormatPlaybackTimeQpc(currentSourceQpc - _openedPlaybackStartQpc);
            lblPlaybackTotalTime.Text = FormatPlaybackTimeQpc(totalQpc);

            if (!_playbackTrackBarDragging)
            {
                int value = ConvertPlaybackQpcToTrackBarValue(currentSourceQpc);

                _playbackTrackBarInternalUpdate = true;
                try
                {
                    _ctrlsLock = true;
                    try
                    {
                        trackBarPlayback.Value = value;
                    }
                    finally
                    {
                        _ctrlsLock = false;
                    }
                }
                finally
                {
                    _playbackTrackBarInternalUpdate = false;
                }
            }

            trackBarPlayback.Enabled = true;
        }

        private void ResetPreviewStaticPageCache()
        {
            _previewPageFirstBlockIndex = -1;
            _previewPageLastBlockIndex = -1;
            _previewPageStartQpc = -1;
            _previewPageSampleCount = 0;
        }

        private void PrepareViewerForPlayback(SignalStream playbackViewerStream)
        {
            _viewerPlaybackMode = true;
            ResetPreviewStaticPageCache();

            _glScope.ExitPreviewStaticPageMode();
            _glScope.DetachInput();
            _glScope.AttachInput(playbackViewerStream);
            _glScope.SetFreezeTimeline(false);
            _glScope.Configure(
                _sessionReader.Header.ChannelCount,
                _sessionReader.Header.SampleRate,
                GetViewerTimeBaseSeconds(),
                GetViewerScaleVolts());
            _glScope.ClearEventMarkers();

            if (_timeFrequencyGlView != null)
            {
                _timeFrequencyGlView.DetachInput();
                _timeFrequencyGlView.AttachInput(playbackViewerStream);
                _timeFrequencyGlView.Configure(
                    _sessionReader.Header.ChannelCount,
                    _sessionReader.Header.SampleRate,
                    GetTimeFrequencyTimeBaseSeconds(),
                    GetTimeFrequencyScaleVolts());
            }
        }

        private void StartPlaybackFromSourceQpc(long sourceStartQpc)
        {
            if (_sessionReader == null || _sessionPlayback == null)
                return;

            if (sourceStartQpc < _sessionReader.GetFirstSourceQpc())
                sourceStartQpc = _sessionReader.GetFirstSourceQpc();
            if (sourceStartQpc > _sessionReader.GetLastSourceQpc())
                sourceStartQpc = _sessionReader.GetLastSourceQpc();

            _playbackStream.SetChannelNames(_sessionReader.ChannelNames);

            if (_ssvepProcessor != null)
                _ssvepProcessor.Reset();

            SignalStream playbackViewerStream = _playbackStream;
            if (_routeRuntimeController != null)
                playbackViewerStream = _routeRuntimeController.ApplyPlaybackInputRoute(_playbackStream);

            // EOG monitoring also works with an unloaded/unused model and
            // when the viewer route goes directly to the original recording.
            if (_ssvepProcessor != null && GetActiveEogRejection().Enabled && _ssvepProcessor.InputStream == null)
                _ssvepProcessor.AttachInput(_playbackStream);
            PrepareViewerForPlayback(playbackViewerStream);

            var covertRejectedEpochs = new List<EpochInfo>();
            var covertRejection = new EogRejection(_polyHubSettings.EogRejectionSettings.CovertAttention);
            foreach (EpochInfo epoch in _sessionReader.GetAllEpochs())
                if (CovertCalibrationEog.IsCovertClass(epoch.ClassId) &&
                    CovertCalibrationEog.CheckRecordedEpoch(_sessionReader, epoch, covertRejection).IsRejected)
                    covertRejectedEpochs.Add(epoch);

            _sessionPlayback.Start(
                _sessionReader,
                _playbackStream,
                _glScope,
                sourceStartQpc,
                _sessionReader.GetLastSourceQpc(),
                _timeFrequencyGlView,
                covertRejectedEpochs);

            UpdatePlaybackUi();
        }

        private void PreviewRecordingAtSourceQpc(long sourceQpc)
        {
            if (_sessionReader == null || _sessionReader.BlockCount <= 0)
                return;

            int viewerTimeBaseSeconds = GetViewerTimeBaseSeconds();
            if (viewerTimeBaseSeconds < 1)
                viewerTimeBaseSeconds = 1;

            int sampleRate = _sessionReader.Header.SampleRate;
            int channelCount = _sessionReader.Header.ChannelCount;

            long firstSourceQpc = _sessionReader.GetFirstSourceQpc();
            long lastSourceQpc = _sessionReader.GetLastSourceQpc();

            if (sourceQpc < firstSourceQpc)
                sourceQpc = firstSourceQpc;
            if (sourceQpc > lastSourceQpc)
                sourceQpc = lastSourceQpc;

            long windowDurationQpc = viewerTimeBaseSeconds * _qpcFreq;
            long windowEndQpc = sourceQpc;
            long windowStartQpc = windowEndQpc - windowDurationQpc;

            if (windowStartQpc < firstSourceQpc)
                windowStartQpc = firstSourceQpc;

            long requiredPageEndQpc = windowStartQpc + windowDurationQpc;
            if (requiredPageEndQpc < windowEndQpc)
                requiredPageEndQpc = windowEndQpc;

            int firstBlockIndex = _sessionReader.FindFirstBlockIndexAtOrAfter(windowStartQpc);
            if (firstBlockIndex > 0)
                firstBlockIndex--;

            if (firstBlockIndex < 0)
                firstBlockIndex = 0;

            int lastBlockIndex = firstBlockIndex;
            long pageStartQpc = 0;
            int pageSampleCount = 0;
            bool pageStarted = false;

            for (int i = firstBlockIndex; i < _sessionReader.BlockCount; i++)
            {
                SessionBlockRecord rec = _sessionReader.GetBlockIndexRecord(i);

                if (!pageStarted)
                {
                    pageStartQpc = rec.QpcTimestamp;
                    pageStarted = true;
                }

                pageSampleCount += rec.SampleCount;
                lastBlockIndex = i;

                long recDurationQpc = 0;
                if (sampleRate > 0)
                    recDurationQpc = (long)((double)rec.SampleCount * _qpcFreq / sampleRate);

                long recEndQpc = rec.QpcTimestamp + recDurationQpc;
                if (recEndQpc >= requiredPageEndQpc)
                    break;
            }

            if (!pageStarted || pageSampleCount <= 0)
            {
                UpdatePlaybackUi();
                return;
            }

            int viewRightPageSample = 0;
            if (sampleRate > 0)
            {
                viewRightPageSample = (int)Math.Round(
                    (double)(windowEndQpc - pageStartQpc) * sampleRate / _qpcFreq);
            }

            if (viewRightPageSample < 0)
                viewRightPageSample = 0;
            if (viewRightPageSample > pageSampleCount - 1)
                viewRightPageSample = pageSampleCount - 1;

            bool samePage =
                _previewPageFirstBlockIndex == firstBlockIndex &&
                _previewPageLastBlockIndex == lastBlockIndex &&
                _previewPageStartQpc == pageStartQpc &&
                _previewPageSampleCount == pageSampleCount;

            if (samePage)
            {
                _glScope.SetPreviewStaticRightPageSample(viewRightPageSample);
                _glScope.RefreshNow();
                PreviewTimeFrequencyAtSourceQpc(sourceQpc);
                UpdatePlaybackUi();
                return;
            }

            PrepareViewerForStaticPage(channelCount, sampleRate, viewerTimeBaseSeconds);
            _glScope.BeginPreviewStaticPage(pageSampleCount, viewRightPageSample);

            _previewPageFirstBlockIndex = firstBlockIndex;
            _previewPageLastBlockIndex = lastBlockIndex;
            _previewPageStartQpc = pageStartQpc;
            _previewPageSampleCount = pageSampleCount;

            long dstFirstSample = 0;

            for (int i = firstBlockIndex; i <= lastBlockIndex; i++)
            {
                SignalBlock block = _sessionReader.ReadBlock(i);
                if (block == null || block.Data == null || block.SampleCount <= 0)
                    continue;

                _glScope.DrawPreviewStaticBlock(
                    block.Data,
                    block.ChannelCount,
                    block.SampleCount,
                    dstFirstSample);

                dstFirstSample += block.SampleCount;
            }

            long pageDurationQpc = 0;
            if (sampleRate > 0)
                pageDurationQpc = (long)((double)pageSampleCount * _qpcFreq / sampleRate);

            long pageEndQpc = pageStartQpc + pageDurationQpc;

            for (int i = 0; i < _sessionReader.EventCount; i++)
            {
                SessionUserEvent evt = _sessionReader.GetEventRecord(i);

                if ((ServiceEventCode)evt.Code != ServiceEventCode.UserEvent)
                    continue;

                if (evt.QpcTimestamp < pageStartQpc)
                    continue;

                if (evt.QpcTimestamp > pageEndQpc)
                    break;

                long pageSample = (long)Math.Round(
                    (double)(evt.QpcTimestamp - pageStartQpc) * sampleRate / _qpcFreq);

                if (pageSample < 0)
                    pageSample = 0;
                if (pageSample >= pageSampleCount)
                    pageSample = pageSampleCount - 1;

                _glScope.AddPreviewStaticEvent(pageSample, evt.Value0, evt.State);
            }

            EogRejection pageRejection = GetActiveEogRejection();
            foreach (EpochInfo epoch in _sessionReader.GetAllEpochs())
            {
                if (epoch.EndQpc < pageStartQpc || epoch.StartQpc > pageEndQpc) continue;
                if (!CheckRecordedEpoch(_sessionReader, epoch, pageRejection).IsRejected) continue;
                long first = (long)Math.Round((double)(epoch.StartQpc - pageStartQpc) * sampleRate / _qpcFreq);
                long last = (long)Math.Round((double)(epoch.EndQpc - pageStartQpc) * sampleRate / _qpcFreq);
                _glScope.AddPreviewRejectedInterval(first, last);
            }
            PreviewTimeFrequencyAtSourceQpc(sourceQpc);
            _glScope.RefreshNow();
            UpdatePlaybackUi();
        }

        private void PreviewTimeFrequencyAtSourceQpc(long sourceQpc)
        {
            if (_timeFrequencyGlView == null || _sessionReader == null || _sessionReader.BlockCount == 0)
                return;

            int sampleRate = _sessionReader.Header.SampleRate;
            long rightSample = _sessionReader.FindSampleIndexByQpc(sourceQpc);
            SessionBlockRecord lastBlock = _sessionReader.GetBlockIndexRecord(_sessionReader.BlockCount - 1);
            rightSample = Math.Max(0, Math.Min(rightSample, lastBlock.FirstSampleIndex + lastBlock.SampleCount));

            // Include the preceding FFT window so the visible left edge has a
            // complete spectrum, even when the two viewers use different timebases.
            long historySamples = (long)sampleRate * GetTimeFrequencyTimeBaseSeconds() +
                TimeFrequencySpectrum.GetFftSize(sampleRate);
            long firstSample = Math.Max(0, rightSample - historySamples);
            int sampleCount = checked((int)(rightSample - firstSample));
            _timeFrequencyGlView.ShowRecordedWindow(new SignalBlock
            {
                ChannelCount = _sessionReader.Header.ChannelCount,
                SampleRate = sampleRate,
                SampleCount = sampleCount,
                Data = _sessionReader.ReadSampleRange(firstSample, sampleCount)
            }, firstSample);

            long firstEventQpc = sourceQpc -
                (long)GetTimeFrequencyTimeBaseSeconds() * _sessionReader.Header.QpcFrequency;
            for (int i = _sessionReader.FindFirstEventIndexAtOrAfter(firstEventQpc);
                i < _sessionReader.EventCount; i++)
            {
                SessionUserEvent evt = _sessionReader.GetEventRecord(i);
                if (evt.QpcTimestamp > sourceQpc)
                    break;
                if ((ServiceEventCode)evt.Code != ServiceEventCode.UserEvent)
                    continue;
                _timeFrequencyGlView.AddRecordedEventMarker(
                    _sessionReader.FindSampleIndexByQpc(evt.QpcTimestamp), evt.Value0, evt.State);
            }
            EogRejection rejection = GetActiveEogRejection();
            foreach (EpochInfo epoch in _sessionReader.GetAllEpochs())
            {
                if (epoch.EndSampleIndex <= firstSample || epoch.StartSampleIndex >= rightSample) continue;
                if (!CheckRecordedEpoch(_sessionReader, epoch, rejection).IsRejected) continue;
                _timeFrequencyGlView.AddRecordedRejectedInterval(
                    Math.Max(firstSample, epoch.StartSampleIndex), Math.Min(rightSample, epoch.EndSampleIndex) - 1);
            }
        }

        private void SetReaderStatusText(string text)
        {
            lock (_statusSync)
            {
                _readerStatusText = text;
            }
        }


        private void SetProcessorStatusText(string text)
        {
            lock (_statusSync)
            {
                _processorStatusText = text;
            }
        }

        private void EnsureViewerSettings()
        {
            if (_polyHubSettings == null)
                return;

            if (_polyHubSettings.PolyHubViewerSettings == null)
                _polyHubSettings.PolyHubViewerSettings = new PolyHubViewerSettings();

            if (_polyHubSettings.PolyHubViewerSettings.ViewerTimeBaseSeconds <= 0)
                _polyHubSettings.PolyHubViewerSettings.ViewerTimeBaseSeconds = DefaultViewerTimeBaseSeconds;

            if (_polyHubSettings.PolyHubViewerSettings.ViewerScaleVolts <= 0.0f)
                _polyHubSettings.PolyHubViewerSettings.ViewerScaleVolts = DefaultViewerScaleVolts;

            if (_polyHubSettings.PolyHubViewerSettings.TimeFrequencyTimeBaseSeconds <= 0)
                _polyHubSettings.PolyHubViewerSettings.TimeFrequencyTimeBaseSeconds = DefaultTimeFrequencyTimeBaseSeconds;

            if (_polyHubSettings.PolyHubViewerSettings.TimeFrequencyScaleVolts <= 0.0f)
                _polyHubSettings.PolyHubViewerSettings.TimeFrequencyScaleVolts = DefaultTimeFrequencyScaleVolts;
        }

        private void InitializeViewerScaleAndTimeUi()
        {
            _ctrlsLock = true;
            try
            {
                int timeBaseSeconds = GetViewerTimeBaseSeconds();

                decimal timeValue = timeBaseSeconds;
                if (timeValue < numUpDwnTimeBaseInSec.Minimum)
                    timeValue = numUpDwnTimeBaseInSec.Minimum;
                if (timeValue > numUpDwnTimeBaseInSec.Maximum)
                    timeValue = numUpDwnTimeBaseInSec.Maximum;

                numUpDwnTimeBaseInSec.Value = timeValue;

                int scaleIndex = FindScaleComboIndex(GetViewerScaleVolts());
                if (scaleIndex >= 0)
                    cmbBxScaleAmpl.SelectedIndex = scaleIndex;
                else if (cmbBxScaleAmpl.Items.Count > 0)
                    cmbBxScaleAmpl.SelectedIndex = 7; // 100 µV
            }
            finally
            {
                _ctrlsLock = false;
            }
        }

        private void InitializeTimeFrequencyScaleAndTimeUi()
        {
            _ctrlsLock = true;
            try
            {
                int timeBaseSeconds = GetTimeFrequencyTimeBaseSeconds();

                decimal timeValue = timeBaseSeconds;
                if (timeValue < numUpDwnTimeFBaseInSec.Minimum)
                    timeValue = numUpDwnTimeFBaseInSec.Minimum;
                if (timeValue > numUpDwnTimeFBaseInSec.Maximum)
                    timeValue = numUpDwnTimeFBaseInSec.Maximum;

                numUpDwnTimeFBaseInSec.Value = timeValue;

                int scaleIndex = FindScaleComboIndex(
                    cmbBxScaleTFAmpl,
                    GetTimeFrequencyScaleVolts());

                if (scaleIndex >= 0)
                    cmbBxScaleTFAmpl.SelectedIndex = scaleIndex;
                else if (cmbBxScaleTFAmpl.Items.Count > 0)
                    cmbBxScaleTFAmpl.SelectedIndex = 4; // 10 µV
            }
            finally
            {
                _ctrlsLock = false;
            }
        }

        private int FindScaleComboIndex(float scaleVolts)
        {
            return FindScaleComboIndex(cmbBxScaleAmpl, scaleVolts);
        }

        private int FindScaleComboIndex(ComboBox comboBox, float scaleVolts)
        {
            int bestIndex = -1;
            double bestDiff = double.MaxValue;

            for (int i = 0; i < comboBox.Items.Count; i++)
            {
                string text = comboBox.Items[i] as string;
                float volts;
                if (!TryParseScaleComboText(text, out volts))
                    continue;

                double diff = Math.Abs((double)volts - scaleVolts);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        private bool TryParseScaleComboText(string text, out float volts)
        {
            volts = 0.0f;

            if (string.IsNullOrWhiteSpace(text))
                return false;

            string s = text.Trim()
                .Replace("μ", "µ")
                .Replace(",", ".");

            string[] parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return false;

            double value;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return false;

            string unit = parts[1].Trim();

            double factor;
            if (string.Equals(unit, "µV", StringComparison.OrdinalIgnoreCase))
                factor = 1e-6;
            else if (string.Equals(unit, "mV", StringComparison.OrdinalIgnoreCase))
                factor = 1e-3;
            else if (string.Equals(unit, "V", StringComparison.OrdinalIgnoreCase))
                factor = 1.0;
            else
                return false;

            volts = (float)(value * factor);
            return true;
        }

        private void ApplyViewerTimeBaseChange(int timeBaseSeconds)
        {
            if (timeBaseSeconds < 1)
                timeBaseSeconds = 1;

            EnsureViewerSettings();
            _polyHubSettings.PolyHubViewerSettings.ViewerTimeBaseSeconds = timeBaseSeconds;

            if (_viewerPlaybackMode &&
                _sessionReader != null &&
                _sessionReader.BlockCount > 0 &&
                (_sessionPlayback == null || _sessionPlayback.State != SessionPlaybackState.Playing))
            {
                ResetPreviewStaticPageCache();
                long sourceQpc = ConvertTrackBarValueToPlaybackQpc(trackBarPlayback.Value);
                PreviewRecordingAtSourceQpc(sourceQpc);
                return;
            }

            if (_glScope != null)
                _glScope.SetTimeBaseSeconds(timeBaseSeconds);
        }

        private void ApplyViewerScaleChange(float scaleVolts)
        {
            if (scaleVolts <= 0.0f)
                return;

            EnsureViewerSettings();
            _polyHubSettings.PolyHubViewerSettings.ViewerScaleVolts = scaleVolts;

            if (_viewerPlaybackMode &&
                _sessionReader != null &&
                _sessionReader.BlockCount > 0 &&
                (_sessionPlayback == null || _sessionPlayback.State != SessionPlaybackState.Playing))
            {
                ResetPreviewStaticPageCache();
                long sourceQpc = ConvertTrackBarValueToPlaybackQpc(trackBarPlayback.Value);
                PreviewRecordingAtSourceQpc(sourceQpc);
                return;
            }

            if (_glScope != null)
                _glScope.SetScaleVolts(scaleVolts);
        }

        private int GetViewerTimeBaseSeconds()
        {
            EnsureViewerSettings();
            return _polyHubSettings.PolyHubViewerSettings.ViewerTimeBaseSeconds;
        }


        private float GetViewerScaleVolts()
        {
            EnsureViewerSettings();
            return _polyHubSettings.PolyHubViewerSettings.ViewerScaleVolts;
        }


        private void ApplyTimeFrequencyTimeBaseChange(int timeBaseSeconds)
        {
            if (timeBaseSeconds < 1)
                timeBaseSeconds = 1;

            EnsureViewerSettings();
            _polyHubSettings.PolyHubViewerSettings.TimeFrequencyTimeBaseSeconds = timeBaseSeconds;

            if (_timeFrequencyGlView != null)
                _timeFrequencyGlView.SetTimeBaseSeconds(timeBaseSeconds);

            if (_viewerPlaybackMode && _sessionReader != null &&
                (_sessionPlayback == null || _sessionPlayback.State != SessionPlaybackState.Playing))
                PreviewTimeFrequencyAtSourceQpc(ConvertTrackBarValueToPlaybackQpc(trackBarPlayback.Value));
        }

        private void ApplyTimeFrequencyScaleChange(float scaleVolts)
        {
            if (scaleVolts <= 0.0f)
                return;

            EnsureViewerSettings();
            _polyHubSettings.PolyHubViewerSettings.TimeFrequencyScaleVolts = scaleVolts;

            if (_timeFrequencyGlView != null)
                _timeFrequencyGlView.SetScaleVolts(scaleVolts);
        }

        private int GetTimeFrequencyTimeBaseSeconds()
        {
            EnsureViewerSettings();
            return _polyHubSettings.PolyHubViewerSettings.TimeFrequencyTimeBaseSeconds;
        }

        private float GetTimeFrequencyScaleVolts()
        {
            EnsureViewerSettings();
            return _polyHubSettings.PolyHubViewerSettings.TimeFrequencyScaleVolts;
        }


        // Преобразует внутреннюю пару nodeId/pinId в короткий человекочитаемый текст для UI.
        private string FormatSourceNodePinText(string sourceNodeId, string sourcePinId)
        {
            if (string.IsNullOrWhiteSpace(sourceNodeId))
                return "<empty>";

            if (sourceNodeId.StartsWith("Service:", StringComparison.OrdinalIgnoreCase))
            {
                string serviceId =
                    sourceNodeId.Substring("Service:".Length);

                ServiceSlot serviceSlot =
                    _serviceCatalog.FindServiceSlot(
                        _serviceSlotList,
                        serviceId);

                if (serviceSlot == null ||
                    serviceSlot.ServiceManifest == null)
                {
                    return "<empty>";
                }

                ServiceDataOutputSlot outputSlot =
                    _serviceCatalog.FindServiceDataOutputSlot(
                        serviceSlot,
                        sourcePinId);

                if (outputSlot == null ||
                    outputSlot.ServiceDataOutputManifest == null)
                {
                    return "<empty>";
                }

                string serviceName =
                    serviceSlot.ServiceManifest.ServiceDisplayName;

                if (string.IsNullOrWhiteSpace(serviceName))
                    serviceName = serviceId;

                string outputName =
                    outputSlot.ServiceDataOutputManifest.ServiceDataPortDisplayName;

                if (string.IsNullOrWhiteSpace(outputName))
                    outputName = sourcePinId;

                return serviceName + "." + outputName;
            }

            SignalProcessorSlot processorSlot =
                _signalProcessorCatalog.FindByNodeId(sourceNodeId);

            if (processorSlot != null)
                return processorSlot.ProcessorDisplayName + "." + sourcePinId;

            if (string.Equals(
                sourceNodeId,
                NodeIdSignalViewer,
                StringComparison.OrdinalIgnoreCase))
            {
                return "SignalViewer." + sourcePinId;
            }

            return sourceNodeId + "." + sourcePinId;
        }


        // Преобразует target node/pin в отображаемый вид для дерева маршрутов.
        private string FormatTargetNodePinText(string targetNodeId, string targetPinId)
        {
            if (string.IsNullOrWhiteSpace(targetNodeId))
                return "<empty>";

            SignalProcessorSlot processorSlot = _signalProcessorCatalog.FindByNodeId(targetNodeId);
            if (processorSlot != null)
                return processorSlot.ProcessorDisplayName + "." + targetPinId;

            if (string.Equals(targetNodeId, NodeIdSignalViewer, StringComparison.OrdinalIgnoreCase))
                return "SignalViewer." + targetPinId;

            return targetNodeId + "." + targetPinId;
        }


        // Собирает краткую строку всех target-пинов, подключённых к заданному source-пину.
        private string BuildTargetsSummary(string sourceNodeId, string sourcePinId)
        {
            List<PolyHubRouteSettings> routes = _routeStore.FindEnabledRoutesFrom(sourceNodeId, sourcePinId);
            if (routes.Count == 0)
                return string.Empty;

            string text = string.Empty;
            for (int i = 0; i < routes.Count; i++)
            {
                if (i > 0)
                    text += ", ";

                text += FormatTargetNodePinText(routes[i].TargetNodeId, routes[i].TargetPinId);
            }

            return text;
        }


        // Полностью перестраивает дерево сервисов на основе текущего каталога и сохранённых routes.
        private void BuildTreeViewServices()
        {
            treeViewServices.BeginUpdate();
            try
            {
                treeViewServices.Nodes.Clear();

                for (int i = 0; i < _serviceSlotList.Count; i++)
                {
                    ServiceSlot serviceSlot = _serviceSlotList[i];

                    TreeNode serviceNode = new TreeNode(serviceSlot.ServiceManifest.ServiceDisplayName);
                    serviceNode.Tag = new ServiceTreeNodeTag
                    {
                        ServiceTreeNodeKind = ServiceTreeNodeKind.Service,
                        ServiceId = serviceSlot.ServiceManifest.ServiceId
                    };

                    TreeNode evtNode = new TreeNode("EventLink");
                    evtNode.Tag = new ServiceTreeNodeTag
                    {
                        ServiceTreeNodeKind = ServiceTreeNodeKind.ServiceEvtLink,
                        ServiceId = serviceSlot.ServiceManifest.ServiceId
                    };
                    serviceNode.Nodes.Add(evtNode);

                    TreeNode outputsRootNode = new TreeNode("Outputs");
                    outputsRootNode.Tag = new ServiceTreeNodeTag
                    {
                        ServiceTreeNodeKind = ServiceTreeNodeKind.ServiceOutputsRoot,
                        ServiceId = serviceSlot.ServiceManifest.ServiceId
                    };

                    for (int j = 0; j < serviceSlot.ServiceDataOutputSlotList.Count; j++)
                    {
                        ServiceDataOutputSlot outputSlot = serviceSlot.ServiceDataOutputSlotList[j];

                        string outputText = outputSlot.ServiceDataOutputManifest.ServiceDataPortDisplayName;

                        string targets = BuildTargetsSummary(
                            _routeStore.MakeServiceNodeId(serviceSlot.ServiceManifest.ServiceId),
                            outputSlot.ServiceDataOutputManifest.ServiceDataPortId);

                        if (!string.IsNullOrWhiteSpace(targets))
                            outputText += "  ->  " + targets;

                        TreeNode outputNode = new TreeNode(outputText);
                        outputNode.Tag = new ServiceTreeNodeTag
                        {
                            ServiceTreeNodeKind = ServiceTreeNodeKind.ServiceOutput,
                            ServiceId = serviceSlot.ServiceManifest.ServiceId,
                            ServiceDataOutputId = outputSlot.ServiceDataOutputManifest.ServiceDataPortId
                        };

                        outputsRootNode.Nodes.Add(outputNode);
                    }

                    serviceNode.Nodes.Add(outputsRootNode);
                    treeViewServices.Nodes.Add(serviceNode);
                }

                treeViewServices.ExpandAll();
            }
            finally
            {
                treeViewServices.EndUpdate();
            }
        }


        // Полностью перестраивает дерево pipeline по зарегистрированным SignalProcessorSlot и routes.
        private void BuildTreeViewPipeline()
        {
            treeViewPipeline.BeginUpdate();
            try
            {
                treeViewPipeline.Nodes.Clear();

                for (int i = 0; i < _signalProcessorCatalog.SignalProcessorSlotList.Count; i++)
                {
                    SignalProcessorSlot processorSlot = _signalProcessorCatalog.SignalProcessorSlotList[i];

                    PolyHubRouteSettings inputRoute =
                        _routeStore.FindEnabledRouteTo(processorSlot.ProcessorNodeId, processorSlot.InputPinId);

                    string inputText = processorSlot.InputPinId + "  <=  <empty>";
                    if (inputRoute != null)
                        inputText = processorSlot.InputPinId + "  <=  " +
                            FormatSourceNodePinText(inputRoute.SourceNodeId, inputRoute.SourcePinId);

                    string outputText = processorSlot.OutputPinId;
                    string outputTargets = BuildTargetsSummary(processorSlot.ProcessorNodeId, processorSlot.OutputPinId);
                    if (!string.IsNullOrWhiteSpace(outputTargets))
                        outputText += "  ->  " + outputTargets;

                    TreeNode processorNode = new TreeNode(processorSlot.ProcessorDisplayName);

                    TreeNode inputNode = new TreeNode(inputText);
                    inputNode.Tag = processorSlot.ProcessorNodeId + "." + processorSlot.InputPinId;

                    TreeNode outputNode = new TreeNode(outputText);
                    outputNode.Tag = processorSlot.ProcessorNodeId + "." + processorSlot.OutputPinId;

                    processorNode.Nodes.Add(inputNode);
                    processorNode.Nodes.Add(outputNode);

                    treeViewPipeline.Nodes.Add(processorNode);
                }

                treeViewPipeline.ExpandAll();
            }
            finally
            {
                treeViewPipeline.EndUpdate();
            }
        }


        // Восстанавливает ранее выбранный сервис из сохранённых настроек UI, если он ещё существует.
        private void RestoreSelectedServiceNode()
        {
            if (_polyHubSettings == null ||
                _polyHubSettings.PolyHubUiSettings == null ||
                string.IsNullOrWhiteSpace(_polyHubSettings.PolyHubUiSettings.SelectedServiceId))
            {
                return;
            }

            string selectedServiceId = _polyHubSettings.PolyHubUiSettings.SelectedServiceId;

            for (int i = 0; i < treeViewServices.Nodes.Count; i++)
            {
                TreeNode node = treeViewServices.Nodes[i];
                ServiceTreeNodeTag tag = node.Tag as ServiceTreeNodeTag;

                if (tag != null &&
                    tag.ServiceTreeNodeKind == ServiceTreeNodeKind.Service &&
                    string.Equals(tag.ServiceId, selectedServiceId, StringComparison.OrdinalIgnoreCase))
                {
                    treeViewServices.SelectedNode = node;
                    return;
                }
            }
        }


        // Обновляет подпись текущего входного route у viewer без изменения самих подключений.
        private void UpdateViewerInputPinText()
        {
            PolyHubRouteSettings viewerRoute = _routeStore.FindEnabledRouteTo(NodeIdSignalViewer, PinIdIn);

            string text = "Viewer.In  <=  <empty>";
            if (viewerRoute != null)
                text = "Viewer.In  <=  " + FormatSourceNodePinText(viewerRoute.SourceNodeId, viewerRoute.SourcePinId);

            if (lblViewerInputPin != null)
                lblViewerInputPin.Text = text;

            if (label9 != null)
                label9.Text = text;
        }


        // Обрабатывает входящие служебные события: обновляет статус и передаёт UserEvent в рекордер и viewer.
        private void OnServiceEvtReceived(ServiceSlot serviceSlot, ServiceEventPacket evt)
        {
            ServiceEventCode code = (ServiceEventCode)evt.Code;

            // Обновляем флаг running по служебным статусам.
            if (code == ServiceEventCode.State || code == ServiceEventCode.Ack)
                serviceSlot.ServiceIsRunning = evt.State != 0;

            lock (_statusSync)
            {
                serviceSlot.ServiceStatusText =
                    serviceSlot.ServiceManifest.ServiceDisplayName + ": " +
                    code.ToString() +
                    " state=" + evt.State.ToString() +
                    " v0=" + evt.Value0.ToString() +
                    " text=" + (evt.Text ?? string.Empty);
            }

            if (code == ServiceEventCode.UserEvent ||
                (code == ServiceEventCode.State &&
                 serviceSlot.ServiceManifest != null &&
                 string.Equals(serviceSlot.ServiceManifest.ServiceId, "NeuroPiano", StringComparison.OrdinalIgnoreCase)))
            {
                if (_sessionRecorder != null)
                    _sessionRecorder.OnServiceEvent(evt);

                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke((Action)(() =>
                    {
                        HandleNeuroPianoCalibrationState(serviceSlot, evt);
                        HandleCovertCalibrationEvent(serviceSlot, evt);
                        if (code == ServiceEventCode.UserEvent)
                        {
                            if (_glScope != null)
                                _glScope.AddEventMarker(evt.QpcTimestamp, evt.Value0, evt.State);
                            if (_timeFrequencyGlView != null)
                                _timeFrequencyGlView.AddEventMarker(evt.QpcTimestamp, evt.Value0, evt.State);
                        }

                        if (glCtrlViewer != null && !glCtrlViewer.IsDisposed)
                            glCtrlViewer.Invalidate();
                    }));
                }

                return;
            }
        }


        // Применяет уже сохранённые route к runtime-потокам и затем синхронизирует визуальное представление.
        private void ApplySavedRoutes(bool showErrorMessage)
        {
            if (_routeRuntimeController != null)
                _routeRuntimeController.ApplySavedRoutes(showErrorMessage, _viewerPlaybackMode);

            BuildTreeViewServices();
            BuildTreeViewPipeline();
            UpdateViewerInputPinText();
        }

        private void DeactivateSavedRoutes()
        {
            if (_routeRuntimeController != null)
                _routeRuntimeController.DeactivateSavedRoutes(_viewerPlaybackMode);

            BuildTreeViewServices();
            BuildTreeViewPipeline();
            UpdateViewerInputPinText();
        }

        private void SignalProcessor_InputDroppedBlockCountChanged(int dropped)
        {
            lock (_statusSync)
            {
                _processorStatusText = "Processor dropped latest blocks: " + dropped.ToString();
            }
        }

        private void UiTimerCallback(int id, int msg, IntPtr user, int dw1, int dw2)
        {
            try
            {
                if (!IsHandleCreated || IsDisposed)
                    return;

                long qpcNow;
                QueryPerformanceCounter(out qpcNow);

                bool statusDue = false;

                if (_sessionPlayback != null && _sessionPlayback.IsPlaying)
                {
                    // Ничего руками здесь не качаем:
                    // новый SessionPlayback живёт на своём timer callback.
                }

                long statusPeriodQpc = _qpcFreq / 10;

                if (qpcNow - _lastStatusUpdateQpc >= statusPeriodQpc)
                {
                    do
                    {
                        _lastStatusUpdateQpc += statusPeriodQpc;
                    }
                    while (qpcNow - _lastStatusUpdateQpc >= statusPeriodQpc);

                    statusDue = true;
                }

                if (_glScope != null)
                    _glScope.OnLiveRenderClockTick();

                if (_timeFrequencyGlView != null)
                    _timeFrequencyGlView.OnLiveRenderClockTick();

                if (statusDue)
                {
                    // статусный код 

                    string servicesText = string.Empty;
                    string readerText;
                    string processorText;

                    lock (_statusSync)
                    {
                        for (int i = 0; i < _serviceSlotList.Count; i++)
                        {
                            if (i > 0)
                                servicesText += " | ";

                            servicesText += _serviceSlotList[i].ServiceStatusText;
                        }

                        if (string.IsNullOrEmpty(servicesText))
                            servicesText = "Services: -";

                        readerText = _readerStatusText;
                        processorText = _processorStatusText;
                    }

                    int readFailTotal = 0;
                    for (int i = 0; i < _serviceSlotList.Count; i++)
                    {
                        ServiceSlot serviceSlot = _serviceSlotList[i];
                        if (serviceSlot == null)
                            continue;

                        for (int j = 0; j < serviceSlot.ServiceDataOutputSlotList.Count; j++)
                        {
                            ServiceDataOutputSlot outputSlot = serviceSlot.ServiceDataOutputSlotList[j];
                            if (outputSlot == null || outputSlot.ServiceDataOutputReader == null)
                                continue;

                            readFailTotal += outputSlot.ServiceDataOutputReader.TryReadBlockFailCount;
                        }
                    }

                    BeginInvoke((Action)(() =>
                    {
                        string combined =
                            servicesText + " | " +
                            readerText + " | " +
                            processorText;

                        if (statusStripMain.Items.Count > 0)
                            statusStripMain.Items[0].Text = combined;
                        else
                            statusStripMain.Text = combined;

                        double cursorFrequency;
                        string frequencyText = _timeFrequencyGlView != null &&
                            _timeFrequencyGlView.TryGetCursorFrequency(out cursorFrequency)
                            ? cursorFrequency.ToString("0.0") + " Гц"
                            : string.Empty;
                        if (toolStripStatusFrequency.Text != frequencyText)
                            toolStripStatusFrequency.Text = frequencyText;

                        lblDebug.Text = "readFail=" + readFailTotal.ToString();

                        if (_recordingUiActive)
                        {
                            long qpcNowUi;
                            QueryPerformanceCounter(out qpcNowUi);

                            long recordedQpc = qpcNowUi - _recordingUiStartQpc;
                            if (recordedQpc < 0)
                                recordedQpc = 0;

                            lblPlaybackTotalTime.Text = FormatPlaybackTimeQpc(recordedQpc);
                        }

                        PumpCovertCalibration();
                        UpdateCalibrationButtons();
                        UpdatePlaybackUi();
                    }));

                }
            }
            catch
            {
            }
        }

        private void panelHost_Resize(object sender, EventArgs e)
        {
            if (_serviceRuntimeController != null)
                _serviceRuntimeController.ResizeActiveHostedService();
        }

        private void CloseOpenedRecordedSessionFile()
        {
            if (_sessionPlayback != null)
                _sessionPlayback.Stop();

            if (_sessionReader != null)
            {
                _sessionReader.Dispose();
                _sessionReader = null;
            }

            _playbackStream.SetChannelNames(null);

            ResetPreviewStaticPageCache();

            _openedPlaybackStartQpc = 0;
            _openedPlaybackEndQpc = 0;

            _playbackTrackBarInternalUpdate = true;
            try
            {
                _ctrlsLock = true;
                try
                {
                    trackBarPlayback.Minimum = 0;
                    trackBarPlayback.Maximum = 1000;
                    trackBarPlayback.Value = 0;
                    trackBarPlayback.Enabled = false;
                }
                finally
                {
                    _ctrlsLock = false;
                }
            }
            finally
            {
                _playbackTrackBarInternalUpdate = false;
            }

            lblPlaybackCurrentTime.Text = "00:00.000";
            if (!_recordingUiActive)
                lblPlaybackTotalTime.Text = "00:00.000";

            System.Threading.Interlocked.Increment(ref _spectrumRequest);
            _spectrumEpoch = null;
            if (_ressSpatialGlView != null)
                _ressSpatialGlView.SetSpectra(null, null, "Select a recorded class");
            listBoxEpochClasses.Items.Clear();
            dataGridViewEpochs.DataSource = null;
            _openedRecordingFilePath = null;

            if (_analysisGlView != null)
                _analysisGlView.SetSelectedClassId(null);
            if (_ressSpatialGlView != null)
                _ressSpatialGlView.SetSelectedClassId(null);

            UpdateSsvepModelButtons();
        }


        // Запускает запись текущего подключённого live-источника, который route runtime подал на вход processor.
        private void tlStrpStartRecording_Click(object sender, EventArgs e)
        {
            ServiceDataOutputSlot sourceOutputSlot =
                _routeRuntimeController != null
                    ? _routeRuntimeController.ConnectedSourceServiceDataOutputSlot
                    : null;

            SignalStream recordingStream =
                _routeRuntimeController != null
                    ? _routeRuntimeController.ViewerAttachedStream
                    : null;

            if (sourceOutputSlot == null ||
                sourceOutputSlot.ServiceDataOutputReader == null ||
                recordingStream == null)
            {
                MessageBox.Show("Сначала подключи поток к processor и его выход к Viewer.");
                return;
            }

            string[] channelNames = recordingStream.ChannelNames;

            if (channelNames == null ||
                channelNames.Length == 0)
            {
                MessageBox.Show("У записываемого потока нет каналов.");
                return;
            }

            SaveFileDialog dlg = new SaveFileDialog();
            dlg.Filter = "PolyHub Session (*.phs)|*.phs|All files (*.*)|*.*";
            dlg.DefaultExt = "phs";

            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;

            CloseOpenedRecordedSessionFile();

            _sessionRecorder.AttachSignalInput(recordingStream);
            _sessionRecorder.StartSession(
                dlg.FileName,
                channelNames,
                sourceOutputSlot.ServiceDataOutputReader.GetSampleRate(),
                _qpcFreq,
                64L * 1024L * 1024L,
                64L * 1024L * 1024L);

            QueryPerformanceCounter(out _recordingUiStartQpc);
            _recordingUiActive = true;
            _viewerPlaybackMode = false;

            lblPlaybackCurrentTime.Text = "00:00.000";
            lblPlaybackTotalTime.Text = "00:00.000";

            _playbackTrackBarInternalUpdate = true;
            try
            {
                _ctrlsLock = true;
                try
                {
                    trackBarPlayback.Enabled = false;
                    trackBarPlayback.Value = trackBarPlayback.Minimum;
                }
                finally
                {
                    _ctrlsLock = false;
                }
            }
            finally
            {
                _playbackTrackBarInternalUpdate = false;
            }
        }


        private void tlStrpStopRecording_Click(object sender, EventArgs e)
        {
            if (_sessionRecorder != null)
                _sessionRecorder.StopSession();

            _recordingUiActive = false;
            UpdatePlaybackUi();
        }


        private void tlStrpOpenRecording_Click(object sender, EventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Filter = "PolyHub Session (*.phs)|*.phs|All files (*.*)|*.*";

            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;

            _recordingUiActive = false;
            CloseOpenedRecordedSessionFile();

            if (_sessionPlayback != null)
                _sessionPlayback.Stop();

            if (_sessionReader != null)
                _sessionReader.Dispose();

            _sessionReader = new SessionReader();
            _sessionReader.Open(dlg.FileName);
            _openedRecordingFilePath = dlg.FileName;

            _playbackStream.SetChannelNames(_sessionReader.ChannelNames);
            AppendSsvepStatus("Opened recording: " + Path.GetFileName(dlg.FileName));
            AppendEpochRejectionSummary();
            UpdateSsvepModelButtons();

            ResetPreviewStaticPageCache();

            _openedPlaybackStartQpc = _sessionReader.GetFirstSourceQpc();
            _openedPlaybackEndQpc = _sessionReader.GetLastSourceQpc();

            long totalQpc = _openedPlaybackEndQpc - _openedPlaybackStartQpc;
            if (totalQpc < 0)
                totalQpc = 0;

            int totalMs = (int)Math.Round((double)totalQpc * 1000.0 / _qpcFreq);
            if (totalMs < 1)
                totalMs = 1;

            _playbackTrackBarInternalUpdate = true;
            try
            {
                _ctrlsLock = true;
                try
                {
                    trackBarPlayback.Minimum = 0;
                    trackBarPlayback.Maximum = totalMs;
                    trackBarPlayback.Value = 0;
                    trackBarPlayback.SmallChange = 1;
                }
                finally
                {
                    _ctrlsLock = false;
                }
            }
            finally
            {
                _playbackTrackBarInternalUpdate = false;
            }

            UpdatePlaybackUi();

            FillEpochClassList();

            PreviewRecordingAtSourceQpc(_openedPlaybackStartQpc);
        }


        // Обновляет список классов эпох после открытия записи.
        private void FillEpochClassList()
        {
            if (_sessionReader == null)
                return;

            _ctrlsLock = true;
            try
            {
                listBoxEpochClasses.BeginUpdate();
                try
                {
                    listBoxEpochClasses.Items.Clear();

                    List<EpochClassSummary> classes = _sessionReader.GetClassSummaries();
                    for (int i = 0; i < classes.Count; i++)
                        listBoxEpochClasses.Items.Add(classes[i]);
                }
                finally
                {
                    listBoxEpochClasses.EndUpdate();
                }

                if (listBoxEpochClasses.Items.Count > 0)
                    listBoxEpochClasses.SelectedIndex = 0;
            }
            finally
            {
                _ctrlsLock = false;
            }

            if (listBoxEpochClasses.SelectedIndex >= 0)
                listBoxEpochClasses_SelectedIndexChanged(listBoxEpochClasses, EventArgs.Empty);
        }

        private void listBoxEpochClasses_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_ctrlsLock)
                return;

            _spectrumEpoch = null;
            RefreshSpatialSpectra();
            EpochClassSummary summary = listBoxEpochClasses.SelectedItem as EpochClassSummary;
            if (summary == null || _sessionReader == null)
            {
                dataGridViewEpochs.DataSource = null;
                if (_analysisGlView != null)
                    _analysisGlView.SetSelectedClassId(null);
                if (_ressSpatialGlView != null)
                    _ressSpatialGlView.SetSelectedClassId(null);
                return;
            }

            if (_analysisGlView != null)
                _analysisGlView.SetSelectedClassId(summary.ClassId);
            if (_ressSpatialGlView != null)
                _ressSpatialGlView.SetSelectedClassId(summary.ClassId);

            _bindingSpectrumClass = true;
            try
            {
                dataGridViewEpochs.AutoGenerateColumns = true;
                dataGridViewEpochs.DataSource = _sessionReader.GetEpochsByClass(summary.ClassId);
                RefreshEpochRejection();

                if (dataGridViewEpochs.Rows.Count > 0)
                {
                    dataGridViewEpochs.ClearSelection();
                    dataGridViewEpochs.Rows[0].Selected = true;
                    dataGridViewEpochs.CurrentCell = dataGridViewEpochs.Rows[0].Cells[0];
                }
            }
            finally { _bindingSpectrumClass = false; }

        }


        private void dataGridViewEpochs_SelectionChanged(object sender, EventArgs e)
        {
            if (_ctrlsLock)
                return;

            if (_sessionReader == null)
                return;

            if (dataGridViewEpochs.CurrentRow == null)
                return;

            EpochInfo epoch = dataGridViewEpochs.CurrentRow.DataBoundItem as EpochInfo;
            if (epoch == null)
                return;

            if (!_bindingSpectrumClass)
            {
                _spectrumEpoch = epoch;
                RefreshSpatialSpectra();
            }
            GoToEpochInViewer(epoch);
            AnalyzeEpochForSsvep(epoch);
        }

        private void AnalyzeCurrentSelectedEpochForSsvep()
        {
            if (dataGridViewEpochs.CurrentRow == null)
                return;

            EpochInfo epoch = dataGridViewEpochs.CurrentRow.DataBoundItem as EpochInfo;
            if (epoch != null)
                AnalyzeEpochForSsvep(epoch);
        }

        private void AnalyzeEpochForSsvep(EpochInfo epoch)
        {
            if (epoch == null || _sessionReader == null || _ssvepProcessor == null)
                return;

            try
            {
                SsvepModel model = _ssvepProcessor.Model;
                if (model == null)
                    return;

                if (_sessionReader.Header.SampleRate != model.SampleRate)
                {
                    _analysisGlView.SetStatus("Model sample rate does not match recording.");
                    return;
                }

                SignalBlock epochBlock = _sessionReader.ReadEpochAsSingleBlock(epoch);
                if (epochBlock == null || epochBlock.SampleCount < model.WindowSamples)
                {
                    _analysisGlView.SetStatus("Selected epoch is shorter than the SSVEP window.");
                    return;
                }

                EogRejectionResult eogCheck = CheckRecordedEpoch(_sessionReader, epoch, GetActiveEogRejection(), epochBlock);

                int[] sourceIndices = ResolveSsvepModelChannelIndices(
                    _sessionReader.ChannelNames,
                    model.ChannelNames);

                int startSample = (epochBlock.SampleCount - model.WindowSamples) / 2;
                if (startSample < 0)
                    startSample = 0;

                int modelChannelCount = model.ChannelNames.Length;
                float[] window = new float[modelChannelCount * model.WindowSamples];

                for (int ch = 0; ch < modelChannelCount; ch++)
                {
                    int sourceBase = sourceIndices[ch] * epochBlock.SampleCount + startSample;
                    int targetBase = ch * model.WindowSamples;

                    Array.Copy(
                        epochBlock.Data,
                        sourceBase,
                        window,
                        targetBase,
                        model.WindowSamples);
                }

                long qpcFrequency = _sessionReader.Header.QpcFrequency > 0
                    ? _sessionReader.Header.QpcFrequency
                    : _qpcFreq;

                long endQpc = epochBlock.QpcTimestamp + (long)Math.Round(
                    (double)(startSample + model.WindowSamples - 1) * qpcFrequency / model.SampleRate);

                SsvepAnalysisResult result = _ssvepProcessor.AnalyzeWindow(window, endQpc, eogCheck);
                if (result != null)
                    _analysisGlView.SetResult(result);
            }
            catch (Exception ex)
            {
                _analysisGlView.SetStatus("SSVEP epoch analysis error: " + ex.Message);
                AppendSsvepStatus("Epoch analysis error: " + ex.Message);
            }
        }

        private static int[] ResolveSsvepModelChannelIndices(
            string[] sourceNames,
            string[] modelChannelNames)
        {
            if (sourceNames == null || modelChannelNames == null)
                throw new InvalidOperationException("Missing channel names for SSVEP analysis.");

            int[] indices = new int[modelChannelNames.Length];

            for (int modelChannel = 0; modelChannel < modelChannelNames.Length; modelChannel++)
            {
                int found = -1;

                for (int sourceChannel = 0; sourceChannel < sourceNames.Length; sourceChannel++)
                {
                    if (string.Equals(
                        sourceNames[sourceChannel],
                        modelChannelNames[modelChannel],
                        StringComparison.OrdinalIgnoreCase))
                    {
                        found = sourceChannel;
                        break;
                    }
                }

                if (found < 0)
                {
                    throw new InvalidOperationException(
                        "Recording does not contain SSVEP model channel: " +
                        modelChannelNames[modelChannel]);
                }

                indices[modelChannel] = found;
            }

            return indices;
        }

        private void PrepareViewerForStaticPage(int channelsCount, int sampleRate, int timeBaseSeconds)
        {
            _viewerPlaybackMode = true;

            // Static recording preview must not be mixed with live SSVEP analysis.
            if (_routeRuntimeController != null)
                _routeRuntimeController.DisconnectProcessorInputRoute();

            if (_ssvepProcessor != null)
                _ssvepProcessor.Reset();
            if (_analysisGlView != null)
                _analysisGlView.Clear();

            _glScope.DetachInput();
            _glScope.AttachInput(_playbackStream);
            _glScope.EnterPreviewStaticPageMode(
                channelsCount,
                sampleRate,
                timeBaseSeconds,
                GetViewerScaleVolts());

            _glScope.ClearEventMarkers();

            if (_timeFrequencyGlView != null)
            {
                _timeFrequencyGlView.DetachInput();
                _timeFrequencyGlView.AttachInput(_playbackStream);
                _timeFrequencyGlView.Configure(
                    channelsCount,
                    sampleRate,
                    GetTimeFrequencyTimeBaseSeconds(),
                    GetTimeFrequencyScaleVolts());
            }
        }


        // 
        private void GoToEpochInViewer(EpochInfo epoch)
        {
            if (epoch == null || _sessionReader == null)
                return;

            if (_sessionPlayback != null)
                _sessionPlayback.Stop();

            ResetPreviewStaticPageCache();

            long targetSourceQpc = epoch.EndQpc;

            if (targetSourceQpc < _openedPlaybackStartQpc)
                targetSourceQpc = _openedPlaybackStartQpc;

            if (_openedPlaybackEndQpc > _openedPlaybackStartQpc &&
                targetSourceQpc > _openedPlaybackEndQpc)
            {
                targetSourceQpc = _openedPlaybackEndQpc;
            }

            int value = ConvertPlaybackQpcToTrackBarValue(targetSourceQpc);

            _playbackTrackBarInternalUpdate = true;
            try
            {
                _ctrlsLock = true;
                try
                {
                    trackBarPlayback.Value = value;
                }
                finally
                {
                    _ctrlsLock = false;
                }
            }
            finally
            {
                _playbackTrackBarInternalUpdate = false;
            }

            PreviewRecordingAtSourceQpc(targetSourceQpc);
            UpdatePlaybackUi();
        }


        private void tlStrpPlayRecording_Click(object sender, EventArgs e)
        {
            if (_sessionReader == null || _sessionReader.BlockCount == 0)
                return;

            if (_sessionPlayback == null)
                return;

            if (_sessionPlayback.State == SessionPlaybackState.Playing)
            {
                _sessionPlayback.Pause();
                UpdatePlaybackUi();
                return;
            }

            long startSourceQpc = ConvertTrackBarValueToPlaybackQpc(trackBarPlayback.Value);
            StartPlaybackFromSourceQpc(startSourceQpc);

/*            if (_sessionPlayback.State == SessionPlaybackState.Paused)
            {
                _sessionPlayback.Play();
                return;
            }

            _viewerPlaybackMode = true;

            _glScope.DetachInput();
            _glScope.AttachInput(_playbackStream);
            _glScope.SetFreezeTimeline(false);
            _glScope.Configure(
                _sessionReader.Header.ChannelCount,
                _sessionReader.Header.SampleRate,
                GetViewerTimeBaseSeconds(),
                GetViewerScaleVolts());
            _glScope.ClearEventMarkers();

            _sessionPlayback.Start(
                _sessionReader,
                _playbackStream,
                _glScope,
                _sessionReader.GetFirstSourceQpc(),
                _sessionReader.GetLastSourceQpc());*/
        }


        private void tlStrpStopPlayback_Click(object sender, EventArgs e)
        {
            StopPlaybackAndReturnViewerToLive();
        }


        // Возвращает viewer из playback-режима обратно к live-route, не меняя конфигурацию routes.
        private void StopPlaybackAndReturnViewerToLive()
        {
            if (_sessionPlayback != null)
                _sessionPlayback.Stop();

            if (_ssvepProcessor != null)
                _ssvepProcessor.Reset();
            if (_analysisGlView != null)
                _analysisGlView.Clear();

            ResetPreviewStaticPageCache();

            _viewerPlaybackMode = false;

            _glScope.ExitPreviewStaticPageMode();
            _glScope.SetFreezeTimeline(false);
            _glScope.ClearEventMarkers();

            if (_routeRuntimeController != null)
            {
                _routeRuntimeController.ApplyProcessorInputRoute(false);
                _routeRuntimeController.ApplyViewerInputRoute(false, _viewerPlaybackMode, true);
            }

            UpdatePlaybackUi();
        }


        private void treeViewServices_AfterSelect(object sender, TreeViewEventArgs e)
        {
            ServiceTreeNodeTag tag = e.Node.Tag as ServiceTreeNodeTag;
            if (tag == null)
                return;

            _selectedServiceSlot = _serviceCatalog.FindServiceSlot(_serviceSlotList, tag.ServiceId);
            _selectedServiceDataOutputSlot = null;

            if (_selectedServiceSlot == null)
                return;

            if (tag.ServiceTreeNodeKind == ServiceTreeNodeKind.ServiceOutput)
                _selectedServiceDataOutputSlot = _serviceCatalog.FindServiceDataOutputSlot(_selectedServiceSlot, tag.ServiceDataOutputId);

            if (tag.ServiceTreeNodeKind == ServiceTreeNodeKind.Service &&
                _selectedServiceSlot.ServiceIsRunning &&
                _serviceRuntimeController != null &&
                _serviceRuntimeController.IsServiceHostedModeEnabled(_selectedServiceSlot))
            {
                _serviceRuntimeController.ShowServiceInHostPanel(_selectedServiceSlot);
            }
        }


        private void treeViewServices_NodeMouseDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            ServiceTreeNodeTag tag = e.Node.Tag as ServiceTreeNodeTag;
            if (tag == null)
                return;

            if (tag.ServiceTreeNodeKind != ServiceTreeNodeKind.Service)
                return;

            ServiceSlot serviceSlot = _serviceCatalog.FindServiceSlot(_serviceSlotList, tag.ServiceId);
            if (serviceSlot == null)
                return;

            if (_serviceRuntimeController != null)
                _serviceRuntimeController.StartService(serviceSlot);
        }


        private void btnStartAllServices_Click(object sender, EventArgs e)
        {
            if (_serviceRuntimeController != null)
                _serviceRuntimeController.StartAllDiscoveredServices();
        }


        private void btnApplyAllRoutes_Click(object sender, EventArgs e)
        {
            ApplySavedRoutes(true);
        }


        // Централизованно подключает все drag-and-drop и tree-события, чтобы не дублировать подписки.
        private void ConfigureDragAndDropUi()
        {
            treeViewServices.AfterSelect -= treeViewServices_AfterSelect;
            treeViewServices.AfterSelect += treeViewServices_AfterSelect;

            treeViewServices.NodeMouseDoubleClick -= treeViewServices_NodeMouseDoubleClick;
            treeViewServices.NodeMouseDoubleClick += treeViewServices_NodeMouseDoubleClick;

            treeViewServices.ItemDrag -= treeViewServices_ItemDrag;
            treeViewServices.ItemDrag += treeViewServices_ItemDrag;

            treeViewPipeline.ItemDrag -= treeViewPipeline_ItemDrag;
            treeViewPipeline.ItemDrag += treeViewPipeline_ItemDrag;

            treeViewPipeline.AllowDrop = true;
            treeViewPipeline.DragEnter -= treeViewPipeline_DragEnter;
            treeViewPipeline.DragEnter += treeViewPipeline_DragEnter;
            treeViewPipeline.DragOver -= treeViewPipeline_DragOver;
            treeViewPipeline.DragOver += treeViewPipeline_DragOver;
            treeViewPipeline.DragDrop -= treeViewPipeline_DragDrop;
            treeViewPipeline.DragDrop += treeViewPipeline_DragDrop;

            if (lblViewerInputPin != null)
            {
                lblViewerInputPin.AllowDrop = true;
                lblViewerInputPin.DragEnter -= lblViewerInputPin_DragEnter;
                lblViewerInputPin.DragEnter += lblViewerInputPin_DragEnter;
                lblViewerInputPin.DragDrop -= lblViewerInputPin_DragDrop;
                lblViewerInputPin.DragDrop += lblViewerInputPin_DragDrop;
            }

            if (label9 != null)
            {
                label9.AllowDrop = true;
                label9.DragEnter -= lblViewerInputPin_DragEnter;
                label9.DragEnter += lblViewerInputPin_DragEnter;
                label9.DragDrop -= lblViewerInputPin_DragDrop;
                label9.DragDrop += lblViewerInputPin_DragDrop;
            }
        }


        private void treeViewServices_ItemDrag(object sender, ItemDragEventArgs e)
        {
            TreeNode node = e.Item as TreeNode;
            if (node == null)
                return;

            ServiceTreeNodeTag tag = node.Tag as ServiceTreeNodeTag;
            if (tag == null)
                return;

            if (tag.ServiceTreeNodeKind != ServiceTreeNodeKind.ServiceOutput)
                return;

            RouteDragInfo dragInfo = new RouteDragInfo
            {
                SourceNodeId = _routeStore.MakeServiceNodeId(tag.ServiceId),
                SourcePinId = tag.ServiceDataOutputId,
                RouteDataType = RouteDataTypeSignal,
                DisplayText = tag.ServiceId + "." + tag.ServiceDataOutputId
            };

            DoDragDrop(dragInfo, DragDropEffects.Copy);
        }


        private void treeViewPipeline_ItemDrag(object sender, ItemDragEventArgs e)
        {
            TreeNode node = e.Item as TreeNode;
            if (node == null)
                return;

            string tag = node.Tag as string;
            if (string.IsNullOrWhiteSpace(tag))
                return;

            int sep = tag.LastIndexOf('.');
            if (sep <= 0 || sep >= tag.Length - 1)
                return;

            string nodeId = tag.Substring(0, sep);
            string pinId = tag.Substring(sep + 1);

            SignalProcessorSlot processorSlot = _signalProcessorCatalog.FindByOutputPin(nodeId, pinId);
            if (processorSlot == null)
                return;

            RouteDragInfo dragInfo = new RouteDragInfo
            {
                SourceNodeId = processorSlot.ProcessorNodeId,
                SourcePinId = processorSlot.OutputPinId,
                RouteDataType = RouteDataTypeSignal,
                DisplayText = processorSlot.ProcessorDisplayName + "." + processorSlot.OutputPinId
            };

            DoDragDrop(dragInfo, DragDropEffects.Copy);
        }


        private void treeViewPipeline_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(RouteDragInfo)))
                e.Effect = DragDropEffects.Copy;
            else
                e.Effect = DragDropEffects.None;
        }


        // Разрешает drop только на input-пины зарегистрированных процессоров и только для signal-route.
        private void treeViewPipeline_DragOver(object sender, DragEventArgs e)
        {
            RouteDragInfo dragInfo = e.Data.GetData(typeof(RouteDragInfo)) as RouteDragInfo;
            if (dragInfo == null || !string.Equals(dragInfo.RouteDataType, RouteDataTypeSignal, StringComparison.OrdinalIgnoreCase))
            {
                e.Effect = DragDropEffects.None;
                return;
            }

            System.Drawing.Point clientPoint = treeViewPipeline.PointToClient(new System.Drawing.Point(e.X, e.Y));
            TreeNode node = treeViewPipeline.GetNodeAt(clientPoint);
            if (node == null)
            {
                e.Effect = DragDropEffects.None;
                return;
            }

            string tag = node.Tag as string;
            if (string.IsNullOrWhiteSpace(tag))
            {
                e.Effect = DragDropEffects.None;
                return;
            }

            int sep = tag.LastIndexOf('.');
            if (sep <= 0 || sep >= tag.Length - 1)
            {
                e.Effect = DragDropEffects.None;
                return;
            }

            string nodeId = tag.Substring(0, sep);
            string pinId = tag.Substring(sep + 1);

            SignalProcessorSlot processorSlot = _signalProcessorCatalog.FindByInputPin(nodeId, pinId);
            if (processorSlot != null)
                e.Effect = DragDropEffects.Copy;
            else
                e.Effect = DragDropEffects.None;
        }


        // Сохраняет route source -> input выбранного процессора. Сам поток здесь не запускается.
        private void treeViewPipeline_DragDrop(object sender, DragEventArgs e)
        {
            RouteDragInfo dragInfo = e.Data.GetData(typeof(RouteDragInfo)) as RouteDragInfo;
            if (dragInfo == null)
                return;

            System.Drawing.Point clientPoint = treeViewPipeline.PointToClient(new System.Drawing.Point(e.X, e.Y));
            TreeNode node = treeViewPipeline.GetNodeAt(clientPoint);
            if (node == null)
                return;

            string tag = node.Tag as string;
            if (string.IsNullOrWhiteSpace(tag))
                return;

            int sep = tag.LastIndexOf('.');
            if (sep <= 0 || sep >= tag.Length - 1)
                return;

            string nodeId = tag.Substring(0, sep);
            string pinId = tag.Substring(sep + 1);

            SignalProcessorSlot processorSlot = _signalProcessorCatalog.FindByInputPin(nodeId, pinId);
            if (processorSlot == null)
                return;

            if (string.Equals(dragInfo.SourceNodeId, processorSlot.ProcessorNodeId, StringComparison.OrdinalIgnoreCase))
                return;

            _routeStore.SaveOrReplaceRoute(
                dragInfo.SourceNodeId,
                dragInfo.SourcePinId,
                processorSlot.ProcessorNodeId,
                processorSlot.InputPinId,
                RouteDataTypeSignal);

            BuildTreeViewServices();
            BuildTreeViewPipeline();
            UpdateViewerInputPinText();
        }


        private void lblViewerInputPin_DragEnter(object sender, DragEventArgs e)
        {
            RouteDragInfo dragInfo = e.Data.GetData(typeof(RouteDragInfo)) as RouteDragInfo;
            if (dragInfo == null)
            {
                e.Effect = DragDropEffects.None;
                return;
            }

            if (string.Equals(dragInfo.RouteDataType, RouteDataTypeSignal, StringComparison.OrdinalIgnoreCase))
                e.Effect = DragDropEffects.Copy;
            else
                e.Effect = DragDropEffects.None;
        }


        // Сохраняет route на вход viewer и обновляет UI без немедленного переподключения потоков.
        private void lblViewerInputPin_DragDrop(object sender, DragEventArgs e)
        {
            RouteDragInfo dragInfo = e.Data.GetData(typeof(RouteDragInfo)) as RouteDragInfo;
            if (dragInfo == null)
                return;

            if (!string.Equals(dragInfo.RouteDataType, RouteDataTypeSignal, StringComparison.OrdinalIgnoreCase))
                return;

            _routeStore.SaveOrReplaceRoute(
                dragInfo.SourceNodeId,
                dragInfo.SourcePinId,
                NodeIdSignalViewer,
                PinIdIn,
                RouteDataTypeSignal);

            BuildTreeViewServices();
            BuildTreeViewPipeline();
            UpdateViewerInputPinText();
        }


        // Разрешает ручное раскрытие/сворачивание дерева только при клике по значку plus/minus.
        private void treeViewServices_MouseDown(object sender, MouseEventArgs e)
        {
          //  TreeViewHitTestInfo hit = treeViewServices.HitTest(e.X, e.Y);
          //  _allowMouseToggle =
          //      hit != null &&
          //      hit.Location == TreeViewHitTestLocations.PlusMinus;
        }


        private void treeViewServices_MouseUp(object sender, MouseEventArgs e)
        {
         //   _allowMouseToggle = false;
        }


        // Блокирует автоматическое раскрытие дерева, если пользователь не нажал именно plus/minus.
        private void treeViewServices_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
         //   if (!_allowMouseToggle)
         //       e.Cancel = true;
        }


        // Блокирует автоматическое сворачивание дерева, если пользователь не нажал именно plus/minus.
        private void treeViewServices_BeforeCollapse(object sender, TreeViewCancelEventArgs e)
        {
         //   if (!_allowMouseToggle)
          //      e.Cancel = true;
        }

        private void btnDeactivateSavedRoutes_Click(object sender, EventArgs e)
        {
            DeactivateSavedRoutes();
        }

        private void numUpDwnTimeBaseInSec_ValueChanged(object sender, EventArgs e)
        {
            if (_ctrlsLock)
                return;

            ApplyViewerTimeBaseChange((int)numUpDwnTimeBaseInSec.Value);
        }

        private void cmbBxScaleAmpl_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_ctrlsLock)
                return;

            string text = cmbBxScaleAmpl.SelectedItem as string;
            float scaleVolts;
            if (!TryParseScaleComboText(text, out scaleVolts))
                return;

            ApplyViewerScaleChange(scaleVolts);
        }

        private void numUpDwnTimeFBaseInSec_ValueChanged(object sender, EventArgs e)
        {
            if (_ctrlsLock)
                return;

            ApplyTimeFrequencyTimeBaseChange((int)numUpDwnTimeFBaseInSec.Value);
        }

        private float GetTimeFrequencyScale(int index)
        {
            float scale = 0.5e-6f;

            for (int i = 1; i <= index; i++)
                scale *= i % 3 == 0 ? 2.5f : 2.0f;

            return scale;
        }

        private void cmbBxScaleTFAmpl_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_ctrlsLock)
                return;

            int index = cmbBxScaleTFAmpl.SelectedIndex;
            if (index < 0)
                return;

            ApplyTimeFrequencyScaleChange(GetTimeFrequencyScale(index));
        }

        private void InitializeEogRejectionUi()
        {
            if (_polyHubSettings.PolyHubUiSettings == null)
                _polyHubSettings.PolyHubUiSettings = new PolyHubUiSettings();

            int profileIndex = _polyHubSettings.PolyHubUiSettings.EogRejectionProfileIndex;
            _bindingEogRejectionUi = true;
            try
            {
                cmbBxEogRejectionProfile.SelectedIndex = profileIndex == 1 ? 1 : 0;
            }
            finally
            {
                _bindingEogRejectionUi = false;
            }

            _polyHubSettings.PolyHubUiSettings.EogRejectionProfileIndex =
                cmbBxEogRejectionProfile.SelectedIndex;
            ShowEogRejectionProfile();
            ApplyEogRejectionSettings();
        }

        // The current SSVEP processor is the gaze-selection layer. The combo
        // edits independent profiles; it is not a processing-mode switch.
        private EogRejection GetActiveEogRejection()
        {
            return new EogRejection(_polyHubSettings.EogRejectionSettings.GazeSelection);
        }

        private void ApplyEogRejectionSettings()
        {
            if (_ssvepProcessor != null)
                _ssvepProcessor.SetEogRejection(GetActiveEogRejection());
        }

        private void AppendEpochRejectionSummary()
        {
            if (_sessionReader == null) return;
            EogRejection rejection = GetActiveEogRejection();
            var epochs = _sessionReader.GetAllEpochs();
            int rejectedCount = 0;
            bool enabled = rejection.Enabled;
            foreach (EpochInfo epoch in epochs)
            {
                if (CovertCalibrationEog.IsCovertClass(epoch.ClassId))
                    enabled |= new EogRejection(_polyHubSettings.EogRejectionSettings.CovertAttention).Enabled;
                if (CheckRecordedEpoch(_sessionReader, epoch, rejection).IsRejected) rejectedCount++;
            }
            AppendSsvepStatus("Rejected epochs: " + rejectedCount + " / " + epochs.Count +
                (enabled ? "." : " (EOG disabled)."));
        }

        private void RefreshEpochRejection()
        {
            if (_sessionReader == null) return;
            EogRejection rejection = GetActiveEogRejection();
            foreach (System.Windows.Forms.DataGridViewRow row in dataGridViewEpochs.Rows)
            {
                EpochInfo epoch = row.DataBoundItem as EpochInfo;
                if (epoch == null) continue;
                EogRejectionResult check = CheckRecordedEpoch(_sessionReader, epoch, rejection);
                row.Tag = check;
                row.DefaultCellStyle.ForeColor = check.IsRejected ? System.Drawing.Color.Gray : dataGridViewEpochs.ForeColor;
                row.DefaultCellStyle.SelectionForeColor = check.IsRejected ? System.Drawing.Color.Silver : dataGridViewEpochs.DefaultCellStyle.SelectionForeColor;
            }
            dataGridViewEpochs.Refresh();
        }

        private void HandleEogRejection(SsvepAnalysisResult result)
        {
            if (!result.IsRejected) return;
            if (_glScope != null)
                _glScope.AddRejectedInterval(result.WindowStartQpc, result.QpcTimestamp);
            if (_timeFrequencyGlView != null)
                _timeFrequencyGlView.AddRejectedInterval(result.WindowStartQpc, result.QpcTimestamp);
            if (!result.IsNewRejection) return;
            if (_glScope != null)
                _glScope.AddEventMarker(result.QpcTimestamp, EogRejection.RejectedEventCode, 0);
            if (_timeFrequencyGlView != null)
                _timeFrequencyGlView.AddEventMarker(result.QpcTimestamp, EogRejection.RejectedEventCode, 0);
            AppendSsvepStatus("Rejected: " + result.EogRejection.Reason);
            // Keep the signal intact. A state-0 diagnostic does not create an
            // extra calibration class/epoch in existing PHS readers.
            if (_sessionRecorder != null && !_viewerPlaybackMode)
                _sessionRecorder.OnServiceEvent(new PolyHubIpc.ServiceEventPacket
                {
                    QpcTimestamp = result.QpcTimestamp, Code = (int)PolyHubIpc.ServiceEventCode.UserEvent,
                    State = 0, Value0 = EogRejection.RejectedEventCode, Value1 = result.EogRejection.Flags
                });
            ServiceSlot piano = _serviceCatalog.FindServiceSlot(_serviceSlotList, "NeuroPiano");
            if (piano == null) return;
            _serviceRuntimeController.EnsureServiceEvtLinkCreated(piano);
            if (piano.ServiceEvtLink != null)
                piano.ServiceEvtLink.SendEvent(PolyHubIpc.ServiceEventCode.UserEvent, 0,
                    EogRejection.RejectedEventCode, result.EogRejection.Flags,
                    "Rejected: " + result.EogRejection.Reason, result.QpcTimestamp);
        }

        private EogRejectionProfileSettings GetEditedEogRejectionProfile()
        {
            return cmbBxEogRejectionProfile.SelectedIndex == 1
                ? _polyHubSettings.EogRejectionSettings.CovertAttention
                : _polyHubSettings.EogRejectionSettings.GazeSelection;
        }

        private void ShowEogRejectionProfile()
        {
            EogRejectionProfileSettings profile = GetEditedEogRejectionProfile();
            _bindingEogRejectionUi = true;
            try
            {
                chkBxVeogRejection.Checked = profile.VeogEnabled;
                chkBxHeogRejection.Checked = profile.HeogEnabled;
                numUpDwnVeogThreshold.Value = (decimal)profile.VeogPeakToPeakThresholdMicrovolts;
                numUpDwnHeogThreshold.Value = (decimal)profile.HeogFixationDeviationThresholdMicrovolts;
                numUpDwnVeogThreshold.Enabled = profile.VeogEnabled;
                numUpDwnHeogThreshold.Enabled = profile.HeogEnabled;
            }
            finally
            {
                _bindingEogRejectionUi = false;
            }
        }

        private void cmbBxEogRejectionProfile_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_bindingEogRejectionUi || _polyHubSettings == null)
                return;

            ShowEogRejectionProfile();
            _polyHubSettings.PolyHubUiSettings.EogRejectionProfileIndex =
                cmbBxEogRejectionProfile.SelectedIndex;
            SaveEogRejectionSettings();
        }

        private void EogRejectionSettings_Changed(object sender, EventArgs e)
        {
            if (_bindingEogRejectionUi || _polyHubSettings == null)
                return;

            EogRejectionProfileSettings profile = GetEditedEogRejectionProfile();
            profile.VeogEnabled = chkBxVeogRejection.Checked;
            profile.HeogEnabled = chkBxHeogRejection.Checked;
            profile.VeogPeakToPeakThresholdMicrovolts = (double)numUpDwnVeogThreshold.Value;
            profile.HeogFixationDeviationThresholdMicrovolts = (double)numUpDwnHeogThreshold.Value;
            numUpDwnVeogThreshold.Enabled = profile.VeogEnabled;
            numUpDwnHeogThreshold.Enabled = profile.HeogEnabled;
            SaveEogRejectionSettings();
            if (cmbBxEogRejectionProfile.SelectedIndex == 1 && _covertCalibrationEog != null)
            {
                try { _covertCalibrationEog.SetRejection(new EogRejection(profile)); }
                catch (Exception ex)
                {
                    _covertCalibrationService.ServiceEvtLink.SendEvent(ServiceEventCode.Error, 1,
                        CovertCalibrationEog.StartCommand, 4, ex.Message);
                    StopCovertCalibrationMonitor();
                }
            }
            if (cmbBxEogRejectionProfile.SelectedIndex != 1) ApplyEogRejectionSettings();
            RefreshEpochRejection();
            AppendEpochRejectionSummary();
            ResetPreviewStaticPageCache();
            if (_viewerPlaybackMode && _sessionReader != null &&
                (_sessionPlayback == null || !_sessionPlayback.IsPlaying))
                PreviewRecordingAtSourceQpc(ConvertTrackBarValueToPlaybackQpc(trackBarPlayback.Value));
            AnalyzeCurrentSelectedEpochForSsvep();
            RefreshSpatialSpectra();
        }


        private void StopCovertCalibrationMonitor()
        {
            if (_covertCalibrationEog != null)
            {
                QueryPerformanceCounter(out _covertCalibrationEndQpc);
                _covertCalibrationEog.Dispose();
                if (_ssvepProcessor != null) _ssvepProcessor.Reset();
            }
            _covertCalibrationEog = null;
            _covertCalibrationService = null;
        }

        private void HandleCovertCalibrationEvent(ServiceSlot service, ServiceEventPacket evt)
        {
            if (service == null || service.ServiceManifest == null ||
                !string.Equals(service.ServiceManifest.ServiceId, "NeuroPiano", StringComparison.OrdinalIgnoreCase)) return;
            if ((ServiceEventCode)evt.Code == ServiceEventCode.State &&
                evt.State == 1 && evt.Value0 == CovertCalibrationEog.StartCommand)
            {
                StopCovertCalibrationMonitor();
                if (evt.Value1 == 0) return;
                try
                {
                    if (_viewerPlaybackMode) throw new InvalidOperationException("Stop recording playback before live covert calibration.");
                    var profile = _polyHubSettings.EogRejectionSettings.CovertAttention;
                    SignalStream source = _ssvepProcessor != null ? _ssvepProcessor.InputStream : null;
                    if (source == null && _routeRuntimeController != null) source = _routeRuntimeController.ViewerAttachedStream;
                    if (source == null) throw new InvalidOperationException("Covert calibration: no EEG input stream attached.");
                    _covertCalibrationEog = new CovertCalibrationEog(source, _qpcFreq, new EogRejection(profile));
                    _covertCalibrationService = service;
                    _covertCalibrationStartQpc = evt.QpcTimestamp;
                    _covertCalibrationEndQpc = 0;
                }
                catch (Exception ex)
                {
                    service.ServiceEvtLink.SendEvent(ServiceEventCode.Error, 1,
                        CovertCalibrationEog.StartCommand, 4, ex.Message);
                    AppendSsvepStatus("Covert calibration: " + ex.Message);
                }
                return;
            }
            if (_covertCalibrationEog != null && ReferenceEquals(service, _covertCalibrationService))
            {
                _covertCalibrationEog.OnEvent(evt);
                PumpCovertCalibration();
            }
        }

        private void PumpCovertCalibration()
        {
            if (_covertCalibrationEog == null || _covertCalibrationService == null) return;
            CovertCalibrationCheck result = _covertCalibrationEog.TryTakeResult();
            if (result == null) return;
            ServiceEventLink link = _covertCalibrationService.ServiceEvtLink;
            if (result.Rejection.IsRejected)
            {
                if (_glScope != null)
                {
                    _glScope.AddRejectedInterval(result.StartQpc, result.EndQpc);
                }
                if (_timeFrequencyGlView != null)
                {
                    _timeFrequencyGlView.AddRejectedInterval(result.StartQpc, result.EndQpc);
                }
                if (link != null) link.SendEvent(ServiceEventCode.UserEvent, 0,
                    EogRejection.RejectedEventCode, result.Rejection.Flags,
                    "Rejected: " + result.Rejection.Reason, result.EndQpc);
                AppendSsvepStatus("Covert epoch " + result.ClassId + " Rejected: " + result.Rejection.Reason);
            }
            else AppendSsvepStatus("Covert epoch " + result.ClassId + " accepted.");
            if (link != null) link.SendEvent(ServiceEventCode.UserEvent, 0,
                CovertCalibrationEog.TrialResultCommand, result.Rejection.Flags,
                result.Rejection.Reason ?? "Accepted", result.EndQpc);
        }

        private EogRejectionResult CheckRecordedEpoch(SessionReader reader, EpochInfo epoch,
            EogRejection gazeRejection, SignalBlock block = null)
        {
            if (CovertCalibrationEog.IsCovertClass(epoch.ClassId))
                return CovertCalibrationEog.CheckRecordedEpoch(reader, epoch,
                    new EogRejection(_polyHubSettings.EogRejectionSettings.CovertAttention));
            return gazeRejection.Check(block ?? reader.ReadEpochAsSingleBlock(epoch), reader.ChannelNames);
        }

        private void SaveEogRejectionSettings()
        {
            try
            {
                _polyHubSettingsStore.Save(_polyHubSettings);
            }
            catch (Exception ex)
            {
                AppendSsvepStatus("EOG settings save error: " + ex.Message);
            }
        }
    }
}
