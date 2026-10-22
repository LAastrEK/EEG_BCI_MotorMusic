using System;

namespace PolyHubNet
{
    public sealed class SsvepAnalysisResult
    {
        public EogRejectionResult EogRejection { get; private set; }
        public bool IsRejected { get { return EogRejection != null && EogRejection.IsRejected; } }
        public bool IsNewRejection { get; private set; }
        public long WindowStartQpc { get; private set; }
        public long QpcTimestamp { get; private set; }
        public int? PredictedClassId { get; private set; }
        public int? CandidateClassId { get; private set; }
        public bool HasPrediction { get { return PredictedClassId.HasValue; } }
        public double AcceptanceDistanceRatio { get; private set; }
        public double BackgroundLogOdds { get; private set; }
        public double[] Features { get; private set; }
        public double[] ClassAcceptanceMargins { get; private set; }

        public SsvepAnalysisResult(
            long qpcTimestamp,
            int? predictedClassId,
            double[] features,
            double[] classScores,
            int? candidateClassId = null,
            double acceptanceDistanceRatio = double.NaN,
            double backgroundLogOdds = double.NaN,
            EogRejectionResult eogRejection = null, bool isNewRejection = false, long windowStartQpc = 0)
        {
            WindowStartQpc = windowStartQpc;
            QpcTimestamp = qpcTimestamp;
            EogRejection = eogRejection;
            IsNewRejection = isNewRejection;
            PredictedClassId = IsRejected ? null : predictedClassId;
            CandidateClassId = candidateClassId;
            AcceptanceDistanceRatio = acceptanceDistanceRatio;
            BackgroundLogOdds = backgroundLogOdds;

            Features = features != null
                ? (double[])features.Clone()
                : new double[0];

            ClassAcceptanceMargins = classScores != null
                ? (double[])classScores.Clone()
                : new double[0];
        }
    }
}
