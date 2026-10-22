using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace SignalGenerator
{
    internal sealed class ProcessingBlockRing : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct Header
        {
            public int Magic;
            public int Version;

            public int ChannelCount;
            public int SampleRate;
            public int BlockSizeSamples;
            public int BlockCount;

            public long WriteSequence;
            public long OldestAvailableSequence;

            public int IsRunning;
            public int StatusCode;

            public int ActiveReaderCount;
            public int DroppedBlockCount;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BlockHeader
        {
            public long Sequence;
            public long QpcTimestamp;
            public int SampleCount;
            public int Reserved;
        }

        public const int MagicValue = 0x50485242; // PHRB
        public const int VersionValue = 1;

        public const int StatusOk = 0;

        private const int ChannelNameCharCapacity = 32;
        private const int ChannelNameBytes = ChannelNameCharCapacity * sizeof(char);

        private readonly int _channelCount;
        private readonly int _sampleRate;
        private readonly int _blockSizeSamples;
        private readonly int _blockCount;

private readonly int _headerSize;
private readonly int _blockHeaderSize;
private readonly int _payloadFloatCountPerBlock;
private readonly long _payloadBytesPerBlock;
private readonly long _slotBytes;
private readonly long _dataOffset;
private readonly long _totalBytes;

        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _accessor;
        private readonly EventWaitHandle _dataReadyEvent;
        private readonly Mutex _headerMutex;

        private IntPtr _viewPtr = IntPtr.Zero;
        private long _viewPointerOffset;
        private bool _viewPointerAcquired;

        public ProcessingBlockRing(
            string mapName,
            string eventName,
            int channelCount,
            int sampleRate,
            int blockSizeSamples,
            int blockCount,
            string[] channelNames)
        {
            if (string.IsNullOrWhiteSpace(mapName))
                throw new ArgumentNullException(nameof(mapName));
            if (string.IsNullOrWhiteSpace(eventName))
                throw new ArgumentNullException(nameof(eventName));
            if (channelCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(channelCount));
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (blockSizeSamples <= 0)
                throw new ArgumentOutOfRangeException(nameof(blockSizeSamples));
            if (blockCount <= 1)
                throw new ArgumentOutOfRangeException(nameof(blockCount));

            if (channelNames == null)
                throw new ArgumentNullException(nameof(channelNames));

            if (channelNames.Length != channelCount)
            {
                throw new ArgumentException(
                    "Channel names count must match channel count.",
                    nameof(channelNames));
            }

            for (int i = 0; i < channelNames.Length; i++)
            {
                string name = channelNames[i] ?? string.Empty;

                if (name.Length > ChannelNameCharCapacity - 1)
                {
                    throw new ArgumentException(
                        "Channel name must not exceed 31 characters.",
                        nameof(channelNames));
                }
            }

            _channelCount = channelCount;
            _sampleRate = sampleRate;
            _blockSizeSamples = blockSizeSamples;
            _blockCount = blockCount;

            _headerSize = Marshal.SizeOf(typeof(Header));
            _blockHeaderSize = Marshal.SizeOf(typeof(BlockHeader));
            _payloadFloatCountPerBlock = _channelCount * _blockSizeSamples;
            _payloadBytesPerBlock = (long)_payloadFloatCountPerBlock * sizeof(float);
            _slotBytes = _blockHeaderSize + _payloadBytesPerBlock;
            _dataOffset = _headerSize + (long)_channelCount * ChannelNameBytes;
            _totalBytes = _dataOffset + _slotBytes * _blockCount;

            _mmf = MemoryMappedFile.CreateOrOpen(
                mapName,
                _totalBytes,
                MemoryMappedFileAccess.ReadWrite);

            _accessor = _mmf.CreateViewAccessor(
                0,
                _totalBytes,
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

            bool createdNew;
            _dataReadyEvent = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                eventName,
                out createdNew);

            bool mutexCreatedNew;
            _headerMutex = new Mutex(
                false,
                mapName + ".HeaderMutex",
                out mutexCreatedNew);

            EnsureInitialized();
            WriteChannelNames(channelNames);
        }

        private unsafe byte* GetBasePtr()
        {
            return (byte*)_viewPtr + _viewPointerOffset;
        }

        private void EnsureInitialized()
        {
            _headerMutex.WaitOne();
            try
            {
                Header header = ReadHeader();

                bool needInit =
                    header.Magic != MagicValue ||
                    header.Version != VersionValue ||
                    header.ChannelCount != _channelCount ||
                    header.SampleRate != _sampleRate ||
                    header.BlockSizeSamples != _blockSizeSamples ||
                    header.BlockCount != _blockCount;

                if (!needInit)
                    return;

                InitializeHeader();
            }
            finally
            {
                _headerMutex.ReleaseMutex();
            }
        }

        private unsafe void InitializeHeader()
        {
            Header header = new Header
            {
                Magic = MagicValue,
                Version = VersionValue,

                ChannelCount = _channelCount,
                SampleRate = _sampleRate,
                BlockSizeSamples = _blockSizeSamples,
                BlockCount = _blockCount,

                WriteSequence = 0,
                OldestAvailableSequence = 1,

                IsRunning = 0,
                StatusCode = StatusOk,

                ActiveReaderCount = 0,
                DroppedBlockCount = 0
            };

            byte* basePtr = GetBasePtr();
            *(Header*)basePtr = header;
        }

        private unsafe void WriteChannelNames(string[] channelNames)
        {
            byte* basePtr = GetBasePtr();

            for (int ch = 0; ch < _channelCount; ch++)
            {
                char* dst = (char*)(
                    basePtr +
                    _headerSize +
                    (long)ch * ChannelNameBytes);

                for (int i = 0; i < ChannelNameCharCapacity; i++)
                    dst[i] = '\0';

                string name = channelNames[ch] ?? string.Empty;

                for (int i = 0; i < name.Length; i++)
                    dst[i] = name[i];
            }
        }

        public unsafe Header ReadHeader()
        {
            byte* basePtr = GetBasePtr();
            if (basePtr == null)
                return default;
            return *(Header*)basePtr;
        }

        public unsafe void UpdateRunning(bool isRunning)
        {
            _headerMutex.WaitOne();
            try
            {
                byte* basePtr = GetBasePtr();
                Header header = *(Header*)basePtr;
                header.IsRunning = isRunning ? 1 : 0;
                *(Header*)basePtr = header;
            }
            finally
            {
                _headerMutex.ReleaseMutex();
            }
        }

        public unsafe void AttachReader()
        {
            _headerMutex.WaitOne();
            try
            {
                byte* basePtr = GetBasePtr();
                Header header = *(Header*)basePtr;

                if (header.ActiveReaderCount < int.MaxValue)
                    header.ActiveReaderCount++;

                *(Header*)basePtr = header;
            }
            finally
            {
                _headerMutex.ReleaseMutex();
            }
        }

        public unsafe void DetachReader()
        {
            _headerMutex.WaitOne();
            try
            {
                byte* basePtr = GetBasePtr();
                Header header = *(Header*)basePtr;

                if (header.ActiveReaderCount > 0)
                    header.ActiveReaderCount--;

                *(Header*)basePtr = header;
            }
            finally
            {
                _headerMutex.ReleaseMutex();
            }
        }

        // blockChannelMajor = [ch0:s0..sN-1][ch1:s0..sN-1]...
        public unsafe void PublishBlock(float[] blockChannelMajor, int sampleCount, long qpcTimestamp)
        {
            if (blockChannelMajor == null)
                throw new ArgumentNullException(nameof(blockChannelMajor));
            if (sampleCount <= 0 || sampleCount > _blockSizeSamples)
                throw new ArgumentOutOfRangeException(nameof(sampleCount));

            int requiredFloatCount = _channelCount * sampleCount;
            if (blockChannelMajor.Length < requiredFloatCount)
                throw new ArgumentException("blockChannelMajor length is too small.");

            _headerMutex.WaitOne();
            try
            {
                byte* basePtr = GetBasePtr();
                Header header = *(Header*)basePtr;

                long nextSequence = header.WriteSequence + 1;
                long oldOldestAvailableSequence = header.OldestAvailableSequence;

                long newOldestAvailableSequence = nextSequence - _blockCount + 1;
                if (newOldestAvailableSequence < 1)
                    newOldestAvailableSequence = 1;

                if (header.ActiveReaderCount > 0 &&
                    newOldestAvailableSequence > oldOldestAvailableSequence)
                {
                    long lost = newOldestAvailableSequence - oldOldestAvailableSequence;
                    long totalDropped = (long)header.DroppedBlockCount + lost;
                    if (totalDropped > int.MaxValue)
                        totalDropped = int.MaxValue;

                    header.DroppedBlockCount = (int)totalDropped;
                }

                header.OldestAvailableSequence = newOldestAvailableSequence;

                int slotIndex = (int)((nextSequence - 1) % _blockCount);
                long slotOffset = _dataOffset + slotIndex * _slotBytes;

                BlockHeader blockHeader = new BlockHeader
                {
                    Sequence = nextSequence,
                    QpcTimestamp = qpcTimestamp,
                    SampleCount = sampleCount,
                    Reserved = 0
                };

                byte* slotPtr = basePtr + slotOffset;
                byte* payloadPtr = slotPtr + _blockHeaderSize;

                long payloadBytes = (long)requiredFloatCount * sizeof(float);

                // 1) Сначала payload
                fixed (float* srcPtr = blockChannelMajor)
                {
                    Buffer.MemoryCopy(
                        srcPtr,
                        payloadPtr,
                        payloadBytes,
                        payloadBytes);
                }

                // 2) Потом заголовок блока
                *(BlockHeader*)slotPtr = blockHeader;

                // 3) И только потом commit заголовка кольца
                header.WriteSequence = nextSequence;
                *(Header*)basePtr = header;
            }
            finally
            {
                _headerMutex.ReleaseMutex();
            }

            _dataReadyEvent.Set();
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

            if (_headerMutex != null)
                _headerMutex.Dispose();

            if (_dataReadyEvent != null)
                _dataReadyEvent.Dispose();

            if (_accessor != null)
                _accessor.Dispose();

            if (_mmf != null)
                _mmf.Dispose();
        }
    }
}