using System;
using System.Threading;

namespace PolyHubNet
{
    public abstract class SignalProcessor : IDisposable
    {
        public string Name { get; private set; }

        public SignalStream InputStream { get; private set; }
        public SignalStream OutputStream { get; private set; }

        private readonly ProcessorBlockRing _inputRing;
        private readonly ManualResetEvent _stopEvent;
        private readonly Thread _workerThread;

        private volatile bool _running;

        public event Action<int> InputDroppedBlockCountChanged;

        protected SignalProcessor(string name, int inputRingCapacity)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentNullException(nameof(name));
            if (inputRingCapacity <= 1)
                throw new ArgumentOutOfRangeException(nameof(inputRingCapacity));

            Name = name;

            _inputRing = new ProcessorBlockRing(inputRingCapacity);
            _inputRing.DroppedBlockCountChanged += InputRing_DroppedBlockCountChanged;

            _stopEvent = new ManualResetEvent(false);

            _running = true;
            _workerThread = new Thread(WorkerLoop);
            _workerThread.IsBackground = true;
            _workerThread.Name = name + ".Worker";
            _workerThread.Priority = ThreadPriority.AboveNormal;
            _workerThread.Start();
        }

        public void AttachInput(SignalStream input)
        {
            DetachInput();

            InputStream = input;

            if (input != null)
            {
                input.BlockPublished += InputStream_BlockPublished;
                input.ChannelNamesChanged += InputStream_ChannelNamesChanged;
            }

            CopyChannelNamesToOutput();
        }

        public void SetOutput(SignalStream output)
        {
            OutputStream = output;
            CopyChannelNamesToOutput();
        }

        private void InputStream_ChannelNamesChanged()
        {
            CopyChannelNamesToOutput();
        }

        private void CopyChannelNamesToOutput()
        {
            SignalStream input = InputStream;
            SignalStream output = OutputStream;

            if (input == null || output == null)
                return;

            output.SetChannelNames(
                input.ChannelNames);
        }

        private void InputStream_BlockPublished(SignalBlock block)
        {
            _inputRing.Enqueue(block);
        }

        private void InputRing_DroppedBlockCountChanged(int dropped)
        {
            Action<int> handler = InputDroppedBlockCountChanged;
            if (handler != null)
                handler(dropped);
        }

        private void WorkerLoop()
        {
            WaitHandle[] handles = new WaitHandle[]
            {
                _inputRing.DataAvailableWaitHandle,
                _stopEvent
            };

            try
            {
                while (_running)
                {
                    int signaled = WaitHandle.WaitAny(handles);

                    if (signaled == 1)
                        break;

                    while (true)
                    {
                        SignalBlock input;
                        if (!_inputRing.TryDequeue(out input))
                            break;

                        SignalBlock result = ProcessBlock(input);

                        if (result != null && OutputStream != null)
                            OutputStream.Publish(result);
                    }
                }
            }
            catch
            {
            }
        }

        protected abstract SignalBlock ProcessBlock(SignalBlock input);

        public void DetachInput()
        {
            SignalStream input = InputStream;

            if (input != null)
            {
                input.BlockPublished -= InputStream_BlockPublished;
                input.ChannelNamesChanged -= InputStream_ChannelNamesChanged;
            }

            InputStream = null;
        }

        public virtual void Dispose()
        {
            _running = false;
            DetachInput();

            try
            {
                _stopEvent.Set();
            }
            catch
            {
            }

            if (_workerThread != null && _workerThread.IsAlive)
                _workerThread.Join(500);

            _stopEvent.Dispose();
            _inputRing.Dispose();
        }
    }
}