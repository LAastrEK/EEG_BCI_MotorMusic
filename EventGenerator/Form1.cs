using PolyHubIpc;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace EventGenerator
{
    public partial class fmMain : Form
    {
        private sealed class ActiveEventInfo
        {
            public int ClassId;
            public long StartQpc;
            public long EndQpc;
            public int DurationMs;
        }

        private const string ServiceSettingsFileName = "EventGenerator.json";
        private const string ServiceManifestFileName = "EventGenerator.service.json";

        private EventGeneratorSettings _serviceProgramSettings;
        private ServiceManifest _serviceManifest;

        private ServiceEventLink _serviceEventLink;

        // mmTimer
        public int mTimerId;
        private TimerEventHandler mHandler;
        private bool _timePeriodActive;

        private delegate void TimerEventHandler(int id, int msg, IntPtr user, int dw1, int dw2);

        private const int TIME_PERIODIC = 1;
        private const int EVENT_TYPE = TIME_PERIODIC;

        private readonly bool _hostedMode;
        private readonly object _sync = new object();

        // QPC
        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceCounter(out long value);

        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceFrequency(out long freq);

        private long _QpcFreq;
        private long _lastStatusUpdateQpc;

        private bool _serviceEnabled = true;
        private string _lastInboundStatusText = "Inbound: -";

        private readonly Dictionary<int, ActiveEventInfo> _activeEvents =
            new Dictionary<int, ActiveEventInfo>();

        // winmm
        [DllImport("winmm.dll")]
        private static extern int timeSetEvent(int delay, int resolution, TimerEventHandler handler, IntPtr user, int eventType);

        [DllImport("winmm.dll")]
        private static extern int timeKillEvent(int id);

        [DllImport("winmm.dll")]
        private static extern int timeBeginPeriod(int msec);

        [DllImport("winmm.dll")]
        private static extern int timeEndPeriod(int msec);

        public fmMain(): this(false)
        {
        }

        public fmMain(bool hosted)
        {
            _hostedMode = hosted;
            InitializeComponent();
            QueryPerformanceFrequency(out _QpcFreq);
            if (hosted)
            {
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
            }
        }

        private void fmMain_Load(object sender, EventArgs e)
        {
            QueryPerformanceCounter(out _lastStatusUpdateQpc);

            LoadLocalJsonFiles();
            ApplyServiceProgramSettingsToUi();

            ConnectServiceEventLink();

            timeBeginPeriod(1);
            _timePeriodActive = true;

            mHandler = new TimerEventHandler(TimerCallback);
            mTimerId = timeSetEvent(1, 1, mHandler, IntPtr.Zero, EVENT_TYPE);

            if (numUpDwnDurationTime != null)
            {
                numUpDwnDurationTime.Minimum = 10;
                numUpDwnDurationTime.Maximum = 600000;
                numUpDwnDurationTime.Value = 1000;
            }

            UpdateControlState();
            UpdateStatusStripText();
        }

        private void TimerCallback(int id, int msg, IntPtr user, int dw1, int dw2)
        {
            try
            {
                if (!IsHandleCreated || IsDisposed)
                    return;

                long qpcNow;
                QueryPerformanceCounter(out qpcNow);

                List<ActiveEventInfo> dueEvents = null;

                lock (_sync)
                {
                    foreach (KeyValuePair<int, ActiveEventInfo> kv in _activeEvents)
                    {
                        if (qpcNow >= kv.Value.EndQpc)
                        {
                            if (dueEvents == null)
                                dueEvents = new List<ActiveEventInfo>();

                            dueEvents.Add(kv.Value);
                        }
                    }

                    if (dueEvents != null)
                    {
                        for (int i = 0; i < dueEvents.Count; i++)
                            _activeEvents.Remove(dueEvents[i].ClassId);
                    }
                }

                if (dueEvents != null && _serviceEventLink != null)
                {
                    for (int i = 0; i < dueEvents.Count; i++)
                    {
                        ActiveEventInfo item = dueEvents[i];

                        _serviceEventLink.SendEvent(
                            ServiceEventCode.UserEvent,
                            2,                  // End
                            item.ClassId,       // Value0 = classId
                            item.DurationMs,    // Value1 = durationMs
                            "Class" + item.ClassId.ToString() + "End",
                            item.EndQpc);
                    }
                }

                long statusPeriodQpc = _QpcFreq / 10; // ~10 Гц
                if (qpcNow - _lastStatusUpdateQpc < statusPeriodQpc)
                    return;

                _lastStatusUpdateQpc = qpcNow;

                BeginInvoke((Action)(() =>
                {
                    UpdateControlState();
                    UpdateStatusStripText();
                }));
            }
            catch
            {
            }
        }

        private string GetServiceSettingsFilePath()
        {
            return System.IO.Path.Combine(Application.StartupPath, ServiceSettingsFileName);
        }

        private string GetServiceManifestFilePath()
        {
            return System.IO.Path.Combine(Application.StartupPath, ServiceManifestFileName);
        }

        private EventGeneratorSettings CreateDefaultServiceProgramSettings()
        {
            return new EventGeneratorSettings
            {
                WindowLeft = 100,
                WindowTop = 100,
                WindowWidth = 900,
                WindowHeight = 600,
                DefaultEventDurationMs = 1000
            };
        }

        private ServiceManifest CreateDefaultServiceManifest()
        {
            return new ServiceManifest
            {
                ServiceId = "EventGenerator",
                ServiceDisplayName = "Event Generator",
                ServiceProgramFolderName = "EventGenerator",
                ServiceExeFileName = "EventGenerator.exe",
                ServiceExeSubFolder = @"bin\Debug",
                ServiceHostArguments = "--hosted",
                ServiceSupportsHosted = true,
                ServiceEvtLinkManifest = new ServiceEvtLinkManifest
                {
                    ServiceEvtLinkMapName = @"Local\PolyHub.EventGenerator.1.ServiceEventLink",
                    HubToServiceEvtName = @"Local\PolyHub.EventGenerator.1.ServiceEventLink.HubToServiceEvent",
                    ServiceToHubEvtName = @"Local\PolyHub.EventGenerator.1.ServiceEventLink.ServiceToHubEvent",
                    ServiceEvtLinkCapacity = 256
                },
                ServiceDataOutputManifestList = new List<ServiceDataPortManifest>(),
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

            // Публикуем копию service.json в общую папку Services
           // JsonFileStore.Save(GetPublishedServiceManifestFilePath(), _serviceManifest);
        }

        private void ApplyServiceProgramSettingsToUi()
        {
            StartPosition = FormStartPosition.Manual;
            Left = _serviceProgramSettings.WindowLeft;
            Top = _serviceProgramSettings.WindowTop;
            Width = _serviceProgramSettings.WindowWidth;
            Height = _serviceProgramSettings.WindowHeight;

            if (numUpDwnDurationTime != null)
                numUpDwnDurationTime.Value = _serviceProgramSettings.DefaultEventDurationMs;
        }

        private void CaptureServiceProgramSettingsFromUi()
        {
            _serviceProgramSettings.WindowLeft = Left;
            _serviceProgramSettings.WindowTop = Top;
            _serviceProgramSettings.WindowWidth = Width;
            _serviceProgramSettings.WindowHeight = Height;

            if (numUpDwnDurationTime != null)
                _serviceProgramSettings.DefaultEventDurationMs = Decimal.ToInt32(numUpDwnDurationTime.Value);
        }

        private void SaveLocalJsonFiles()
        {
            JsonFileStore.Save(GetServiceSettingsFilePath(), _serviceProgramSettings);
            JsonFileStore.Save(GetServiceManifestFilePath(), _serviceManifest);
        }

        private void ConnectServiceEventLink()
        {
            if (_serviceEventLink != null)
            {
                _serviceEventLink.Dispose();
                _serviceEventLink = null;
            }

            _serviceEventLink = new ServiceEventLink(
                _serviceManifest.ServiceEvtLinkManifest.ServiceEvtLinkMapName,
                _serviceManifest.ServiceEvtLinkManifest.HubToServiceEvtName,
                _serviceManifest.ServiceEvtLinkManifest.ServiceToHubEvtName,
                ServiceEventSide.Service,
                _serviceManifest.ServiceEvtLinkManifest.ServiceEvtLinkCapacity);

            _serviceEventLink.EventReceived += ServiceEventLink_EventReceived;

            // Сообщаем хабу, что сервис поднялся.
            _serviceEventLink.SendEvent(
                ServiceEventCode.State,
                _serviceEnabled ? 1 : 0,
                0,
                0,
                _hostedMode ? "EventGenerator hosted online" : "EventGenerator standalone online");
        }

        private void ServiceEventLink_EventReceived(ServiceEventPacket evt)
        {
            ServiceEventCode code = (ServiceEventCode)evt.Code;

            lock (_sync)
            {
                _lastInboundStatusText =
                    "Inbound: " +
                    code.ToString() +
                    " state=" + evt.State.ToString() +
                    " v0=" + evt.Value0.ToString() +
                    " text=" + (evt.Text ?? string.Empty);
            }

            try
            {
                switch (code)
                {
                    case ServiceEventCode.Start:
                        _serviceEnabled = true;

                        _serviceEventLink.SendEvent(
                            ServiceEventCode.Ack,
                            1,
                            0,
                            0,
                            "EventGenerator started");

                        _serviceEventLink.SendEvent(
                            ServiceEventCode.State,
                            1,
                            0,
                            0,
                            "EventGenerator enabled");
                        break;

                    case ServiceEventCode.Stop:
                        _serviceEnabled = false;
                        StopAllActiveEvents(sendEndEvents: true);

                        _serviceEventLink.SendEvent(
                            ServiceEventCode.Ack,
                            0,
                            0,
                            0,
                            "EventGenerator stopped");

                        _serviceEventLink.SendEvent(
                            ServiceEventCode.State,
                            0,
                            0,
                            0,
                            "EventGenerator disabled");
                        break;

                    case ServiceEventCode.Reset:
                        StopAllActiveEvents(sendEndEvents: true);

                        _serviceEventLink.SendEvent(
                            ServiceEventCode.Ack,
                            _serviceEnabled ? 1 : 0,
                            0,
                            0,
                            "EventGenerator reset");
                        break;

                    case ServiceEventCode.Ping:
                        _serviceEventLink.SendEvent(
                            ServiceEventCode.Ack,
                            _serviceEnabled ? 1 : 0,
                            0,
                            0,
                            "EventGenerator pong");

                        _serviceEventLink.SendEvent(
                            ServiceEventCode.State,
                            _serviceEnabled ? 1 : 0,
                            0,
                            0,
                            "EventGenerator state");
                        break;
                }
            }
            catch
            {
            }

            try
            {
                BeginInvoke((Action)(() =>
                {
                    UpdateControlState();
                    UpdateStatusStripText();
                }));
            }
            catch
            {
            }
        }

        private void UpdateControlState()
        {
            bool canClick = _serviceEnabled;

            if (btnStartEvtClass1 != null)
                btnStartEvtClass1.Enabled = canClick;

            if (btnStartEvtClass2 != null)
                btnStartEvtClass2.Enabled = canClick;

            if (btnStartEvtClass3 != null)
                btnStartEvtClass3.Enabled = canClick;

            if (numUpDwnDurationTime != null)
                numUpDwnDurationTime.Enabled = canClick;
        }

        private void UpdateStatusStripText()
        {
            string inboundText;
            string activeText;
            string modeText = _hostedMode ? "hosted" : "standalone";
            string stateText = _serviceEnabled ? "enabled" : "disabled";

            lock (_sync)
            {
                inboundText = _lastInboundStatusText;
                activeText = BuildActiveEventText_NoLock();
            }

            string text =
                "Mode=" + modeText +
                " | State=" + stateText +
                " | " + activeText +
                " | " + inboundText;

            if (statusStripMain != null)
            {
                if (statusStripMain.Items.Count > 0)
                    statusStripMain.Items[0].Text = text;
                else
                    statusStripMain.Text = text;
            }
        }

        private string BuildActiveEventText_NoLock()
        {
            if (_activeEvents.Count == 0)
                return "ActiveEvents: none";

            StringBuilder sb = new StringBuilder();
            sb.Append("ActiveEvents: ");

            bool first = true;
            foreach (KeyValuePair<int, ActiveEventInfo> kv in _activeEvents)
            {
                if (!first)
                    sb.Append(", ");

                first = false;
                sb.Append("C");
                sb.Append(kv.Value.ClassId);
            }

            return sb.ToString();
        }

        private void StartClassEvent(int classId)
        {
            if (_serviceEventLink == null)
                return;

            if (!_serviceEnabled)
                return;

            int durationMs = 1000;
            if (numUpDwnDurationTime != null)
                durationMs = Decimal.ToInt32(numUpDwnDurationTime.Value);

            long qpcNow;
            QueryPerformanceCounter(out qpcNow);

            long durationQpc = (long)((double)durationMs * _QpcFreq / 1000.0);
            if (durationQpc <= 0)
                durationQpc = 1;

            bool created = false;

            lock (_sync)
            {
                ActiveEventInfo existing;
                if (_activeEvents.TryGetValue(classId, out existing))
                {
                    // Для MVP повторный старт того же класса, пока событие активно, игнорируем.
                    return;
                }

                _activeEvents[classId] = new ActiveEventInfo
                {
                    ClassId = classId,
                    StartQpc = qpcNow,
                    EndQpc = qpcNow + durationQpc,
                    DurationMs = durationMs
                };

                created = true;
            }

            if (!created)
                return;

            _serviceEventLink.SendEvent(
                ServiceEventCode.UserEvent,
                1,                      // Start
                classId,                // Value0 = classId
                durationMs,             // Value1 = durationMs
                "Class" + classId.ToString() + "Start",
                qpcNow);

            UpdateStatusStripText();
        }

        private void StopAllActiveEvents(bool sendEndEvents)
        {
            List<ActiveEventInfo> toStop = new List<ActiveEventInfo>();

            lock (_sync)
            {
                foreach (KeyValuePair<int, ActiveEventInfo> kv in _activeEvents)
                    toStop.Add(kv.Value);

                _activeEvents.Clear();
            }

            if (!sendEndEvents || _serviceEventLink == null)
                return;

            long qpcNow;
            QueryPerformanceCounter(out qpcNow);

            for (int i = 0; i < toStop.Count; i++)
            {
                ActiveEventInfo item = toStop[i];

                _serviceEventLink.SendEvent(
                    ServiceEventCode.UserEvent,
                    2,                  // End
                    item.ClassId,       // Value0 = classId
                    item.DurationMs,    // Value1 = durationMs
                    "Class" + item.ClassId.ToString() + "End",
                    qpcNow);
            }
        }

        private void fmMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            CaptureServiceProgramSettingsFromUi();
            SaveLocalJsonFiles();

            StopAllActiveEvents(sendEndEvents: false);

            if (mTimerId != 0)
            {
                try
                {
                    timeKillEvent(mTimerId);
                }
                catch
                {
                }

                mTimerId = 0;
            }

            if (_timePeriodActive)
            {
                try
                {
                    timeEndPeriod(1);
                }
                catch
                {
                }

                _timePeriodActive = false;
            }

            if (_serviceEventLink != null)
            {
                try
                {
                    _serviceEventLink.Dispose();
                }
                catch
                {
                }

                _serviceEventLink = null;
            }
        }

        private void btnStartEvtClass1_Click(object sender, EventArgs e)
        {
            StartClassEvent(1);
        }

        private void btnStartEvtClass2_Click(object sender, EventArgs e)
        {
            StartClassEvent(2);
        }

        private void btnStartEvtClass3_Click(object sender, EventArgs e)
        {
            StartClassEvent(3);
        }
    }
}
