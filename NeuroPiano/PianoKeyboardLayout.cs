using System;
using System.Collections.Generic;
using System.Drawing;

namespace NeuroPiano
{
    public sealed class PianoKeyboardLayout
    {
        public const int BackgroundTargetCount = 6;

        private const float BlackKeyWidthFactor = 0.62f;
        private const float BlackKeyHeightFactor = 0.62f;

        private readonly List<PianoKey> _whiteKeys = new List<PianoKey>();
        private readonly List<PianoKey> _blackKeys = new List<PianoKey>();
        private readonly List<RectangleF> _backgroundTargets = new List<RectangleF>(BackgroundTargetCount);

        public IList<PianoKey> WhiteKeys
        {
            get { return _whiteKeys; }
        }

        public IList<PianoKey> BlackKeys
        {
            get { return _blackKeys; }
        }

        public IReadOnlyList<RectangleF> BackgroundTargets
        {
            get { return _backgroundTargets; }
        }

        public RectangleF Bounds { get; private set; }
        public int CenterMidiNote { get; private set; }
        public int FirstVisibleWhiteKeyIndex { get; private set; }

        public void Build(RectangleF bounds, int centerMidiNote, int firstVisibleWhiteKeyIndex)
        {
            Bounds = bounds;
            CenterMidiNote = PianoNotes.ClampToRange(centerMidiNote);
            FirstVisibleWhiteKeyIndex = PianoNotes.ClampVisibleWhiteKeyIndex(firstVisibleWhiteKeyIndex);

            _whiteKeys.Clear();
            _blackKeys.Clear();
            _backgroundTargets.Clear();

            if (bounds.Width <= 1.0f || bounds.Height <= 1.0f)
                return;

            float whiteKeyWidth = bounds.Width / PianoNotes.VisibleWhiteKeyCount;
            float viewportWorldLeft = FirstVisibleWhiteKeyIndex;

            for (int midiNumber = PianoNotes.FirstMidiNote;
                 midiNumber <= PianoNotes.LastMidiNote;
                 midiNumber++)
            {
                if (PianoNotes.IsBlackKey(midiNumber))
                    AddBlackKey(bounds, whiteKeyWidth, viewportWorldLeft, midiNumber);
                else
                    AddWhiteKey(bounds, whiteKeyWidth, viewportWorldLeft, midiNumber);
            }
        }

        public void BuildBackgroundTargets(RectangleF viewportBounds)
        {
            _backgroundTargets.Clear();

            if (Bounds.Width <= 1.0f || Bounds.Height <= 1.0f)
                return;

            const int targetsPerRow = BackgroundTargetCount / 2;
            float width = Bounds.Width / PianoNotes.VisibleWhiteKeyCount;
            float height = width * 0.5f;

            // Keep the frames inside the free bands without changing the keyboard.
            if (height > Bounds.Top - viewportBounds.Top ||
                height > viewportBounds.Bottom - Bounds.Bottom)
                return;

            // Stable order: top left-to-right, then bottom left-to-right.
            for (int row = 0; row < 2; row++)
            {
                float centerY = row == 0
                    ? (viewportBounds.Top + Bounds.Top) * 0.5f
                    : (Bounds.Bottom + viewportBounds.Bottom) * 0.5f;

                for (int column = 0; column < targetsPerRow; column++)
                {
                    float centerX = Bounds.Left +
                        Bounds.Width * (column + 1) / (targetsPerRow + 1);

                    _backgroundTargets.Add(new RectangleF(
                        centerX - width * 0.5f,
                        centerY - height * 0.5f,
                        width,
                        height));
                }
            }
        }

        public PianoKey HitTest(float x, float y)
        {
            if (!Bounds.Contains(x, y))
                return null;

            // Black keys are visually above white keys and therefore get hit-test priority.
            for (int i = 0; i < _blackKeys.Count; i++)
            {
                if (_blackKeys[i].Bounds.Contains(x, y))
                    return _blackKeys[i];
            }

            for (int i = 0; i < _whiteKeys.Count; i++)
            {
                if (_whiteKeys[i].Bounds.Contains(x, y))
                    return _whiteKeys[i];
            }

            return null;
        }

        public PianoKey FindByMidi(int midiNumber)
        {
            for (int i = 0; i < _whiteKeys.Count; i++)
            {
                if (_whiteKeys[i].MidiNumber == midiNumber)
                    return _whiteKeys[i];
            }

            for (int i = 0; i < _blackKeys.Count; i++)
            {
                if (_blackKeys[i].MidiNumber == midiNumber)
                    return _blackKeys[i];
            }

            return null;
        }

        private void AddWhiteKey(
            RectangleF bounds,
            float whiteKeyWidth,
            float viewportWorldLeft,
            int midiNumber)
        {
            float worldLeft = GetWhiteKeyWorldLeft(midiNumber);
            float screenLeft = bounds.Left + (worldLeft - viewportWorldLeft) * whiteKeyWidth;

            RectangleF rawBounds = new RectangleF(
                screenLeft,
                bounds.Top,
                whiteKeyWidth,
                bounds.Height);

            RectangleF visibleBounds;
            if (!TryClip(rawBounds, bounds, out visibleBounds))
                return;

            _whiteKeys.Add(new PianoKey
            {
                MidiNumber = midiNumber,
                NoteName = PianoNotes.GetNoteName(midiNumber),
                IsBlack = false,
                Bounds = visibleBounds
            });
        }

        private void AddBlackKey(
            RectangleF bounds,
            float whiteKeyWidth,
            float viewportWorldLeft,
            int midiNumber)
        {
            float worldCenter = GetBlackKeyWorldCenter(midiNumber);
            float screenCenter = bounds.Left + (worldCenter - viewportWorldLeft) * whiteKeyWidth;

            float blackWidth = whiteKeyWidth * BlackKeyWidthFactor;
            float blackHeight = bounds.Height * BlackKeyHeightFactor;

            RectangleF rawBounds = new RectangleF(
                screenCenter - blackWidth * 0.5f,
                bounds.Top,
                blackWidth,
                blackHeight);

            RectangleF visibleBounds;
            if (!TryClip(rawBounds, bounds, out visibleBounds))
                return;

            _blackKeys.Add(new PianoKey
            {
                MidiNumber = midiNumber,
                NoteName = PianoNotes.GetNoteName(midiNumber),
                IsBlack = true,
                Bounds = visibleBounds
            });
        }

        private static bool TryClip(RectangleF source, RectangleF clip, out RectangleF result)
        {
            result = RectangleF.Intersect(source, clip);
            return result.Width > 0.0f && result.Height > 0.0f;
        }

        private static float GetKeyWorldCenter(int midiNumber)
        {
            if (PianoNotes.IsBlackKey(midiNumber))
                return GetBlackKeyWorldCenter(midiNumber);

            return GetWhiteKeyWorldLeft(midiNumber) + 0.5f;
        }

        private static float GetWhiteKeyWorldLeft(int midiNumber)
        {
            int relativeOctave = (midiNumber - PianoNotes.FirstMidiNote) / 12;
            int pitchClass = midiNumber % 12;

            int whiteIndexInOctave;
            switch (pitchClass)
            {
                case 0: whiteIndexInOctave = 0; break; // C
                case 2: whiteIndexInOctave = 1; break; // D
                case 4: whiteIndexInOctave = 2; break; // E
                case 5: whiteIndexInOctave = 3; break; // F
                case 7: whiteIndexInOctave = 4; break; // G
                case 9: whiteIndexInOctave = 5; break; // A
                case 11: whiteIndexInOctave = 6; break; // B
                default:
                    throw new ArgumentException("MIDI note is not a white key.", nameof(midiNumber));
            }

            return relativeOctave * 7.0f + whiteIndexInOctave;
        }

        private static float GetBlackKeyWorldCenter(int midiNumber)
        {
            int relativeOctave = (midiNumber - PianoNotes.FirstMidiNote) / 12;
            int pitchClass = midiNumber % 12;

            int boundaryInOctave;
            switch (pitchClass)
            {
                case 1: boundaryInOctave = 1; break; // C# between C and D
                case 3: boundaryInOctave = 2; break; // D# between D and E
                case 6: boundaryInOctave = 4; break; // F# between F and G
                case 8: boundaryInOctave = 5; break; // G# between G and A
                case 10: boundaryInOctave = 6; break; // A# between A and B
                default:
                    throw new ArgumentException("MIDI note is not a black key.", nameof(midiNumber));
            }

            return relativeOctave * 7.0f + boundaryInOctave;
        }
    }
}
