using PolyHubIpc;
using System;
using System.Collections.Generic;

namespace PolyHubNet
{
    public sealed class CovertCalibrationCheck
    {
        public int ClassId { get; internal set; }
        public long StartQpc { get; internal set; }
        public long EndQpc { get; internal set; }
        public EogRejectionResult Rejection { get; internal set; }
    }

    // Collects EOG only; validation does not depend on a loaded SSVEP model.
    public sealed class CovertCalibrationEog : IDisposable
    {
        public const int StartCommand = 2006;
        public const int TrialResultCommand = 2007;
        public const int NeutralClassBase = 1300;

        private static readonly string[] EogNames = { "VEOG", "HEOG" };
        private readonly object _sync = new object();
        private readonly SignalStream _source;
        private readonly long _frequency;
        private readonly List<SignalBlock> _blocks = new List<SignalBlock>();
        private EogRejection _rejection, _trialRejection;
        private long _start, _end;
        private int _classId;
        private bool _disposed;

        public CovertCalibrationEog(SignalStream source, long frequency, EogRejection rejection)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
            _frequency = frequency;
            _rejection = rejection ?? throw new ArgumentNullException(nameof(rejection));
            rejection.ValidateChannels(source.ChannelNames);
            _source.BlockPublished += OnBlock;
        }

        public void SetRejection(EogRejection rejection)
        {
            if (rejection == null) throw new ArgumentNullException(nameof(rejection));
            rejection.ValidateChannels(_source.ChannelNames);
            lock (_sync) _rejection = rejection;
        }

        public static bool IsNeutralClass(int code)
        { return code == NeutralClassBase; }
        public static bool IsPairClass(int code)
        {
            return code > NeutralClassBase && code <= NeutralClassBase + 8;
        }
        public static bool IsCovertClass(int code)
        { return IsNeutralClass(code) || IsPairClass(code); }

        public void OnEvent(ServiceEventPacket evt)
        {
            lock (_sync)
            {
                if (_disposed) return;
                ServiceEventCode code = (ServiceEventCode)evt.Code;
                if (code == ServiceEventCode.State && evt.State == 1 &&
                    evt.Value1 == 0 && IsPairClass(evt.Value0))
                {
                    Begin(evt.Value0, evt.QpcTimestamp);
                }
                else if (code == ServiceEventCode.UserEvent && evt.State == 1 && IsNeutralClass(evt.Value0))
                {
                    Begin(evt.Value0, evt.QpcTimestamp);
                }
                else if (code == ServiceEventCode.UserEvent && evt.State == 1 && IsPairClass(evt.Value0))
                {
                    if (_classId != evt.Value0 || _start <= 0)
                        Begin(evt.Value0, evt.QpcTimestamp);
                }
                else if (code == ServiceEventCode.UserEvent && evt.State == 2 && IsCovertClass(evt.Value0))
                {
                    if (_classId != evt.Value0 || _start <= 0)
                        return;
                    _end = evt.QpcTimestamp;
                }
            }
        }

        private void Begin(int classId, long start)
        {
            _classId = classId; _start = start; _end = 0;
            _trialRejection = _rejection;
        }

        private void OnBlock(SignalBlock input)
        {
            if (input == null || input.Data == null || input.SampleRate <= 0 || input.SampleCount <= 0) return;
            string[] names = _source.ChannelNames;
            int v = EogRejection.FindChannel(names, "VEOG"), h = EogRejection.FindChannel(names, "HEOG");
            var data = new float[input.SampleCount * 2];
            bool valid = names != null && names.Length == input.ChannelCount &&
                input.Data.Length >= (long)input.ChannelCount * input.SampleCount;
            for (int i = 0; i < input.SampleCount; i++)
            {
                data[i] = valid && v >= 0 ? input.Data[v * input.SampleCount + i] : float.NaN;
                data[input.SampleCount + i] = valid && h >= 0 ? input.Data[h * input.SampleCount + i] : float.NaN;
            }
            lock (_sync)
            {
                if (_disposed) return;
                _blocks.Add(new SignalBlock { QpcTimestamp = input.QpcTimestamp,
                    ChannelCount = 2, SampleRate = input.SampleRate, SampleCount = input.SampleCount, Data = data });
                long keepFrom = _start > 0 ? _start : input.QpcTimestamp - 5 * _frequency;
                while (_blocks.Count > 0 && BlockEnd(_blocks[0], _frequency) < keepFrom)
                    _blocks.RemoveAt(0);
            }
        }

        public CovertCalibrationCheck TryTakeResult()
        {
            lock (_sync)
            {
                if (_disposed || _end <= 0) return null;
                bool dataReady = _blocks.Count > 0 && BlockEnd(_blocks[_blocks.Count - 1], _frequency) >= _end;
                if (_trialRejection.Enabled && !dataReady) return null;
                EogRejectionResult check = CheckInterval(_blocks, EogNames, _frequency,
                    _start, _end, _trialRejection);
                var result = new CovertCalibrationCheck { ClassId = _classId, StartQpc = _start,
                    EndQpc = _end, Rejection = check };
                _start = _end = 0;
                return result;
            }
        }

        private static long BlockEnd(SignalBlock block, long frequency)
        { return block.QpcTimestamp + (long)Math.Round(block.SampleCount * (double)frequency / block.SampleRate); }

        public static EogRejectionResult CheckRecordedEpoch(SessionReader reader, EpochInfo epoch,
            EogRejection rejection)
        {
            if (rejection != null && !rejection.Enabled) return EogRejectionResult.Accepted;
            long start = epoch.StartQpc;
            if (IsPairClass(epoch.ClassId))
            {
                int first = Math.Min(reader.EventCount - 1, reader.FindFirstEventIndexAtOrAfter(epoch.StartQpc));
                for (int i = first; i >= 0; i--)
                {
                    SessionUserEvent evt = reader.GetEventRecord(i);
                    if (evt.QpcTimestamp > epoch.StartQpc) continue;
                    if ((ServiceEventCode)evt.Code == ServiceEventCode.State &&
                        evt.State == 1 && evt.Value0 == epoch.ClassId && evt.Value1 == 0)
                    {
                        start = evt.QpcTimestamp;
                        break;
                    }
                    if ((ServiceEventCode)evt.Code == ServiceEventCode.UserEvent &&
                        evt.State == 2 && evt.Value0 == epoch.ClassId) break;
                }
            }
            var blocks = new List<SignalBlock>();
            int blockIndex = Math.Max(0, reader.FindFirstBlockIndexAtOrAfter(start) - 1);
            for (; blockIndex < reader.BlockCount; blockIndex++)
            {
                if (reader.GetBlockIndexRecord(blockIndex).QpcTimestamp >= epoch.EndQpc) break;
                blocks.Add(reader.ReadBlock(blockIndex));
            }
            return CheckInterval(blocks, reader.ChannelNames, reader.Header.QpcFrequency,
                start, epoch.EndQpc, rejection);
        }

        // Half-open interval [CueOff, END), or [START, END) for neutral epochs.
        // Preserve sustained HEOG deviations; do not infer a new baseline at CueOff.
        public static EogRejectionResult CheckInterval(IEnumerable<SignalBlock> blocks, string[] names,
            long frequency, long start, long end, EogRejection rejection)
        {
            if (rejection != null && !rejection.Enabled) return EogRejectionResult.Accepted;
            if (rejection == null || end <= start || frequency <= 0)
                return EogRejectionResult.Invalid("Invalid covert interval");
            try { rejection.ValidateChannels(names); }
            catch (Exception ex) { return EogRejectionResult.Invalid(ex.Message); }
            int v = EogRejection.FindChannel(names, "VEOG"), h = EogRejection.FindChannel(names, "HEOG");
            var vertical = new List<float>(); var horizontal = new List<float>();
            foreach (SignalBlock block in blocks)
            {
                if (block == null || block.SampleRate <= 0 || block.SampleCount <= 0 ||
                    block.QpcTimestamp >= end || BlockEnd(block, frequency) <= start) continue;
                if (block.Data == null || names == null || block.ChannelCount != names.Length ||
                    block.Data.Length < (long)block.ChannelCount * block.SampleCount)
                    return EogRejectionResult.Invalid("Invalid covert EOG block");
                double samplePeriod = (double)frequency / block.SampleRate;
                for (int i = 0; i < block.SampleCount; i++)
                {
                    long qpc = block.QpcTimestamp + (long)Math.Round(i * samplePeriod);
                    if (qpc < start || qpc >= end) continue;
                    vertical.Add(v >= 0 ? block.Data[v * block.SampleCount + i] : float.NaN);
                    horizontal.Add(h >= 0 ? block.Data[h * block.SampleCount + i] : float.NaN);
                }
            }
            if (vertical.Count == 0) return EogRejectionResult.Accepted;
            var data = new float[vertical.Count * 2];
            vertical.CopyTo(data, 0); horizontal.CopyTo(data, vertical.Count);
            return rejection.Check(data, vertical.Count, v >= 0 ? 0 : -1, h >= 0 ? 1 : -1);
        }

        public void Dispose()
        {
            _source.BlockPublished -= OnBlock;
            lock (_sync) { _disposed = true; _blocks.Clear(); _start = _end = 0; }
        }
    }
}
