using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using PolyHubIpc;

namespace NeuroPiano
{
    public partial class fmMain : Form
    {
        private const string ServiceSettingsFileName = "NeuroPiano.json";
        private const string ServiceManifestFileName = "NeuroPiano.service.json";
        private const int NoteVelocity = 100;

        private readonly bool _hostedMode;

        private NeuroPianoSettings _serviceProgramSettings;
        private ServiceManifest _serviceManifest;
        private ServiceEventLink _serviceEventLink;
        private PianoAudioEngine _audioEngine;
        private PianoKeyboardControl pianoKeyboard;

        private SSVEPStimulusEngine _ssvepStimulusEngine;
        private SSVEPCalibrationController _ssvepCalibrationController;
        private CovertCalibrationController _covertCalibrationController;
        private bool _serviceRunning = true;

        public fmMain()
            : this(false)
        {
        }

        public fmMain(bool hostedMode)
        {
            _hostedMode = hostedMode;
            InitializeComponent();

            if (_hostedMode)
            {
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
            }
        }

        private void fmMain_Load(object sender, EventArgs e)
        {
            LoadLocalJsonFiles();
            InitializeKeyboardControl();

            ApplyServiceProgramSettingsToUi();

            if (!_hostedMode)
                WindowState = FormWindowState.Maximized;

            InitializeDisplayInfo();

            InitializeSSVEPStimulusEngine();
            pianoKeyboard.SetSSVEPStimulusEngine(_ssvepStimulusEngine);

            ConnectServiceEventLink();
            InitializeSSVEPCalibrationController();
            InitializeAudio();
            PublishServiceState("NeuroPiano online");
        }

        private void fmMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_covertCalibrationController != null)
            {
                _covertCalibrationController.Dispose();
                _covertCalibrationController = null;
            }

            if (_ssvepCalibrationController != null)
            {
                _ssvepCalibrationController.FeedbackStarted -=
                    SSVEPCalibrationController_FeedbackStarted;

                _ssvepCalibrationController.Dispose();
                _ssvepCalibrationController = null;
            }

            if (pianoKeyboard != null)
                pianoKeyboard.ReleaseCurrentKey();

            if (_audioEngine != null)
            {
                _audioEngine.Dispose();
                _audioEngine = null;
            }

            CaptureServiceProgramSettingsFromUi();
            SaveLocalJsonFiles();

            if (_serviceEventLink != null)
            {
                _serviceEventLink.Dispose();
                _serviceEventLink = null;
            }
        }

        protected override bool ProcessCmdKey(ref Message msg,Keys keyData)
        {
            if (keyData == Keys.F5)
            {
                StartSSVEPCalibration();
                return true;
            }

            if (keyData == Keys.F6)
            {
                StartCovertCalibration();
                return true;
            }

            if (keyData == Keys.Escape &&
                ((_ssvepCalibrationController != null && _ssvepCalibrationController.IsRunning) ||
                 (_covertCalibrationController != null && _covertCalibrationController.IsRunning)))
            {
                StopSSVEPCalibration();
                return true;
            }

            return base.ProcessCmdKey(
                ref msg,
                keyData);
        }

        private void PianoKeyboard_NotePressed(PianoKey key)
        {
            if (!_serviceRunning || key == null)
                return;

            if (_audioEngine != null)
                _audioEngine.PlayNote(key.MidiNumber);

            SendPianoUserEvent(NeuroPianoUserEventState.NoteOn, key);
        }

        private void PianoKeyboard_NoteReleased(PianoKey key)
        {
            if (key == null)
                return;

            // Звук не обрываем при отпускании мыши:
            // каждый WAV уже содержит собственное естественное затухание.
            if (_serviceRunning)
                SendPianoUserEvent(NeuroPianoUserEventState.NoteOff, key);
        }

        private void InitializeKeyboardControl()
        {
            if (pianoKeyboard != null)
                return;

            pianoKeyboard = new PianoKeyboardControl();
            pianoKeyboard.CenterMidiNote = _serviceProgramSettings.CenterMidiNote;
            pianoKeyboard.FirstVisibleWhiteKeyIndex = _serviceProgramSettings.FirstVisibleWhiteKeyIndex;
            pianoKeyboard.Dock = DockStyle.Fill;
            pianoKeyboard.NotePressed += PianoKeyboard_NotePressed;
            pianoKeyboard.NoteReleased += PianoKeyboard_NoteReleased;

            pnlKeyboardHost.Controls.Add(pianoKeyboard);
            pianoKeyboard.BringToFront();
        }
        private void StartSSVEPCalibration()
        {
            if (!_serviceRunning ||
                _ssvepCalibrationController == null)
            {
                return;
            }

            if (_covertCalibrationController != null)
                _covertCalibrationController.Stop();

            if (pianoKeyboard != null)
                pianoKeyboard.ReleaseCurrentKey();

            if (_audioEngine != null)
                _audioEngine.AllNotesOff();

            _ssvepCalibrationController.Start();
        }

        private void StopSSVEPCalibration()
        {
            if (_ssvepCalibrationController != null)
                _ssvepCalibrationController.Stop();
            if (_covertCalibrationController != null)
                _covertCalibrationController.Stop();
        }

        private void StartCovertCalibration()
        {
            if (!_serviceRunning || pianoKeyboard == null || _serviceEventLink == null)
                return;
            try
            {
                if (_covertCalibrationController == null)
                {
                    _covertCalibrationController = new CovertCalibrationController(
                        this, pianoKeyboard, _serviceEventLink,
                        _serviceProgramSettings.SSVEPFrequencies, _serviceProgramSettings);
                    _covertCalibrationController.FeedbackStarted +=
                        SSVEPCalibrationController_FeedbackStarted;
                    _covertCalibrationController.Failed += reason =>
                        _serviceEventLink.SendEvent(ServiceEventCode.Error,
                            _serviceRunning ? 1 : 0, CovertCalibrationController.StartCommand, 0, reason);
                }
                if (_covertCalibrationController.IsRunning) return;
                if (_ssvepCalibrationController != null) _ssvepCalibrationController.Stop();
                if (_audioEngine != null) _audioEngine.AllNotesOff();
                _covertCalibrationController.Start();
            }
            catch (Exception ex)
            {
                _serviceEventLink.SendEvent(ServiceEventCode.Error,
                    _serviceRunning ? 1 : 0, CovertCalibrationController.StartCommand, 0, ex.Message);
            }
        }
        private void InitializeSSVEPCalibrationController()
        {
            SSVEPCalibrationStimulusMode stimulusMode =
                (SSVEPCalibrationStimulusMode)
                _serviceProgramSettings.SSVEPCalibrationStimulusMode;

            _ssvepCalibrationController =
                new SSVEPCalibrationController(
                    this,
                    pianoKeyboard,
                    _serviceEventLink,
                    _serviceProgramSettings.SSVEPFrequencies,
                    stimulusMode,
                    _serviceProgramSettings.SSVEPCalibrationBaselineDurationMs,
                    _serviceProgramSettings.SSVEPCalibrationCueDurationMs,
                    _serviceProgramSettings.SSVEPCalibrationSettlingDurationMs,
                    _serviceProgramSettings.SSVEPCalibrationStimulusDurationMs,
                    _serviceProgramSettings.SSVEPCalibrationFeedbackDurationMs,
                    _serviceProgramSettings.SSVEPCalibrationInterTrialDurationMs,
                    _serviceProgramSettings.SSVEPCalibrationBlockRestDurationMs,
                    _serviceProgramSettings.SSVEPCalibrationRepetitionsPerTarget,
                    _serviceProgramSettings.SSVEPCalibrationBackgroundCueDurationMs,
                    _serviceProgramSettings.SSVEPCalibrationBackgroundSettlingDurationMs,
                    _serviceProgramSettings.SSVEPCalibrationBackgroundStimulusDurationMs,
                    _serviceProgramSettings.SSVEPCalibrationBackgroundInterTrialDurationMs);

            _ssvepCalibrationController.FeedbackStarted +=
                SSVEPCalibrationController_FeedbackStarted;
        }

        private void SSVEPCalibrationController_FeedbackStarted(
            int visibleIndex,
            long qpcTimestamp)
        {
            if (!_serviceRunning ||
                _audioEngine == null ||
                pianoKeyboard == null)
            {
                return;
            }

            if (visibleIndex < 0 ||
                visibleIndex >= pianoKeyboard.KeyboardLayout.WhiteKeys.Count)
            {
                return;
            }

            PianoKey key =
                pianoKeyboard.KeyboardLayout.WhiteKeys[visibleIndex];

            _audioEngine.PlayNote(key.MidiNumber);
        }
        private void InitializeAudio()
        {
            try
            {
                _audioEngine = new PianoAudioEngine();
                _audioEngine.Initialize(GetPianoSampleFolder());
            }
            catch (Exception ex)
            {
                if (_audioEngine != null)
                {
                    _audioEngine.Dispose();
                    _audioEngine = null;
                }

                if (_serviceEventLink != null)
                {
                    _serviceEventLink.SendEvent(
                        ServiceEventCode.Error,
                        _serviceRunning ? 1 : 0,
                        0,
                        0,
                        "Audio: " + ex.Message);
                }

                if (!_hostedMode)
                {
                    MessageBox.Show(
                        this,
                        ex.Message,
                        "NeuroPiano audio",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
        }

        private void ConnectServiceEventLink()
        {
            if (_serviceEventLink != null)
            {
                _serviceEventLink.Dispose();
                _serviceEventLink = null;
            }

            ServiceEvtLinkManifest eventManifest = _serviceManifest.ServiceEvtLinkManifest;

            _serviceEventLink = new ServiceEventLink(
                eventManifest.ServiceEvtLinkMapName,
                eventManifest.HubToServiceEvtName,
                eventManifest.ServiceToHubEvtName,
                ServiceEventSide.Service,
                eventManifest.ServiceEvtLinkCapacity);

            _serviceEventLink.EventReceived += ServiceEventLink_EventReceived;
        }

        private void ServiceEventLink_EventReceived(ServiceEventPacket evt)
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            BeginInvoke((Action)(() => HandleServiceEvent(evt)));
        }

        private void HandleServiceEvent(ServiceEventPacket evt)
        {
            ServiceEventCode code = (ServiceEventCode)evt.Code;

            try
            {
                switch (code)
                {
                    case ServiceEventCode.Start:
                        _serviceRunning = true;
                        break;

                    case ServiceEventCode.Stop:
                        StopSSVEPCalibration();
                        _serviceRunning = false;
                        if (pianoKeyboard != null)
                            pianoKeyboard.ReleaseCurrentKey();

                        if (_audioEngine != null)
                            _audioEngine.AllNotesOff();

                        CaptureServiceProgramSettingsFromUi();
                        SaveLocalJsonFiles();
                        break;

                    case ServiceEventCode.Reset:
                        StopSSVEPCalibration();
                        if (pianoKeyboard != null)
                            pianoKeyboard.ReleaseCurrentKey();

                        if (_audioEngine != null)
                            _audioEngine.AllNotesOff();
                        break;

                    case ServiceEventCode.Ping:
                        break;

                    case ServiceEventCode.Error:
                        if (evt.Value0 == CovertCalibrationController.StartCommand &&
                            _covertCalibrationController != null && _covertCalibrationController.IsRunning)
                            _covertCalibrationController.Abort(evt.Text);
                        break;

                    case ServiceEventCode.UserEvent:
                        if (evt.Value0 ==
                            (int)NeuroPianoUserCommand.StartSSVEPCalibration)
                        {
                            StartSSVEPCalibration();
                        }
                        else if (evt.Value0 ==
                                 (int)NeuroPianoUserCommand.StopSSVEPCalibration)
                        {
                            StopSSVEPCalibration();
                        }
                        else if (evt.Value0 ==
                                 (int)NeuroPianoUserCommand.PlayVisibleKey)
                        {
                            if (_serviceRunning && pianoKeyboard != null &&
                                (_covertCalibrationController == null || !_covertCalibrationController.IsRunning))
                            {
                                pianoKeyboard.PressVisibleWhiteKey(
                                    evt.Value1);
                            }
                        }
                        else if (evt.Value0 ==
                                 (int)NeuroPianoUserCommand.ReleaseCurrentKey)
                        {
                            if (pianoKeyboard != null)
                                pianoKeyboard.ReleaseCurrentKey();
                        }
                        else if (evt.Value0 ==
                                 (int)NeuroPianoUserCommand.EpochRejected)
                        {
                            if (_serviceRunning && evt.State == 0)
                            {
                                if (_audioEngine != null)
                                    _audioEngine.PlayRejected();

                                if (_ssvepCalibrationController != null)
                                    _ssvepCalibrationController.RejectCurrentTrial(
                                        evt.QpcTimestamp);
                            }
                        }
                        else if (evt.Value0 == (int)NeuroPianoUserCommand.StartCovertCalibration)
                        {
                            StartCovertCalibration();
                        }
                        else if (evt.Value0 == (int)NeuroPianoUserCommand.CovertTrialResult)
                        {
                            if (evt.State == 0 && _covertCalibrationController != null)
                                _covertCalibrationController.HandleTrialResult(evt.QpcTimestamp, evt.Value1);
                        }
                        else
                        {
                            return;
                        }
                        break;
                }

                _serviceEventLink.SendEvent(
                    ServiceEventCode.Ack,
                    _serviceRunning ? 1 : 0,
                    (int)code,
                    0,
                    "OK");

                PublishServiceState("NeuroPiano state");
            }
            catch (Exception ex)
            {
                _serviceEventLink.SendEvent(
                    ServiceEventCode.Error,
                    _serviceRunning ? 1 : 0,
                    (int)code,
                    0,
                    ex.Message);
            }
        }

        private void SendPianoUserEvent(NeuroPianoUserEventState state, PianoKey key)
        {
            if (_serviceEventLink == null || key == null)
                return;

            int velocity = state == NeuroPianoUserEventState.NoteOn ? NoteVelocity : 0;

            _serviceEventLink.SendEvent(
                ServiceEventCode.UserEvent,
                (int)state,
                key.MidiNumber,
                velocity,
                key.NoteName);
        }

        private void PublishServiceState(string text)
        {
            if (_serviceEventLink == null)
                return;

            _serviceEventLink.SendEvent(
                ServiceEventCode.State,
                _serviceRunning ? 1 : 0,
                0,
                0,
                text);
        }

        private string GetPianoSampleFolder()
        {
            return Path.Combine(Application.StartupPath, "Samples", "Piano");
        }

        private string GetServiceSettingsFilePath()
        {
            return Path.Combine(Application.StartupPath, ServiceSettingsFileName);
        }

        private string GetServiceManifestFilePath()
        {
            return Path.Combine(Application.StartupPath, ServiceManifestFileName);
        }

        private void InitializeSSVEPStimulusEngine()
        {
            _ssvepStimulusEngine = new SSVEPStimulusEngine();

            _ssvepStimulusEngine.Initialize(
                _serviceProgramSettings.MonitorRefreshRate,
                _serviceProgramSettings.SSVEPFrequencies);
        }

        private void InitializeDisplayInfo()
        {
            Screen screen = Screen.FromControl(this);

            _serviceProgramSettings.MonitorRefreshRate =
                DisplayInfoProvider.GetRefreshRate(screen);
        }

        private void LoadLocalJsonFiles()
        {
            _serviceProgramSettings = JsonFileStore.LoadOrCreate(
                GetServiceSettingsFilePath(),
                CreateDefaultServiceProgramSettings);

            _serviceManifest = JsonFileStore.LoadOrCreate(
                GetServiceManifestFilePath(),
                CreateDefaultServiceManifest);

            // Backward compatibility with settings created before CenterMidiNote existed.
            if (!PianoNotes.IsInRange(_serviceProgramSettings.CenterMidiNote))
                _serviceProgramSettings.CenterMidiNote = PianoNotes.DefaultCenterMidiNote;

            if (_serviceProgramSettings.SSVEPCalibrationBaselineDurationMs <= 0)
            {
                _serviceProgramSettings.SSVEPCalibrationBaselineDurationMs =
                    SSVEPCalibrationController.DefaultBaselineDurationMs;
            }

            if (_serviceProgramSettings.SSVEPCalibrationCueDurationMs <= 0)
            {
                _serviceProgramSettings.SSVEPCalibrationCueDurationMs =
                    SSVEPCalibrationController.DefaultCueDurationMs;
            }

            if (_serviceProgramSettings.SSVEPCalibrationSettlingDurationMs <= 0)
            {
                _serviceProgramSettings.SSVEPCalibrationSettlingDurationMs =
                    SSVEPCalibrationController.DefaultSettlingDurationMs;
            }

            if (_serviceProgramSettings.SSVEPCalibrationStimulusDurationMs <= 0)
            {
                _serviceProgramSettings.SSVEPCalibrationStimulusDurationMs =
                    SSVEPCalibrationController.DefaultStimulusDurationMs;
            }

            if (_serviceProgramSettings.SSVEPCalibrationFeedbackDurationMs <= 0)
            {
                _serviceProgramSettings.SSVEPCalibrationFeedbackDurationMs =
                    SSVEPCalibrationController.DefaultFeedbackDurationMs;
            }

            if (_serviceProgramSettings.SSVEPCalibrationInterTrialDurationMs <= 0)
            {
                _serviceProgramSettings.SSVEPCalibrationInterTrialDurationMs =
                    SSVEPCalibrationController.DefaultInterTrialDurationMs;
            }

            if (_serviceProgramSettings.SSVEPCalibrationBlockRestDurationMs <= 0)
            {
                _serviceProgramSettings.SSVEPCalibrationBlockRestDurationMs =
                    SSVEPCalibrationController.DefaultBlockRestDurationMs;
            }

            if (_serviceProgramSettings.SSVEPCalibrationRepetitionsPerTarget <= 0)
            {
                _serviceProgramSettings.SSVEPCalibrationRepetitionsPerTarget =
                    SSVEPCalibrationController.DefaultRepetitionsPerTarget;
            }

            // Existing JSON files inherit their current key timings for background trials.
            if (_serviceProgramSettings.SSVEPCalibrationBackgroundCueDurationMs <= 0)
                _serviceProgramSettings.SSVEPCalibrationBackgroundCueDurationMs =
                    _serviceProgramSettings.SSVEPCalibrationCueDurationMs;

            if (_serviceProgramSettings.SSVEPCalibrationBackgroundSettlingDurationMs <= 0)
                _serviceProgramSettings.SSVEPCalibrationBackgroundSettlingDurationMs =
                    _serviceProgramSettings.SSVEPCalibrationSettlingDurationMs;

            if (_serviceProgramSettings.SSVEPCalibrationBackgroundStimulusDurationMs <= 0)
                _serviceProgramSettings.SSVEPCalibrationBackgroundStimulusDurationMs =
                    _serviceProgramSettings.SSVEPCalibrationStimulusDurationMs;

            if (_serviceProgramSettings.SSVEPCalibrationBackgroundInterTrialDurationMs <= 0)
                _serviceProgramSettings.SSVEPCalibrationBackgroundInterTrialDurationMs =
                    _serviceProgramSettings.SSVEPCalibrationInterTrialDurationMs;

            if (_serviceProgramSettings.SSVEPCalibrationStimulusMode <
                    (int)SSVEPCalibrationStimulusMode.IsolatedTarget ||
                _serviceProgramSettings.SSVEPCalibrationStimulusMode >
                    (int)SSVEPCalibrationStimulusMode.AllTargets)
            {
                _serviceProgramSettings.SSVEPCalibrationStimulusMode =
                    (int)SSVEPCalibrationStimulusMode.IsolatedTarget;
            }

            if (!PianoNotes.IsInRange(_serviceProgramSettings.CovertCalibrationStartMidiNote) ||
                PianoNotes.IsBlackKey(_serviceProgramSettings.CovertCalibrationStartMidiNote))
                _serviceProgramSettings.CovertCalibrationStartMidiNote = CovertCalibrationController.GetDefaultStartMidiNote(
                    _serviceProgramSettings.FirstVisibleWhiteKeyIndex);
            if (_serviceProgramSettings.CovertCalibrationRepetitionsPerTarget <= 0)
                _serviceProgramSettings.CovertCalibrationRepetitionsPerTarget = CovertCalibrationController.DefaultRepetitionsPerTarget;
            if (_serviceProgramSettings.CovertCalibrationNeutralDurationMs <= 0)
                _serviceProgramSettings.CovertCalibrationNeutralDurationMs = CovertCalibrationController.DefaultNeutralDurationMs;

            // Make all normalized timing fields visible in JSON on startup.
            JsonFileStore.Save(GetServiceSettingsFilePath(), _serviceProgramSettings);
        }

        private NeuroPianoSettings CreateDefaultServiceProgramSettings()
        {
            return new NeuroPianoSettings
            {
                WindowLeft = 80,
                WindowTop = 80,
                WindowWidth = 1400,
                WindowHeight = 760,
                CenterMidiNote = PianoNotes.DefaultCenterMidiNote,

                MonitorRefreshRate = 0,

                SSVEPFrequencies = new double[]
                {
/*                    1,
                    10,
                    3,
                    15,
                    5,
                    20,
                    10,
                    50*/
                    18,
                    22,
                    17,
                    21,
                    16,
                    20,
                    15,
                    19
                },
                ActiveStimulusVisibleIndex = -1,
                SSVEPCalibrationBaselineDurationMs = SSVEPCalibrationController.DefaultBaselineDurationMs,

                SSVEPCalibrationCueDurationMs = SSVEPCalibrationController.DefaultCueDurationMs,

                SSVEPCalibrationSettlingDurationMs = SSVEPCalibrationController.DefaultSettlingDurationMs,

                SSVEPCalibrationStimulusDurationMs = SSVEPCalibrationController.DefaultStimulusDurationMs,

                SSVEPCalibrationFeedbackDurationMs = SSVEPCalibrationController.DefaultFeedbackDurationMs,

                SSVEPCalibrationInterTrialDurationMs = SSVEPCalibrationController.DefaultInterTrialDurationMs,

                SSVEPCalibrationBlockRestDurationMs = SSVEPCalibrationController.DefaultBlockRestDurationMs,

                SSVEPCalibrationRepetitionsPerTarget = SSVEPCalibrationController.DefaultRepetitionsPerTarget,

                SSVEPCalibrationStimulusMode = (int)SSVEPCalibrationStimulusMode.IsolatedTarget,

                SSVEPCalibrationBackgroundCueDurationMs = SSVEPCalibrationController.DefaultBackgroundCueDurationMs,

                SSVEPCalibrationBackgroundSettlingDurationMs = SSVEPCalibrationController.DefaultBackgroundSettlingDurationMs,

                SSVEPCalibrationBackgroundStimulusDurationMs = SSVEPCalibrationController.DefaultBackgroundStimulusDurationMs,

                SSVEPCalibrationBackgroundInterTrialDurationMs = SSVEPCalibrationController.DefaultBackgroundInterTrialDurationMs,
                CovertCalibrationStartMidiNote = CovertCalibrationController.GetDefaultStartMidiNote(0),
                CovertCalibrationRepetitionsPerTarget = CovertCalibrationController.DefaultRepetitionsPerTarget,
                CovertCalibrationNeutralDurationMs = CovertCalibrationController.DefaultNeutralDurationMs
            };
        }

        private ServiceManifest CreateDefaultServiceManifest()
        {
            return new ServiceManifest
            {
                ServiceId = "NeuroPiano",
                ServiceDisplayName = "NeuroPiano",
                ServiceProgramFolderName = "NeuroPiano",
                ServiceExeFileName = "NeuroPiano.exe",
                ServiceExeSubFolder = @"bin\Debug",
                ServiceHostArguments = "--hosted",
                ServiceSupportsHosted = false,

                ServiceEvtLinkManifest = new ServiceEvtLinkManifest
                {
                    ServiceEvtLinkMapName = @"Local\PolyHub.NeuroPiano.1.ServiceEventLink",
                    HubToServiceEvtName = @"Local\PolyHub.NeuroPiano.1.ServiceEventLink.HubToServiceEvent",
                    ServiceToHubEvtName = @"Local\PolyHub.NeuroPiano.1.ServiceEventLink.ServiceToHubEvent",
                    ServiceEvtLinkCapacity = 256
                },

                ServiceDataOutputManifestList = new List<ServiceDataPortManifest>(),
                ServiceDataInputManifestList = new List<ServiceDataPortManifest>()
            };
        }

        private void ApplyServiceProgramSettingsToUi()
        {
            StartPosition = FormStartPosition.Manual;
            Left = _serviceProgramSettings.WindowLeft;
            Top = _serviceProgramSettings.WindowTop;
            Width = _serviceProgramSettings.WindowWidth;
            Height = _serviceProgramSettings.WindowHeight;

            if (pianoKeyboard != null)
            {
                pianoKeyboard.ActiveStimulusVisibleIndex =
                    _serviceProgramSettings.ActiveStimulusVisibleIndex;
            }
        }

        private void CaptureServiceProgramSettingsFromUi()
        {
            if (pianoKeyboard != null)
            {
                _serviceProgramSettings.CenterMidiNote = pianoKeyboard.CenterMidiNote;
                _serviceProgramSettings.FirstVisibleWhiteKeyIndex = pianoKeyboard.FirstVisibleWhiteKeyIndex;
                _serviceProgramSettings.ActiveStimulusVisibleIndex = pianoKeyboard.ActiveStimulusVisibleIndex;
            }

            if (WindowState != FormWindowState.Minimized)
            {
                _serviceProgramSettings.WindowLeft = Left;
                _serviceProgramSettings.WindowTop = Top;
                _serviceProgramSettings.WindowWidth = Width;
                _serviceProgramSettings.WindowHeight = Height;
            }
        }

        private void SaveLocalJsonFiles()
        {
            JsonFileStore.Save(GetServiceSettingsFilePath(), _serviceProgramSettings);
            JsonFileStore.Save(GetServiceManifestFilePath(), _serviceManifest);
        }
    }
}
