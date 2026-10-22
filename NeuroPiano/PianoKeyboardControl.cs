using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Threading;
using OpenTK;

namespace NeuroPiano
{
    [DesignerCategory("")]
    public sealed class PianoKeyboardControl : GLControl
    {
        private const float HorizontalMargin = 18.0f;
        private const float KeyboardAspect = 3.55f;
        private const float MaximumHeightFraction = 0.58f;

        private readonly PianoKeyboardLayout _keyboardLayout = new PianoKeyboardLayout();
        private readonly PianoKeyboardRenderer _renderer = new PianoKeyboardRenderer();

        private SSVEPStimulusEngine _ssvepStimulusEngine;

        private int _renderTimerId;
        private TimerEventHandler _renderTimerHandler;

        private delegate void TimerEventHandler(int id, int msg, IntPtr user, int dw1, int dw2);

        [DllImport("winmm.dll")]
        private static extern int timeSetEvent(int delay, int resolution, TimerEventHandler handler, IntPtr user, int eventType);

        [DllImport("winmm.dll")]
        private static extern int timeKillEvent(int id);

        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceCounter(out long value);

        private const int TIME_PERIODIC = 1;
        private const int RenderTimerPeriodMs = 2;
        private bool _glInitialized;
        private int _pressedMidiNumber = -1;
        private int _centerMidiNote = PianoNotes.DefaultCenterMidiNote;
        private int _firstVisibleWhiteKeyIndex;

        // -1: мерцают все видимые белые клавиши.
        // 0..7: мерцает только выбранная экранная позиция.
        private int _activeStimulusVisibleIndex = -1;
        private int _attentionCueVisibleIndex = -1;
        private int _backgroundCueVisibleIndex = -1;
        private int _feedbackVisibleIndex = -1;
        private bool _manualInputEnabled = true;

        public event Action<PianoKey> NotePressed;
        public event Action<PianoKey> NoteReleased;

        // Вызывается непосредственно из существующего MM timer callback.
        internal event Action<long> MultimediaTimerTick;

        public PianoKeyboardControl()
        {
            BackColor = Color.Black;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public PianoKeyboardLayout KeyboardLayout
        {
            get { return _keyboardLayout; }
        }

        public int FirstVisibleWhiteKeyIndex
        {
            get { return _firstVisibleWhiteKeyIndex; }
            set
            {
                int clamped = PianoNotes.ClampVisibleWhiteKeyIndex(value);
                if (_firstVisibleWhiteKeyIndex == clamped)
                    return;

                ReleaseCurrentKey();
                _firstVisibleWhiteKeyIndex = clamped;

                if (_glInitialized)
                {
                    RebuildKeyboardLayout();
                    Invalidate();
                }
            }
        }

        public int CenterMidiNote
        {
            get { return _centerMidiNote; }
            set
            {
                int clamped = PianoNotes.ClampToRange(value);
                if (_centerMidiNote == clamped)
                    return;

                ReleaseCurrentKey();
                _centerMidiNote = clamped;

                if (_glInitialized)
                {
                    RebuildKeyboardLayout();
                    Invalidate();
                }
            }
        }

        public int ActiveStimulusVisibleIndex
        {
            get
            {
                return _activeStimulusVisibleIndex;
            }
            set
            {
                if (value < -2 || value > 7)
                    value = -1;

                _activeStimulusVisibleIndex = value;
                Invalidate();
            }
        }

        public int AttentionCueVisibleIndex
        {
            get
            {
                return _attentionCueVisibleIndex;
            }
            set
            {
                if (value < -1 || value > 7)
                    value = -1;

                if (_attentionCueVisibleIndex == value)
                    return;

                _attentionCueVisibleIndex = value;
                Invalidate();
            }
        }

        public int BackgroundCueVisibleIndex
        {
            get { return _backgroundCueVisibleIndex; }
            set
            {
                if (value < -1 || value >= PianoKeyboardLayout.BackgroundTargetCount)
                    value = -1;

                if (_backgroundCueVisibleIndex == value)
                    return;

                _backgroundCueVisibleIndex = value;
                Invalidate();
            }
        }

        public int FeedbackVisibleIndex
        {
            get
            {
                return _feedbackVisibleIndex;
            }
            set
            {
                if (value < -1 || value > 7)
                    value = -1;

                if (_feedbackVisibleIndex == value)
                    return;

                _feedbackVisibleIndex = value;
                Invalidate();
            }
        }

        public bool ManualInputEnabled
        {
            get
            {
                return _manualInputEnabled;
            }
            set
            {
                if (_manualInputEnabled == value)
                    return;

                _manualInputEnabled = value;

                if (!_manualInputEnabled)
                {
                    ReleaseCurrentKey();
                    Capture = false;
                }
            }
        }

        public void SetSSVEPStimulusEngine(SSVEPStimulusEngine stimulusEngine)
        {
            _ssvepStimulusEngine = stimulusEngine;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Left || keyData == Keys.Right)
                return true;

            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (!_manualInputEnabled)
                return;

            if (e.KeyCode == Keys.Left)
            {
                FirstVisibleWhiteKeyIndex--;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Right)
            {
                FirstVisibleWhiteKeyIndex++;
                e.Handled = true;
            }
            else if (e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D8)
            {
                ActiveStimulusVisibleIndex = e.KeyCode - Keys.D1;
                Invalidate();
                e.Handled = true;
            }
            else if (e.KeyCode >= Keys.NumPad1 && e.KeyCode <= Keys.NumPad8)
            {
                ActiveStimulusVisibleIndex = e.KeyCode - Keys.NumPad1;
                Invalidate();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.D0 || e.KeyCode == Keys.NumPad0)
            {
                ActiveStimulusVisibleIndex = -1;
                Invalidate();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.D9 || e.KeyCode == Keys.NumPad9)
            {
                ActiveStimulusVisibleIndex = -2;
                Invalidate();
                e.Handled = true;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            MakeCurrent();
            _glInitialized = true;
            SetupOpenGl();
            VSync = true;
            RebuildKeyboardLayout();

            StartRenderTimer();

            Invalidate();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            StopRenderTimer();

            _glInitialized = false;
            base.OnHandleDestroyed(e);
        }

        private void StartRenderTimer()
        {
            if (_renderTimerId != 0)
                return;

            _renderTimerHandler = RenderTimerCallback;

            _renderTimerId = timeSetEvent(
                RenderTimerPeriodMs,
                1,
                _renderTimerHandler,
                IntPtr.Zero,
                TIME_PERIODIC);
        }

        private void StopRenderTimer()
        {
            if (_renderTimerId == 0)
                return;

            timeKillEvent(_renderTimerId);
            _renderTimerId = 0;
        }

        private void RenderTimerCallback(int id, int msg, IntPtr user, int dw1, int dw2)
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            long qpcTimestamp;
            QueryPerformanceCounter(out qpcTimestamp);

            try
            {
                Action<long> timerHandler = MultimediaTimerTick;
                if (timerHandler != null)
                    timerHandler(qpcTimestamp);
            }
            catch
            {
            }

            try
            {
                BeginInvoke((Action)(Invalidate));
            }
            catch
            {
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);

            if (!_glInitialized)
                return;

            MakeCurrent();
            SetupOpenGl();
            RebuildKeyboardLayout();
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (!_glInitialized)
                return;

            MakeCurrent();

            _renderer.Render(
                _keyboardLayout,
                _pressedMidiNumber,
                _ssvepStimulusEngine,
                _activeStimulusVisibleIndex,
                _attentionCueVisibleIndex,
                _feedbackVisibleIndex,
                _backgroundCueVisibleIndex);

            SwapBuffers();

            if (_ssvepStimulusEngine != null)
                _ssvepStimulusEngine.NextFrame();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (!_manualInputEnabled)
                return;

            if (e.Button != MouseButtons.Left)
                return;

            Capture = true;
            PressKeyAt(e.X, e.Y);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (!_manualInputEnabled)
                return;

            if ((e.Button & MouseButtons.Left) == 0 || !Capture)
                return;

            PianoKey key = _keyboardLayout.HitTest(e.X, e.Y);
            if (key == null)
            {
                ReleaseCurrentKey();
                return;
            }

            if (key.MidiNumber == _pressedMidiNumber)
                return;

            ReleaseCurrentKey();
            PressKey(key);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (!_manualInputEnabled)
                return;

            if (e.Button != MouseButtons.Left)
                return;

            ReleaseCurrentKey();
            Capture = false;
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);

            if (!Capture)
                ReleaseCurrentKey();
        }

        public void PressVisibleWhiteKey(int visibleIndex)
        {
            if (visibleIndex < 0 ||
                visibleIndex >= _keyboardLayout.WhiteKeys.Count)
            {
                return;
            }

            PianoKey key =
                _keyboardLayout.WhiteKeys[visibleIndex];

            if (_pressedMidiNumber == key.MidiNumber)
                return;

            ReleaseCurrentKey();
            PressKey(key);
        }

        public void ReleaseCurrentKey()
        {
            if (_pressedMidiNumber < 0)
                return;

            PianoKey key = _keyboardLayout.FindByMidi(_pressedMidiNumber);
            _pressedMidiNumber = -1;
            Invalidate();

            Action<PianoKey> handler = NoteReleased;
            if (key != null && handler != null)
                handler(key);
        }

        private void PressKeyAt(int x, int y)
        {
            PianoKey key = _keyboardLayout.HitTest(x, y);
            if (key == null)
                return;

            if (_pressedMidiNumber == key.MidiNumber)
                return;

            ReleaseCurrentKey();
            PressKey(key);
        }

        private void PressKey(PianoKey key)
        {
            _pressedMidiNumber = key.MidiNumber;
            Invalidate();

            Action<PianoKey> handler = NotePressed;
            if (handler != null)
                handler(key);
        }

        private void SetupOpenGl()
        {
            _renderer.SetupViewport(ClientSize.Width, ClientSize.Height);
        }

        private void RebuildKeyboardLayout()
        {
            float controlWidth = ClientSize.Width;
            float controlHeight = ClientSize.Height;

            float keyboardWidth = Math.Max(1.0f, controlWidth - HorizontalMargin * 2.0f);
            float keyboardHeight = keyboardWidth / KeyboardAspect;

            float maximumHeight = controlHeight * MaximumHeightFraction;
            if (keyboardHeight > maximumHeight)
                keyboardHeight = maximumHeight;

            float x = (controlWidth - keyboardWidth) * 0.5f;
            float y = (controlHeight - keyboardHeight) * 0.5f;

            _keyboardLayout.Build(new RectangleF(x, y, keyboardWidth, keyboardHeight), _centerMidiNote, _firstVisibleWhiteKeyIndex);
            _keyboardLayout.BuildBackgroundTargets(new RectangleF(0.0f, 0.0f, controlWidth, controlHeight));
        }
    }
}
