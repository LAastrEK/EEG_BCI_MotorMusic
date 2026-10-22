using PolyHubIpc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace PolyHubNet
{
    public sealed class SessionReader : IDisposable
    {
        private const int ChannelNameCharCapacity = 32;
        private const int ChannelNameBytes = ChannelNameCharCapacity * sizeof(char);

        private readonly int _fileHeaderSize;
        private readonly int _recordHeaderSize;

        private FileStream _fileStream;
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _accessor;
        private IntPtr _viewPtr = IntPtr.Zero;
        private long _viewPointerOffset;
        private bool _viewPointerAcquired;

        private SessionFileHeader _header;
        private string[] _channelNames = new string[0];
        private readonly List<SessionBlockRecord> _blocks = new List<SessionBlockRecord>();
        private readonly List<SessionUserEvent> _events = new List<SessionUserEvent>();

        public SessionFileHeader Header
        {
            get { return _header; }
        }

        public string[] ChannelNames
        {
            get { return _channelNames; }
        }

        public int BlockCount
        {
            get { return _blocks.Count; }
        }

        public int EventCount
        {
            get { return _events.Count; }
        }

        public SessionReader()
        {
            _fileHeaderSize = Marshal.SizeOf(typeof(SessionFileHeader));
            _recordHeaderSize = Marshal.SizeOf(typeof(SessionRecordHeader));
        }

        public void Open(string filePath)
        {
            Dispose();

            _fileStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);

            long fileLength = _fileStream.Length;

            _mmf = MemoryMappedFile.CreateFromFile(
                _fileStream,
                null,
                0,
                MemoryMappedFileAccess.Read,
                null,
                HandleInheritability.None,
                false);

            _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            unsafe
            {
                byte* ptr = null;
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                if (ptr == null)
                    throw new InvalidOperationException("Не удалось получить указатель на SessionReader MMF view.");

                _viewPtr = (IntPtr)ptr;
                _viewPointerOffset = _accessor.PointerOffset;
                _viewPointerAcquired = true;
            }

            unsafe
            {
                byte* basePtr = GetBasePtr();
                _header = *(SessionFileHeader*)basePtr;
            }

            if (_header.Magic != 0x50485358 ||
                _header.Version != 1)
            {
                throw new InvalidOperationException(
                    "Unsupported session file format.");
            }

            if (_header.ChannelCount <= 0 ||
                _header.SampleRate <= 0)
            {
                throw new InvalidOperationException(
                    "Invalid session signal format.");
            }

            long expectedDataStartOffset =
                _fileHeaderSize +
                (long)_header.ChannelCount * ChannelNameBytes;

            if (_header.DataStartOffset != expectedDataStartOffset)
            {
                throw new InvalidOperationException(
                    "Invalid session data offset.");
            }

            if (_header.DataStartOffset > fileLength)
            {
                throw new InvalidOperationException(
                    "Invalid session file length.");
            }

            if (_header.CommittedBytes > fileLength)
                _header.CommittedBytes = fileLength;

            if (_header.CommittedBytes < _header.DataStartOffset)
            {
                throw new InvalidOperationException(
                    "Invalid committed session length.");
            }

            _channelNames =
                ReadChannelNames(_header.ChannelCount);

            BuildIndexes();

        }

        public SessionBlockRecord GetBlockIndexRecord(int index)
        {
            return _blocks[index];
        }

        public SessionUserEvent GetEventRecord(int index)
        {
            return _events[index];
        }

        private unsafe string[] ReadChannelNames(int channelCount)
        {
            string[] names =
                new string[channelCount];

            byte* basePtr = GetBasePtr();

            for (int ch = 0; ch < channelCount; ch++)
            {
                char* src = (char*)(
                    basePtr +
                    _fileHeaderSize +
                    (long)ch * ChannelNameBytes);

                int length = 0;

                while (length < ChannelNameCharCapacity &&
                       src[length] != '\0')
                {
                    length++;
                }

                if (length > 0)
                {
                    names[ch] =
                        new string(src, 0, length);
                }
                else
                {
                    names[ch] =
                        "Ch " + (ch + 1).ToString();
                }
            }

            return names;
        }

        public unsafe void ReadBlockInto(int index, SignalBlock target)
        {
            SessionBlockRecord rec = _blocks[index];

            int floatCount = _header.ChannelCount * rec.SampleCount;

            if (target.Data == null || target.Data.Length < floatCount)
                target.Data = new float[floatCount];

            byte* basePtr = GetBasePtr();
            byte* srcPtr = basePtr + rec.PayloadOffset;
            long bytesToCopy = (long)rec.PayloadBytes;

            fixed (float* dstPtr = target.Data)
            {
                Buffer.MemoryCopy(srcPtr, dstPtr, bytesToCopy, bytesToCopy);
            }

            target.Sequence = rec.Sequence;
            target.QpcTimestamp = rec.QpcTimestamp;
            target.ChannelCount = _header.ChannelCount;
            target.SampleRate = _header.SampleRate;
            target.SampleCount = rec.SampleCount;
        }

        public unsafe SignalBlock ReadBlock(int index)
        {
            SessionBlockRecord rec = _blocks[index];

            int floatCount = _header.ChannelCount * rec.SampleCount;
            float[] data = new float[floatCount];

            byte* basePtr = GetBasePtr();
            byte* srcPtr = basePtr + rec.PayloadOffset;
            long bytesToCopy = (long)rec.PayloadBytes;

            fixed (float* dstPtr = data)
            {
                Buffer.MemoryCopy(srcPtr, dstPtr, bytesToCopy, bytesToCopy);
            }

            return new SignalBlock
            {
                Sequence = rec.Sequence,
                QpcTimestamp = rec.QpcTimestamp,
                ChannelCount = _header.ChannelCount,
                SampleRate = _header.SampleRate,
                SampleCount = rec.SampleCount,
                Data = data
            };
        }

        public long FindSampleIndexByQpc(long qpcTimestamp)
        {
            if (_blocks.Count == 0)
                return 0;

            int lo = 0;
            int hi = _blocks.Count - 1;
            int best = 0;

            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (_blocks[mid].QpcTimestamp <= qpcTimestamp)
                {
                    best = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            SessionBlockRecord block = _blocks[best];

            long deltaQpc = qpcTimestamp - block.QpcTimestamp;
            if (deltaQpc < 0)
                deltaQpc = 0;

            long blockDurationQpc =
                (long)((double)block.SampleCount * _header.QpcFrequency / _header.SampleRate);

            if (deltaQpc > blockDurationQpc)
                deltaQpc = blockDurationQpc;

            long sampleOffset =
                (long)((double)deltaQpc * _header.SampleRate / _header.QpcFrequency);

            if (sampleOffset < 0)
                sampleOffset = 0;
            if (sampleOffset > block.SampleCount)
                sampleOffset = block.SampleCount;

            return block.FirstSampleIndex + sampleOffset;
        }

        private unsafe void CopyBlockRegionToResult(
            SessionBlockRecord rec,
            int srcSampleOffset,
            int copySamples,
            float[] result,
            int dstSampleOffset,
            int resultSamplesPerChannel)
        {
            byte* basePtr = GetBasePtr();
            float* srcBasePtr = (float*)(basePtr + rec.PayloadOffset);

            fixed (float* dstBasePtr = result)
            {
                for (int ch = 0; ch < _header.ChannelCount; ch++)
                {
                    float* srcPtr = srcBasePtr + ch * rec.SampleCount + srcSampleOffset;
                    float* dstPtr = dstBasePtr + ch * resultSamplesPerChannel + dstSampleOffset;

                    long bytes = (long)copySamples * sizeof(float);
                    Buffer.MemoryCopy(srcPtr, dstPtr, bytes, bytes);
                }
            }
        }

        public float[] ReadSampleRange(long startSampleIndex, int sampleCount)
        {
            if (sampleCount <= 0)
                return new float[0];

            float[] result = new float[_header.ChannelCount * sampleCount];

            long rangeStart = startSampleIndex;
            long rangeEnd = startSampleIndex + sampleCount;

            for (int i = 0; i < _blocks.Count; i++)
            {
                SessionBlockRecord block = _blocks[i];

                long blockStart = block.FirstSampleIndex;
                long blockEnd = block.FirstSampleIndex + block.SampleCount;

                if (blockEnd <= rangeStart)
                    continue;

                if (blockStart >= rangeEnd)
                    break;

                long overlapStart = rangeStart > blockStart ? rangeStart : blockStart;
                long overlapEnd = rangeEnd < blockEnd ? rangeEnd : blockEnd;
                int overlapSamples = (int)(overlapEnd - overlapStart);

                int srcSampleOffset = (int)(overlapStart - blockStart);
                int dstSampleOffset = (int)(overlapStart - rangeStart);

                CopyBlockRegionToResult(
                    block,
                    srcSampleOffset,
                    overlapSamples,
                    result,
                    dstSampleOffset,
                    sampleCount);
            }

            return result;
        }

        public SignalBlock ReadEpochAsSingleBlock(EpochInfo epoch)
        {
            int sampleCount = epoch.SampleCount;
            if (sampleCount <= 0)
                return null;

            float[] data = ReadSampleRange(epoch.StartSampleIndex, sampleCount);

            return new SignalBlock
            {
                Sequence = epoch.EpochId,
                QpcTimestamp = epoch.StartQpc,
                ChannelCount = _header.ChannelCount,
                SampleRate = _header.SampleRate,
                SampleCount = sampleCount,
                Data = data
            };
        }

        public List<EpochClassSummary> GetClassSummaries()
        {
            Dictionary<int, int> counts = new Dictionary<int, int>();
            List<EpochInfo> all = GetAllEpochs();

            for (int i = 0; i < all.Count; i++)
            {
                int classId = all[i].ClassId;
                int count;
                if (!counts.TryGetValue(classId, out count))
                    count = 0;

                counts[classId] = count + 1;
            }

            List<EpochClassSummary> result = new List<EpochClassSummary>();
            foreach (KeyValuePair<int, int> kv in counts)
            {
                result.Add(new EpochClassSummary
                {
                    ClassId = kv.Key,
                    Count = kv.Value
                });
            }

            result.Sort((a, b) => a.ClassId.CompareTo(b.ClassId));
            return result;
        }

        public List<EpochInfo> GetEpochsByClass(int classId)
        {
            List<EpochInfo> all = GetAllEpochs();
            List<EpochInfo> result = new List<EpochInfo>();

            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].ClassId == classId)
                    result.Add(all[i]);
            }

            return result;
        }

        public List<EpochInfo> GetAllEpochs()
        {
            Dictionary<int, SessionUserEvent> openByClass = new Dictionary<int, SessionUserEvent>();
            List<EpochInfo> result = new List<EpochInfo>();
            long nextEpochId = 1;

            for (int i = 0; i < _events.Count; i++)
            {
                SessionUserEvent evt = _events[i];

                if ((ServiceEventCode)evt.Code != ServiceEventCode.UserEvent)
                    continue;

                int classId = evt.Value0;

                if (evt.State == 1)
                {
                    openByClass[classId] = evt;
                }
                else if (evt.State == 2)
                {
                    SessionUserEvent startEvt;
                    if (!openByClass.TryGetValue(classId, out startEvt))
                        continue;

                    openByClass.Remove(classId);

                    long startSample = FindSampleIndexByQpc(startEvt.QpcTimestamp);
                    long endSample = FindSampleIndexByQpc(evt.QpcTimestamp);

                    if (endSample < startSample)
                        endSample = startSample;

                    result.Add(new EpochInfo
                    {
                        EpochId = nextEpochId++,
                        ClassId = classId,
                        Value1 = startEvt.Value1,
                        StartQpc = startEvt.QpcTimestamp,
                        EndQpc = evt.QpcTimestamp,
                        StartSampleIndex = startSample,
                        EndSampleIndex = endSample
                    });
                }
            }

            return result;
        }

        public int FindFirstBlockIndexAtOrAfter(long qpcTimestamp)
        {
            for (int i = 0; i < _blocks.Count; i++)
            {
                if (_blocks[i].QpcTimestamp >= qpcTimestamp)
                    return i;
            }

            return _blocks.Count;
        }

        public int FindFirstEventIndexAtOrAfter(long qpcTimestamp)
        {
            for (int i = 0; i < _events.Count; i++)
            {
                if (_events[i].QpcTimestamp >= qpcTimestamp)
                    return i;
            }

            return _events.Count;
        }

        public long GetFirstSourceQpc()
        {
            if (_blocks.Count == 0)
                return 0;

            return _blocks[0].QpcTimestamp;
        }

        public long GetLastSourceQpc()
        {
            long lastQpc = 0;

            if (_blocks.Count > 0)
            {
                SessionBlockRecord lastBlock = _blocks[_blocks.Count - 1];
                long blockDurationQpc =
                    (long)((double)lastBlock.SampleCount * _header.QpcFrequency / _header.SampleRate);
                lastQpc = lastBlock.QpcTimestamp + blockDurationQpc;
            }

            if (_events.Count > 0 && _events[_events.Count - 1].QpcTimestamp > lastQpc)
                lastQpc = _events[_events.Count - 1].QpcTimestamp;

            return lastQpc;
        }

        private unsafe void BuildIndexes()
        {
            _blocks.Clear();
            _events.Clear();

            long offset = _header.DataStartOffset;
            long endOffset = _header.CommittedBytes;

            byte* basePtr = GetBasePtr();

            while (offset + _recordHeaderSize <= endOffset)
            {
                SessionRecordHeader header = *(SessionRecordHeader*)(basePtr + offset);
                long payloadOffset = offset + _recordHeaderSize;
                long nextOffset = payloadOffset + header.PayloadBytes;

                if (nextOffset > endOffset)
                    break;

                if (header.RecordType == (int)SessionRecordType.SignalBlock)
                {
                    _blocks.Add(new SessionBlockRecord
                    {
                        Sequence = header.Sequence,
                        QpcTimestamp = header.QpcTimestamp,
                        FirstSampleIndex = header.FirstSampleIndex,
                        SampleCount = header.SampleCount,
                        PayloadOffset = payloadOffset,
                        PayloadBytes = header.PayloadBytes
                    });
                }
                else if (header.RecordType == (int)SessionRecordType.UserEvent)
                {
                    _events.Add(new SessionUserEvent
                    {
                        Sequence = header.Sequence,
                        QpcTimestamp = header.QpcTimestamp,
                        Code = header.Code,
                        State = header.State,
                        Value0 = header.Value0,
                        Value1 = header.Value1
                    });
                }
                else
                {
                    break;
                }

                offset = nextOffset;
            }
            // Analysis diagnostics can be written after a newer service marker.
            // Stable ordering preserves equal-time events and keeps playback/seek
            // chronological without changing the PHS format or signal samples.
            SessionUserEvent[] orderedEvents = _events.OrderBy(e => e.QpcTimestamp).ToArray();
            _events.Clear();
            _events.AddRange(orderedEvents);
        }

        private unsafe byte* GetBasePtr()
        {
            return (byte*)_viewPtr + _viewPointerOffset;
        }

        public void Dispose()
        {
            if (_accessor != null && _viewPointerAcquired)
            {
                try
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
                catch
                {
                }

                _viewPointerAcquired = false;
                _viewPtr = IntPtr.Zero;
                _viewPointerOffset = 0;
            }

            if (_accessor != null)
            {
                _accessor.Dispose();
                _accessor = null;
            }

            if (_mmf != null)
            {
                _mmf.Dispose();
                _mmf = null;
            }

            if (_fileStream != null)
            {
                _fileStream.Dispose();
                _fileStream = null;
            }

            _blocks.Clear();
            _events.Clear();

            _channelNames = new string[0];
        }
    }
}
