using System;

namespace PolyHubNet
{
    // Immutable snapshot: the UI may edit settings while a recording is training.
    public sealed class EogRejection
    {
        public const int RejectedEventCode = 2005;
        private readonly bool _veogEnabled, _heogEnabled;
        private readonly double _veogThreshold, _heogThreshold;
        public bool Enabled { get { return _veogEnabled || _heogEnabled; } }

        public EogRejection(EogRejectionProfileSettings profile)
        {
            if (profile == null) return;
            _veogEnabled = profile.VeogEnabled;
            _heogEnabled = profile.HeogEnabled;
            _veogThreshold = profile.VeogPeakToPeakThresholdMicrovolts;
            _heogThreshold = profile.HeogFixationDeviationThresholdMicrovolts;
            if ((_veogEnabled && !ValidThreshold(_veogThreshold)) ||
                (_heogEnabled && !ValidThreshold(_heogThreshold)))
                throw new ArgumentException("EOG thresholds must be finite and positive.", "profile");
        }

        public static int FindChannel(string[] names, string name)
        {
            if (names != null)
                for (int i = 0; i < names.Length; i++)
                    if (string.Equals(names[i] == null ? null : names[i].Trim(), name,
                        StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public void ValidateChannels(string[] names)
        {
            if (_veogEnabled && FindChannel(names, "VEOG") < 0)
                throw new InvalidOperationException("VEOG rejection is enabled, but the source has no VEOG channel.");
            if (_heogEnabled && FindChannel(names, "HEOG") < 0)
                throw new InvalidOperationException("HEOG rejection is enabled, but the source has no HEOG channel.");
        }

        public EogRejectionResult Check(SignalBlock block, string[] names)
        {
            if (!Enabled) return EogRejectionResult.Accepted;
            if (block == null || block.Data == null || names == null ||
                names.Length != block.ChannelCount || block.SampleCount <= 0 ||
                block.Data.Length < (long)block.ChannelCount * block.SampleCount)
                return EogRejectionResult.Invalid("EOG data unavailable");
            return Check(block.Data, block.SampleCount,
                FindChannel(names, "VEOG"), FindChannel(names, "HEOG"));
        }

        // Values in the actiCHamp stream/PHS are volts; controls are microvolts.
        // HEOG is compared directly to zero of the differential output. No
        // unverified fixation baseline is inferred from a file or a moving window.
        internal EogRejectionResult Check(float[] data, int samples, int veog, int heog)
        {
            if (!Enabled) return EogRejectionResult.Accepted;
            if (_veogEnabled && veog < 0) return EogRejectionResult.Invalid("VEOG channel unavailable");
            if (_heogEnabled && heog < 0) return EogRejectionResult.Invalid("HEOG channel unavailable");
            double minV = double.PositiveInfinity, maxV = double.NegativeInfinity, maxH = 0.0;
            for (int i = 0; i < samples; i++)
            {
                if (_veogEnabled)
                {
                    double v = data[veog * samples + i];
                    if (!Finite(v)) return EogRejectionResult.Invalid("Invalid VEOG samples");
                    minV = Math.Min(minV, v);
                    maxV = Math.Max(maxV, v);
                }
                if (_heogEnabled)
                {
                    double h = data[heog * samples + i];
                    if (!Finite(h)) return EogRejectionResult.Invalid("Invalid HEOG samples");
                    maxH = Math.Max(maxH, Math.Abs(h));
                }
            }
            double vUv = _veogEnabled ? (maxV - minV) * 1e6 : double.NaN;
            double hUv = _heogEnabled ? maxH * 1e6 : double.NaN;
            int flags = (_veogEnabled && vUv > _veogThreshold ? 1 : 0) |
                (_heogEnabled && hUv > _heogThreshold ? 2 : 0);
            string reason = flags == 0 ? null :
                ((flags & 1) != 0 ? "VEOG " + vUv.ToString("0.0") + " µV" : "") +
                (flags == 3 ? "; " : "") +
                ((flags & 2) != 0 ? "HEOG " + hUv.ToString("0.0") + " µV" : "");
            return new EogRejectionResult(flags, reason, vUv, hUv);
        }

        private static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
        private static bool ValidThreshold(double x) { return Finite(x) && x > 0.0; }
    }

    public sealed class EogRejectionResult
    {
        public static readonly EogRejectionResult Accepted =
            new EogRejectionResult(0, null, double.NaN, double.NaN);
        public int Flags { get; private set; } // 1: VEOG, 2: HEOG, 4: unavailable/invalid
        public bool IsRejected { get { return Flags != 0; } }
        public string Reason { get; private set; }
        public double VeogPeakToPeakMicrovolts { get; private set; }
        public double HeogMagnitudeMicrovolts { get; private set; }
        internal EogRejectionResult(int flags, string reason, double veog, double heog)
        {
            Flags = flags; Reason = reason;
            VeogPeakToPeakMicrovolts = veog; HeogMagnitudeMicrovolts = heog;
        }
        internal static EogRejectionResult Invalid(string reason)
        { return new EogRejectionResult(4, reason, double.NaN, double.NaN); }
    }
}
