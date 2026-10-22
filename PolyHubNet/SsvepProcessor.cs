using System;
using System.Collections.Generic;

namespace PolyHubNet
{
    public sealed class SsvepProcessor : SignalProcessor
    {
        private readonly object _sync = new object();
        private readonly long _qpcFrequency;

        private EogRejection _eogRejection = new EogRejection(null);
        private float[] _eogRing;
        private int _veogIndex = -1, _heogIndex = -1;
        private int _windowSamples, _stepSamples;
        private bool _rejectionActive;
        private bool _modelInputCompatible;
        private SsvepModel _model;
        private SsvepFeatureExtractor _featureExtractor;

        private string[] _configuredSourceChannelNames;
        private int[] _sourceChannelIndices;
        private int _configuredSampleRate;
        private int _configuredChannelCount;

        private float[] _ringBuffer;
        private float[] _windowBuffer;
        private double[] _features;
        private double[] _classScores;

        private int _writePosition;
        private int _bufferedSamples;
        private int _samplesSinceAnalysis;
        private bool _hasAnalyzedWindow;

        private long _lastSequence;
        private bool _hasLastSequence;

        public event Action<SsvepAnalysisResult> AnalysisReady;

        public SsvepProcessor(string name, int inputRingCapacity, long qpcFrequency)
            : base(name, inputRingCapacity)
        {
            if (qpcFrequency <= 0)
                throw new ArgumentOutOfRangeException("qpcFrequency");

            _qpcFrequency = qpcFrequency;
        }

        public SsvepModel Model
        {
            get
            {
                lock (_sync)
                    return _model;
            }
        }

        public void SetEogRejection(EogRejection rejection)
        {
            lock (_sync)
            {
                _eogRejection = rejection ?? new EogRejection(null);
                ClearConfiguration_NoLock();
                ResetBuffer_NoLock();
            }
        }

        public void SetModel(SsvepModel model)
        {
            if (model == null)
                throw new ArgumentNullException("model");

            model.Validate();

            lock (_sync)
            {
                _model = model;
                _featureExtractor = new SsvepFeatureExtractor(model);
                ClearConfiguration_NoLock();
                ResetBuffer_NoLock();
            }
        }

        public void ClearModel()
        {
            lock (_sync)
            {
                _model = null;
                _featureExtractor = null;
                ClearConfiguration_NoLock();
                ResetBuffer_NoLock();
            }
        }

        public void Reset()
        {
            lock (_sync)
                ResetBuffer_NoLock();
        }

        // Useful later for a static cursor over a recording. The window must already
        // contain only model channels, in model.ChannelNames order.
        public SsvepAnalysisResult AnalyzeWindow(float[] channelMajorWindow, long windowEndQpc,
            EogRejectionResult eogCheck = null)
        {
            lock (_sync)
            {
                if (_model == null || _featureExtractor == null)
                    return null;

                if (channelMajorWindow == null ||
                    channelMajorWindow.Length < _model.ChannelNames.Length * _model.WindowSamples)
                {
                    throw new ArgumentException("SSVEP analysis window is too small.", "channelMajorWindow");
                }

                if (eogCheck != null && eogCheck.IsRejected)
                    return new SsvepAnalysisResult(windowEndQpc, null, null, null, eogRejection: eogCheck);
                if (_eogRejection.Enabled && eogCheck == null)
                    throw new InvalidOperationException("Static SSVEP analysis requires the full source EOG check.");
                EnsureAnalysisArrays_NoLock();
                _featureExtractor.Extract(channelMajorWindow, _features);
                return EvaluateFeatures_NoLock(windowEndQpc);
            }
        }

        protected override SignalBlock ProcessBlock(SignalBlock input)
        {
            if (input == null || input.Data == null || input.SampleCount <= 0)
                return input;

            List<SsvepAnalysisResult> ready = null;

            lock (_sync)
            {
                if (_model == null && !_eogRejection.Enabled)
                    return input;

                if (!EnsureInputConfiguration_NoLock(input))
                    return input;

                if (_hasLastSequence && input.Sequence != _lastSequence + 1)
                    ResetBuffer_NoLock();

                _lastSequence = input.Sequence;
                _hasLastSequence = true;

                int modelChannelCount = _sourceChannelIndices.Length;
                int windowSamples = _windowSamples;

                for (int sample = 0; sample < input.SampleCount; sample++)
                {
                    for (int ch = 0; ch < modelChannelCount; ch++)
                    {
                        int sourceChannel = _sourceChannelIndices[ch];
                        _ringBuffer[ch * windowSamples + _writePosition] =
                            input.Data[sourceChannel * input.SampleCount + sample];
                    }

                    if (_eogRing != null)
                    {
                        _eogRing[_writePosition] = _veogIndex >= 0
                            ? input.Data[_veogIndex * input.SampleCount + sample] : 0.0f;
                        _eogRing[windowSamples + _writePosition] = _heogIndex >= 0
                            ? input.Data[_heogIndex * input.SampleCount + sample] : 0.0f;
                    }
                    _writePosition++;
                    if (_writePosition >= windowSamples)
                        _writePosition = 0;

                    if (_bufferedSamples < windowSamples)
                        _bufferedSamples++;

                    if (_bufferedSamples < windowSamples)
                        continue;

                    bool analyze = false;

                    if (!_hasAnalyzedWindow)
                    {
                        analyze = true;
                        _hasAnalyzedWindow = true;
                        _samplesSinceAnalysis = 0;
                    }
                    else
                    {
                        _samplesSinceAnalysis++;
                        if (_samplesSinceAnalysis >= _stepSamples)
                        {
                            analyze = true;
                            _samplesSinceAnalysis = 0;
                        }
                    }

                    if (!analyze)
                        continue;

                    long sampleQpcOffset = (long)Math.Round(
                        (double)sample * _qpcFrequency / input.SampleRate);

                    long endQpc = input.QpcTimestamp + sampleQpcOffset;
                    EogRejectionResult eogCheck = _eogRing != null
                        ? _eogRejection.Check(_eogRing, windowSamples,
                            _veogIndex >= 0 ? 0 : -1, _heogIndex >= 0 ? 1 : -1)
                        : EogRejectionResult.Accepted;
                    bool newRejection = eogCheck.IsRejected && !_rejectionActive;
                    _rejectionActive = eogCheck.IsRejected;
                    SsvepAnalysisResult result;
                    if (eogCheck.IsRejected || !_modelInputCompatible)
                        result = new SsvepAnalysisResult(endQpc, null, null, null,
                            eogRejection: eogCheck, isNewRejection: newRejection,
                            windowStartQpc: endQpc - (long)Math.Round((windowSamples - 1.0) * _qpcFrequency / input.SampleRate));
                    else
                    {
                        CopyLatestWindow_NoLock();
                        _featureExtractor.Extract(_windowBuffer, _features);
                        result = EvaluateFeatures_NoLock(endQpc);
                    }

                    if (ready == null)
                        ready = new List<SsvepAnalysisResult>(2);

                    ready.Add(result);
                }
            }

            if (ready != null)
            {
                Action<SsvepAnalysisResult> handler = AnalysisReady;
                if (handler != null)
                {
                    for (int i = 0; i < ready.Count; i++)
                        handler(ready[i]);
                }
            }

            // SSVEP analysis does not modify the signal. If an output stream is ever
            // connected, this also makes the processor a zero-copy pass-through.
            return input;
        }

        private bool EnsureInputConfiguration_NoLock(SignalBlock input)
        {
            if (input.SampleRate <= 0)
                return false;

            SignalStream inputStream = InputStream;
            string[] sourceNames = inputStream != null ? inputStream.ChannelNames : null;

            if (sourceNames == null || sourceNames.Length != input.ChannelCount ||
                input.Data.Length < (long)input.ChannelCount * input.SampleCount)
                return false;

            if (ReferenceEquals(sourceNames, _configuredSourceChannelNames) &&
                _configuredSampleRate == input.SampleRate &&
                _configuredChannelCount == input.ChannelCount &&
                _sourceChannelIndices != null)
            {
                return true;
            }

            bool modelCompatible = _model != null && input.SampleRate == _model.SampleRate;
            int[] indices = new int[modelCompatible ? _model.ChannelNames.Length : 0];

            for (int modelChannel = 0; modelChannel < indices.Length; modelChannel++)
            {
                int found = -1;

                for (int sourceChannel = 0; sourceChannel < sourceNames.Length; sourceChannel++)
                {
                    if (string.Equals(
                        sourceNames[sourceChannel],
                        _model.ChannelNames[modelChannel],
                        StringComparison.OrdinalIgnoreCase))
                    {
                        found = sourceChannel;
                        break;
                    }
                }

                if (found < 0)
                {
                    modelCompatible = false;
                    indices = new int[0];
                    break;
                }

                indices[modelChannel] = found;
            }

            if (!modelCompatible && !_eogRejection.Enabled) return false;
            _modelInputCompatible = modelCompatible;
            _sourceChannelIndices = indices;
            _configuredSourceChannelNames = sourceNames;
            _configuredSampleRate = input.SampleRate;
            _configuredChannelCount = input.ChannelCount;

            int channelCount = indices.Length;
            _windowSamples = modelCompatible ? _model.WindowSamples : input.SampleRate;
            _stepSamples = modelCompatible ? _model.StepSamples : Math.Max(1, input.SampleRate / 4);
            int windowSamples = _windowSamples;
            _veogIndex = EogRejection.FindChannel(sourceNames, "VEOG");
            _heogIndex = EogRejection.FindChannel(sourceNames, "HEOG");
            _eogRing = _eogRejection.Enabled ? new float[2 * windowSamples] : null;
            _ringBuffer = new float[channelCount * windowSamples];
            _windowBuffer = new float[channelCount * windowSamples];
            if (_model != null) EnsureAnalysisArrays_NoLock();
            ResetBuffer_NoLock();

            return true;
        }

        private SsvepAnalysisResult EvaluateFeatures_NoLock(long qpcTimestamp)
        {
            int candidate;
            double distanceRatio, backgroundEvidence;
            int accepted = _model.EvaluateClassIndex(_features, _classScores,
                out candidate, out distanceRatio, out backgroundEvidence);
            return new SsvepAnalysisResult(qpcTimestamp,
                accepted >= 0 ? (int?)_model.ClassIds[accepted] : null,
                _features, _classScores,
                candidate >= 0 ? (int?)_model.ClassIds[candidate] : null,
                distanceRatio, backgroundEvidence);
        }

        private void EnsureAnalysisArrays_NoLock()
        {
            if (_features == null || _features.Length != _model.FeatureCount)
                _features = new double[_model.FeatureCount];

            if (_classScores == null || _classScores.Length != _model.ClassCount)
                _classScores = new double[_model.ClassCount];
        }

        private void CopyLatestWindow_NoLock()
        {
            int channelCount = _model.ChannelNames.Length;
            int windowSamples = _model.WindowSamples;
            int firstPart = windowSamples - _writePosition;

            for (int ch = 0; ch < channelCount; ch++)
            {
                int ringBase = ch * windowSamples;
                int windowBase = ch * windowSamples;

                Array.Copy(
                    _ringBuffer,
                    ringBase + _writePosition,
                    _windowBuffer,
                    windowBase,
                    firstPart);

                if (_writePosition > 0)
                {
                    Array.Copy(
                        _ringBuffer,
                        ringBase,
                        _windowBuffer,
                        windowBase + firstPart,
                        _writePosition);
                }
            }
        }

        private void ClearConfiguration_NoLock()
        {
            _configuredSourceChannelNames = null;
            _sourceChannelIndices = null;
            _configuredSampleRate = 0;
            _configuredChannelCount = 0;

            _modelInputCompatible = false;
            _eogRing = null;
            _ringBuffer = null;
            _windowBuffer = null;
            _features = null;
            _classScores = null;
        }

        private void ResetBuffer_NoLock()
        {
            _rejectionActive = false;
            _writePosition = 0;
            _bufferedSamples = 0;
            _samplesSinceAnalysis = 0;
            _hasAnalyzedWindow = false;
            _lastSequence = 0;
            _hasLastSequence = false;

            if (_ringBuffer != null)
                Array.Clear(_ringBuffer, 0, _ringBuffer.Length);
        }
    }
}
