using System;
using System.Collections.Generic;

namespace PolyHubNet
{
    public sealed class SsvepTrainer
    {
        private const double RessReferenceShrinkage = 0.05;
        private const int GeneralizedPowerIterations = 80;
        private const double GeneralizedPowerTolerance = 1e-10;

        public SsvepModel Train(
            SessionReader reader,
            string[] channelNames,
            int[] classIds,
            double[] targetFrequencies,
            int windowMilliseconds,
            int stepMilliseconds,
            int harmonicCount,
            Action<int, string> progress = null,
            EogRejection eogRejection = null)
        {
            ReportProgress(progress, 0, "Validating calibration data...");

            if (reader == null)
                throw new ArgumentNullException("reader");
            if (channelNames == null)
                throw new ArgumentNullException("channelNames");
            channelNames = SsvepChannelSelection.SelectEegChannels(channelNames);
            if (channelNames.Length == 0)
                throw new ArgumentException("No EEG channels available for SSVEP training.", "channelNames");
            if (classIds == null || classIds.Length == 0)
                throw new ArgumentException("No SSVEP classes specified.", "classIds");
            if (targetFrequencies == null || targetFrequencies.Length != classIds.Length)
                throw new ArgumentException("SSVEP class/frequency count mismatch.", "targetFrequencies");
            if (windowMilliseconds <= 0)
                throw new ArgumentOutOfRangeException("windowMilliseconds");
            if (stepMilliseconds <= 0)
                throw new ArgumentOutOfRangeException("stepMilliseconds");
            if (harmonicCount <= 0)
                throw new ArgumentOutOfRangeException("harmonicCount");

            int sampleRate = reader.Header.SampleRate;
            if (sampleRate <= 0)
                throw new InvalidOperationException("Recording sample rate is invalid.");

            int windowSamples = (int)Math.Round(sampleRate * windowMilliseconds / 1000.0);
            int stepSamples = (int)Math.Round(sampleRate * stepMilliseconds / 1000.0);

            if (windowSamples <= 0 || stepSamples <= 0 || stepSamples > windowSamples)
                throw new InvalidOperationException("Invalid SSVEP training window settings.");

            eogRejection = eogRejection ?? new EogRejection(null);
            eogRejection.ValidateChannels(reader.ChannelNames);
            var acceptedEpochs = new Dictionary<int, List<EpochInfo>>();
            foreach (int id in classIds)
                acceptedEpochs[id] = SelectAcceptedEpochs(reader, id, eogRejection, progress);
            acceptedEpochs[SsvepModel.BackgroundClassId] = SelectAcceptedEpochs(
                reader, SsvepModel.BackgroundClassId, eogRejection, progress);

            int[] sourceChannelIndices = ResolveChannelIndices(reader.ChannelNames, channelNames);
            int classCount = classIds.Length;
            int featureCount = classCount * harmonicCount;
            int channelCount = channelNames.Length;

            SsvepModel model = new SsvepModel
            {
                Version = 1, // The extractor can validate the RESS stage before acceptance is fitted.
                SampleRate = sampleRate,
                WindowSamples = windowSamples,
                StepSamples = stepSamples,
                ChannelNames = (string[])channelNames.Clone(),
                ClassIds = (int[])classIds.Clone(),
                TargetFrequencies = (double[])targetFrequencies.Clone(),
                HarmonicCount = harmonicCount,
                RessWeights = new double[featureCount][],
                LdaWeights = new double[classCount][],
                LdaBias = new double[classCount],
                ClassFeatureMeans = new double[classCount][]
            };

            TrainRessWeights(
                reader,
                sourceChannelIndices,
                classIds,
                targetFrequencies,
                harmonicCount,
                windowSamples,
                stepSamples,
                model.RessWeights,
                acceptedEpochs,
                progress);

            ReportProgress(progress, 55, "Extracting training features...");

            // Validate the RESS part before the common runtime extractor is created.
            for (int k = 0; k < classCount; k++)
            {
                model.LdaWeights[k] = new double[featureCount];
                model.ClassFeatureMeans[k] = new double[featureCount];
            }

            model.Validate();

            SsvepFeatureExtractor extractor = new SsvepFeatureExtractor(model);
            List<double[]> trainingFeatures = new List<double[]>();
            List<int> trainingLabels = new List<int>();
            List<SsvepAcceptance.Trial> trainingTrials = new List<SsvepAcceptance.Trial>();

            float[] selectedWindow = new float[channelCount * windowSamples];

            for (int classIndex = 0; classIndex < classCount; classIndex++)
            {
                int featureProgress = 55 + (int)Math.Round(20.0 * classIndex / Math.Max(1, classCount));
                ReportProgress(
                    progress,
                    featureProgress,
                    "Extracting features: class " + (classIndex + 1).ToString() + "/" + classCount.ToString());

                List<EpochInfo> epochs = acceptedEpochs[classIds[classIndex]];
                if (epochs.Count == 0)
                    throw new InvalidOperationException("No calibration epochs for class " + classIds[classIndex].ToString() + ".");

                int classWindowCount = 0;

                for (int e = 0; e < epochs.Count; e++)
                {
                    SignalBlock epochBlock = reader.ReadEpochAsSingleBlock(epochs[e]);
                    if (epochBlock == null || epochBlock.SampleCount < windowSamples)
                        continue;

                    SsvepAcceptance.Trial trial = new SsvepAcceptance.Trial { ClassIndex = classIndex };

                    for (int start = 0; start + windowSamples <= epochBlock.SampleCount; start += stepSamples)
                    {
                        CopySelectedWindow(
                            epochBlock.Data,
                            epochBlock.SampleCount,
                            sourceChannelIndices,
                            start,
                            windowSamples,
                            selectedWindow);

                        double[] features = new double[featureCount];
                        extractor.Extract(selectedWindow, features);

                        trainingFeatures.Add(features);
                        trainingLabels.Add(classIndex);
                        trial.Features.Add(features);
                        classWindowCount++;
                    }
                    trainingTrials.Add(trial);
                }

                if (classWindowCount < 2)
                {
                    throw new InvalidOperationException(
                        "Not enough SSVEP training windows for class " +
                        classIds[classIndex].ToString() + ".");
                }
            }

            ReportProgress(progress, 78, "Training shrinkage LDA...");

            TrainShrinkageLda(
                trainingFeatures,
                trainingLabels,
                classCount,
                featureCount,
                model.LdaWeights,
                model.LdaBias,
                model.ClassFeatureMeans);

            ReportProgress(progress, 82, "Fitting full-feature acceptance corridors...");
            List<SsvepAcceptance.Trial> backgroundTrials = new List<SsvepAcceptance.Trial>();
            foreach (EpochInfo epoch in acceptedEpochs[SsvepModel.BackgroundClassId])
            {
                SignalBlock block = reader.ReadEpochAsSingleBlock(epoch);
                if (block == null || block.SampleCount < windowSamples) continue;
                SsvepAcceptance.Trial trial = new SsvepAcceptance.Trial { ClassIndex = -1 };
                for (int start = 0; start + windowSamples <= block.SampleCount; start += stepSamples)
                {
                    CopySelectedWindow(block.Data, block.SampleCount, sourceChannelIndices,
                        start, windowSamples, selectedWindow);
                    double[] features = new double[featureCount];
                    extractor.Extract(selectedWindow, features);
                    trial.Features.Add(features);
                }
                backgroundTrials.Add(trial);
            }
            SsvepAcceptance.Train(model, trainingTrials, backgroundTrials);
            model.Version = SsvepModel.CurrentVersion;
            ReportProgress(progress, 94, "Acceptance: " + featureCount + " features, " +
                trainingTrials.Count + " target trials, " + backgroundTrials.Count + " background trials.");

            model.Validate();
            ReportProgress(progress, 95, "SSVEP model trained.");
            return model;
        }

        public SsvepModel Train(
            SessionReader reader,
            string[] channelNames,
            int[] classIds,
            double[] targetFrequencies)
        {
            return Train(
                reader,
                channelNames,
                classIds,
                targetFrequencies,
                1000,
                250,
                2);
        }

        private static List<EpochInfo> SelectAcceptedEpochs(SessionReader reader, int classId,
            EogRejection rejection, Action<int, string> progress)
        {
            List<EpochInfo> epochs = reader.GetEpochsByClass(classId);
            if (!rejection.Enabled) return epochs;
            var accepted = new List<EpochInfo>();
            foreach (EpochInfo epoch in epochs)
            {
                EogRejectionResult check = rejection.Check(reader.ReadEpochAsSingleBlock(epoch), reader.ChannelNames);
                if (!check.IsRejected) accepted.Add(epoch);
            }
            ReportProgress(progress, 3, "EOG class " + classId + ": " + accepted.Count +
                " accepted, " + (epochs.Count - accepted.Count) + " Rejected (whole epochs).");
            if (epochs.Count > 0 && accepted.Count == 0)
                throw new InvalidOperationException("All calibration epochs for class " + classId +
                    " were Rejected by EOG. Check the threshold or record more clean epochs.");
            return accepted;
        }

        private static int[] ResolveChannelIndices(string[] sourceNames, string[] requestedNames)
        {
            if (sourceNames == null || sourceNames.Length == 0)
                throw new InvalidOperationException("Recording has no channel names.");

            int[] indices = new int[requestedNames.Length];

            for (int i = 0; i < requestedNames.Length; i++)
            {
                int found = -1;

                for (int ch = 0; ch < sourceNames.Length; ch++)
                {
                    if (string.Equals(sourceNames[ch], requestedNames[i], StringComparison.OrdinalIgnoreCase))
                    {
                        found = ch;
                        break;
                    }
                }

                if (found < 0)
                    throw new InvalidOperationException("SSVEP channel not found in recording: " + requestedNames[i]);

                indices[i] = found;
            }

            return indices;
        }

        private static void CopySelectedWindow(
            float[] source,
            int sourceSamplesPerChannel,
            int[] sourceChannelIndices,
            int sourceStartSample,
            int windowSamples,
            float[] target)
        {
            for (int ch = 0; ch < sourceChannelIndices.Length; ch++)
            {
                int src = sourceChannelIndices[ch] * sourceSamplesPerChannel + sourceStartSample;
                int dst = ch * windowSamples;
                Array.Copy(source, src, target, dst, windowSamples);
            }
        }

        private void TrainRessWeights(
            SessionReader reader,
            int[] sourceChannelIndices,
            int[] classIds,
            double[] targetFrequencies,
            int harmonicCount,
            int windowSamples,
            int stepSamples,
            double[][] outputWeights,
            Dictionary<int, List<EpochInfo>> acceptedEpochs,
            Action<int, string> progress)
        {
            int channelCount = sourceChannelIndices.Length;
            int classCount = classIds.Length;
            int sampleRate = reader.Header.SampleRate;

            for (int classIndex = 0; classIndex < classCount; classIndex++)
            {
                int ressProgress = 10 + (int)Math.Round(40.0 * classIndex / Math.Max(1, classCount));
                ReportProgress(
                    progress,
                    ressProgress,
                    "Training RESS: class " + (classIndex + 1).ToString() + "/" + classCount.ToString() +
                    " (" + targetFrequencies[classIndex].ToString("0.##") + " Hz)");

                List<EpochInfo> epochs = acceptedEpochs[classIds[classIndex]];
                if (epochs.Count == 0)
                    throw new InvalidOperationException("No calibration epochs for class " + classIds[classIndex].ToString() + ".");

                double[,] referenceCovariance = new double[channelCount, channelCount];
                double[,,] signalCovariances = new double[harmonicCount, channelCount, channelCount];
                double[][] sin = new double[harmonicCount][];
                double[][] cos = new double[harmonicCount][];

                for (int h = 0; h < harmonicCount; h++)
                {
                    sin[h] = new double[windowSamples];
                    cos[h] = new double[windowSamples];

                    double frequency = targetFrequencies[classIndex] * (h + 1);
                    for (int n = 0; n < windowSamples; n++)
                    {
                        double phase = 2.0 * Math.PI * frequency * n / sampleRate;
                        sin[h][n] = Math.Sin(phase);
                        cos[h][n] = Math.Cos(phase);
                    }
                }

                int windowCount = 0;
                double[] means = new double[channelCount];
                double[][] harmonicA = new double[harmonicCount][];
                double[][] harmonicB = new double[harmonicCount][];

                for (int h = 0; h < harmonicCount; h++)
                {
                    harmonicA[h] = new double[channelCount];
                    harmonicB[h] = new double[channelCount];
                }

                for (int e = 0; e < epochs.Count; e++)
                {
                    SignalBlock epochBlock = reader.ReadEpochAsSingleBlock(epochs[e]);
                    if (epochBlock == null || epochBlock.SampleCount < windowSamples)
                        continue;

                    for (int start = 0; start + windowSamples <= epochBlock.SampleCount; start += stepSamples)
                    {
                        Array.Clear(means, 0, means.Length);
                        for (int h = 0; h < harmonicCount; h++)
                        {
                            Array.Clear(harmonicA[h], 0, channelCount);
                            Array.Clear(harmonicB[h], 0, channelCount);
                        }

                        for (int ch = 0; ch < channelCount; ch++)
                        {
                            int srcBase = sourceChannelIndices[ch] * epochBlock.SampleCount + start;
                            double sum = 0.0;

                            for (int n = 0; n < windowSamples; n++)
                                sum += epochBlock.Data[srcBase + n];

                            means[ch] = sum / windowSamples;
                        }

                        // Broadband covariance is the RESS reference covariance.
                        for (int c1 = 0; c1 < channelCount; c1++)
                        {
                            int base1 = sourceChannelIndices[c1] * epochBlock.SampleCount + start;

                            for (int c2 = c1; c2 < channelCount; c2++)
                            {
                                int base2 = sourceChannelIndices[c2] * epochBlock.SampleCount + start;
                                double sum = 0.0;

                                for (int n = 0; n < windowSamples; n++)
                                {
                                    double v1 = epochBlock.Data[base1 + n] - means[c1];
                                    double v2 = epochBlock.Data[base2 + n] - means[c2];
                                    sum += v1 * v2;
                                }

                                double covariance = sum / windowSamples;
                                referenceCovariance[c1, c2] += covariance;
                                if (c1 != c2)
                                    referenceCovariance[c2, c1] += covariance;
                            }
                        }

                        // Phase-independent sinusoidal coefficients for every requested harmonic.
                        for (int h = 0; h < harmonicCount; h++)
                        {
                            double scale = 2.0 / windowSamples;

                            for (int ch = 0; ch < channelCount; ch++)
                            {
                                int srcBase = sourceChannelIndices[ch] * epochBlock.SampleCount + start;
                                double a = 0.0;
                                double b = 0.0;

                                for (int n = 0; n < windowSamples; n++)
                                {
                                    double value = epochBlock.Data[srcBase + n] - means[ch];
                                    a += value * cos[h][n];
                                    b += value * sin[h][n];
                                }

                                harmonicA[h][ch] = scale * a;
                                harmonicB[h][ch] = scale * b;
                            }

                            for (int c1 = 0; c1 < channelCount; c1++)
                            {
                                for (int c2 = c1; c2 < channelCount; c2++)
                                {
                                    double covariance = 0.5 *
                                        (harmonicA[h][c1] * harmonicA[h][c2] +
                                         harmonicB[h][c1] * harmonicB[h][c2]);

                                    signalCovariances[h, c1, c2] += covariance;
                                    if (c1 != c2)
                                        signalCovariances[h, c2, c1] += covariance;
                                }
                            }
                        }

                        windowCount++;
                    }
                }

                if (windowCount < 2)
                {
                    throw new InvalidOperationException(
                        "Not enough SSVEP calibration data for class " + classIds[classIndex].ToString() + ".");
                }

                ScaleMatrix(referenceCovariance, 1.0 / windowCount);
                ShrinkReferenceCovariance(referenceCovariance, RessReferenceShrinkage);

                for (int h = 0; h < harmonicCount; h++)
                {
                    double[,] signalCovariance = new double[channelCount, channelCount];
                    for (int r = 0; r < channelCount; r++)
                    {
                        for (int c = 0; c < channelCount; c++)
                            signalCovariance[r, c] = signalCovariances[h, r, c] / windowCount;
                    }

                    int featureIndex = classIndex * harmonicCount + h;
                    outputWeights[featureIndex] = SolveDominantGeneralizedEigenvector(
                        signalCovariance,
                        referenceCovariance);
                }
            }
        }

        private static void ScaleMatrix(double[,] matrix, double scale)
        {
            int rows = matrix.GetLength(0);
            int cols = matrix.GetLength(1);

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                    matrix[r, c] *= scale;
            }
        }

        private static void ShrinkReferenceCovariance(double[,] matrix, double shrinkage)
        {
            int n = matrix.GetLength(0);
            double trace = 0.0;

            for (int i = 0; i < n; i++)
                trace += matrix[i, i];

            double meanVariance = trace / n;
            if (meanVariance <= 0.0 || double.IsNaN(meanVariance) || double.IsInfinity(meanVariance))
                meanVariance = 1.0;

            double keep = 1.0 - shrinkage;

            for (int r = 0; r < n; r++)
            {
                for (int c = 0; c < n; c++)
                    matrix[r, c] *= keep;

                matrix[r, r] += shrinkage * meanVariance;
            }
        }

        private static double[] SolveDominantGeneralizedEigenvector(double[,] signal, double[,] reference)
        {
            int n = signal.GetLength(0);
            double[,] cholesky = CholeskyWithJitter(reference);

            double[] vector = new double[n];
            double startScale = 1.0 / Math.Sqrt(n);
            for (int i = 0; i < n; i++)
                vector[i] = startScale;

            double[] signalTimesVector = new double[n];
            double[] next = new double[n];

            for (int iteration = 0; iteration < GeneralizedPowerIterations; iteration++)
            {
                MultiplyMatrixVector(signal, vector, signalTimesVector);
                SolveCholesky(cholesky, signalTimesVector, next);

                double norm = VectorNorm(next);
                if (norm < 1e-30)
                {
                    int best = 0;
                    double bestDiagonal = signal[0, 0];
                    for (int i = 1; i < n; i++)
                    {
                        if (signal[i, i] > bestDiagonal)
                        {
                            bestDiagonal = signal[i, i];
                            best = i;
                        }
                    }

                    Array.Clear(vector, 0, vector.Length);
                    vector[best] = 1.0;
                    return vector;
                }

                for (int i = 0; i < n; i++)
                    next[i] /= norm;

                double dot = 0.0;
                for (int i = 0; i < n; i++)
                    dot += vector[i] * next[i];

                if (dot < 0.0)
                {
                    for (int i = 0; i < n; i++)
                        next[i] = -next[i];
                }

                double difference = 0.0;
                for (int i = 0; i < n; i++)
                {
                    double d = next[i] - vector[i];
                    difference += d * d;
                    vector[i] = next[i];
                }

                if (difference < GeneralizedPowerTolerance * GeneralizedPowerTolerance)
                    break;
            }

            // Fix the otherwise arbitrary sign for reproducible saved models.
            int maxIndex = 0;
            double maxAbs = Math.Abs(vector[0]);
            for (int i = 1; i < vector.Length; i++)
            {
                double value = Math.Abs(vector[i]);
                if (value > maxAbs)
                {
                    maxAbs = value;
                    maxIndex = i;
                }
            }

            if (vector[maxIndex] < 0.0)
            {
                for (int i = 0; i < vector.Length; i++)
                    vector[i] = -vector[i];
            }

            return vector;
        }

        private static void MultiplyMatrixVector(double[,] matrix, double[] vector, double[] result)
        {
            int n = vector.Length;

            for (int r = 0; r < n; r++)
            {
                double sum = 0.0;
                for (int c = 0; c < n; c++)
                    sum += matrix[r, c] * vector[c];

                result[r] = sum;
            }
        }

        private static double VectorNorm(double[] vector)
        {
            double sum = 0.0;
            for (int i = 0; i < vector.Length; i++)
                sum += vector[i] * vector[i];

            return Math.Sqrt(sum);
        }

        private static double[,] CholeskyWithJitter(double[,] source)
        {
            int n = source.GetLength(0);
            double trace = 0.0;
            for (int i = 0; i < n; i++)
                trace += source[i, i];

            double scale = trace > 0.0 ? trace / n : 1.0;
            double jitter = scale * 1e-12;

            for (int attempt = 0; attempt < 8; attempt++)
            {
                double[,] matrix = (double[,])source.Clone();
                for (int i = 0; i < n; i++)
                    matrix[i, i] += jitter;

                double[,] lower;
                if (TryCholesky(matrix, out lower))
                    return lower;

                jitter *= 10.0;
            }

            throw new InvalidOperationException("Unable to factorize covariance matrix.");
        }

        private static bool TryCholesky(double[,] matrix, out double[,] lower)
        {
            int n = matrix.GetLength(0);
            lower = new double[n, n];

            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j <= i; j++)
                {
                    double sum = matrix[i, j];

                    for (int k = 0; k < j; k++)
                        sum -= lower[i, k] * lower[j, k];

                    if (i == j)
                    {
                        if (sum <= 0.0 || double.IsNaN(sum) || double.IsInfinity(sum))
                            return false;

                        lower[i, j] = Math.Sqrt(sum);
                    }
                    else
                    {
                        if (lower[j, j] == 0.0)
                            return false;

                        lower[i, j] = sum / lower[j, j];
                    }
                }
            }

            return true;
        }

        private static void SolveCholesky(double[,] lower, double[] rhs, double[] result)
        {
            int n = rhs.Length;
            double[] y = new double[n];

            for (int i = 0; i < n; i++)
            {
                double sum = rhs[i];
                for (int k = 0; k < i; k++)
                    sum -= lower[i, k] * y[k];

                y[i] = sum / lower[i, i];
            }

            for (int i = n - 1; i >= 0; i--)
            {
                double sum = y[i];
                for (int k = i + 1; k < n; k++)
                    sum -= lower[k, i] * result[k];

                result[i] = sum / lower[i, i];
            }
        }

        private static void TrainShrinkageLda(
            List<double[]> samples,
            List<int> labels,
            int classCount,
            int featureCount,
            double[][] outputWeights,
            double[] outputBias,
            double[][] outputClassMeans)
        {
            if (samples.Count != labels.Count || samples.Count == 0)
                throw new InvalidOperationException("Invalid SSVEP LDA training set.");

            int[] classCounts = new int[classCount];

            for (int i = 0; i < samples.Count; i++)
            {
                int classIndex = labels[i];
                classCounts[classIndex]++;

                for (int j = 0; j < featureCount; j++)
                    outputClassMeans[classIndex][j] += samples[i][j];
            }

            for (int k = 0; k < classCount; k++)
            {
                if (classCounts[k] < 2)
                    throw new InvalidOperationException("Not enough LDA samples in one of the SSVEP classes.");

                for (int j = 0; j < featureCount; j++)
                    outputClassMeans[k][j] /= classCounts[k];
            }

            double[,] covariance = new double[featureCount, featureCount];

            for (int i = 0; i < samples.Count; i++)
            {
                int classIndex = labels[i];
                double[] sample = samples[i];
                double[] mean = outputClassMeans[classIndex];

                for (int r = 0; r < featureCount; r++)
                {
                    double dr = sample[r] - mean[r];

                    for (int c = r; c < featureCount; c++)
                    {
                        double dc = sample[c] - mean[c];
                        covariance[r, c] += dr * dc;
                    }
                }
            }

            double scale = 1.0 / samples.Count;
            for (int r = 0; r < featureCount; r++)
            {
                for (int c = r; c < featureCount; c++)
                {
                    covariance[r, c] *= scale;
                    covariance[c, r] = covariance[r, c];
                }
            }

            ApplyOasShrinkage(covariance, samples.Count);
            double[,] lower = CholeskyWithJitter(covariance);

            for (int k = 0; k < classCount; k++)
            {
                double[] weights = new double[featureCount];
                SolveCholesky(lower, outputClassMeans[k], weights);
                outputWeights[k] = weights;

                double dot = 0.0;
                for (int j = 0; j < featureCount; j++)
                    dot += outputClassMeans[k][j] * weights[j];

                // Equal class priors: the common log-prior term can be omitted.
                outputBias[k] = -0.5 * dot;
            }
        }

        private static void ApplyOasShrinkage(double[,] covariance, int sampleCount)
        {
            int p = covariance.GetLength(0);
            double trace = 0.0;
            double squareMean = 0.0;

            for (int r = 0; r < p; r++)
            {
                trace += covariance[r, r];

                for (int c = 0; c < p; c++)
                    squareMean += covariance[r, c] * covariance[r, c];
            }

            squareMean /= (double)(p * p);
            double mu = trace / p;
            double numerator = squareMean + mu * mu;
            double denominator = (sampleCount + 1.0) *
                                 (squareMean - (mu * mu) / p);

            double shrinkage;
            if (denominator <= 0.0)
                shrinkage = 1.0;
            else
                shrinkage = numerator / denominator;

            if (shrinkage < 0.0)
                shrinkage = 0.0;
            if (shrinkage > 1.0)
                shrinkage = 1.0;

            double keep = 1.0 - shrinkage;

            for (int r = 0; r < p; r++)
            {
                for (int c = 0; c < p; c++)
                    covariance[r, c] *= keep;

                covariance[r, r] += shrinkage * mu;
            }
        }
        private static void ReportProgress(Action<int, string> progress, int percent, string text)
        {
            if (progress == null)
                return;

            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            progress(percent, text);
        }

    }
}
