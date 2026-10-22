using PolyHubIpc;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PolyHubNet
{
    public enum SessionRecordType : int
    {
        None = 0,
        SignalBlock = 1,
        UserEvent = 2
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SessionFileHeader
    {
        public int Magic;
        public int Version;

        public int ChannelCount;
        public int SampleRate;

        public long QpcFrequency;
        public long DataStartOffset;
        public long CommittedBytes;
        public long TotalSamplesPerChannel;
        public long GrowStepBytes;
        public long Reserved0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SessionRecordHeader
    {
        public int RecordType;
        public int SampleCount;
        public int PayloadBytes;
        public int Reserved0;

        public long Sequence;
        public long QpcTimestamp;
        public long FirstSampleIndex;

        public int Code;
        public int State;
        public int Value0;
        public int Value1;
    }

    public struct SessionBlockRecord
    {
        public long Sequence;
        public long QpcTimestamp;
        public long FirstSampleIndex;
        public int SampleCount;
        public long PayloadOffset;
        public int PayloadBytes;
    }

    public struct SessionUserEvent
    {
        public long Sequence;
        public long QpcTimestamp;
        public int Code;
        public int State;
        public int Value0;
        public int Value1;
    }

    public sealed class SessionRecorder : IDisposable
    {
        private const int MagicValue = 0x50485358; // PHSX
        private const int VersionValue = 1;

        private const int ChannelNameCharCapacity = 32;
        private const int ChannelNameBytes = ChannelNameCharCapacity * sizeof(char);

        private readonly object _ioSync = new object();
        private readonly object _eventSync = new object();

        private readonly int _signalRingCapacity;
        private readonly long _defaultInitialFileBytes;
        private readonly long _defaultGrowStepBytes;

        private readonly int _fileHeaderSize;
        private readonly int _recordHeaderSize;

        private readonly ProcessorBlockRing _signalRing;
        private readonly Queue<SessionUserEvent> _eventQueue;

        private readonly AutoResetEvent _eventAvailableEvent;
        private readonly ManualResetEvent _stopEvent;
        private readonly Thread _writerThread;

        private volatile bool _running;
        private volatile bool _isRecording;

        private SignalStream _signalInput;

        private FileStream _fileStream;
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _accessor;
        private IntPtr _viewPtr = IntPtr.Zero;
        private long _viewPointerOffset;
        private bool _viewPointerAcquired;

        private string _filePath;
        private long _fileCapacityBytes;
        private long _writePosition;
        private SessionFileHeader _header;

        private long _preGrowThresholdBytes;
        private bool _headerDirty;

        private volatile bool _writerFaulted;
        private string _lastErrorText;

        public bool WriterFaulted
        {
            get { return _writerFaulted; }
        }

        public string LastErrorText
        {
            get { return _lastErrorText; }
        }

        public bool IsRecording
        {
            get { return _isRecording; }
        }

        public SessionRecorder()
            : this(32, 5L * 1024L * 1024L, 5L * 1024L * 1024L)
        {
        }

        public SessionRecorder(int signalRingCapacity, long initialFileBytes, long growStepBytes)
        {
            if (signalRingCapacity <= 1)
                throw new ArgumentOutOfRangeException("signalRingCapacity");
            if (initialFileBytes <= 0)
                throw new ArgumentOutOfRangeException("initialFileBytes");
            if (growStepBytes <= 0)
                throw new ArgumentOutOfRangeException("growStepBytes");

            _signalRingCapacity = signalRingCapacity;
            _defaultInitialFileBytes = initialFileBytes;
            _defaultGrowStepBytes = growStepBytes;

            _fileHeaderSize = Marshal.SizeOf(typeof(SessionFileHeader));
            _recordHeaderSize = Marshal.SizeOf(typeof(SessionRecordHeader));

            _signalRing = new ProcessorBlockRing(signalRingCapacity);
            _eventQueue = new Queue<SessionUserEvent>();

            _eventAvailableEvent = new AutoResetEvent(false);
            _stopEvent = new ManualResetEvent(false);

            _running = true;
            _writerThread = new Thread(WriterLoop);
            _writerThread.IsBackground = true;
            _writerThread.Name = "SessionRecorder.Writer";
            _writerThread.Priority = ThreadPriority.AboveNormal;
            _writerThread.Start();
        }

        public void AttachSignalInput(SignalStream input)
        {
            DetachSignalInput();

            _signalInput = input;
            if (_signalInput != null)
                _signalInput.BlockPublished += SignalInput_BlockPublished;
        }

        public void DetachSignalInput()
        {
            if (_signalInput != null)
                _signalInput.BlockPublished -= SignalInput_BlockPublished;

            _signalInput = null;
        }

        private void ClearPendingBuffers()
        {
            lock (_eventSync)
            {
                _eventQueue.Clear();
            }

            while (true)
            {
                SignalBlock block;
                if (!_signalRing.TryDequeue(out block))
                    break;
            }
        }

        public void StartSession(string filePath, string[] channelNames, int sampleRate, long qpcFrequency)
        {
            StartSession(filePath, channelNames, sampleRate, qpcFrequency, _defaultInitialFileBytes, _defaultGrowStepBytes);
        }

        public void StartSession(
            string filePath,
            string[] channelNames,
            int sampleRate,
            long qpcFrequency,
            long initialFileBytes,
            long growStepBytes)
        {
            StopSession();

            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentNullException("filePath");

            if (channelNames == null)
                throw new ArgumentNullException("channelNames");

            if (channelNames.Length <= 0)
                throw new ArgumentException(
                    "At least one channel is required.",
                    "channelNames");

            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException("sampleRate");

            if (qpcFrequency <= 0)
                throw new ArgumentOutOfRangeException("qpcFrequency");

            if (initialFileBytes <= 0)
                throw new ArgumentOutOfRangeException("initialFileBytes");

            if (growStepBytes <= 0)
                throw new ArgumentOutOfRangeException("growStepBytes");

            for (int i = 0; i < channelNames.Length; i++)
            {
                string name = channelNames[i] ?? string.Empty;

                if (name.Length > ChannelNameCharCapacity - 1)
                {
                    throw new ArgumentException(
                        "Channel name must not exceed 31 characters.",
                        "channelNames");
                }
            }

            int channelCount = channelNames.Length;

            long dataStartOffset =
                _fileHeaderSize +
                (long)channelCount * ChannelNameBytes;

            lock (_ioSync)
            {
                _filePath = filePath;
                _fileCapacityBytes = initialFileBytes;
                _writerFaulted = false;
                _lastErrorText = null;

                long minFileSize =
                    dataStartOffset +
                    _recordHeaderSize +
                    4096;

                if (_fileCapacityBytes < minFileSize)
                    _fileCapacityBytes = minFileSize;

                _fileStream = new FileStream(
                    _filePath,
                    FileMode.Create,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite);

                _fileStream.SetLength(_fileCapacityBytes);
                _fileStream.Flush();

                CreateMapAndAcquirePointer();

                _header = new SessionFileHeader
                {
                    Magic = MagicValue,
                    Version = VersionValue,
                    ChannelCount = channelCount,
                    SampleRate = sampleRate,
                    QpcFrequency = qpcFrequency,
                    DataStartOffset = dataStartOffset,
                    CommittedBytes = dataStartOffset,
                    TotalSamplesPerChannel = 0,
                    GrowStepBytes = growStepBytes,
                    Reserved0 = 0
                };

                _preGrowThresholdBytes =
                    Math.Max(
                        1024 * 1024,
                        _header.GrowStepBytes / 4);

                _headerDirty = false;

                WriteFileHeader();
                WriteChannelNames(channelNames);

                _writePosition = _header.DataStartOffset;
                _isRecording = true;
            }
        }

        public void StopSession()
        {
            lock (_ioSync)
            {
                _isRecording = false;

                if (_accessor != null)
                {
                    if (_headerDirty)
                    {
                        WriteFileHeader();
                        _headerDirty = false;
                    }
                    _accessor.Flush();
                }

                ReleaseAndDisposeMap();

                if (_fileStream != null)
                {
                    _fileStream.Dispose();
                    _fileStream = null;
                }
            }
            ClearPendingBuffers();
        }

        public void OnServiceEvent(ServiceEventPacket evt)
        {
            if (!_isRecording)
                return;

            ServiceEventCode code = (ServiceEventCode)evt.Code;
            // Preserve the EOG check start as service data, not an epoch marker.
            if (code != ServiceEventCode.UserEvent &&
                !(code == ServiceEventCode.State && evt.State == 1 && evt.Value1 == 0 &&
                  CovertCalibrationEog.IsPairClass(evt.Value0)))
                return;

            SessionUserEvent record = new SessionUserEvent
            {
                Sequence = evt.Sequence,
                QpcTimestamp = evt.QpcTimestamp,
                Code = evt.Code,
                State = evt.State,
                Value0 = evt.Value0,
                Value1 = evt.Value1
            };

            lock (_eventSync)
            {
                _eventQueue.Enqueue(record);
            }

            _eventAvailableEvent.Set();
        }

        private void SignalInput_BlockPublished(SignalBlock block)
        {
            if (block == null || block.Data == null)
                return;

            if (!_isRecording)
                return;

            _signalRing.Enqueue(block);
        }

        private void WriterLoop()
        {
            WaitHandle[] handles = new WaitHandle[]
            {
                _signalRing.DataAvailableWaitHandle,
                _eventAvailableEvent,
                _stopEvent
            };

            try
            {
                while (_running)
                {
                    int signaled = WaitHandle.WaitAny(handles);
                    if (signaled == 2)
                        break;

                    DrainEventQueue();
                    DrainSignalQueue();
                }
            }
            catch (Exception ex)
            {
                _writerFaulted = true;
                _lastErrorText = ex.ToString();
                _isRecording = false;

                try
                {
                    //Debug.WriteLine(ex.ToString());
                    MessageBox.Show(ex.ToString(), "SessionRecorder writer error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch
                {
                }
            }
        }

        private void DrainEventQueue()
        {
            while (true)
            {
                SessionUserEvent record;
                if (!TryDequeueEvent(out record))
                    break;

                WriteEventRecord(record);
            }
        }

        private void DrainSignalQueue()
        {
            while (true)
            {
                SignalBlock block;
                if (!_signalRing.TryDequeue(out block))
                    break;

                WriteSignalBlock(block);
            }
        }

        private bool TryDequeueEvent(out SessionUserEvent record)
        {
            lock (_eventSync)
            {
                if (_eventQueue.Count <= 0)
                {
                    record = default(SessionUserEvent);
                    return false;
                }

                record = _eventQueue.Dequeue();
                return true;
            }
        }

        private unsafe void WriteEventRecord(SessionUserEvent record)
        {
            lock (_ioSync)
            {
                if (!_isRecording || _accessor == null)
                    return;

                long recordBytes = _recordHeaderSize;
                EnsureWritable(recordBytes);

                byte* basePtr = GetBasePtr();
                byte* recordPtr = basePtr + _writePosition;

                SessionRecordHeader header = new SessionRecordHeader
                {
                    RecordType = (int)SessionRecordType.UserEvent,
                    SampleCount = 0,
                    PayloadBytes = 0,
                    Reserved0 = 0,
                    Sequence = record.Sequence,
                    QpcTimestamp = record.QpcTimestamp,
                    FirstSampleIndex = _header.TotalSamplesPerChannel,
                    Code = record.Code,
                    State = record.State,
                    Value0 = record.Value0,
                    Value1 = record.Value1
                };

                *(SessionRecordHeader*)recordPtr = header;

                _writePosition += recordBytes;
                _header.CommittedBytes = _writePosition;
                _headerDirty = true;
            }
        }

        private unsafe void WriteSignalBlock(SignalBlock block)
        {
            if (block == null || block.Data == null)
                return;

            lock (_ioSync)
            {
                if (!_isRecording || _accessor == null)
                    return;

                if (block.ChannelCount != _header.ChannelCount ||
                    block.SampleRate != _header.SampleRate)
                {
                    return;
                }

                int floatCount = block.ChannelCount * block.SampleCount;
                int payloadBytes = checked(floatCount * sizeof(float));
                long recordBytes = _recordHeaderSize + payloadBytes;

                EnsureWritable(recordBytes);

                byte* basePtr = GetBasePtr();
                byte* recordPtr = basePtr + _writePosition;
                byte* payloadPtr = recordPtr + _recordHeaderSize;

                SessionRecordHeader header = new SessionRecordHeader
                {
                    RecordType = (int)SessionRecordType.SignalBlock,
                    SampleCount = block.SampleCount,
                    PayloadBytes = payloadBytes,
                    Reserved0 = 0,
                    Sequence = block.Sequence,
                    QpcTimestamp = block.QpcTimestamp,
                    FirstSampleIndex = _header.TotalSamplesPerChannel,
                    Code = 0,
                    State = 0,
                    Value0 = 0,
                    Value1 = 0
                };

                *(SessionRecordHeader*)recordPtr = header;

                fixed (float* srcPtr = block.Data)
                {
                    Buffer.MemoryCopy(srcPtr, payloadPtr, payloadBytes, payloadBytes);
                }

                _writePosition += recordBytes;
                _header.TotalSamplesPerChannel += block.SampleCount;
                _header.CommittedBytes = _writePosition;
                _headerDirty = true;
            }
        }

        private void EnsureWritable(long bytesNeeded)
        {
            long requiredBytes = _writePosition + bytesNeeded;
            long safeLimit = _fileCapacityBytes - _preGrowThresholdBytes;

            if (requiredBytes <= safeLimit)
                return;

            long growStepBytes = _header.GrowStepBytes;
            if (growStepBytes <= 0)
                growStepBytes = _defaultGrowStepBytes;

            long newCapacity = _fileCapacityBytes;
            while (requiredBytes > newCapacity - _preGrowThresholdBytes)
                newCapacity += growStepBytes;

            if (_headerDirty)
            {
                WriteFileHeader();
                _accessor.Flush();
                _headerDirty = false;
            }

            ReleaseAndDisposeMap();

            _fileStream.SetLength(newCapacity);
           // _fileStream.Flush();

            _fileCapacityBytes = newCapacity;
            CreateMapAndAcquirePointer();

            WriteFileHeader();
            _accessor.Flush();
            _headerDirty = false;
        }

        private void CreateMapAndAcquirePointer()
        {
            _mmf = MemoryMappedFile.CreateFromFile(
                _fileStream,
                null,
                _fileCapacityBytes,
                MemoryMappedFileAccess.ReadWrite,
                null,
                HandleInheritability.None,
                true);

            _accessor = _mmf.CreateViewAccessor(
                0,
                _fileCapacityBytes,
                MemoryMappedFileAccess.ReadWrite);

            unsafe
            {
                byte* ptr = null;
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                if (ptr == null)
                    throw new InvalidOperationException("Не удалось получить указатель на MMF view.");

                _viewPtr = (IntPtr)ptr;
                _viewPointerOffset = _accessor.PointerOffset;
                _viewPointerAcquired = true;
            }
        }

        private void ReleaseAndDisposeMap()
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
        }

        private unsafe byte* GetBasePtr()
        {
            return (byte*)_viewPtr + _viewPointerOffset;
        }

        private unsafe void WriteChannelNames(string[] channelNames)
        {
            if (_accessor == null)
                return;

            byte* basePtr = GetBasePtr();

            for (int ch = 0; ch < channelNames.Length; ch++)
            {
                char* dst = (char*)(
                    basePtr +
                    _fileHeaderSize +
                    (long)ch * ChannelNameBytes);

                for (int i = 0; i < ChannelNameCharCapacity; i++)
                    dst[i] = '\0';

                string name = channelNames[ch] ?? string.Empty;

                for (int i = 0; i < name.Length; i++)
                    dst[i] = name[i];
            }
        }

        private unsafe void WriteFileHeader()
        {
            if (_accessor == null)
                return;

            byte* basePtr = GetBasePtr();
            *(SessionFileHeader*)basePtr = _header;
        }

        public void Dispose()
        {
            DetachSignalInput();

            _running = false;

            try
            {
                _stopEvent.Set();
            }
            catch
            {
            }

            if (_writerThread != null && _writerThread.IsAlive)
                _writerThread.Join(500);

            StopSession();

            _stopEvent.Dispose();
            _eventAvailableEvent.Dispose();
            _signalRing.Dispose();
        }
    }
}
