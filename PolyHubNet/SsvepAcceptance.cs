using System;
using System.Collections.Generic;

namespace PolyHubNet
{
    // One full-feature metric; several calibration exemplars per note. Background
    // is negative evidence, never an output class or an acceptance corridor.
    internal static class SsvepAcceptance
    {
        internal sealed class Trial
        {
            public int ClassIndex;
            public readonly List<double[]> Features = new List<double[]>();
        }

        internal static void Train(SsvepModel model, List<Trial> targets, List<Trial> background)
        {
            int p = model.FeatureCount;
            double[,] covariance = new double[p, p];
            int count = 0;
            foreach (Trial trial in targets)
            {
                foreach (double[] x in trial.Features)
                {
                    double[] mean = model.ClassFeatureMeans[trial.ClassIndex];
                    for (int r = 0; r < p; r++)
                        for (int c = 0; c <= r; c++)
                            covariance[r, c] += (x[r] - mean[r]) * (x[c] - mean[c]);
                    count++;
                }
            }

            // Shrink toward the diagonal, retaining all variances and half of
            // the cross-feature covariance. Overlapping windows are not counted
            // as independent trials to choose a vanishing regularization.
            for (int r = 0; r < p; r++)
            {
                for (int c = 0; c <= r; c++)
                {
                    double v = covariance[r, c] / count;
                    if (r != c) v *= 0.5;
                    else v = Math.Max(v, 1e-6);
                    covariance[r, c] = covariance[c, r] = v;
                }
            }
            model.ClassAcceptancePrecision = InvertPositiveDefinite(covariance);
            model.ClassAcceptanceCenters = new double[model.ClassCount][][];
            model.ClassAcceptanceRadiiSquared = new double[model.ClassCount];
            model.BackgroundRejectionLogOddsThresholds = new double[model.ClassCount];

            for (int k = 0; k < model.ClassCount; k++)
            {
                List<double[]> centers = new List<double[]>();
                foreach (Trial trial in targets)
                    if (trial.ClassIndex == k) centers.Add(Mean(trial.Features, p));
                model.ClassAcceptanceCenters[k] = centers.ToArray();

                List<double> heldOutDistances = new List<double>();
                double fittedMaximum = 0.0;
                int own = 0;
                foreach (Trial trial in targets)
                {
                    if (trial.ClassIndex != k) continue;
                    foreach (double[] x in trial.Features)
                    {
                        double fitted, excluded;
                        LogDensity(x, model.ClassAcceptanceCenters[k], model.ClassAcceptancePrecision, -1, out fitted);
                        LogDensity(x, model.ClassAcceptanceCenters[k], model.ClassAcceptancePrecision, own, out excluded);
                        fittedMaximum = Math.Max(fittedMaximum, fitted);
                        if (IsFinite(excluded)) heldOutDistances.Add(excluded);
                    }
                    own++;
                }
                // Leave the entire own trial out, not just an overlapping window.
                // A single-trial calibration still remains usable for bench tests.
                heldOutDistances.Sort();
                double radius = heldOutDistances.Count == 0 ? fittedMaximum :
                    heldOutDistances[(int)Math.Ceiling(0.95 * heldOutDistances.Count) - 1];
                model.ClassAcceptanceRadiiSquared[k] = Math.Max(1e-6, Math.Max(radius, fittedMaximum));
            }

            List<double[]> backgroundCenters = new List<double[]>();
            foreach (Trial trial in background) backgroundCenters.Add(Mean(trial.Features, p));
            model.BackgroundFeatureCenters = backgroundCenters.ToArray();

            // Background epochs have no part in RESS/LDA fitting. Leave their own
            // centers out when estimating the per-note negative evidence limit.
            // Log-average densities give equal hypothesis weights regardless of
            // the eight-times-larger number of background trials.
            for (int k = 0; k < model.ClassCount; k++)
            {
                double threshold = 0.0;
                for (int t = 0; t < background.Count; t++)
                {
                    foreach (double[] x in background[t].Features)
                    {
                        double unused;
                        double targetLog = LogDensity(x, model.ClassAcceptanceCenters[k],
                            model.ClassAcceptancePrecision, -1, out unused);
                        int exclude = background.Count > 1 ? t : -1;
                        double backgroundLog = LogDensity(x, model.BackgroundFeatureCenters,
                            model.ClassAcceptancePrecision, exclude, out unused);
                        threshold = Math.Max(threshold, targetLog - backgroundLog);
                    }
                }
                model.BackgroundRejectionLogOddsThresholds[k] = threshold;
            }
        }

        internal static bool Accept(SsvepModel model, double[] features, int candidate, double[] scores,
            out double distanceRatio, out double backgroundEvidence)
        {
            distanceRatio = double.NaN;
            backgroundEvidence = double.NaN;
            if (!model.HasClassAcceptance || candidate < 0)
            {
                for (int k = 0; k < scores.Length; k++) scores[k] = double.NegativeInfinity;
                return false;
            }
            double[] lda = (double[])scores.Clone();
            double[] logDensity = new double[model.ClassCount];
            double[] distances = new double[model.ClassCount];
            for (int k = 0; k < model.ClassCount; k++)
                logDensity[k] = LogDensity(features, model.ClassAcceptanceCenters[k],
                    model.ClassAcceptancePrecision, -1, out distances[k]);
            double width = model.ClassAcceptanceRadiusMultiplier;
            distanceRatio = distances[candidate] / model.ClassAcceptanceRadiiSquared[candidate] / width / width;
            bool hasBackground = model.BackgroundFeatureCenters.Length > 0;
            double unused;
            double backgroundLog = hasBackground ? LogDensity(features, model.BackgroundFeatureCenters,
                model.ClassAcceptancePrecision, -1, out unused) : double.NegativeInfinity;
            if (hasBackground) backgroundEvidence = logDensity[candidate] - backgroundLog;

            // These are signed margins of the actual inequalities, in log units.
            // A positive bar is exactly an accepted note; there is no Softmax
            // or normalization that forces the eight bars to sum to 100%.
            for (int k = 0; k < model.ClassCount; k++)
            {
                double otherLog = double.NegativeInfinity, otherLda = double.NegativeInfinity;
                for (int j = 0; j < model.ClassCount; j++)
                    if (j != k)
                    {
                        otherLog = Math.Max(otherLog, logDensity[j]);
                        otherLda = Math.Max(otherLda, lda[j]);
                    }
                double corridorMargin = Math.Log(model.ClassAcceptanceRadiiSquared[k]) +
                    2.0 * Math.Log(width) - Math.Log(Math.Max(1e-24, distances[k]));
                double margin = Math.Min(corridorMargin,
                    Math.Min(logDensity[k] - otherLog, lda[k] - otherLda));
                if (hasBackground)
                    margin = Math.Min(margin, logDensity[k] - backgroundLog -
                        model.BackgroundRejectionLogOddsThresholds[k] - model.BackgroundRejectionLogOddsMargin);
                else
                    margin = Math.Min(margin, 0.0); // Missing reference cannot establish target/background odds.
                scores[k] = IsFinite(margin) && IsFinite(logDensity[k]) ? margin : double.NegativeInfinity;
            }
            return scores[candidate] > 0.0;
        }

        private static double[] Mean(List<double[]> samples, int p)
        {
            double[] mean = new double[p];
            foreach (double[] x in samples)
                for (int j = 0; j < p; j++) mean[j] += x[j] / samples.Count;
            return mean;
        }

        private static double LogDensity(double[] x, double[][] centers, double[][] precision,
            int exclude, out double minimumDistance)
        {
            minimumDistance = double.PositiveInfinity;
            double maximum = double.NegativeInfinity, sum = 0.0;
            int count = 0;
            for (int i = 0; i < centers.Length; i++)
            {
                if (i == exclude) continue;
                double d = DistanceSquared(x, centers[i], precision);
                minimumDistance = Math.Min(minimumDistance, d);
                double value = -0.5 * d;
                if (value > maximum)
                {
                    sum = sum * Math.Exp(maximum - value) + 1.0;
                    maximum = value;
                }
                else sum += Math.Exp(value - maximum);
                count++;
            }
            return count > 0 ? maximum + Math.Log(sum / count) : double.NegativeInfinity;
        }

        internal static double DistanceSquared(double[] x, double[] mean, double[][] precision)
        {
            double distance = 0.0;
            for (int r = 0; r < x.Length; r++)
            {
                double row = 0.0;
                for (int c = 0; c < x.Length; c++) row += precision[r][c] * (x[c] - mean[c]);
                distance += (x[r] - mean[r]) * row;
            }
            return Math.Max(0.0, distance);
        }

        private static double[][] InvertPositiveDefinite(double[,] covariance)
        {
            int p = covariance.GetLength(0);
            double[,] lower = new double[p, p];
            for (int r = 0; r < p; r++)
                for (int c = 0; c <= r; c++)
                {
                    double value = covariance[r, c];
                    for (int j = 0; j < c; j++) value -= lower[r, j] * lower[c, j];
                    lower[r, c] = r == c ? Math.Sqrt(value) : value / lower[c, c];
                }
            double[][] inverse = new double[p][];
            for (int r = 0; r < p; r++) inverse[r] = new double[p];
            for (int c = 0; c < p; c++)
            {
                double[] y = new double[p], x = new double[p];
                for (int r = 0; r < p; r++)
                {
                    double value = r == c ? 1.0 : 0.0;
                    for (int j = 0; j < r; j++) value -= lower[r, j] * y[j];
                    y[r] = value / lower[r, r];
                }
                for (int r = p - 1; r >= 0; r--)
                {
                    double value = y[r];
                    for (int j = r + 1; j < p; j++) value -= lower[j, r] * x[j];
                    x[r] = value / lower[r, r];
                    inverse[r][c] = x[r];
                }
            }
            // Roundoff must not make the serialized symmetric metric asymmetric.
            for (int r = 0; r < p; r++)
                for (int c = 0; c < r; c++)
                    inverse[r][c] = inverse[c][r] = 0.5 * (inverse[r][c] + inverse[c][r]);
            return inverse;
        }

        internal static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
