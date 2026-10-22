using System;

namespace NeuroPiano
{
    public class SSVEPStimulusEngine
    {
        private long _nextFrameCounter;
        private long _lastReportTime;

        private sealed class Channel
        {
            public double Frequency;
            public double Phase;
            public double PhaseStep;
        }

        private long _frameIndex;
        private Channel[] _channels = new Channel[0];

        public double RefreshRate { get; private set; }

        public double[] Frequencies { get; private set; }

        public long FrameIndex
        {
            get { return _frameIndex; }
        }

        public void Initialize(double refreshRate, double[] frequencies)
        {
            RefreshRate = refreshRate;
            Frequencies = frequencies;

            _channels = new Channel[frequencies.Length];

            for (int i = 0; i < frequencies.Length; i++)
            {
                _channels[i] = new Channel
                {
                    Frequency = frequencies[i],
                    Phase = 0.0,
                    PhaseStep = frequencies[i] / refreshRate
                };
            }
        }

        public void NextFrame()
        {
/*            _nextFrameCounter++;

            long now = Environment.TickCount;

            if (now - _lastReportTime > 1000)
            {
                Console.WriteLine(
                    "NextFrame FPS = " + _nextFrameCounter);

                _nextFrameCounter = 0;
                _lastReportTime = now;
            }*/

            _frameIndex++;

            for (int i = 0; i < _channels.Length; i++)
            {
                _channels[i].Phase += _channels[i].PhaseStep;

                if (_channels[i].Phase >= 1.0)
                    _channels[i].Phase -= Math.Floor(_channels[i].Phase);
            }
        }

        public double GetBrightness(int channelIndex)
        {
            if (channelIndex < 0 || channelIndex >= _channels.Length)
                return 1.0;

            double phase = _channels[channelIndex].Phase;

            double modulation = Math.Sin(2.0 * Math.PI * phase);

             return 0.75 + 0.25 * modulation;
            //return 0.5 + 0.5 * modulation;
        }
    }
}
