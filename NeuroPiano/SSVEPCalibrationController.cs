using PolyHubIpc;
using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace NeuroPiano
{
    internal enum SSVEPCalibrationStimulusMode
    {
        IsolatedTarget = 0,
        AllTargets = 1
    }

    internal enum SSVEPCalibrationState
    {
        Idle,
        Baseline,
        Cue,
        Settling,
        Stimulus,
        Feedback,
        InterTrial,
        BlockRest,
        Completed
    }

    internal sealed class SSVEPCalibrationController : IDisposable
    {
        public const int DefaultBaselineDurationMs = 5000;
        public const int DefaultCueDurationMs = 500;
        public const int DefaultSettlingDurationMs = 500;
        public const int DefaultStimulusDurationMs = 1500;
        public const int DefaultFeedbackDurationMs = 1000;
        public const int DefaultInterTrialDurationMs = 1000;
        public const int DefaultBlockRestDurationMs = 10000;
        public const int DefaultRepetitionsPerTarget = 15;
        public const int DefaultBackgroundCueDurationMs = DefaultCueDurationMs;
        public const int DefaultBackgroundSettlingDurationMs = DefaultSettlingDurationMs;
        public const int DefaultBackgroundStimulusDurationMs = DefaultStimulusDurationMs;
        public const int DefaultBackgroundInterTrialDurationMs = DefaultInterTrialDurationMs;

        private const int TargetCount = PianoNotes.VisibleWhiteKeyCountInteger;
        private const int BackgroundTargetCount = PianoKeyboardLayout.BackgroundTargetCount;

        private const int EventStateStart = 1;
        private const int EventStateEnd = 2;
        private const int StimulusClassIdBase = 1200;
        private const int BackgroundClassId = StimulusClassIdBase;

        private readonly Control _uiOwner;
        private readonly PianoKeyboardControl _keyboard;
        private readonly ServiceEventLink _serviceEventLink;
        private readonly double[] _frequencies;
        private readonly SSVEPCalibrationStimulusMode _stimulusMode;

        private readonly int _baselineDurationMs;
        private readonly int _cueDurationMs;
        private readonly int _settlingDurationMs;
        private readonly int _stimulusDurationMs;
        private readonly int _feedbackDurationMs;
        private readonly int _interTrialDurationMs;
        private readonly int _blockRestDurationMs;
        private readonly int _repetitionsPerTarget;
        private readonly int _backgroundCueDurationMs;
        private readonly int _backgroundSettlingDurationMs;
        private readonly int _backgroundStimulusDurationMs;
        private readonly int _backgroundInterTrialDurationMs;

        private readonly Random _random = new Random();
        private readonly int[] _currentOrder = new int[TargetCount];
        private readonly int[] _backgroundOrder = new int[BackgroundTargetCount];

        private long _qpcFrequency;
        private long _stateEndQpc;
        private long _stimulusStartQpc;
        private long _stimulusEndQpc;
        private bool _currentTrialRejected;

        private int _blockIndex;
        private int _trialIndex;
        private int _currentTarget = -1;
        private int _lastTarget = -1;
        private int _backgroundOrderIndex;
        private int _currentBackgroundTarget = -1;
        private int _lastBackgroundTarget = -1;
        private bool _isBackgroundTrial;

        private int _previousActiveStimulusVisibleIndex;
        private int _previousAttentionCueVisibleIndex;
        private int _previousBackgroundCueVisibleIndex;
        private int _previousFeedbackVisibleIndex;
        private bool _previousManualInputEnabled;

        private volatile bool _running;
        private bool _disposed;
        private int _transitionPending;

        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceCounter(out long value);

        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceFrequency(out long value);

        public SSVEPCalibrationController(
            Control uiOwner,
            PianoKeyboardControl keyboard,
            ServiceEventLink serviceEventLink,
            double[] frequencies,
            SSVEPCalibrationStimulusMode stimulusMode,
            int baselineDurationMs,
            int cueDurationMs,
            int settlingDurationMs,
            int stimulusDurationMs,
            int feedbackDurationMs,
            int interTrialDurationMs,
            int blockRestDurationMs,
            int repetitionsPerTarget,
            int backgroundCueDurationMs,
            int backgroundSettlingDurationMs,
            int backgroundStimulusDurationMs,
            int backgroundInterTrialDurationMs)
        {
            if (uiOwner == null)
                throw new ArgumentNullException(nameof(uiOwner));

            if (keyboard == null)
                throw new ArgumentNullException(nameof(keyboard));

            if (frequencies == null || frequencies.Length < TargetCount)
                throw new ArgumentException(
                    "Для SSVEP-калибровки нужны 8 частот.",
                    nameof(frequencies));

            _uiOwner = uiOwner;
            _keyboard = keyboard;
            _serviceEventLink = serviceEventLink;
            _frequencies = frequencies;
            _stimulusMode = stimulusMode;

            _baselineDurationMs = baselineDurationMs;
            _cueDurationMs = cueDurationMs;
            _settlingDurationMs = settlingDurationMs;
            _stimulusDurationMs = stimulusDurationMs;
            _feedbackDurationMs = feedbackDurationMs;
            _interTrialDurationMs = interTrialDurationMs;
            _blockRestDurationMs = blockRestDurationMs;
            _repetitionsPerTarget = repetitionsPerTarget;
            _backgroundCueDurationMs = backgroundCueDurationMs;
            _backgroundSettlingDurationMs = backgroundSettlingDurationMs;
            _backgroundStimulusDurationMs = backgroundStimulusDurationMs;
            _backgroundInterTrialDurationMs = backgroundInterTrialDurationMs;

            if (!QueryPerformanceFrequency(out _qpcFrequency) ||
                _qpcFrequency <= 0)
            {
                throw new InvalidOperationException(
                    "QueryPerformanceFrequency failed.");
            }

            _keyboard.MultimediaTimerTick += Keyboard_MultimediaTimerTick;

            State = SSVEPCalibrationState.Idle;
        }

        public event Action<int, long> FeedbackStarted;

        public bool IsRunning
        {
            get { return _running; }
        }

        public SSVEPCalibrationState State { get; private set; }

        public bool Start()
        {
            if (_disposed)
                throw new ObjectDisposedException(
                    nameof(SSVEPCalibrationController));

            if (_running)
                return false;

            _previousActiveStimulusVisibleIndex =
                _keyboard.ActiveStimulusVisibleIndex;

            _previousAttentionCueVisibleIndex =
                _keyboard.AttentionCueVisibleIndex;

            _previousBackgroundCueVisibleIndex =
                _keyboard.BackgroundCueVisibleIndex;

            _previousFeedbackVisibleIndex =
                _keyboard.FeedbackVisibleIndex;

            _previousManualInputEnabled =
                _keyboard.ManualInputEnabled;

            _blockIndex = 0;
            _trialIndex = 0;
            _currentTarget = -1;
            _lastTarget = -1;
            _backgroundOrderIndex = BackgroundTargetCount;
            _currentBackgroundTarget = -1;
            _lastBackgroundTarget = -1;
            _isBackgroundTrial = false;
            _currentTrialRejected = false;
            _stimulusStartQpc = 0;
            _stimulusEndQpc = 0;

            CreateBlockOrder(-1);

            _keyboard.ReleaseCurrentKey();
            _keyboard.ManualInputEnabled = false;
            _keyboard.AttentionCueVisibleIndex = -1;
            _keyboard.BackgroundCueVisibleIndex = -1;
            _keyboard.FeedbackVisibleIndex = -1;
            _keyboard.ActiveStimulusVisibleIndex = -2;

            _running = true;
            State = SSVEPCalibrationState.Baseline;

            SetStateDeadline(
                GetQpcNow(),
                _baselineDurationMs);

            _serviceEventLink.SendEvent(ServiceEventCode.State, 1,
                (int)NeuroPianoUserCommand.StartSSVEPCalibration, 1,
                "Calibration 1 started.");

            return true;
        }

        public void Stop()
        {
            if (!_running)
                return;

            long qpcNow = GetQpcNow();

            if (State == SSVEPCalibrationState.Stimulus)
                SendStimulusMarker(EventStateEnd, qpcNow);

            _running = false;
            State = SSVEPCalibrationState.Idle;
            _currentTrialRejected = false;
            _stimulusStartQpc = 0;
            _stimulusEndQpc = 0;

            Interlocked.Exchange(ref _stateEndQpc, 0);
            Interlocked.Exchange(ref _transitionPending, 0);

            RestoreKeyboard();

            _serviceEventLink.SendEvent(ServiceEventCode.State, 1,
                (int)NeuroPianoUserCommand.StartSSVEPCalibration, 0,
                "Calibration 1 stopped.", qpcNow);
        }

        public bool RejectCurrentTrial(long qpcTimestamp)
        {
            // The event carries the rejected window's end time, not its arrival time.
            // Allow delivery during Feedback/InterTrial, but never reject a later trial.
            if (!_running || _currentTrialRejected || _stimulusStartQpc <= 0 ||
                qpcTimestamp < _stimulusStartQpc || qpcTimestamp > _stimulusEndQpc ||
                (State != SSVEPCalibrationState.Stimulus &&
                 State != SSVEPCalibrationState.Feedback &&
                 State != SSVEPCalibrationState.InterTrial))
            {
                return false;
            }

            _currentTrialRejected = true;
            return true;
        }

        private void Keyboard_MultimediaTimerTick(long qpcNow)
        {
            if (!_running || _disposed)
                return;

            long stateEndQpc =
                Interlocked.Read(ref _stateEndQpc);

            if (stateEndQpc <= 0 ||
                qpcNow < stateEndQpc)
            {
                return;
            }

            if (Interlocked.Exchange(
                    ref _transitionPending,
                    1) != 0)
            {
                return;
            }

            long transitionQpc = stateEndQpc;

            try
            {
                _uiOwner.BeginInvoke((Action)(() =>
                {
                    try
                    {
                        AdvanceState(transitionQpc);
                    }
                    finally
                    {
                        Interlocked.Exchange(
                            ref _transitionPending,
                            0);
                    }
                }));
            }
            catch
            {
                Interlocked.Exchange(
                    ref _transitionPending,
                    0);
            }
        }

        private void AdvanceState(long transitionQpc)
        {
            if (!_running)
                return;

            switch (State)
            {
                case SSVEPCalibrationState.Baseline:
                    EnterCue(GetQpcNow());
                    break;

                case SSVEPCalibrationState.Cue:
                    EnterSettling(GetQpcNow());
                    break;

                case SSVEPCalibrationState.Settling:
                    EnterStimulus(transitionQpc);
                    break;

                case SSVEPCalibrationState.Stimulus:
                    SendStimulusMarker(
                        EventStateEnd,
                        transitionQpc);

                    if (_isBackgroundTrial || _currentTrialRejected)
                    {
                        EnterInterTrial(GetQpcNow());
                    }
                    else
                    {
                        EnterFeedback(GetQpcNow());
                    }
                    break;

                case SSVEPCalibrationState.Feedback:
                    if (!_currentTrialRejected)
                        _lastTarget = _currentTarget;

                    _keyboard.FeedbackVisibleIndex = -1;
                    _keyboard.AttentionCueVisibleIndex = -1;
                    _keyboard.BackgroundCueVisibleIndex = -1;
                    SetInterTrialStimulusState();

                    _currentTarget = -1;
                    EnterInterTrial(GetQpcNow());
                    break;

                case SSVEPCalibrationState.InterTrial:
                    if (_currentTrialRejected)
                    {
                        EnterCue(GetQpcNow(), true);
                        break;
                    }

                    if (!_isBackgroundTrial)
                    {
                        // Every key, including the last, is followed by a background.
                        _isBackgroundTrial = true;
                        EnterCue(GetQpcNow());
                    }
                    else
                    {
                        // A key/background pair is complete only after the background pause.
                        _isBackgroundTrial = false;
                        _trialIndex++;

                        if (_trialIndex < TargetCount)
                        {
                            EnterCue(GetQpcNow());
                        }
                        else if (_blockIndex + 1 < _repetitionsPerTarget)
                        {
                            EnterBlockRest(GetQpcNow());
                        }
                        else
                        {
                            Complete();
                        }
                    }
                    break;

                case SSVEPCalibrationState.BlockRest:
                    _blockIndex++;
                    _trialIndex = 0;

                    CreateBlockOrder(_lastTarget);
                    EnterCue(GetQpcNow());
                    break;
            }
        }

        private void EnterCue(long qpcNow, bool repeatCurrent = false)
        {
            _currentTrialRejected = false;
            _stimulusStartQpc = 0;
            _stimulusEndQpc = 0;

            if (_isBackgroundTrial)
            {
                _currentTarget = -1;
                if (!repeatCurrent)
                    _currentBackgroundTarget = NextBackgroundTarget();
            }
            else
            {
                _currentTarget = _currentOrder[_trialIndex];
            }

            _keyboard.FeedbackVisibleIndex = -1;
            _keyboard.AttentionCueVisibleIndex =
                _isBackgroundTrial ? -1 : _currentTarget;
            _keyboard.BackgroundCueVisibleIndex =
                _isBackgroundTrial ? _currentBackgroundTarget : -1;

            _keyboard.ActiveStimulusVisibleIndex =
                _stimulusMode ==
                SSVEPCalibrationStimulusMode.AllTargets
                    ? -1
                    : -2;

            State = SSVEPCalibrationState.Cue;

            SetStateDeadline(
                qpcNow,
                _isBackgroundTrial ? _backgroundCueDurationMs : _cueDurationMs);
        }

        private void EnterSettling(long qpcNow)
        {
            // Рамка исчезает. С этого момента устанавливаем ровно тот
            // визуальный режим, в котором затем будет записываться SSVEP.
            // Интервал Settling не маркируем, чтобы отклик на cue не попадал
            // в обучающую эпоху.
            _keyboard.AttentionCueVisibleIndex = -1;
            _keyboard.BackgroundCueVisibleIndex = -1;
            _keyboard.FeedbackVisibleIndex = -1;

            _keyboard.ActiveStimulusVisibleIndex =
                _stimulusMode ==
                SSVEPCalibrationStimulusMode.AllTargets
                    ? -1
                    : (_isBackgroundTrial ? -2 : _currentTarget);

            State = SSVEPCalibrationState.Settling;

            SetStateDeadline(
                qpcNow,
                _isBackgroundTrial ? _backgroundSettlingDurationMs : _settlingDurationMs);
        }

        private void EnterStimulus(long transitionQpc)
        {
            // Визуально ничего не меняем: Stimulus является продолжением
            // Settling. Здесь появляется только START-маркер чистой эпохи.
            State = SSVEPCalibrationState.Stimulus;

            SendStimulusMarker(
                EventStateStart,
                transitionQpc);

            SetStateDeadline(
                transitionQpc,
                _isBackgroundTrial ? _backgroundStimulusDurationMs : _stimulusDurationMs);

            _stimulusStartQpc = transitionQpc;
            _stimulusEndQpc = Interlocked.Read(ref _stateEndQpc);
        }

        private void EnterFeedback(long qpcNow)
        {
            _keyboard.AttentionCueVisibleIndex = -1;
            _keyboard.BackgroundCueVisibleIndex = -1;
            _keyboard.FeedbackVisibleIndex =
                _currentTarget;

            if (_stimulusMode ==
                SSVEPCalibrationStimulusMode.AllTargets)
            {
                // Остальные клавиши продолжают мерцать. Целевая остаётся
                // серой, потому что Feedback имеет приоритет в renderer.
                _keyboard.ActiveStimulusVisibleIndex = -1;
            }
            else
            {
                _keyboard.ActiveStimulusVisibleIndex = -2;
            }

            State = SSVEPCalibrationState.Feedback;

            SetStateDeadline(
                qpcNow,
                _feedbackDurationMs);

            Action<int, long> handler = FeedbackStarted;
            if (handler != null)
                handler(_currentTarget, qpcNow);
        }

        private void EnterInterTrial(long qpcNow)
        {
            _keyboard.AttentionCueVisibleIndex = -1;
            _keyboard.BackgroundCueVisibleIndex = -1;
            _keyboard.FeedbackVisibleIndex = -1;
            SetInterTrialStimulusState();

            State = SSVEPCalibrationState.InterTrial;

            SetStateDeadline(
                qpcNow,
                _isBackgroundTrial ? _backgroundInterTrialDurationMs : _interTrialDurationMs);
        }

        private void SetInterTrialStimulusState()
        {
            _keyboard.ActiveStimulusVisibleIndex =
                _stimulusMode ==
                SSVEPCalibrationStimulusMode.AllTargets
                    ? -1
                    : -2;
        }

        private void EnterBlockRest(long qpcNow)
        {
            _keyboard.AttentionCueVisibleIndex = -1;
            _keyboard.BackgroundCueVisibleIndex = -1;
            _keyboard.FeedbackVisibleIndex = -1;
            _keyboard.ActiveStimulusVisibleIndex = -2;

            State = SSVEPCalibrationState.BlockRest;

            SetStateDeadline(
                qpcNow,
                _blockRestDurationMs);
        }

        private void Complete()
        {
            _running = false;
            State = SSVEPCalibrationState.Completed;

            Interlocked.Exchange(ref _stateEndQpc, 0);

            RestoreKeyboard();

            _serviceEventLink.SendEvent(ServiceEventCode.State, 1,
                (int)NeuroPianoUserCommand.StartSSVEPCalibration, 0,
                "Calibration 1 completed.");
        }

        private void CreateBlockOrder(int previousTarget)
        {
            CreateOrder(_currentOrder, previousTarget);
        }

        private int NextBackgroundTarget()
        {
            if (_backgroundOrderIndex >= BackgroundTargetCount)
            {
                CreateOrder(_backgroundOrder, _lastBackgroundTarget);
                _backgroundOrderIndex = 0;
            }

            _lastBackgroundTarget = _backgroundOrder[_backgroundOrderIndex++];
            return _lastBackgroundTarget;
        }

        private void CreateOrder(int[] order, int previousTarget)
        {
            for (int i = 0; i < order.Length; i++)
                order[i] = i;

            for (int i = order.Length - 1;
                 i > 0;
                 i--)
            {
                int j = _random.Next(i + 1);

                int temp = order[i];
                order[i] = order[j];
                order[j] = temp;
            }

            if (previousTarget >= 0 &&
                order[0] == previousTarget)
            {
                int temp = order[0];
                order[0] = order[1];
                order[1] = temp;
            }
        }

        private void SendStimulusMarker(
            int state,
            long qpcTimestamp)
        {
            if (_serviceEventLink == null ||
                (_isBackgroundTrial ? _currentBackgroundTarget < 0 : _currentTarget < 0))
            {
                return;
            }

            int frequencyMilliHz =
                _isBackgroundTrial ? 0 :
                    (int)Math.Round(_frequencies[_currentTarget] * 1000.0);

            int classId = _isBackgroundTrial
                ? BackgroundClassId
                : StimulusClassIdBase + _currentTarget + 1;

            string text = _isBackgroundTrial
                ? "SSVEP Background object=" +
                    (_currentBackgroundTarget + 1).ToString(CultureInfo.InvariantCulture)
                : BuildStimulusText(_currentTarget);

            _serviceEventLink.SendEvent(
                ServiceEventCode.UserEvent,
                state,
                classId,
                frequencyMilliHz,
                text +
                    (state == EventStateStart
                        ? " Start"
                        : " End"),
                qpcTimestamp);
        }

        private string BuildStimulusText(int targetIndex)
        {
            return
                "SSVEP Stimulus key=" +
                (targetIndex + 1).ToString(
                    CultureInfo.InvariantCulture) +
                " freq=" +
                _frequencies[targetIndex].ToString(
                    "0.###",
                    CultureInfo.InvariantCulture) +
                "Hz";
        }

        private void SetStateDeadline(
            long stateStartQpc,
            int durationMs)
        {
            long durationQpc =
                MillisecondsToQpc(durationMs);

            Interlocked.Exchange(
                ref _stateEndQpc,
                stateStartQpc + durationQpc);
        }

        private long MillisecondsToQpc(int milliseconds)
        {
            long result =
                (long)Math.Round(
                    milliseconds *
                    (double)_qpcFrequency /
                    1000.0);

            return result > 0 ? result : 1;
        }

        private static long GetQpcNow()
        {
            long qpc;
            QueryPerformanceCounter(out qpc);
            return qpc;
        }

        private void RestoreKeyboard()
        {
            _keyboard.AttentionCueVisibleIndex =
                _previousAttentionCueVisibleIndex;

            _keyboard.BackgroundCueVisibleIndex =
                _previousBackgroundCueVisibleIndex;

            _keyboard.FeedbackVisibleIndex =
                _previousFeedbackVisibleIndex;

            _keyboard.ActiveStimulusVisibleIndex =
                _previousActiveStimulusVisibleIndex;

            _keyboard.ManualInputEnabled =
                _previousManualInputEnabled;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            if (_running)
                Stop();

            _keyboard.MultimediaTimerTick -=
                Keyboard_MultimediaTimerTick;

            _disposed = true;
        }
    }
}
