using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace PolyHubIpc
{
    public enum ServiceEventSide
    {
        Hub = 0,
        Service = 1
    }

    public enum ServiceEventCode
    {
        None = 0,

        Start = 1,
        Stop = 2,
        Reset = 3,
        Ping = 4,

        Ack = 100,
        Error = 101,
        State = 102,

        UserEvent = 1000
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ServiceEventHeader
    {
        public int Magic;
        public int Version;
        public int EventCapacity;
        public int Reserved0;

        public long HubToServiceWriteSequence;
        public long HubToServiceOldestAvailableSequence;
        public int HubToServiceDroppedEventCount;
        public int Reserved1;

        public long ServiceToHubWriteSequence;
        public long ServiceToHubOldestAvailableSequence;
        public int ServiceToHubDroppedEventCount;
        public int Reserved2;
    }

    // Важно:
    // Этот struct НЕ пишем напрямую через accessor.Write(ref ...),
    // потому что внутри есть string.
    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    public struct ServiceEventPacket
    {
        public long Sequence;
        public long QpcTimestamp;

        public int Code;
        public int State;
        public int Value0;
        public int Value1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Text;
    }

    public sealed class ServiceEventLink : IDisposable
    {
        public const int MagicValue = 0x50484556; // PHEV
        public const int VersionValue = 1;

        private const int TextCharCapacity = 128;

        private const long SequenceOffset = 0;
        private const long QpcTimestampOffset = SequenceOffset + sizeof(long);
        private const long CodeOffset = QpcTimestampOffset + sizeof(long);
        private const long StateOffset = CodeOffset + sizeof(int);
        private const long Value0Offset = StateOffset + sizeof(int);
        private const long Value1Offset = Value0Offset + sizeof(int);
        private const long TextOffset = Value1Offset + sizeof(int);

        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceCounter(out long value);

        private readonly ServiceEventSide _side;
        private readonly int _eventCapacity;

        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _accessor;

        private readonly EventWaitHandle _hubToServiceEventHandle;
        private readonly EventWaitHandle _serviceToHubEventHandle;

        private readonly AutoResetEvent _sendEventQueueHandle;
        private readonly ManualResetEvent _stopEventHandle;

        private readonly Thread _receiveEventThread;
        private readonly Thread _sendEventThread;

        private readonly object _ioSync = new object();
        private readonly object _queueSync = new object();
        private readonly Queue<ServiceEventPacket> _outboundEventQueue = new Queue<ServiceEventPacket>();

        private readonly int _headerSize;
        private readonly int _packetSize;
        private readonly long _hubToServiceBaseOffset;
        private readonly long _serviceToHubBaseOffset;

        private volatile bool _running;
        private long _nextInboundEventSequence;
        private int _lastInboundDroppedEventCount = -1;

        public event Action<ServiceEventPacket> EventReceived;
        public event Action<int> InboundDroppedEventCountChanged;

        public ServiceEventLink(
            string mapName,
            string hubToServiceEventHandleName,
            string serviceToHubEventHandleName,
            ServiceEventSide side,
            int eventCapacity)
        {
            if (string.IsNullOrWhiteSpace(mapName))
                throw new ArgumentNullException(nameof(mapName));
            if (string.IsNullOrWhiteSpace(hubToServiceEventHandleName))
                throw new ArgumentNullException(nameof(hubToServiceEventHandleName));
            if (string.IsNullOrWhiteSpace(serviceToHubEventHandleName))
                throw new ArgumentNullException(nameof(serviceToHubEventHandleName));
            if (eventCapacity <= 1)
                throw new ArgumentOutOfRangeException(nameof(eventCapacity));

            _side = side;
            _eventCapacity = eventCapacity;

            _headerSize = Marshal.SizeOf(typeof(ServiceEventHeader));
            _packetSize = (int)(TextOffset + TextCharCapacity * sizeof(char));

            long laneBytes = (long)_eventCapacity * _packetSize;
            _hubToServiceBaseOffset = _headerSize;
            _serviceToHubBaseOffset = _headerSize + laneBytes;

            long totalBytes = _headerSize + laneBytes + laneBytes;

            _mmf = MemoryMappedFile.CreateOrOpen(
                mapName,
                totalBytes,
                MemoryMappedFileAccess.ReadWrite);

            _accessor = _mmf.CreateViewAccessor(
                0,
                totalBytes,
                MemoryMappedFileAccess.ReadWrite);

            bool createdNew;
            _hubToServiceEventHandle = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                hubToServiceEventHandleName,
                out createdNew);

            _serviceToHubEventHandle = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                serviceToHubEventHandleName,
                out createdNew);

            _sendEventQueueHandle = new AutoResetEvent(false);
            _stopEventHandle = new ManualResetEvent(false);

            EnsureInitialized();

            _running = true;

            _receiveEventThread = new Thread(ReceiveEventLoop);
            _receiveEventThread.IsBackground = true;
            _receiveEventThread.Name = "ServiceEventLink.ReceiveEvent";
            _receiveEventThread.Priority = ThreadPriority.AboveNormal;
            _receiveEventThread.Start();

            _sendEventThread = new Thread(SendEventLoop);
            _sendEventThread.IsBackground = true;
            _sendEventThread.Name = "ServiceEventLink.SendEvent";
            _sendEventThread.Priority = ThreadPriority.AboveNormal;
            _sendEventThread.Start();
        }

        public void SendEvent(
            ServiceEventCode code,
            int state,
            int value0,
            int value1,
            string text)
        {
            SendEvent(code, state, value0, value1, text, GetQpcNow());
        }

        public void SendEvent(
            ServiceEventCode code,
            int state,
            int value0,
            int value1,
            string text,
            long qpcTimestamp)
        {
            ServiceEventPacket evt = new ServiceEventPacket
            {
                Sequence = 0,
                QpcTimestamp = qpcTimestamp,
                Code = (int)code,
                State = state,
                Value0 = value0,
                Value1 = value1,
                Text = text ?? string.Empty
            };

            lock (_queueSync)
            {
                _outboundEventQueue.Enqueue(evt);
            }

            _sendEventQueueHandle.Set();
        }

        public void Dispose()
        {
            _running = false;

            try
            {
                _stopEventHandle.Set();
                _sendEventQueueHandle.Set();
            }
            catch
            {
            }

            if (_receiveEventThread != null && _receiveEventThread.IsAlive)
                _receiveEventThread.Join(500);

            if (_sendEventThread != null && _sendEventThread.IsAlive)
                _sendEventThread.Join(500);

            _stopEventHandle.Dispose();
            _sendEventQueueHandle.Dispose();
            _serviceToHubEventHandle.Dispose();
            _hubToServiceEventHandle.Dispose();
            _accessor.Dispose();
            _mmf.Dispose();
        }

        private static long GetQpcNow()
        {
            long qpc;
            QueryPerformanceCounter(out qpc);
            return qpc;
        }

        private void EnsureInitialized()
        {
            lock (_ioSync)
            {
                ServiceEventHeader header;
                _accessor.Read(0, out header);

                bool needInit =
                    header.Magic != MagicValue ||
                    header.Version != VersionValue ||
                    header.EventCapacity != _eventCapacity;

                if (!needInit)
                    return;

                header = new ServiceEventHeader
                {
                    Magic = MagicValue,
                    Version = VersionValue,
                    EventCapacity = _eventCapacity,
                    Reserved0 = 0,

                    HubToServiceWriteSequence = 0,
                    HubToServiceOldestAvailableSequence = 1,
                    HubToServiceDroppedEventCount = 0,
                    Reserved1 = 0,

                    ServiceToHubWriteSequence = 0,
                    ServiceToHubOldestAvailableSequence = 1,
                    ServiceToHubDroppedEventCount = 0,
                    Reserved2 = 0
                };

                _accessor.Write(0, ref header);

                ServiceEventPacket empty = new ServiceEventPacket
                {
                    Sequence = 0,
                    QpcTimestamp = 0,
                    Code = (int)ServiceEventCode.None,
                    State = 0,
                    Value0 = 0,
                    Value1 = 0,
                    Text = string.Empty
                };

                for (int i = 0; i < _eventCapacity; i++)
                {
                    long offA = _hubToServiceBaseOffset + (long)i * _packetSize;
                    long offB = _serviceToHubBaseOffset + (long)i * _packetSize;

                    WriteServiceEventPacket(offA, empty);
                    WriteServiceEventPacket(offB, empty);
                }
            }
        }

        private void ReceiveEventLoop()
        {
            WaitHandle inboundEventHandle = _side == ServiceEventSide.Hub
                ? (WaitHandle)_serviceToHubEventHandle
                : (WaitHandle)_hubToServiceEventHandle;

            WaitHandle[] handles = new WaitHandle[] { inboundEventHandle, _stopEventHandle };

            try
            {
                DrainInboundEvents();

                while (_running)
                {
                    int signaled = WaitHandle.WaitAny(handles);

                    if (signaled == 1)
                        break;

                    DrainInboundEvents();
                }
            }
            catch
            {
            }
        }

        private void SendEventLoop()
        {
            WaitHandle[] handles = new WaitHandle[] { _sendEventQueueHandle, _stopEventHandle };

            try
            {
                while (_running)
                {
                    int signaled = WaitHandle.WaitAny(handles);

                    if (signaled == 1)
                        break;

                    while (true)
                    {
                        ServiceEventPacket evt;

                        lock (_queueSync)
                        {
                            if (_outboundEventQueue.Count == 0)
                                break;

                            evt = _outboundEventQueue.Dequeue();
                        }

                        PublishOutboundEvent(ref evt);
                    }
                }
            }
            catch
            {
            }
        }

        private void PublishOutboundEvent(ref ServiceEventPacket evt)
        {
            lock (_ioSync)
            {
                ServiceEventHeader header;
                _accessor.Read(0, out header);

                bool outboundHubToService = _side == ServiceEventSide.Hub;

                long writeSequence;
                long oldestAvailableSequence;
                int droppedEventCount;

                GetLaneState(
                    header,
                    outboundHubToService,
                    out writeSequence,
                    out oldestAvailableSequence,
                    out droppedEventCount);

                long nextSequence = writeSequence + 1;
                long newOldestAvailableSequence = nextSequence - _eventCapacity + 1;
                if (newOldestAvailableSequence < 1)
                    newOldestAvailableSequence = 1;

                if (newOldestAvailableSequence > oldestAvailableSequence)
                {
                    long lost = newOldestAvailableSequence - oldestAvailableSequence;
                    long totalDropped = (long)droppedEventCount + lost;
                    if (totalDropped > int.MaxValue)
                        totalDropped = int.MaxValue;

                    droppedEventCount = (int)totalDropped;
                    oldestAvailableSequence = newOldestAvailableSequence;
                }

                evt.Sequence = nextSequence;

                int slotIndex = (int)((nextSequence - 1) % _eventCapacity);
                long slotOffset = GetLaneBaseOffset(outboundHubToService) + (long)slotIndex * _packetSize;

                WriteServiceEventPacket(slotOffset, evt);

                SetLaneState(
                    ref header,
                    outboundHubToService,
                    nextSequence,
                    oldestAvailableSequence,
                    droppedEventCount);

                _accessor.Write(0, ref header);
            }

            if (_side == ServiceEventSide.Hub)
                _hubToServiceEventHandle.Set();
            else
                _serviceToHubEventHandle.Set();
        }

        private void DrainInboundEvents()
        {
            ServiceEventHeader header;

            lock (_ioSync)
            {
                _accessor.Read(0, out header);
            }

            bool inboundHubToService = _side == ServiceEventSide.Service;

            long writeSequence;
            long oldestAvailableSequence;
            int droppedEventCount;

            GetLaneState(
                header,
                inboundHubToService,
                out writeSequence,
                out oldestAvailableSequence,
                out droppedEventCount);

            if (_lastInboundDroppedEventCount != droppedEventCount)
            {
                _lastInboundDroppedEventCount = droppedEventCount;

                Action<int> droppedHandler = InboundDroppedEventCountChanged;
                if (droppedHandler != null)
                    droppedHandler(droppedEventCount);
            }

            if (writeSequence <= 0)
                return;

            if (_nextInboundEventSequence == 0)
                _nextInboundEventSequence = oldestAvailableSequence;

            if (_nextInboundEventSequence < oldestAvailableSequence)
                _nextInboundEventSequence = oldestAvailableSequence;

            for (long seq = _nextInboundEventSequence; seq <= writeSequence; seq++)
            {
                ServiceEventPacket evt;
                if (TryReadInboundEvent(seq, inboundHubToService, out evt))
                {
                    Action<ServiceEventPacket> handler = EventReceived;
                    if (handler != null)
                        handler(evt);
                }
            }

            _nextInboundEventSequence = writeSequence + 1;
        }

        private bool TryReadInboundEvent(long sequence, bool hubToServiceLane, out ServiceEventPacket evt)
        {
            evt = default(ServiceEventPacket);

            int slotIndex = (int)((sequence - 1) % _eventCapacity);
            long slotOffset = GetLaneBaseOffset(hubToServiceLane) + (long)slotIndex * _packetSize;

            lock (_ioSync)
            {
                evt = ReadServiceEventPacket(slotOffset);
            }

            return evt.Sequence == sequence;
        }

        private void WriteServiceEventPacket(long offset, ServiceEventPacket evt)
        {
            _accessor.Write(offset + SequenceOffset, evt.Sequence);
            _accessor.Write(offset + QpcTimestampOffset, evt.QpcTimestamp);
            _accessor.Write(offset + CodeOffset, evt.Code);
            _accessor.Write(offset + StateOffset, evt.State);
            _accessor.Write(offset + Value0Offset, evt.Value0);
            _accessor.Write(offset + Value1Offset, evt.Value1);

            char[] textBuffer = new char[TextCharCapacity];
            if (!string.IsNullOrEmpty(evt.Text))
            {
                int len = evt.Text.Length;
                if (len > TextCharCapacity)
                    len = TextCharCapacity;

                evt.Text.CopyTo(0, textBuffer, 0, len);
            }

            _accessor.WriteArray(offset + TextOffset, textBuffer, 0, TextCharCapacity);
        }

        private ServiceEventPacket ReadServiceEventPacket(long offset)
        {
            ServiceEventPacket evt = new ServiceEventPacket();

            evt.Sequence = _accessor.ReadInt64(offset + SequenceOffset);
            evt.QpcTimestamp = _accessor.ReadInt64(offset + QpcTimestampOffset);
            evt.Code = _accessor.ReadInt32(offset + CodeOffset);
            evt.State = _accessor.ReadInt32(offset + StateOffset);
            evt.Value0 = _accessor.ReadInt32(offset + Value0Offset);
            evt.Value1 = _accessor.ReadInt32(offset + Value1Offset);

            char[] textBuffer = new char[TextCharCapacity];
            _accessor.ReadArray(offset + TextOffset, textBuffer, 0, TextCharCapacity);

            int zeroIndex = Array.IndexOf(textBuffer, '\0');
            if (zeroIndex < 0)
                zeroIndex = TextCharCapacity;

            evt.Text = new string(textBuffer, 0, zeroIndex);

            return evt;
        }

        private long GetLaneBaseOffset(bool hubToServiceLane)
        {
            return hubToServiceLane ? _hubToServiceBaseOffset : _serviceToHubBaseOffset;
        }

        private static void GetLaneState(
            ServiceEventHeader header,
            bool hubToServiceLane,
            out long writeSequence,
            out long oldestAvailableSequence,
            out int droppedEventCount)
        {
            if (hubToServiceLane)
            {
                writeSequence = header.HubToServiceWriteSequence;
                oldestAvailableSequence = header.HubToServiceOldestAvailableSequence;
                droppedEventCount = header.HubToServiceDroppedEventCount;
            }
            else
            {
                writeSequence = header.ServiceToHubWriteSequence;
                oldestAvailableSequence = header.ServiceToHubOldestAvailableSequence;
                droppedEventCount = header.ServiceToHubDroppedEventCount;
            }
        }

        private static void SetLaneState(
            ref ServiceEventHeader header,
            bool hubToServiceLane,
            long writeSequence,
            long oldestAvailableSequence,
            int droppedEventCount)
        {
            if (hubToServiceLane)
            {
                header.HubToServiceWriteSequence = writeSequence;
                header.HubToServiceOldestAvailableSequence = oldestAvailableSequence;
                header.HubToServiceDroppedEventCount = droppedEventCount;
            }
            else
            {
                header.ServiceToHubWriteSequence = writeSequence;
                header.ServiceToHubOldestAvailableSequence = oldestAvailableSequence;
                header.ServiceToHubDroppedEventCount = droppedEventCount;
            }
        }
    }
}