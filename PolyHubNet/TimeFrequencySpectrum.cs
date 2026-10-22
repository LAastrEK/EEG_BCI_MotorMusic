using System;
using System.Collections.Generic;

namespace PolyHubNet
{
    // Worker-owned numerical state. No WinForms or OpenGL calls are made here.
    internal sealed class TimeFrequencySpectrum
    {
        internal const int FrequencyRows = 128;
        internal const double MinFrequency = 0.5;
        internal const double MaxFrequency = 50.0;
        internal readonly int FftSize;
        internal readonly int HopSamples;
        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly float[] _history;
        private readonly double[] _hann;
        private readonly double _hannSum;
        private readonly double[] _real;
        private readonly double[] _imag;
        private readonly double[] _magnitudes;
        private readonly double[] _rowBins = new double[FrequencyRows];
        private readonly int[] _rowFirstBin = new int[FrequencyRows];
        private readonly int[] _rowLastBin = new int[FrequencyRows];
        private int _writePosition;
        private int _historyCount;
        private long _nextInputSample = -1;
        private long _nextSliceEnd;

        internal TimeFrequencySpectrum(int channels, int sampleRate, int windowSamples = 0)
        {
            if (channels <= 0 || sampleRate <= 1)
                throw new ArgumentOutOfRangeException("channels/sampleRate");
            _channels = channels;
            _sampleRate = sampleRate;
            // About two seconds at every EEG rate; always a radix-2 FFT.
            int size = windowSamples > 0 ? 128 : GetFftSize(sampleRate);
            while (size < windowSamples) size = checked(size * 2);
            int taperedSamples = windowSamples > 0 ? windowSamples : size;
            FftSize = size;
            HopSamples = Math.Max(1, (int)Math.Round(sampleRate * 0.04));
            _history = new float[checked(channels * size)];
            _hann = new double[size];
            _real = new double[size];
            _imag = new double[size];
            _magnitudes = new double[size / 2 + 1];
            double sum = 0;
            for (int i = 0; i < taperedSamples; i++)
            {
                _hann[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (taperedSamples - 1));
                sum += _hann[i];
            }
            _hannSum = sum;
            double max = Math.Min(MaxFrequency, sampleRate * 0.5);
            double logRange = Math.Log(max / MinFrequency);
            for (int row = 0; row < FrequencyRows; row++)
            {
                double frequency = MinFrequency * Math.Exp(logRange * row / (FrequencyRows - 1));
                _rowBins[row] = frequency * size / sampleRate;
                double low = MinFrequency * Math.Exp(logRange * Math.Max(0, row - 0.5) / (FrequencyRows - 1));
                double high = MinFrequency * Math.Exp(logRange * Math.Min(FrequencyRows - 1, row + 0.5) / (FrequencyRows - 1));
                _rowFirstBin[row] = Math.Max(1, (int)Math.Ceiling(low * size / sampleRate));
                _rowLastBin[row] = Math.Min(size / 2, (int)Math.Floor(high * size / sampleRate));
            }
        }

        // One spectrum of the complete epoch. Zero padding does not change
        // the Hann normalization or introduce samples outside the selected window.
        internal static float[] CalculateEpoch(SignalBlock block)
        {
            if (block == null || block.SampleCount < 3)
                return null;
            var spectrum = new TimeFrequencySpectrum(block.ChannelCount, block.SampleRate, block.SampleCount);
            for (int ch = 0; ch < block.ChannelCount; ch++)
                for (int i = 0; i < block.SampleCount; i++)
                {
                    float value = block.Data[ch * block.SampleCount + i];
                    spectrum._history[ch * spectrum.FftSize + i] =
                        float.IsNaN(value) || float.IsInfinity(value) ? 0.0f : value;
                }
            return spectrum.BuildSlice(block.SampleCount).Amplitudes;
        }

        internal static float GetIntensity(float amplitude, float scaleVolts)
        {
            if (float.IsNaN(amplitude) || amplitude <= 0.0f)
                return 0.0f;
            return (float)Math.Min(1.0, 20.0 * amplitude / scaleVolts);
        }

        internal static int GetFftSize(int sampleRate)
        {
            int size = 128;
            while (size < 2L * sampleRate && size < 32768)
                size <<= 1;
            return size;
        }

        internal List<SpectralSlice> Append(SignalBlock block, long firstSample, Func<bool> isCancelled = null)
        {
            if (block.ChannelCount != _channels || block.SampleRate != _sampleRate)
                throw new ArgumentException("Signal format changed.");
            if (_nextInputSample != firstSample)
            {
                // Never join nonadjacent samples into an FFT after an input queue overrun.
                _historyCount = 0;
                _writePosition = 0;
                _nextSliceEnd = firstSample + FftSize;
                long remainder = firstSample % HopSamples;
                if (remainder != 0)
                    _nextSliceEnd += HopSamples - remainder;
            }
            List<SpectralSlice> completed = new List<SpectralSlice>();
            for (int sample = 0; sample < block.SampleCount; sample++)
            {
                for (int ch = 0; ch < _channels; ch++)
                {
                    float value = block.Data[ch * block.SampleCount + sample];
                    _history[ch * FftSize + _writePosition] =
                        float.IsNaN(value) || float.IsInfinity(value) ? 0.0f : value;
                }
                _writePosition = (_writePosition + 1) % FftSize;
                _historyCount = Math.Min(_historyCount + 1, FftSize);
                long endSample = firstSample + sample + 1;
                if (_historyCount == FftSize && endSample == _nextSliceEnd)
                {
                    if (isCancelled != null && isCancelled())
                        return completed;
                    completed.Add(BuildSlice(endSample));
                    _nextSliceEnd += HopSamples;
                }
            }
            _nextInputSample = firstSample + block.SampleCount;
            return completed;
        }

        private SpectralSlice BuildSlice(long endSample)
        {
            float[] amplitudes = new float[_channels * FrequencyRows];
            for (int ch = 0; ch < _channels; ch++)
            {
                int offset = ch * FftSize;
                double mean = 0;
                for (int i = 0; i < FftSize; i++)
                {
                    int index = (_writePosition + i) % FftSize;
                    mean += _history[offset + index] * _hann[i];
                }
                // Match DC estimation to the taper: partial sine cycles must not
                // turn into a large low-frequency component after windowing.
                mean /= _hannSum;
                for (int i = 0; i < FftSize; i++)
                {
                    int index = (_writePosition + i) % FftSize;
                    _real[i] = (_history[offset + index] - mean) * _hann[i];
                    _imag[i] = 0;
                }
                FftInPlace(_real, _imag);
                for (int bin = 0; bin < _magnitudes.Length; bin++)
                {
                    double factor = bin == 0 || bin == FftSize / 2 ? 1.0 : 2.0;
                    _magnitudes[bin] = factor * Math.Sqrt(_real[bin] * _real[bin] + _imag[bin] * _imag[bin]) / _hannSum;
                }
                for (int row = 0; row < FrequencyRows; row++)
                {
                    double binPosition = _rowBins[row];
                    int bin0 = Math.Min(FftSize / 2, (int)binPosition);
                    int bin1 = Math.Min(FftSize / 2, bin0 + 1);
                    double amplitude = _magnitudes[bin0] +
                        (_magnitudes[bin1] - _magnitudes[bin0]) * (binPosition - bin0);
                    // Log-frequency resampling must not miss a narrow high-frequency peak.
                    for (int bin = _rowFirstBin[row]; bin <= _rowLastBin[row]; bin++)
                        amplitude = Math.Max(amplitude, _magnitudes[bin]);
                    amplitudes[ch * FrequencyRows + row] = (float)amplitude;
                }
            }
            return new SpectralSlice
            {
                EndAbsoluteSample = endSample,
                ChannelsCount = _channels,
                HopSamples = HopSamples,
                Amplitudes = amplitudes
            };
        }

        private static void FftInPlace(double[] real, double[] imag)
        {
            int n = real.Length;

            int j = 0;
            for (int i = 1; i < n; i++)
            {
                int bit = n >> 1;
                while ((j & bit) != 0)
                {
                    j ^= bit;
                    bit >>= 1;
                }

                j ^= bit;

                if (i < j)
                {
                    double temp = real[i];
                    real[i] = real[j];
                    real[j] = temp;

                    temp = imag[i];
                    imag[i] = imag[j];
                    imag[j] = temp;
                }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                double angle = -2.0 * Math.PI / len;
                double wLenReal = Math.Cos(angle);
                double wLenImag = Math.Sin(angle);

                for (int i = 0; i < n; i += len)
                {
                    double wReal = 1.0;
                    double wImag = 0.0;
                    int half = len >> 1;

                    for (int k = 0; k < half; k++)
                    {
                        int even = i + k;
                        int odd = even + half;

                        double oddReal = real[odd] * wReal - imag[odd] * wImag;
                        double oddImag = real[odd] * wImag + imag[odd] * wReal;

                        double evenReal = real[even];
                        double evenImag = imag[even];

                        real[even] = evenReal + oddReal;
                        imag[even] = evenImag + oddImag;
                        real[odd] = evenReal - oddReal;
                        imag[odd] = evenImag - oddImag;

                        double nextWReal = wReal * wLenReal - wImag * wLenImag;
                        wImag = wReal * wLenImag + wImag * wLenReal;
                        wReal = nextWReal;
                    }
                }
            }
        }

    }
}
