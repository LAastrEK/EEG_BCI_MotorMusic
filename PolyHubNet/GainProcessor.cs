using System.Threading;

namespace PolyHubNet
{
    public sealed class GainProcessor : SignalProcessor
    {
        public float Gain { get; set; }

        public GainProcessor(string name, float gain, int inputRingCapacity)
            : base(name, inputRingCapacity)
        {
            Gain = gain;
        }

        protected override SignalBlock ProcessBlock(SignalBlock input)
        {
            if (input == null || input.Data == null)
                return null;

            SignalBlock output = new SignalBlock
            {
                Sequence = input.Sequence,
                QpcTimestamp = input.QpcTimestamp,
                ChannelCount = input.ChannelCount,
                SampleRate = input.SampleRate,
                SampleCount = input.SampleCount,
                Data = new float[input.Data.Length]
            };

            float gain = Gain;
            for (int i = 0; i < input.Data.Length; i++)
                output.Data[i] = input.Data[i] * gain;

           // Thread.Sleep(4);

            return output;
        }
    }
}