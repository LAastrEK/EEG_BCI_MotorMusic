using System.Drawing;

namespace NeuroPiano
{
    public sealed class PianoKey
    {
        public int MidiNumber { get; set; }
        public string NoteName { get; set; }
        public bool IsBlack { get; set; }
        public RectangleF Bounds { get; set; }
    }
}
