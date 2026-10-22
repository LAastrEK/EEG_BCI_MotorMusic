using System;

namespace PolyHubNet
{
    public sealed class SsvepFeatureExtractor
    {
        private readonly SsvepModel _model;
        private readonly double[][] _sin;
        private readonly double[][] _cos;
        private readonly double[] _sinSum;
        private readonly double[] _cosSum;

        public int FeatureCount
        {
            get { return _model.FeatureCount; }
        }

        public SsvepFeatureExtractor(SsvepModel model)
        {
            if (model == null)
                throw new ArgumentNullException("model");

            model.Validate();
            _model = model;

            int featureCount = model.FeatureCount;
            int sampleCount = model.WindowSamples;

            _sin = new double[featureCount][];
            _cos = new double[featureCount][];
            _sinSum = new double[featureCount];
            _cosSum = new double[featureCount];

            for (int feature = 0; feature < featureCount; feature++)
            {
                double frequency = model.GetFeatureFrequency(feature);
                double[] sin = new double[sampleCount];
                double[] cos = new double[sampleCount];
                double sinSum = 0.0;
                double cosSum = 0.0;

                for (int n = 0; n < sampleCount; n++)
                {
                    double phase = 2.0 * Math.PI * frequency * n / model.SampleRate;
                    double sv = Math.Sin(phase);
                    double cv = Math.Cos(phase);

                    sin[n] = sv;
                    cos[n] = cv;
                    sinSum += sv;
                    cosSum += cv;
                }

                _sin[feature] = sin;
                _cos[feature] = cos;
                _sinSum[feature] = sinSum;
                _cosSum[feature] = cosSum;
            }
        }

        // window is channel-major and already ordered exactly as model.ChannelNames.
        public void Extract(float[] window, double[] features)
        {
            if (window == null)
                throw new ArgumentNullException("window");
            if (features == null)
                throw new ArgumentNullException("features");

            int channelCount = _model.ChannelNames.Length;
            int sampleCount = _model.WindowSamples;

            if (window.Length < channelCount * sampleCount)
                throw new ArgumentException("SSVEP window is too small.", "window");
            if (features.Length != _model.FeatureCount)
                throw new ArgumentException("SSVEP feature vector size mismatch.", "features");

            const double epsilon = 1e-24;
            double amplitudeScale = 2.0 / sampleCount;

            for (int feature = 0; feature < _model.FeatureCount; feature++)
            {
                double[] weights = _model.RessWeights[feature];
                double[] sin = _sin[feature];
                double[] cos = _cos[feature];

                double weightedSum = 0.0;
                double weightedSin = 0.0;
                double weightedCos = 0.0;

                for (int ch = 0; ch < channelCount; ch++)
                {
                    double weight = weights[ch];
                    if (weight == 0.0)
                        continue;

                    int baseIndex = ch * sampleCount;
                    double channelSum = 0.0;
                    double channelSin = 0.0;
                    double channelCos = 0.0;

                    for (int n = 0; n < sampleCount; n++)
                    {
                        double value = window[baseIndex + n];
                        channelSum += value;
                        channelSin += value * sin[n];
                        channelCos += value * cos[n];
                    }

                    weightedSum += weight * channelSum;
                    weightedSin += weight * channelSin;
                    weightedCos += weight * channelCos;
                }

                double mean = weightedSum / sampleCount;
                weightedSin -= mean * _sinSum[feature];
                weightedCos -= mean * _cosSum[feature];

                double a = amplitudeScale * weightedCos;
                double b = amplitudeScale * weightedSin;
                double power = a * a + b * b;

                features[feature] = Math.Log(power + epsilon);
            }
        }
    }
}
