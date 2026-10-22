using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace PolyHubNet
{
    [DataContract]
    public sealed class SsvepModel
    {
        public const int CurrentVersion = 2;
        public const int BackgroundClassId = 1200;

        [DataMember(Order = 1)]
        public int Version { get; set; }

        [DataMember(Order = 2)]
        public int SampleRate { get; set; }

        [DataMember(Order = 3)]
        public int WindowSamples { get; set; }

        [DataMember(Order = 4)]
        public int StepSamples { get; set; }

        [DataMember(Order = 5)]
        public string[] ChannelNames { get; set; }

        [DataMember(Order = 6)]
        public int[] ClassIds { get; set; }

        [DataMember(Order = 7)]
        public double[] TargetFrequencies { get; set; }

        [DataMember(Order = 8)]
        public int HarmonicCount { get; set; }

        // [feature][channel], feature order: class-major, harmonic-minor.
        [DataMember(Order = 9)]
        public double[][] RessWeights { get; set; }

        // Final linear discriminant: score[k] = W[k] * features + bias[k].
        [DataMember(Order = 10)]
        public double[][] LdaWeights { get; set; }

        [DataMember(Order = 11)]
        public double[] LdaBias { get; set; }

        // Kept for visualization of the learned class fingerprints.
        [DataMember(Order = 12)]
        public double[][] ClassFeatureMeans { get; set; }

        // Full 16-feature Mahalanobis metric, shared by all calibration exemplars.
        [DataMember(Order = 13)]
        public double[][] ClassAcceptancePrecision { get; set; }

        [DataMember(Order = 14)]
        public double[][][] ClassAcceptanceCenters { get; set; }

        [DataMember(Order = 15)]
        public double[] ClassAcceptanceRadiiSquared { get; set; }

        [DataMember(Order = 16)]
        public double[][] BackgroundFeatureCenters { get; set; }

        [DataMember(Order = 17)]
        public double[] BackgroundRejectionLogOddsThresholds { get; set; }

        [DataMember(Order = 18)]
        public double ClassAcceptanceRadiusMultiplier { get; set; } = 1.0;

        [DataMember(Order = 19)]
        public double BackgroundRejectionLogOddsMargin { get; set; } = 0.5;

        public bool HasClassAcceptance
        {
            get { return Version == CurrentVersion && ClassAcceptancePrecision != null; }
        }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            ClassAcceptanceRadiusMultiplier = 1.0;
            BackgroundRejectionLogOddsMargin = 0.5;
        }

        public int ClassCount
        {
            get { return ClassIds != null ? ClassIds.Length : 0; }
        }

        public int FeatureCount
        {
            get { return ClassCount * HarmonicCount; }
        }

        public double GetFeatureFrequency(int featureIndex)
        {
            if (featureIndex < 0 || featureIndex >= FeatureCount)
                throw new ArgumentOutOfRangeException("featureIndex");

            int classIndex = featureIndex / HarmonicCount;
            int harmonicIndex = featureIndex % HarmonicCount;
            return TargetFrequencies[classIndex] * (harmonicIndex + 1);
        }

        public void Validate()
        {
            if (Version != 1 && Version != CurrentVersion)
                throw new InvalidDataException("Unsupported SSVEP model version.");

            if (SampleRate <= 0)
                throw new InvalidDataException("Invalid SSVEP model sample rate.");
            if (WindowSamples <= 0)
                throw new InvalidDataException("Invalid SSVEP model window size.");
            if (StepSamples <= 0 || StepSamples > WindowSamples)
                throw new InvalidDataException("Invalid SSVEP model window step.");

            if (ChannelNames == null || ChannelNames.Length == 0)
                throw new InvalidDataException("SSVEP model has no EEG channels.");

            for (int i = 0; i < ChannelNames.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(ChannelNames[i]))
                    throw new InvalidDataException("SSVEP model contains an empty channel name.");
            }

            if (ClassIds == null || ClassIds.Length == 0)
                throw new InvalidDataException("SSVEP model has no classes.");
            if (TargetFrequencies == null || TargetFrequencies.Length != ClassIds.Length)
                throw new InvalidDataException("SSVEP model class/frequency count mismatch.");
            if (HarmonicCount <= 0)
                throw new InvalidDataException("SSVEP model harmonic count is invalid.");

            double nyquist = 0.5 * SampleRate;
            for (int i = 0; i < TargetFrequencies.Length; i++)
            {
                if (!SsvepAcceptance.IsFinite(TargetFrequencies[i]) || TargetFrequencies[i] <= 0.0)
                    throw new InvalidDataException("SSVEP model contains an invalid target frequency.");

                if (TargetFrequencies[i] * HarmonicCount >= nyquist)
                    throw new InvalidDataException("SSVEP model harmonic reaches Nyquist frequency.");
            }

            int featureCount = FeatureCount;
            int channelCount = ChannelNames.Length;
            int classCount = ClassCount;

            if (RessWeights == null || RessWeights.Length != featureCount)
                throw new InvalidDataException("SSVEP model RESS weight count mismatch.");

            for (int i = 0; i < RessWeights.Length; i++)
            {
                if (RessWeights[i] == null || RessWeights[i].Length != channelCount)
                    throw new InvalidDataException("SSVEP model RESS channel count mismatch.");
            }

            if (LdaWeights == null || LdaWeights.Length != classCount)
                throw new InvalidDataException("SSVEP model LDA class count mismatch.");

            for (int i = 0; i < LdaWeights.Length; i++)
            {
                if (LdaWeights[i] == null || LdaWeights[i].Length != featureCount)
                    throw new InvalidDataException("SSVEP model LDA feature count mismatch.");
            }

            if (LdaBias == null || LdaBias.Length != classCount)
                throw new InvalidDataException("SSVEP model LDA bias count mismatch.");

            if (ClassFeatureMeans != null)
            {
                if (ClassFeatureMeans.Length != classCount)
                    throw new InvalidDataException("SSVEP model class mean count mismatch.");

                for (int i = 0; i < ClassFeatureMeans.Length; i++)
                {
                    if (ClassFeatureMeans[i] == null || ClassFeatureMeans[i].Length != featureCount)
                        throw new InvalidDataException("SSVEP model class mean feature count mismatch.");
                }
            }

            if (Version == 1) return; // Legacy scores remain viewable, without note output.
            if (!SsvepAcceptance.IsFinite(ClassAcceptanceRadiusMultiplier) || ClassAcceptanceRadiusMultiplier <= 0.0 ||
                !SsvepAcceptance.IsFinite(BackgroundRejectionLogOddsMargin))
                throw new InvalidDataException("Invalid SSVEP acceptance settings.");
            ValidateVectors(ClassAcceptancePrecision, featureCount, featureCount);
            if (ClassAcceptanceCenters == null || ClassAcceptanceCenters.Length != classCount ||
                ClassAcceptanceRadiiSquared == null || ClassAcceptanceRadiiSquared.Length != classCount ||
                BackgroundRejectionLogOddsThresholds == null || BackgroundRejectionLogOddsThresholds.Length != classCount)
                throw new InvalidDataException("Invalid SSVEP acceptance class statistics.");
            for (int k = 0; k < classCount; k++)
            {
                if (ClassAcceptanceCenters[k] == null || ClassAcceptanceCenters[k].Length == 0)
                    throw new InvalidDataException("SSVEP class has no acceptance centers.");
                ValidateVectors(ClassAcceptanceCenters[k], ClassAcceptanceCenters[k].Length, featureCount);
                if (!SsvepAcceptance.IsFinite(ClassAcceptanceRadiiSquared[k]) || ClassAcceptanceRadiiSquared[k] <= 0.0 ||
                    !SsvepAcceptance.IsFinite(BackgroundRejectionLogOddsThresholds[k]))
                    throw new InvalidDataException("Invalid SSVEP acceptance threshold.");
            }
            if (BackgroundFeatureCenters == null)
                throw new InvalidDataException("Missing SSVEP background statistics.");
            ValidateVectors(BackgroundFeatureCenters, BackgroundFeatureCenters.Length, featureCount);
            // Reject a corrupted or non-positive-definite metric on load, not in realtime.
            double[,] lower = new double[featureCount, featureCount];
            for (int r = 0; r < featureCount; r++)
                for (int c = 0; c <= r; c++)
                {
                    double value = ClassAcceptancePrecision[r][c];
                    if (Math.Abs(value - ClassAcceptancePrecision[c][r]) >
                        1e-8 * Math.Max(1.0, Math.Abs(value)))
                        throw new InvalidDataException("SSVEP acceptance metric is asymmetric.");
                    for (int j = 0; j < c; j++) value -= lower[r, j] * lower[c, j];
                    if (r == c && (!SsvepAcceptance.IsFinite(value) || value <= 0.0))
                        throw new InvalidDataException("SSVEP acceptance metric is not positive definite.");
                    lower[r, c] = r == c ? Math.Sqrt(value) : value / lower[c, c];
                }
        }

        private static void ValidateVectors(double[][] vectors, int rows, int columns)
        {
            if (vectors == null || vectors.Length != rows)
                throw new InvalidDataException("Invalid SSVEP acceptance matrix size.");
            foreach (double[] row in vectors)
            {
                if (row == null || row.Length != columns)
                    throw new InvalidDataException("Invalid SSVEP acceptance vector size.");
                foreach (double value in row)
                    if (!SsvepAcceptance.IsFinite(value))
                        throw new InvalidDataException("Non-finite SSVEP acceptance statistic.");
            }
        }

        public int EvaluateClassIndex(double[] features, double[] classScores)
        {
            int candidate;
            double distanceRatio, backgroundEvidence;
            return EvaluateClassIndex(features, classScores, out candidate, out distanceRatio, out backgroundEvidence);
        }

        public int EvaluateClassIndex(double[] features, double[] classScores, out int candidate,
            out double distanceRatio, out double backgroundEvidence)
        {
            if (features == null)
                throw new ArgumentNullException("features");
            if (classScores == null)
                throw new ArgumentNullException("classScores");
            if (features.Length != FeatureCount)
                throw new ArgumentException("SSVEP feature count mismatch.", "features");
            if (classScores.Length != ClassCount)
                throw new ArgumentException("SSVEP class score count mismatch.", "classScores");

            candidate = -1;
            distanceRatio = backgroundEvidence = double.NaN;
            foreach (double value in features)
            {
                if (SsvepAcceptance.IsFinite(value)) continue;
                for (int k = 0; k < classScores.Length; k++) classScores[k] = double.NegativeInfinity;
                return -1;
            }
            int bestIndex = -1;
            double bestScore = double.NegativeInfinity;

            for (int k = 0; k < ClassCount; k++)
            {
                double score = LdaBias[k];
                double[] weights = LdaWeights[k];

                for (int j = 0; j < features.Length; j++)
                    score += weights[j] * features[j];

                classScores[k] = score;

                if (!SsvepAcceptance.IsFinite(score))
                {
                    for (int i = 0; i < classScores.Length; i++) classScores[i] = double.NegativeInfinity;
                    return -1;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = k;
                }
            }

            candidate = bestIndex;
            return SsvepAcceptance.Accept(this, features, candidate, classScores, out distanceRatio, out backgroundEvidence)
                ? candidate : -1;
        }

        public void Save(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentNullException("filePath");

            Validate();

            DataContractJsonSerializer serializer =
                new DataContractJsonSerializer(typeof(SsvepModel));

            using (FileStream stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                serializer.WriteObject(stream, this);
        }

        public static SsvepModel Load(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentNullException("filePath");

            DataContractJsonSerializer serializer =
                new DataContractJsonSerializer(typeof(SsvepModel));

            SsvepModel model;
            using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                model = serializer.ReadObject(stream) as SsvepModel;

            if (model == null)
                throw new InvalidDataException("Unable to load SSVEP model.");

            model.Validate();
            return model;
        }
    }
}
