using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace PolyHubNet
{
    public sealed class ProcessingBlockRingReader : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct RingHeader
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
        private struct RingBlockHeader
        {
            public long Sequence;
            public long QpcTimestamp;
            public int SampleCount;
            public int Reserved;
        }

        private const int MagicValue = 0x50485242; // PHRB
        private const int VersionValue = 1;

        private const int ChannelNameCharCapacity = 32;
        private const int ChannelNameBytes = ChannelNameCharCapacity * sizeof(char);

        private readonly string _mapName;
        private readonly string _eventName;
        private readonly SignalStream _outputStream;

        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _accessor;
        private EventWaitHandle _dataReadyEvent;
        private Mutex _headerMutex;

        private Thread _thread;
        private readonly ManualResetEvent _stopEvent;
        private volatile bool _running;

        private readonly int _headerSize;
        private readonly int _blockHeaderSize;

        private long _dataOffset;

        private long _nextSequence;
        private int _missedBlockCount;
        public int TryReadBlockFailCount;

        public event Action<int> MissedBlockCountChanged;

        private IntPtr _viewPtr = IntPtr.Zero;
        private long _viewPointerOffset;
        private bool _viewPointerAcquired;

        public ProcessingBlockRingReader(string mapName, string eventName, SignalStream outputStream)
        {
            if (string.IsNullOrWhiteSpace(mapName))
                throw new ArgumentNullException(nameof(mapName));
            if (string.IsNullOrWhiteSpace(eventName))
                throw new ArgumentNullException(nameof(eventName));
            if (outputStream == null)
                throw new ArgumentNullException(nameof(outputStream));

            _mapName = mapName;
            _eventName = eventName;
            _outputStream = outputStream;

            _headerSize = Marshal.SizeOf(typeof(RingHeader));
            _blockHeaderSize = Marshal.SizeOf(typeof(RingBlockHeader));

            _stopEvent = new ManualResetEvent(false);
        }

        public bool Start(int retryCount, int retryDelayMs)
        {
            for (int i = 0; i < retryCount; i++)
            {
                try
                {
                    _mmf = MemoryMappedFile.OpenExisting(_mapName, MemoryMappedFileRights.ReadWrite);
                    _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
                    _dataReadyEvent = EventWaitHandle.OpenExisting(_eventName);
                    _headerMutex = Mutex.OpenExisting(_mapName + ".HeaderMutex");

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

                    _headerMutex.WaitOne();
                    try
                    {
                        RingHeader header;
                        _accessor.Read(0, out header);

                        if (header.Magic != MagicValue || header.Version != VersionValue)
                            throw new InvalidOperationException("Неверный формат ProcessingBlockRing.");

                        _dataOffset = _headerSize + (long)header.ChannelCount * ChannelNameBytes;
                        _outputStream.SetChannelNames(ReadChannelNames(header.ChannelCount));

                        // Регистрируемся как реальный читатель
                        if (header.ActiveReaderCount < int.MaxValue)
                            header.ActiveReaderCount++;

                        _accessor.Write(0, ref header);

                        // Жизнь PolyHub начинается СЕЙЧАС.
                        // Не догоняем прошлое, а стартуем с живого края.
                        _nextSequence = header.WriteSequence + 1;
                        _missedBlockCount = 0;
                        TryReadBlockFailCount = 0;
                    }
                    finally
                    {
                        _headerMutex.ReleaseMutex();
                    }

                    _running = true;
                    _thread = new Thread(ReadLoop);
                    _thread.IsBackground = true;
                    _thread.Name = "ProcessingBlockRingReader";
                    _thread.Start();

                    return true;
                }
                catch
                {
                    CleanupOpenHandles();
                    Thread.Sleep(retryDelayMs);
                }
            }

            return false;
        }

        public int GetChannelCount()
        {
            return ReadHeader().ChannelCount;
        }

        public int GetSampleRate()
        {
            return ReadHeader().SampleRate;
        }

        public int GetBlockSizeSamples()
        {
            return ReadHeader().BlockSizeSamples;
        }

        private unsafe string[] ReadChannelNames(int channelCount)
        {
            string[] names = new string[channelCount];

            byte* basePtr =
                (byte*)_viewPtr +
                _viewPointerOffset;

            for (int ch = 0; ch < channelCount; ch++)
            {
                char* src = (char*)(
                    basePtr +
                    _headerSize +
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

        private unsafe RingHeader ReadHeader()
        {
            byte* basePtr = (byte*)_viewPtr + _viewPointerOffset;
            return *(RingHeader*)basePtr;
            
        }

        private void ReadLoop()
        {
            WaitHandle[] handles = new WaitHandle[] { _dataReadyEvent, _stopEvent };

            try
            {
                while (_running)
                {
                    int signaled = WaitHandle.WaitAny(handles);

                    if (signaled == 1)
                        break;

                    PumpAvailableBlocks();
                }
            }
            catch
            {
            }
        }

        private void PumpAvailableBlocks()
        {
            RingHeader header = ReadHeader();

            if (header.WriteSequence <= 0)
                return;

            // Если reader отстал уже ПОСЛЕ подключения — считаем локально
            if (_nextSequence < header.OldestAvailableSequence)
            {
                long missed = header.OldestAvailableSequence - _nextSequence;
                long total = (long)_missedBlockCount + missed;
                if (total > int.MaxValue)
                    total = int.MaxValue;

                _missedBlockCount = (int)total;
                _nextSequence = header.OldestAvailableSequence;

                Action<int> handler = MissedBlockCountChanged;
                if (handler != null)
                    handler(_missedBlockCount);
            }

            long lastSequence = header.WriteSequence;

            for (long seq = _nextSequence; seq <= lastSequence; seq++)
            {
                SignalBlock block;
                if (TryReadBlock(seq, header, out block))
                {
                    _outputStream.Publish(block);
                }               
                else
                {
                    //для отладки
                    Interlocked.Increment(ref TryReadBlockFailCount);// Сюда никогда не попадаем, так что, всё ок !!!!!!
                }
            }

            _nextSequence = lastSequence + 1;
        }

        private unsafe bool TryReadBlock(long sequence, RingHeader header, out SignalBlock block)
        {
            block = null;

            int slotIndex = (int)((sequence - 1) % header.BlockCount);

            int payloadFloatCapacity = header.ChannelCount * header.BlockSizeSamples;
            long payloadBytesCapacity = (long)payloadFloatCapacity * sizeof(float);
            long slotBytes = _blockHeaderSize + payloadBytesCapacity;
            long slotOffset = _dataOffset + slotIndex * slotBytes;

            byte* basePtr = (byte*)_viewPtr + _viewPointerOffset;
            byte* slotPtr = basePtr + slotOffset;

            RingBlockHeader blockHeader1 = *(RingBlockHeader*)slotPtr;

            if (blockHeader1.Sequence != sequence)
                return false;

            int sampleCount = blockHeader1.SampleCount;
            if (sampleCount <= 0 || sampleCount > header.BlockSizeSamples)
                return false;

            int requiredFloatCount = header.ChannelCount * sampleCount;
            long requiredBytes = (long)requiredFloatCount * sizeof(float);

            float[] data = new float[requiredFloatCount];

            byte* payloadPtr = slotPtr + _blockHeaderSize;

            fixed (float* dstPtr = data)
            {
                Buffer.MemoryCopy(
                    payloadPtr,
                    dstPtr,
                    requiredBytes,
                    requiredBytes);
            }

            // Перечитываем заголовок ещё раз против гонки перезаписи writer-ом.
            RingBlockHeader blockHeader2 = *(RingBlockHeader*)slotPtr;

            if (blockHeader2.Sequence != sequence ||
                blockHeader2.SampleCount != sampleCount ||
                blockHeader2.QpcTimestamp != blockHeader1.QpcTimestamp)
            {
                return false;
            }

            block = new SignalBlock
            {
                Sequence = blockHeader1.Sequence,
                QpcTimestamp = blockHeader1.QpcTimestamp,
                ChannelCount = header.ChannelCount,
                SampleRate = header.SampleRate,
                SampleCount = sampleCount,
                Data = data
            };

            return true;
        }

        public void Dispose()
        {
            _running = false;

            try
            {
                _stopEvent.Set();
            }
            catch
            {
            }

            if (_thread != null && _thread.IsAlive)
                _thread.Join(500);

            try
            {
                if (_headerMutex != null)
                {
                    _headerMutex.WaitOne();
                    try
                    {
                        if (_accessor != null)
                        {
                            RingHeader header;
                            _accessor.Read(0, out header);

                            if (header.ActiveReaderCount > 0)
                                header.ActiveReaderCount--;

                            _accessor.Write(0, ref header);
                        }
                    }
                    finally
                    {
                        _headerMutex.ReleaseMutex();
                    }
                }
            }
            catch
            {
            }

            CleanupOpenHandles();

            if (_stopEvent != null)
                _stopEvent.Dispose();
        }

        private void CleanupOpenHandles()
        {
            if (_headerMutex != null)
            {
                _headerMutex.Dispose();
                _headerMutex = null;
            }

            if (_dataReadyEvent != null)
            {
                _dataReadyEvent.Dispose();
                _dataReadyEvent = null;
            }

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
    }
}