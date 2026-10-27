using PolyHubIpc;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace NeuroPiano
{
    internal enum CovertCalibrationPhaseState
    {
        Idle, Baseline, PhaseIntro, AnchorCue, AnchorSettling, Neutral, CheckNeutral,
        NeutralInterTrial, TargetCue, Settling, Stimulus, CheckStimulus, Feedback,
        InterTrial, BlockRest, NextPhase, Completed
    }

    // Calib2 v2: eight phases. In phase p the gaze anchor is white key p (yellow cue),
    // then each of the other seven keys becomes a covert attention target (red cue).
    // Cue durations are reused from NeuroPianoSettings unchanged. Epoch classes:
    // 1300 neutral, 1300 + phase*8 + rank + 1 for targets (1301..1357). Side of the
    // target relative to the anchor (left/right/neutral) is derived offline from the
    // white-key order, exactly like in the Python prototype calib2_eight_phases.py.
    internal sealed class CovertCalibrationEightPhasesController : IDisposable
    {
        public const int StartCommand = 2006;
        public const int TrialResultCommand = 2007;
        public const int NeutralClassBase = 1300;
        public const int PhaseStride = 8;
        public const int PhaseCount = 8;

        private readonly Control _owner;
        private readonly PianoKeyboardControl _keyboard;
        private readonly ServiceEventLink _link;
        private readonly double[] _frequencies;
        private readonly NeuroPianoSettings _settings;
        private readonly Random _random = new Random();
        private readonly int[] _targets = new int[7];
        private readonly long _frequency;
        private long _deadline, _awaitedEnd;
        private int _phase, _anchor, _target, _position, _round, _transitionPending;
        private int _savedActive, _savedCue, _savedBackground, _savedFeedback;
        private bool _savedManual, _retry, _disposed;
        private volatile bool _running;

        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceCounter(out long qpc);
        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceFrequency(out long frequency);

        public CovertCalibrationEightPhasesController(Control owner, PianoKeyboardControl keyboard,
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
        public CovertCalibrationPhaseState State { get; private set; }
        public int CurrentPhase { get { return _phase; } }

        public bool Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CovertCalibrationEightPhasesController));
            if (_running) return false;
            var keys = _keyboard.KeyboardLayout.WhiteKeys;
            if (keys.Count < PhaseCount)
                throw new InvalidOperationException("Covert calibration: eight visible white keys are required.");
            _savedActive = _keyboard.ActiveStimulusVisibleIndex;
            _savedCue = _keyboard.AttentionCueVisibleIndex;
            _savedBackground = _keyboard.BackgroundCueVisibleIndex;
            _savedFeedback = _keyboard.FeedbackVisibleIndex;
            _savedManual = _keyboard.ManualInputEnabled;
            _phase = 0;
            _round = 0;
            EnterPhase(Now());
            _running = true;
            _keyboard.ReleaseCurrentKey();
            _keyboard.ManualInputEnabled = false;
            _link.SendEvent(ServiceEventCode.State, 1, StartCommand, PhaseCount,
                "Calibration 2 (eight phases) started.", Now());
            return true;
        }

        public void Stop()
        {
            if (!_running) return;
            long now = Now();
            if (State == CovertCalibrationPhaseState.Neutral)
                Send(2, NeutralClassBase, 0, now);
            else if (State == CovertCalibrationPhaseState.Stimulus)
                Send(2, TargetClass, TargetFrequency, now);
            Finish(CovertCalibrationPhaseState.Idle, now);
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
                (State != CovertCalibrationPhaseState.CheckNeutral &&
                 State != CovertCalibrationPhaseState.CheckStimulus)) return false;
            bool neutral = State == CovertCalibrationPhaseState.CheckNeutral;
            _awaitedEnd = 0;
            if (neutral && flags == 0)
            {
                long now = Now();
                State = CovertCalibrationPhaseState.NeutralInterTrial;
                Deadline(now, _settings.SSVEPCalibrationBackgroundInterTrialDurationMs);
            }
            else
            {
                _retry = flags != 0;
                long now = Now();
                if (_retry)
                {
                    State = CovertCalibrationPhaseState.InterTrial;
                    Deadline(now, _settings.SSVEPCalibrationInterTrialDurationMs);
                }
                else
                {
                    _keyboard.FeedbackVisibleIndex = _target;
                    State = CovertCalibrationPhaseState.Feedback;
                    Deadline(now, _settings.SSVEPCalibrationFeedbackDurationMs);
                    Action<int, long> handler = FeedbackStarted;
                    if (handler != null) handler(_target, now);
                }
            }
            return true;
        }

        private int TargetClass { get { return NeutralClassBase + _phase * PhaseStride + _position + 1; } }
        private int TargetFrequency { get { return (int)Math.Round(_frequencies[_target] * 1000.0); } }

        private void EnterPhase(long now)
        {
            _anchor = _phase;
            _position = 0;
            CreateOrder();
            _keyboard.AttentionCueVisibleIndex = -1;
            _keyboard.BackgroundCueVisibleIndex = -1;
            _keyboard.FeedbackVisibleIndex = -1;
            _keyboard.ActiveStimulusVisibleIndex = -1;
            State = CovertCalibrationPhaseState.PhaseIntro;
            Deadline(now, _settings.SSVEPCalibrationBaselineDurationMs > 0 ?
                _settings.CovertCalibrationNeutralDurationMs : 1500);
        }

        private void Advance(long transitionQpc)
        {
            if (!_running) return;
            switch (State)
            {
                case CovertCalibrationPhaseState.Baseline:
                    EnterAnchorCue(Now());
                    break;
                case CovertCalibrationPhaseState.PhaseIntro:
                    EnterAnchorCue(Now());
                    break;
                case CovertCalibrationPhaseState.AnchorCue:
                    // Yellow anchor cue ends; same duration as before (unchanged).
                    _keyboard.AttentionCueVisibleIndex = -1;
                    _keyboard.FeedbackVisibleIndex = _anchor;
                    long anchorCueOff = Now();
                    State = CovertCalibrationPhaseState.AnchorSettling;
                    Deadline(anchorCueOff, _settings.SSVEPCalibrationSettlingDurationMs);
                    Action<int, long> handler = FeedbackStarted;
                    if (handler != null) handler(_anchor, anchorCueOff);
                    break;
                case CovertCalibrationPhaseState.AnchorSettling:
                    Send(1, NeutralClassBase, 0, transitionQpc);
                    State = CovertCalibrationPhaseState.Neutral;
                    Deadline(transitionQpc, _settings.CovertCalibrationNeutralDurationMs);
                    break;
                case CovertCalibrationPhaseState.Neutral:
                    Send(2, NeutralClassBase, 0, transitionQpc);
                    AwaitResult(CovertCalibrationPhaseState.CheckNeutral, transitionQpc);
                    break;
                case CovertCalibrationPhaseState.NeutralInterTrial:
                    _keyboard.FeedbackVisibleIndex = -1;
                    _keyboard.AttentionCueVisibleIndex = _target;   // red cue on target
                    long pauseEnd = Now();
                    State = CovertCalibrationPhaseState.TargetCue;
                    Deadline(pauseEnd, _settings.SSVEPCalibrationCueDurationMs);
                    break;
                case CovertCalibrationPhaseState.TargetCue:
                    _keyboard.AttentionCueVisibleIndex = -1;
                    long cueOff = Now();
                    _link.SendEvent(ServiceEventCode.State, 1, TargetClass, 0,
                        "Covert EOG check started", cueOff);
                    State = CovertCalibrationPhaseState.Settling;
                    Deadline(cueOff, _settings.SSVEPCalibrationSettlingDurationMs);
                    break;
                case CovertCalibrationPhaseState.Settling:
                    Send(1, TargetClass, TargetFrequency, transitionQpc);
                    State = CovertCalibrationPhaseState.Stimulus;
                    Deadline(transitionQpc, _settings.SSVEPCalibrationStimulusDurationMs);
                    break;
                case CovertCalibrationPhaseState.Stimulus:
                    Send(2, TargetClass, TargetFrequency, transitionQpc);
                    AwaitResult(CovertCalibrationPhaseState.CheckStimulus, transitionQpc);
                    break;
                case CovertCalibrationPhaseState.CheckNeutral:
                case CovertCalibrationPhaseState.CheckStimulus:
                    Abort("Covert calibration: no EOG validation received from PolyHub.");
                    break;
                case CovertCalibrationPhaseState.Feedback:
                    State = CovertCalibrationPhaseState.InterTrial;
                    Deadline(transitionQpc, _settings.SSVEPCalibrationInterTrialDurationMs);
                    break;
                case CovertCalibrationPhaseState.InterTrial:
                    if (!_retry)
                    {
                        _position++;
                        if (_position == _targets.Length)
                        {
                            State = CovertCalibrationPhaseState.NextPhase;
                            Deadline(Now(), _settings.SSVEPCalibrationBlockRestDurationMs);
                            break;
                        }
                    }
                    _retry = false;
                    _target = _targets[_position];
                    State = CovertCalibrationPhaseState.NeutralInterTrial;
                    Deadline(Now(), _settings.SSVEPCalibrationBackgroundInterTrialDurationMs);
                    break;
                case CovertCalibrationPhaseState.NextPhase:
                    _keyboard.FeedbackVisibleIndex = -1;
                    _phase++;
                    if (_phase == PhaseCount)
                    {
                        _phase = 0;
                        _round++;
                        if (_round == _settings.CovertCalibrationRepetitionsPerTarget)
                        {
                            Finish(CovertCalibrationPhaseState.Completed, Now());
                            break;
                        }
                    }
                    EnterPhase(Now());
                    break;
            }
        }

        private void EnterAnchorCue(long now)
        {
            _target = _targets[_position];
            _keyboard.AttentionCueVisibleIndex = _anchor;   // yellow anchor highlight
            _keyboard.BackgroundCueVisibleIndex = -1;
            _keyboard.FeedbackVisibleIndex = -1;
            _keyboard.ActiveStimulusVisibleIndex = -1;
            State = CovertCalibrationPhaseState.AnchorCue;
            Deadline(now, _settings.SSVEPCalibrationCueDurationMs);
        }

        private void AwaitResult(CovertCalibrationPhaseState state, long endQpc)
        {
            State = state;
            _awaitedEnd = endQpc;
            Deadline(Now(), 5000);
        }

        private void CreateOrder()
        {
            int n = 0;
            for (int i = 0; i < PhaseCount; i++) if (i != _anchor) _targets[n++] = i;
            for (int i = _targets.Length - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1), temp = _targets[i];
                _targets[i] = _targets[j]; _targets[j] = temp;
            }
        }

        private void Finish(CovertCalibrationPhaseState state, long now)
        {
            _running = false;
            State = state;
            _awaitedEnd = 0;
            Interlocked.Exchange(ref _deadline, 0);
            _link.SendEvent(ServiceEventCode.State, 1, StartCommand, 0,
                state == CovertCalibrationPhaseState.Completed
                    ? "Calibration 2 (eight phases) completed." : "Calibration 2 stopped.", now);
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
