using System;

namespace ActiChamp
{
    /// <summary>
    /// Stateful causal filtering for a contiguous group of channels
    /// in the final actiCHamp signal stream.
    ///
    /// Processing order: high-pass -> low-pass -> 50 Hz notch.
    /// Filter state is preserved across acquisition blocks.
    /// </summary>
    internal sealed class ActiChampSignalFilter
    {
        private const double ButterworthQ = 0.70710678118654752440;
        private const double NotchFrequencyHz = 50.0;
        private const double NotchBandwidthHz = 2.0; // PyCorder uses 49..51 Hz.

        private struct BiquadCoefficients
        {
            public double B0;
            public double B1;
            public double B2;
            public double A1;
            public double A2;
        }

        private struct BiquadState
        {
            public double Z1;
            public double Z2;
        }

        private double _sampleRateHz;
        private int _channelCount;
        private double _lowCutoffHz;
        private double _highCutoffHz;
        private bool _notch50Enabled;

        private bool _highPassEnabled;
        private bool _lowPassEnabled;
        private bool _notchEnabled;

        private BiquadCoefficients _highPass;
        private BiquadCoefficients _lowPass;
        private BiquadCoefficients _notch;

        private BiquadState[] _highPassState;
        private BiquadState[] _lowPassState;
        private BiquadState[] _notchState;

        public bool IsEnabled
        {
            get { return _highPassEnabled || _lowPassEnabled || _notchEnabled; }
        }

        public void Configure(
            double sampleRateHz,
            int channelCount,
            double lowCutoffHz,
            double highCutoffHz,
            bool notch50Enabled)
        {
            ValidateConfiguration(sampleRateHz, lowCutoffHz, highCutoffHz, notch50Enabled);

            _sampleRateHz = sampleRateHz;
            _channelCount = channelCount < 0 ? 0 : channelCount;
            _lowCutoffHz = lowCutoffHz;
            _highCutoffHz = highCutoffHz;
            _notch50Enabled = notch50Enabled;

            _highPassEnabled = lowCutoffHz > 0.0;
            _lowPassEnabled = highCutoffHz > 0.0;
            _notchEnabled = notch50Enabled;

            if (_highPassEnabled)
                _highPass = DesignHighPass(sampleRateHz, lowCutoffHz);

            if (_lowPassEnabled)
                _lowPass = DesignLowPass(sampleRateHz, highCutoffHz);

            if (_notchEnabled)
                _notch = DesignNotch(sampleRateHz, NotchFrequencyHz, NotchBandwidthHz);

            Reset();
        }

        public void Reset()
        {
            _highPassState = new BiquadState[_channelCount];
            _lowPassState = new BiquadState[_channelCount];
            _notchState = new BiquadState[_channelCount];
        }

        public void ProcessInPlace(
            ActiChampDataBlock block,
            int channelOffset,
            int channelCount)
        {
            if (block == null || block.SignalsVoltsChannelMajor == null)
                return;

            int sampleCount = block.SampleCount;

            if (sampleCount <= 0 || channelCount <= 0 || !IsEnabled)
                return;

            if (channelOffset < 0 ||
                channelCount < 0 ||
                channelOffset + channelCount > block.ChannelCount)
            {
                throw new ArgumentOutOfRangeException("channelOffset");
            }

            if (channelCount != _channelCount)
            {
                throw new InvalidOperationException(
                    "Filter channel count changed during acquisition. Expected " +
                    _channelCount.ToString() + ", got " + channelCount.ToString() + ".");
            }

            float[] data = block.SignalsVoltsChannelMajor;

            for (int ch = 0; ch < channelCount; ch++)
            {
                int baseIndex = (channelOffset + ch) * sampleCount;

                for (int s = 0; s < sampleCount; s++)
                {
                    double value = data[baseIndex + s];

                    if (_highPassEnabled)
                        value = ProcessBiquad(value, _highPass, ref _highPassState[ch]);

                    if (_lowPassEnabled)
                        value = ProcessBiquad(value, _lowPass, ref _lowPassState[ch]);

                    if (_notchEnabled)
                        value = ProcessBiquad(value, _notch, ref _notchState[ch]);

                    data[baseIndex + s] = (float)value;
                }
            }
        }

        public static void ValidateConfiguration(
            double sampleRateHz,
            double lowCutoffHz,
            double highCutoffHz,
            bool notch50Enabled)
        {
            if (sampleRateHz <= 0.0)
                throw new ArgumentOutOfRangeException("sampleRateHz");
            if (lowCutoffHz < 0.0)
                throw new ArgumentOutOfRangeException("lowCutoffHz");
            if (highCutoffHz < 0.0)
                throw new ArgumentOutOfRangeException("highCutoffHz");

            double nyquist = sampleRateHz * 0.5;

            if (lowCutoffHz > 0.0 && lowCutoffHz >= nyquist)
                throw new InvalidOperationException("Low cutoff должен быть ниже частоты Найквиста (" + nyquist.ToString("0.###") + " Hz).");

            if (highCutoffHz > 0.0 && highCutoffHz >= nyquist)
                throw new InvalidOperationException("High cutoff должен быть ниже частоты Найквиста (" + nyquist.ToString("0.###") + " Hz).");

            if (lowCutoffHz > 0.0 && highCutoffHz > 0.0 && lowCutoffHz >= highCutoffHz)
                throw new InvalidOperationException("Low cutoff должен быть ниже High cutoff.");

            if (notch50Enabled && nyquist <= NotchFrequencyHz + NotchBandwidthHz * 0.5)
                throw new InvalidOperationException("Для Notch 50 Hz частота дискретизации слишком мала.");
        }

        public static string Describe(double lowCutoffHz, double highCutoffHz, bool notch50Enabled)
        {
            string band;

            if (lowCutoffHz > 0.0 && highCutoffHz > 0.0)
                band = lowCutoffHz.ToString("0.###") + "-" + highCutoffHz.ToString("0.###") + " Hz";
            else if (lowCutoffHz > 0.0)
                band = "HP " + lowCutoffHz.ToString("0.###") + " Hz";
            else if (highCutoffHz > 0.0)
                band = "LP " + highCutoffHz.ToString("0.###") + " Hz";
            else
                band = "band off";

            return band + ", Notch 50 Hz " + (notch50Enabled ? "on" : "off");
        }

        private static double ProcessBiquad(
            double input,
            BiquadCoefficients coefficients,
            ref BiquadState state)
        {
            // Direct Form II Transposed. a0 is normalized to 1.
            double output = coefficients.B0 * input + state.Z1;
            state.Z1 = coefficients.B1 * input - coefficients.A1 * output + state.Z2;
            state.Z2 = coefficients.B2 * input - coefficients.A2 * output;
            return output;
        }

        private static BiquadCoefficients DesignLowPass(double sampleRateHz, double cutoffHz)
        {
            double omega = 2.0 * Math.PI * cutoffHz / sampleRateHz;
            double cos = Math.Cos(omega);
            double sin = Math.Sin(omega);
            double alpha = sin / (2.0 * ButterworthQ);

            double b0 = (1.0 - cos) * 0.5;
            double b1 = 1.0 - cos;
            double b2 = (1.0 - cos) * 0.5;
            double a0 = 1.0 + alpha;
            double a1 = -2.0 * cos;
            double a2 = 1.0 - alpha;

            return Normalize(b0, b1, b2, a0, a1, a2);
        }

        private static BiquadCoefficients DesignHighPass(double sampleRateHz, double cutoffHz)
        {
            double omega = 2.0 * Math.PI * cutoffHz / sampleRateHz;
            double cos = Math.Cos(omega);
            double sin = Math.Sin(omega);
            double alpha = sin / (2.0 * ButterworthQ);

            double b0 = (1.0 + cos) * 0.5;
            double b1 = -(1.0 + cos);
            double b2 = (1.0 + cos) * 0.5;
            double a0 = 1.0 + alpha;
            double a1 = -2.0 * cos;
            double a2 = 1.0 - alpha;

            return Normalize(b0, b1, b2, a0, a1, a2);
        }

        private static BiquadCoefficients DesignNotch(
            double sampleRateHz,
            double centerFrequencyHz,
            double bandwidthHz)
        {
            double omega = 2.0 * Math.PI * centerFrequencyHz / sampleRateHz;
            double cos = Math.Cos(omega);
            double sin = Math.Sin(omega);
            double q = centerFrequencyHz / bandwidthHz;
            double alpha = sin / (2.0 * q);

            double b0 = 1.0;
            double b1 = -2.0 * cos;
            double b2 = 1.0;
            double a0 = 1.0 + alpha;
            double a1 = -2.0 * cos;
            double a2 = 1.0 - alpha;

            return Normalize(b0, b1, b2, a0, a1, a2);
        }

        private static BiquadCoefficients Normalize(
            double b0,
            double b1,
            double b2,
            double a0,
            double a1,
            double a2)
        {
            if (Math.Abs(a0) < 1e-30)
                throw new InvalidOperationException("Invalid IIR filter coefficients.");

            BiquadCoefficients result = new BiquadCoefficients();
            result.B0 = b0 / a0;
            result.B1 = b1 / a0;
            result.B2 = b2 / a0;
            result.A1 = a1 / a0;
            result.A2 = a2 / a0;
            return result;
        }
    }
}
