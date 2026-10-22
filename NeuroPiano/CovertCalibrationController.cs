using PolyHubIpc;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace NeuroPiano
{
    internal enum CovertCalibrationState
    {
        Idle, Baseline, AnchorCue, AnchorSettling, Neutral, CheckNeutral, NeutralInterTrial, Cue, Settling,
        Stimulus, CheckStimulus, Feedback, InterTrial, BlockRest, Completed
    }

    // One fixed gaze anchor and the other seven visible keys as peripheral targets.
    internal sealed class CovertCalibrationController : IDisposable
    {
        public const int StartCommand = 2006;
        public const int TrialResultCommand = 2007;
        public const int NeutralClassBase = 1300;
        public const int DefaultRepetitionsPerTarget = 10;
        public const int DefaultNeutralDurationMs = 2500;

        private readonly Control _owner;
        private readonly PianoKeyboardControl _keyboard;
        private readonly ServiceEventLink _link;
        private readonly double[] _frequencies;
        private readonly NeuroPianoSettings _settings;
        private readonly Random _random = new Random();
        private readonly int[] _targets = new int[7];
        private readonly long _frequency;
        private long _deadline, _awaitedEnd;
        private int _anchor, _target, _position, _round, _transitionPending;
        private int _savedActive, _savedCue, _savedBackground, _savedFeedback;
        private bool _savedManual, _retry, _disposed;
        private volatile bool _running;

        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceCounter(out long qpc);
        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceFrequency(out long frequency);

        public CovertCalibrationController(Control owner, PianoKeyboardControl keyboard,
            ServiceEventLink link, double[] frequencies, NeuroPianoSettings settings)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _keyboard = keyboard ?? throw new ArgumentNullException(nameof(keyboard));
            _link = link ?? throw new ArgumentNullException(nameof(link));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            if (frequencies == null || frequencies.Length < 8)
                throw new ArgumentException("Eight SSVEP frequencies are required.", nameof(frequencies));
            _frequencies = frequencies;
            if (!QueryPerformanceFrequency(out _frequency) || _frequency <= 0)
                throw new InvalidOperationException("QueryPerformanceFrequency failed.");
            _keyboard.MultimediaTimerTick += TimerTick;
        }

        public event Action<int, long> FeedbackStarted;
        public event Action<string> Failed;
        public bool IsRunning { get { return _running; } }
        public CovertCalibrationState State { get; private set; }

        public static int GetDefaultStartMidiNote(int firstVisibleWhiteKeyIndex)
        {
            int index = 0;
            firstVisibleWhiteKeyIndex = PianoNotes.ClampVisibleWhiteKeyIndex(firstVisibleWhiteKeyIndex);
            for (int midi = PianoNotes.FirstMidiNote; midi <= PianoNotes.LastMidiNote; midi++)
            {
                if (PianoNotes.IsBlackKey(midi)) continue;
                if (index >= firstVisibleWhiteKeyIndex && index < firstVisibleWhiteKeyIndex + 8 && midi % 12 == 0)
                    return midi;
                index++;
            }
            throw new InvalidOperationException("No C note in the visible keyboard range.");
        }

        public bool Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CovertCalibrationController));
            if (_running) return false;
            _anchor = -1;
            var keys = _keyboard.KeyboardLayout.WhiteKeys;
            for (int i = 0; i < Math.Min(8, keys.Count); i++)
                if (keys[i].MidiNumber == _settings.CovertCalibrationStartMidiNote) _anchor = i;
            if (_anchor < 0)
                throw new InvalidOperationException("Covert calibration: start note must be one of the eight visible white keys.");

            _savedActive = _keyboard.ActiveStimulusVisibleIndex;
            _savedCue = _keyboard.AttentionCueVisibleIndex;
            _savedBackground = _keyboard.BackgroundCueVisibleIndex;
            _savedFeedback = _keyboard.FeedbackVisibleIndex;
            _savedManual = _keyboard.ManualInputEnabled;
            _round = _position = 0;
            _awaitedEnd = 0;
            _retry = false;
            CreateOrder();
            _keyboard.ReleaseCurrentKey();
            _keyboard.ManualInputEnabled = false;
            _keyboard.AttentionCueVisibleIndex = -1;
            _keyboard.BackgroundCueVisibleIndex = -1;
            _keyboard.FeedbackVisibleIndex = -1;
            _keyboard.ActiveStimulusVisibleIndex = -2;
            _running = true;
            long now = Now();
            _link.SendEvent(ServiceEventCode.State, 1, StartCommand,
                _settings.CovertCalibrationStartMidiNote, "Calibration 2 started.", now);
            State = CovertCalibrationState.Baseline;
            Deadline(now, _settings.SSVEPCalibrationBaselineDurationMs);
            return true;
        }

        public void Stop()
        {
            if (!_running) return;
            long now = Now();
            if (State == CovertCalibrationState.Neutral)
                Send(2, NeutralClassBase, 0, now);
            else if (State == CovertCalibrationState.Stimulus)
                Send(2, TargetClass, TargetFrequency, now);
            Finish(CovertCalibrationState.Idle, now);
        }

        public void Abort(string reason)
        {
            Stop();
            Action<string> handler = Failed;
            if (handler != null) handler(reason);
        }

        // A result must refer to the exact END currently awaiting validation.
        public bool HandleTrialResult(long endQpc, int flags)
        {
            if (!_running || endQpc != _awaitedEnd ||
                (State != CovertCalibrationState.CheckNeutral &&
                 State != CovertCalibrationState.CheckStimulus)) return false;
            bool neutral = State == CovertCalibrationState.CheckNeutral;
            _awaitedEnd = 0;
            if (neutral && flags == 0)
            {
                long now = Now();
                State = CovertCalibrationState.NeutralInterTrial;
                Deadline(now, _settings.SSVEPCalibrationBackgroundInterTrialDurationMs);
            }
            else
            {
                _retry = flags != 0;
                long now = Now();
                if (_retry)
                {
                    State = CovertCalibrationState.InterTrial;
                    Deadline(now, _settings.SSVEPCalibrationInterTrialDurationMs);
                }
                else
                {
                    _keyboard.FeedbackVisibleIndex = _target;
                    State = CovertCalibrationState.Feedback;
                    Deadline(now, _settings.SSVEPCalibrationFeedbackDurationMs);
                    Action<int, long> handler = FeedbackStarted;
                    if (handler != null) handler(_target, now);
                }
            }
            return true;
        }

        private int TargetClass { get { return NeutralClassBase + _target + 1; } }
        private int TargetFrequency { get { return (int)Math.Round(_frequencies[_target] * 1000.0); } }

        private void EnterAnchorCue(long now)
        {
            _target = _targets[_position];
            _keyboard.AttentionCueVisibleIndex = _anchor;
            _keyboard.BackgroundCueVisibleIndex = -1;
            _keyboard.FeedbackVisibleIndex = -1;
            _keyboard.ActiveStimulusVisibleIndex = -1;
            State = CovertCalibrationState.AnchorCue;
            Deadline(now, _settings.SSVEPCalibrationCueDurationMs);
        }

        private void Advance(long transitionQpc)
        {
            if (!_running) return;
            switch (State)
            {
                case CovertCalibrationState.Baseline:
                case CovertCalibrationState.BlockRest:
                    EnterAnchorCue(Now());
                    break;
                case CovertCalibrationState.AnchorCue:
                    _keyboard.AttentionCueVisibleIndex = -1;
                    _keyboard.FeedbackVisibleIndex = _anchor;
                    long anchorCueOff = Now();
                    State = CovertCalibrationState.AnchorSettling;
                    Deadline(anchorCueOff, _settings.SSVEPCalibrationSettlingDurationMs);
                    Action<int, long> handler = FeedbackStarted;
                    if (handler != null) handler(_anchor, anchorCueOff);
                    break;
                case CovertCalibrationState.AnchorSettling:
                    Send(1, NeutralClassBase, 0, transitionQpc);
                    State = CovertCalibrationState.Neutral;
                    Deadline(transitionQpc, _settings.CovertCalibrationNeutralDurationMs);
                    break;
                case CovertCalibrationState.Neutral:
                    Send(2, NeutralClassBase, 0, transitionQpc);
                    AwaitResult(CovertCalibrationState.CheckNeutral, transitionQpc);
                    break;
                case CovertCalibrationState.NeutralInterTrial:
                    _keyboard.AttentionCueVisibleIndex = _target;
                    long neutralPauseEnd = Now();
                    State = CovertCalibrationState.Cue;
                    Deadline(neutralPauseEnd, _settings.SSVEPCalibrationCueDurationMs);
                    break;
                case CovertCalibrationState.Cue:
                    _keyboard.AttentionCueVisibleIndex = -1;
                    long cueOff = Now();
                    _link.SendEvent(ServiceEventCode.State, 1, TargetClass, 0,
                        "Covert EOG check started", cueOff);
                    State = CovertCalibrationState.Settling;
                    Deadline(cueOff, _settings.SSVEPCalibrationSettlingDurationMs);
                    break;
                case CovertCalibrationState.Settling:
                    Send(1, TargetClass, TargetFrequency, transitionQpc);
                    State = CovertCalibrationState.Stimulus;
                    Deadline(transitionQpc, _settings.SSVEPCalibrationStimulusDurationMs);
                    break;
                case CovertCalibrationState.Stimulus:
                    Send(2, TargetClass, TargetFrequency, transitionQpc);
                    AwaitResult(CovertCalibrationState.CheckStimulus, transitionQpc);
                    break;
                case CovertCalibrationState.CheckNeutral:
                case CovertCalibrationState.CheckStimulus:
                    Abort("Covert calibration: no EOG validation received from PolyHub.");
                    break;
                case CovertCalibrationState.Feedback:
                    State = CovertCalibrationState.InterTrial;
                    Deadline(transitionQpc, _settings.SSVEPCalibrationInterTrialDurationMs);
                    break;
                case CovertCalibrationState.InterTrial:
                    if (!_retry)
                    {
                        _position++;
                        if (_position == _targets.Length)
                        {
                            _position = 0;
                            _round++;
                            if (_round == _settings.CovertCalibrationRepetitionsPerTarget)
                            {
                                Finish(CovertCalibrationState.Completed, Now());
                                break;
                            }
                            CreateOrder();
                            State = CovertCalibrationState.BlockRest;
                            Deadline(Now(), _settings.SSVEPCalibrationBlockRestDurationMs);
                            break;
                        }
                    }
                    _retry = false;
                    EnterAnchorCue(Now());
                    break;
            }
        }

        private void AwaitResult(CovertCalibrationState state, long endQpc)
        {
            State = state;
            _awaitedEnd = endQpc;
            Deadline(Now(), 5000);
        }

        private void CreateOrder()
        {
            int n = 0;
            for (int i = 0; i < 8; i++) if (i != _anchor) _targets[n++] = i;
            for (int i = _targets.Length - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1), temp = _targets[i];
                _targets[i] = _targets[j]; _targets[j] = temp;
            }
        }

        private void Finish(CovertCalibrationState state, long now)
        {
            _running = false;
            State = state;
            _awaitedEnd = 0;
            Interlocked.Exchange(ref _deadline, 0);
            _link.SendEvent(ServiceEventCode.State, 1, StartCommand, 0,
                state == CovertCalibrationState.Completed
                    ? "Calibration 2 completed." : "Calibration 2 stopped.", now);
            _keyboard.ActiveStimulusVisibleIndex = _savedActive;
            _keyboard.AttentionCueVisibleIndex = _savedCue;
            _keyboard.BackgroundCueVisibleIndex = _savedBackground;
            _keyboard.FeedbackVisibleIndex = _savedFeedback;
            _keyboard.ManualInputEnabled = _savedManual;
        }

        private void Send(int state, int code, int value, long qpc)
        {
            _link.SendEvent(ServiceEventCode.UserEvent, state, code, value,
                "Covert calibration", qpc);
        }

        private void Deadline(long start, int milliseconds)
        {
            Interlocked.Exchange(ref _deadline,
                start + Math.Max(1, (long)Math.Round(milliseconds * (double)_frequency / 1000.0)));
        }

        private static long Now() { long qpc; QueryPerformanceCounter(out qpc); return qpc; }

        private void TimerTick(long now)
        {
            if (!_running || _disposed) return;
            long end = Interlocked.Read(ref _deadline);
            if (end <= 0 || now < end || Interlocked.Exchange(ref _transitionPending, 1) != 0) return;
            try
            {
                _owner.BeginInvoke((Action)(() =>
                {
                    try { if (_running && Interlocked.Read(ref _deadline) == end) Advance(end); }
                    finally { Interlocked.Exchange(ref _transitionPending, 0); }
                }));
            }
            catch { Interlocked.Exchange(ref _transitionPending, 0); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _keyboard.MultimediaTimerTick -= TimerTick;
            _disposed = true;
        }
    }
}
