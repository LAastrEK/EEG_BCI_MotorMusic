using System;
using System.Threading;

namespace SignalGenerator
{
    internal sealed class SignalGeneratorEngine
    {
        public const int MinimumSamplingRate = 50;
        public const double MinimumSineFrequencyHz = 0.1;
        public const double MaximumSineFrequencyHz = 100.0;

        public static double GetMaximumSineFrequencyHz(int samplingRate)
        {
            if (samplingRate < MinimumSamplingRate)
                throw new ArgumentOutOfRangeException(nameof(samplingRate));

            // Наибольшая десятая доля Гц строго ниже частоты Найквиста.
            return Math.Min(MaximumSineFrequencyHz, (samplingRate * 5.0 - 1.0) / 10.0);
        }

        public enum WaveFormKind
        {
            Sine,
            AsEEG
        }

        internal sealed class ChannelInfo
        {
            public string Name { get; set; }

            // Для Sine
            public float SineAmplitudeV { get; set; }
            public double SineFrequencyHz { get; set; }
            public double SinePhaseRadians { get; set; }

            // Для AsEEG
            public float AlphaAmplitudeV { get; set; }
            public float ThetaAmplitudeV { get; set; }
            public float BetaAmplitudeV { get; set; }
            public float DriftAmplitudeV { get; set; }
            public float NoiseStdV { get; set; }

            public double AlphaFrequencyHz { get; set; }
            public double ThetaFrequencyHz { get; set; }
            public double BetaFrequencyHz { get; set; }
            public double DriftFrequencyHz { get; set; }

            public double AlphaPhaseRadians { get; set; }
            public double ThetaPhaseRadians { get; set; }
            public double BetaPhaseRadians { get; set; }
            public double DriftPhaseRadians { get; set; }

            public float DcOffsetV { get; set; }
        }

        private readonly Random _rand = new Random();

        private int _channelsCount;
        private int _samplingRate;
        private int _blockSize;
        private WaveFormKind _waveForm;
        private ChannelInfo[] _channels;
        private double[] _pendingSineFrequenciesHz;

        public int ChannelsCount
        {
            get { return _channelsCount; }
        }

        public int SamplingRate
        {
            get { return _samplingRate; }
        }

        public int BlockSize
        {
            get { return _blockSize; }
        }

        public ChannelInfo[] Channels
        {
            get { return _channels; }
        }

        public void Configure(int channelsCount, int samplingRate, int blockSize, WaveFormKind waveForm,
            double[] sineFrequenciesHz = null)
        {
            if (channelsCount <= 0) channelsCount = 1;
            double maximumFrequency = GetMaximumSineFrequencyHz(samplingRate);
            if (sineFrequenciesHz != null)
                ValidateSineFrequencies(sineFrequenciesHz, channelsCount, maximumFrequency);
            if (blockSize <= 0) blockSize = 32;

            _channelsCount = channelsCount;
            _samplingRate = samplingRate;
            _blockSize = blockSize;
            _waveForm = waveForm;

            Interlocked.Exchange(ref _pendingSineFrequenciesHz, null);
            _channels = new ChannelInfo[_channelsCount];

            for (int ch = 0; ch < _channelsCount; ch++)
            {
                _channels[ch] = new ChannelInfo
                {
                    Name = "Ch " + (ch + 1).ToString(),

                    // Sine
                    SineAmplitudeV = 50e-6f,
                    SineFrequencyHz = sineFrequenciesHz != null
                        ? sineFrequenciesHz[ch] : Math.Min(8.0 + ch, maximumFrequency),
                    SinePhaseRadians = _rand.NextDouble() * 2.0 * Math.PI,

                    // AsEEG
                    AlphaAmplitudeV = MicroVolts(15.0f + (float)_rand.NextDouble() * 20.0f),
                    ThetaAmplitudeV = MicroVolts(3.0f + (float)_rand.NextDouble() * 7.0f),
                    BetaAmplitudeV = MicroVolts(2.0f + (float)_rand.NextDouble() * 6.0f),
                    DriftAmplitudeV = MicroVolts(5.0f + (float)_rand.NextDouble() * 10.0f),
                    NoiseStdV = MicroVolts(1.0f + (float)_rand.NextDouble() * 3.0f),

                    AlphaFrequencyHz = 8.0 + _rand.NextDouble() * 4.0,
                    ThetaFrequencyHz = 4.0 + _rand.NextDouble() * 3.0,
                    BetaFrequencyHz = 16.0 + _rand.NextDouble() * 8.0,
                    DriftFrequencyHz = 0.15 + _rand.NextDouble() * 0.35,

                    AlphaPhaseRadians = _rand.NextDouble() * 2.0 * Math.PI,
                    ThetaPhaseRadians = _rand.NextDouble() * 2.0 * Math.PI,
                    BetaPhaseRadians = _rand.NextDouble() * 2.0 * Math.PI,
                    DriftPhaseRadians = _rand.NextDouble() * 2.0 * Math.PI,

                    DcOffsetV = 0.0f
                };
            }
        }

        public void SetSineFrequencies(double[] frequenciesHz)
        {
            if (frequenciesHz == null)
                throw new ArgumentNullException(nameof(frequenciesHz));

            double[] snapshot = (double[])frequenciesHz.Clone();
            ValidateSineFrequencies(snapshot, _channelsCount, GetMaximumSineFrequencyHz(_samplingRate));
            Interlocked.Exchange(ref _pendingSineFrequenciesHz, snapshot);
        }

        private static void ValidateSineFrequencies(double[] frequenciesHz, int channelsCount,
            double maximumFrequency)
        {
            if (frequenciesHz.Length != channelsCount)
                throw new ArgumentException("One sine frequency is required per channel.", nameof(frequenciesHz));

            foreach (double frequency in frequenciesHz)
            {
                if (double.IsNaN(frequency) || double.IsInfinity(frequency) ||
                    frequency < MinimumSineFrequencyHz || frequency > maximumFrequency)
                    throw new ArgumentOutOfRangeException(nameof(frequenciesHz));
            }
        }

        public float[] GenerateBlock()
        {
            return GenerateSamples(_blockSize);
        }

        public float[] GenerateSamples(int sampleCount)
        {
            if (_channelsCount <= 0 || _samplingRate <= 0 || sampleCount <= 0 || _channels == null)
                return new float[0];

            // Обновляем только между блоками; текущие фазы каналов сохраняем.
            double[] frequencies = Interlocked.Exchange(ref _pendingSineFrequenciesHz, null);
            if (frequencies != null)
            {
                for (int ch = 0; ch < _channelsCount; ch++)
                    _channels[ch].SineFrequencyHz = frequencies[ch];
            }

            // channel-major:
            // [ch0:s0..sN-1][ch1:s0..sN-1]...
            float[] data = new float[sampleCount * _channelsCount];

            for (int ch = 0; ch < _channelsCount; ch++)
            {
                int baseIndex = ch * sampleCount;
                ChannelInfo c = _channels[ch];

                for (int s = 0; s < sampleCount; s++)
                {
                    float value;

                    if (_waveForm == WaveFormKind.Sine)
                    {
                        value = c.DcOffsetV
                            + c.SineAmplitudeV * (float)Math.Sin(c.SinePhaseRadians);

                        c.SinePhaseRadians = AdvancePhase(c.SinePhaseRadians, c.SineFrequencyHz);
                    }
                    else
                    {
                        value = c.DcOffsetV
                            + c.AlphaAmplitudeV * (float)Math.Sin(c.AlphaPhaseRadians)
                            + c.ThetaAmplitudeV * (float)Math.Sin(c.ThetaPhaseRadians)
                            + c.BetaAmplitudeV * (float)Math.Sin(c.BetaPhaseRadians)
                            + c.DriftAmplitudeV * (float)Math.Sin(c.DriftPhaseRadians)
                            + NextGaussian() * c.NoiseStdV;

                        c.AlphaPhaseRadians = AdvancePhase(c.AlphaPhaseRadians, c.AlphaFrequencyHz);
                        c.ThetaPhaseRadians = AdvancePhase(c.ThetaPhaseRadians, c.ThetaFrequencyHz);
                        c.BetaPhaseRadians = AdvancePhase(c.BetaPhaseRadians, c.BetaFrequencyHz);
                        c.DriftPhaseRadians = AdvancePhase(c.DriftPhaseRadians, c.DriftFrequencyHz);
                    }

                    data[baseIndex + s] = value;
                }
            }

            return data;
        }

        private double AdvancePhase(double phaseRadians, double frequencyHz)
        {
            double dphi = 2.0 * Math.PI * frequencyHz / _samplingRate;
            phaseRadians += dphi;

            while (phaseRadians >= 2.0 * Math.PI)
                phaseRadians -= 2.0 * Math.PI;

            return phaseRadians;
        }

        private float NextGaussian()
        {
            double u1 = 1.0 - _rand.NextDouble();
            double u2 = 1.0 - _rand.NextDouble();
            double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
            return (float)z;
        }

        private static float MicroVolts(float uv)
        {
            return uv * 1e-6f;
        }
    }
}