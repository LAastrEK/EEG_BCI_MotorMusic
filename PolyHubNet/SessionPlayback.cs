using PolyHubIpc;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PolyHubNet
{
    public enum SessionPlaybackState
    {
        Stopped = 0,
        Playing = 1,
        Paused = 2
    }

    public sealed class SessionPlayback : IDisposable
    {
        private delegate void TimerEventHandler(int id, int msg, IntPtr user, int dw1, int dw2);

        private const int TIME_PERIODIC = 1;

        [DllImport("winmm.dll")]
        private static extern int timeSetEvent(int delay, int resolution, TimerEventHandler handler, IntPtr user, int eventType);

        [DllImport("winmm.dll")]
        private static extern int timeKillEvent(int id);

        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceCounter(out long value);

        private readonly object _sync = new object();
        private readonly object _eventSync = new object();
        private readonly TimerEventHandler _timerHandler;
        private readonly int _signalRingCapacity;
        private readonly int _eventRingCapacity;

        private ProcessorBlockRing _signalRing;
        private Thread _publisherThread;
        private ManualResetEvent _publisherStopEvent;

        private int _timerId;

        private SessionReader _reader;
        private SignalStream _playbackStream;
        private GlScope _glScope;
        private TimeFrequencyGlView _timeFrequencyGlView;

        private SessionPlaybackState _state;

        private int _nextBlockIndex;
        private int _nextEventIndex;
        private IList<EpochInfo> _rejectedEpochs;
        private int _nextRejectedEpoch;

        private long _localStartQpc;
        private long _sourceStartQpc;
        private long _sourceEndQpc;
        private long _pauseLocalQpc;

        private SessionUserEvent[] _eventRing;
        private int _eventWriteIndex;
        private int _eventReadIndex;
        private int _eventCount;

        private long _currentSourceQpc;

        public SessionPlaybackState State
        {
            get { return _state; }
        }

        public long CurrentSourceQpc
        {
            get
            {
                lock (_sync)
                {
                    return _currentSourceQpc;
                }
            }
        }

        public bool IsPlaying
        {
            get { return _state == SessionPlaybackState.Playing; }
        }

        public SessionPlayback()
            : this(32, 256)
        {
        }

        public SessionPlayback(int signalRingCapacity, int eventRingCapacity)
        {
            if (signalRingCapacity <= 1)
                throw new ArgumentOutOfRangeException("signalRingCapacity");
            if (eventRingCapacity <= 1)
                throw new ArgumentOutOfRangeException("eventRingCapacity");

            _signalRingCapacity = signalRingCapacity;
            _eventRingCapacity = eventRingCapacity;
            _eventRing = new SessionUserEvent[_eventRingCapacity];
            _timerHandler = PlaybackTimerCallback;
            _state = SessionPlaybackState.Stopped;
        }

        public void Start(SessionReader reader, SignalStream playbackStream, GlScope glScope, long sourceStartQpc, long sourceEndQpc,
            TimeFrequencyGlView timeFrequencyGlView = null, IList<EpochInfo> rejectedEpochs = null)
        {
            if (reader == null)
                throw new ArgumentNullException("reader");
            if (playbackStream == null)
                throw new ArgumentNullException("playbackStream");
            if (glScope == null)
                throw new ArgumentNullException("glScope");

            Stop();

            lock (_sync)
            {
                _reader = reader;
                _playbackStream = playbackStream;
                _glScope = glScope;
                _timeFrequencyGlView = timeFrequencyGlView;
                _rejectedEpochs = rejectedEpochs;
                _nextRejectedEpoch = 0;

                QueryPerformanceCounter(out _localStartQpc);
                _sourceStartQpc = sourceStartQpc <= 0 ? reader.GetFirstSourceQpc() : sourceStartQpc;
                _sourceEndQpc = sourceEndQpc <= 0 ? reader.GetLastSourceQpc() : sourceEndQpc;

                _currentSourceQpc = _sourceStartQpc;

                _nextBlockIndex = _reader.FindFirstBlockIndexAtOrAfter(_sourceStartQpc);
                _nextEventIndex = _reader.FindFirstEventIndexAtOrAfter(_sourceStartQpc);

                _eventWriteIndex = 0;
                _eventReadIndex = 0;
                _eventCount = 0;

                _signalRing = new ProcessorBlockRing(_signalRingCapacity);
                _publisherStopEvent = new ManualResetEvent(false);
                _publisherThread = new Thread(PublisherLoop);
                _publisherThread.IsBackground = true;
                _publisherThread.Name = "SessionPlayback.Publisher";
                _publisherThread.Priority = ThreadPriority.AboveNormal;
                _publisherThread.Start();

                _timerId = timeSetEvent(4, 1, _timerHandler, IntPtr.Zero, TIME_PERIODIC);
                _state = SessionPlaybackState.Playing;
            }
        }

        public void Pause()
        {
            lock (_sync)
            {
                if (_state != SessionPlaybackState.Playing)
                    return;

                QueryPerformanceCounter(out _pauseLocalQpc);
                _state = SessionPlaybackState.Paused;
            }
        }

        public void Play()
        {
            lock (_sync)
            {
                if (_state != SessionPlaybackState.Paused)
                    return;

                long qpcNow;
                QueryPerformanceCounter(out qpcNow);
                long pausedDelta = qpcNow - _pauseLocalQpc;
                _localStartQpc += pausedDelta;
                _state = SessionPlaybackState.Playing;
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                _state = SessionPlaybackState.Stopped;
                _currentSourceQpc = _sourceStartQpc;

                if (_timerId != 0)
                {
                    timeKillEvent(_timerId);
                    _timerId = 0;
                }
            }

            if (_publisherStopEvent != null)
            {
                try
                {
                    _publisherStopEvent.Set();
                }
                catch
                {
                }
            }

            if (_publisherThread != null && _publisherThread.IsAlive)
                _publisherThread.Join(500);

            if (_publisherStopEvent != null)
            {
                _publisherStopEvent.Dispose();
                _publisherStopEvent = null;
            }

            if (_signalRing != null)
            {
                _signalRing.Dispose();
                _signalRing = null;
            }
        }

        public bool TryDequeuePlayedEvent(out SessionUserEvent evt)
        {
            lock (_eventSync)
            {
                if (_eventCount <= 0)
                {
                    evt = default(SessionUserEvent);
                    return false;
                }

                evt = _eventRing[_eventReadIndex];
                _eventReadIndex++;
                if (_eventReadIndex >= _eventRingCapacity)
                    _eventReadIndex = 0;
                _eventCount--;
                return true;
            }
        }

        private void PublisherLoop()
        {
            WaitHandle[] handles = new WaitHandle[]
            {
                _signalRing.DataAvailableWaitHandle,
                _publisherStopEvent
            };

            try
            {
                while (true)
                {
                    int signaled = WaitHandle.WaitAny(handles);
                    if (signaled == 1)
                        break;

                    while (true)
                    {
                        SignalBlock block;
                        if (!_signalRing.TryDequeue(out block))
                            break;

                        if (_playbackStream != null)
                            _playbackStream.Publish(block);
                    }
                }
            }
            catch
            {
            }
        }

        private void PlaybackTimerCallback(int id, int msg, IntPtr user, int dw1, int dw2)
        {
            try
            {
                SessionReader reader;
                GlScope glScope;
                TimeFrequencyGlView timeFrequencyGlView;
                long localStartQpc;
                long sourceStartQpc;
                long sourceEndQpc;
                int nextBlockIndex;
                int nextEventIndex;

                lock (_sync)
                {
                    if (_state != SessionPlaybackState.Playing || _reader == null || _glScope == null || _signalRing == null)
                        return;

                    reader = _reader;
                    glScope = _glScope;
                    timeFrequencyGlView = _timeFrequencyGlView;
                    localStartQpc = _localStartQpc;
                    sourceStartQpc = _sourceStartQpc;
                    sourceEndQpc = _sourceEndQpc;
                    nextBlockIndex = _nextBlockIndex;
                    nextEventIndex = _nextEventIndex;
                }

                long localNowQpc;
                QueryPerformanceCounter(out localNowQpc);

                long elapsedLocalQpc = localNowQpc - localStartQpc;
                if (elapsedLocalQpc < 0)
                    elapsedLocalQpc = 0;

                long targetSourceQpc = sourceStartQpc + elapsedLocalQpc;

                if (targetSourceQpc > sourceEndQpc)
                    targetSourceQpc = sourceEndQpc;

                while (nextEventIndex < reader.EventCount)
                {
                    SessionUserEvent evt = reader.GetEventRecord(nextEventIndex);
                    if (evt.QpcTimestamp > targetSourceQpc)
                        break;

                    if (evt.QpcTimestamp <= sourceEndQpc &&
                        (ServiceEventCode)evt.Code == ServiceEventCode.UserEvent)
                    {
                        long localMarkerQpc = localStartQpc + (evt.QpcTimestamp - sourceStartQpc);
                        glScope.AddEventMarker(localMarkerQpc, evt.Value0, evt.State);
                        if (timeFrequencyGlView != null)
                            timeFrequencyGlView.AddEventMarker(localMarkerQpc, evt.Value0, evt.State);
                        EnqueuePlayedEvent(evt);
                    }

                    nextEventIndex++;
                }

                while (nextBlockIndex < reader.BlockCount)
                {
                    SessionBlockRecord block = reader.GetBlockIndexRecord(nextBlockIndex);
                    if (block.QpcTimestamp > targetSourceQpc)
                        break;

                    if (block.QpcTimestamp <= sourceEndQpc)
                    {
                        SignalBlock playbackBlock = new SignalBlock();
                        reader.ReadBlockInto(nextBlockIndex, playbackBlock);
                        playbackBlock.QpcTimestamp = localStartQpc + (playbackBlock.QpcTimestamp - sourceStartQpc);

                        _signalRing.Enqueue(playbackBlock);
                    }

                    nextBlockIndex++;
                }

                bool doneBlocks = nextBlockIndex >= reader.BlockCount ||
                                  (nextBlockIndex < reader.BlockCount &&
                                   reader.GetBlockIndexRecord(nextBlockIndex).QpcTimestamp > sourceEndQpc);

                bool doneEvents = nextEventIndex >= reader.EventCount ||
                                  (nextEventIndex < reader.EventCount &&
                                   reader.GetEventRecord(nextEventIndex).QpcTimestamp > sourceEndQpc);

                bool shouldStop = false;

                lock (_sync)
                {
                    while (_rejectedEpochs != null && _nextRejectedEpoch < _rejectedEpochs.Count &&
                        _rejectedEpochs[_nextRejectedEpoch].EndQpc <= targetSourceQpc)
                    {
                        EpochInfo epoch = _rejectedEpochs[_nextRejectedEpoch++];
                        if (epoch.EndQpc <= sourceStartQpc) continue;
                        long start = localStartQpc + Math.Max(0, epoch.StartQpc - sourceStartQpc);
                        long end = localStartQpc + epoch.EndQpc - sourceStartQpc;
                        glScope.AddRejectedInterval(start, end);
                        if (timeFrequencyGlView != null) timeFrequencyGlView.AddRejectedInterval(start, end);
                        bool recordedRejection = false;
                        for (int i = reader.FindFirstEventIndexAtOrAfter(epoch.EndQpc); i < reader.EventCount; i++)
                        {
                            SessionUserEvent evt = reader.GetEventRecord(i);
                            if (evt.QpcTimestamp != epoch.EndQpc) break;
                            if ((ServiceEventCode)evt.Code == ServiceEventCode.UserEvent &&
                                evt.Value0 == EogRejection.RejectedEventCode && evt.State == 0)
                            { recordedRejection = true; break; }
                        }
                        if (!recordedRejection && !CovertCalibrationEog.IsCovertClass(epoch.ClassId))
                        {
                            glScope.AddEventMarker(end, EogRejection.RejectedEventCode, 0);
                            if (timeFrequencyGlView != null) timeFrequencyGlView.AddEventMarker(end, EogRejection.RejectedEventCode, 0);
                        }
                    }
                    _nextBlockIndex = nextBlockIndex;
                    _nextEventIndex = nextEventIndex;

                    _currentSourceQpc = targetSourceQpc;

                    if (targetSourceQpc >= sourceEndQpc && doneBlocks && doneEvents)
                        shouldStop = true;
                }

                if (shouldStop)
                    Stop();
            }
            catch
            {
            }
        }

        private void EnqueuePlayedEvent(SessionUserEvent evt)
        {
            lock (_eventSync)
            {
                if (_eventCount >= _eventRingCapacity)
                {
                    _eventReadIndex++;
                    if (_eventReadIndex >= _eventRingCapacity)
                        _eventReadIndex = 0;
                    _eventCount--;
                }

                _eventRing[_eventWriteIndex] = evt;
                _eventWriteIndex++;
                if (_eventWriteIndex >= _eventRingCapacity)
                    _eventWriteIndex = 0;
                _eventCount++;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
