using System;
using System.Threading;

namespace PolyHubNet
{
    public sealed class ProcessorBlockRing : IDisposable
    {
        private readonly object _sync = new object();
        private readonly SignalBlock[] _buffer;
        private readonly int _capacity;

        private int _readIndex;
        private int _writeIndex;
        private int _count;
        private int _droppedBlockCount;

        private readonly AutoResetEvent _dataAvailableEvent;

        public event Action<int> DroppedBlockCountChanged;

        public ProcessorBlockRing(int capacity)
        {
            if (capacity <= 1)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            _capacity = capacity;
            _buffer = new SignalBlock[_capacity];
            _dataAvailableEvent = new AutoResetEvent(false);
        }

        public WaitHandle DataAvailableWaitHandle
        {
            get { return _dataAvailableEvent; }
        }

        public void Enqueue(SignalBlock block)
        {
            if (block == null)
                return;

            int newDroppedCount = -1;

            lock (_sync)
            {
                if (_count == _capacity)
                {
                    // overwrite oldest
                    _buffer[_writeIndex] = block;

                    _writeIndex++;
                    if (_writeIndex >= _capacity)
                        _writeIndex = 0;

                    _readIndex = _writeIndex;

                    _droppedBlockCount++;
                    newDroppedCount = _droppedBlockCount;
                }
                else
                {
                    _buffer[_writeIndex] = block;

                    _writeIndex++;
                    if (_writeIndex >= _capacity)
                        _writeIndex = 0;

                    _count++;
                }
            }

            _dataAvailableEvent.Set();

            if (newDroppedCount >= 0)
            {
                Action<int> handler = DroppedBlockCountChanged;
                if (handler != null)
                    handler(newDroppedCount);
            }
        }

        public bool TryDequeue(out SignalBlock block)
        {
            lock (_sync)
            {
                if (_count <= 0)
                {
                    block = null;
                    return false;
                }

                block = _buffer[_readIndex];
                _buffer[_readIndex] = null;

                _readIndex++;
                if (_readIndex >= _capacity)
                    _readIndex = 0;

                _count--;
                return true;
            }
        }

        public void Dispose()
        {
            _dataAvailableEvent.Dispose();
        }
    }
}