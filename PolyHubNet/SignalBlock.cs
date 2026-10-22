using System;

namespace PolyHubNet
{
    public sealed class SignalBlock
    {
        public long Sequence { get; set; }
        public long QpcTimestamp { get; set; }

        public int ChannelCount { get; set; }
        public int SampleRate { get; set; }
        public int SampleCount { get; set; }

        // channel-major:
        // [ch0:s0..sN-1][ch1:s0..sN-1]...
        public float[] Data { get; set; }

        public SignalBlock Clone()
        {
            SignalBlock copy = new SignalBlock
            {
                Sequence = Sequence,
                QpcTimestamp = QpcTimestamp,
                ChannelCount = ChannelCount,
                SampleRate = SampleRate,
                SampleCount = SampleCount,
                Data = null
            };

            if (Data != null)
            {
                copy.Data = new float[Data.Length];
                Array.Copy(Data, copy.Data, Data.Length);
            }

            return copy;
        }
    }
}