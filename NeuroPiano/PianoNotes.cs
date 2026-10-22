using System;

namespace NeuroPiano
{
    internal static class PianoNotes
    {
        public const int FirstMidiNote = 36;          // C2
        public const int LastMidiNote = 84;           // C6
        public const int DefaultCenterMidiNote = 60;  // C4
        public const float VisibleWhiteKeyCount = 8.0f;
        public const int VisibleWhiteKeyCountInteger = 8;

        private static readonly string[] PitchNames =
        {
            "C", "C#", "D", "D#", "E", "F",
            "F#", "G", "G#", "A", "A#", "B"
        };


        public static int ClampVisibleWhiteKeyIndex(int index)
        {
            int maxIndex = GetTotalWhiteKeyCount() - VisibleWhiteKeyCountInteger;
            if (index < 0)
                return 0;

            if (index > maxIndex)
                return maxIndex;

            return index;
        }

        private static int GetTotalWhiteKeyCount()
        {
            int count = 0;
            for (int midi = FirstMidiNote; midi <= LastMidiNote; midi++)
            {
                if (!IsBlackKey(midi))
                    count++;
            }

            return count;
        }

        public static bool IsInRange(int midiNumber)
        {
            return midiNumber >= FirstMidiNote && midiNumber <= LastMidiNote;
        }

        public static int ClampToRange(int midiNumber)
        {
            if (midiNumber < FirstMidiNote)
                return FirstMidiNote;

            if (midiNumber > LastMidiNote)
                return LastMidiNote;

            return midiNumber;
        }

        public static bool IsBlackKey(int midiNumber)
        {
            switch (PositiveMod(midiNumber, 12))
            {
                case 1:
                case 3:
                case 6:
                case 8:
                case 10:
                    return true;

                default:
                    return false;
            }
        }

        public static string GetNoteName(int midiNumber)
        {
            int pitchClass = PositiveMod(midiNumber, 12);
            int octave = midiNumber / 12 - 1;
            return PitchNames[pitchClass] + octave.ToString();
        }

        public static string GetSampleFileName(int midiNumber)
        {
            return GetNoteName(midiNumber).Replace("#", "s") + ".wav";
        }

        private static int PositiveMod(int value, int modulus)
        {
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }
    }
}
