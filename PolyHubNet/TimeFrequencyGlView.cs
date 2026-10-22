using OpenTK;
using OpenTK.Graphics.OpenGL;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Windows.Forms;

namespace PolyHubNet
{
    public sealed class TimeFrequencyGlView : IDisposable
    {
        private const int FrequencyRows = TimeFrequencySpectrum.FrequencyRows;
        private const double MinFrequency = TimeFrequencySpectrum.MinFrequency;
        private const double MaxFrequency = TimeFrequencySpectrum.MaxFrequency;
        private const float PlotLeft = 70.0f;
        private const float PlotTop = 18.0f;
        private const float PlotRight = 10.0f;
        private const float PlotBottom = 24.0f;

        private sealed class PendingBlock
        {
            public SignalBlock Block;
            public long FirstSample;
            public int Generation;
        }

        private struct EventMarker
        {
            public long AbsoluteSample;
            public long QpcTimestamp;
            public int ClassId;
            public int State;
        }

        private readonly GLControl _glCtrl;
        private readonly long _qpcFrequency;
        private readonly List<EventMarker> _markers = new List<EventMarker>();
        private EventMarker[] _markersUi = new EventMarker[0];
        private long _lastDataEndQpc;
        private readonly object _sync = new object();
        private readonly Action _invalidateOnUiThreadAction;
        private readonly Queue<PendingBlock> _inputQueue = new Queue<PendingBlock>();
        private readonly List<SpectralSlice> _slices = new List<SpectralSlice>();
        private readonly AutoResetEvent _workAvailable = new AutoResetEvent(false);
        private readonly Thread _worker;
        private SignalStream _inputStream;
        private Action<SignalBlock> _blockHandler;
        private Action _namesHandler;
        private bool _disposed;
        private int _attachmentId;
        private int _generation;
        private int _inputChannelsCount;
        private int _inputSampleRate = 500;
        private long _queuedSamples;
        private long _totalReceivedSamples;
        private bool _keepRecordingChannelSelection;
        private long _dataVersion;
        private string _processingError;

        // These fields and all OpenGL objects belong exclusively to the UI thread.
        private bool _glInitialized;
        private int _uiRefreshPending;
        private long _lastRenderClock;
        private long _minimumFrameTicks = Stopwatch.Frequency / 30;
        private int _channelsCount;
        private int _sampleRate = 500;
        private int _timeBaseSeconds = 5;
        private float _scaleVolts = 10e-6f;
        private long _samplesPerPage = 2500;
        private long _latestDataSample;
        private int _viewGeneration = -1;
        private long _viewDataVersion = -1;
        private SpectralSlice[] _slicesUi = new SpectralSlice[0];
        private string[] _channelNames = new string[0];
        private string _statusText = "Waiting for data...";
        private bool _pagesDirty = true;
        private SpectralSlice _lastRenderedSlice;
        private int[] _spectralTex = new int[0];
        private Color[] _channelColors = new Color[0];
        private int _textureColumns;
        private int _textureHopSamples;
        private int _textureWindowSamples;
        private readonly byte[] _uploadColumn = new byte[FrequencyRows * 4];
        private readonly byte[] _blankColumn = new byte[FrequencyRows * 4];
        private const float DisplayGain = 20.0f;
        private int _overlayTextureId;
        private Bitmap _overlayBitmap;
        private bool _overlayDirty = true;
        private bool[] _selectedChannels = new bool[0];
        private int[] _visibleChannels = new int[0];
        private int[] _channelToVisibleIndex = new int[0];
        private bool _showSelectedOnly;

        public TimeFrequencyGlView(GLControl glCtrl, long qpcFrequency = 0)
        {
            if (glCtrl == null)
                throw new ArgumentNullException("glCtrl");
            _glCtrl = glCtrl;
            _qpcFrequency = qpcFrequency > 0 ? qpcFrequency : Stopwatch.Frequency;
            _glCtrl.Paint += glCtrl_Paint;
            _glCtrl.Resize += glCtrl_Resize;
            _glCtrl.MouseClick += glCtrl_MouseClick;
            _glCtrl.MouseDoubleClick += glCtrl_MouseDoubleClick;
            _invalidateOnUiThreadAction = InvalidateOnUiThread;
            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Name = "TimeFrequency.Worker";
            _worker.Start();
        }

        public void SetUiTimerPeriodMs(int uiTimerPeriodMs)
        {
            // The timer requests frames; it never advances the signal timeline.
            Interlocked.Exchange(ref _minimumFrameTicks, Math.Max(1, Stopwatch.Frequency *
                Math.Max(33, uiTimerPeriodMs) / 1000));
        }

        // Called on the UI thread by the existing status refresh.
        public bool TryGetCursorFrequency(out double frequency)
        {
            frequency = 0.0;
            if (_disposed || _glCtrl.IsDisposed || !_glCtrl.IsHandleCreated ||
                !_glCtrl.Visible || !_showSelectedOnly || _visibleChannels.Length != 1)
                return false;

            Point mousePoint = _glCtrl.PointToClient(Control.MousePosition);
            RectangleF rect = GetPlotRectangle();
            if (!_glCtrl.ClientRectangle.Contains(mousePoint) || !rect.Contains(mousePoint))
                return false;

            double maxFrequency = Math.Min(MaxFrequency, _sampleRate * 0.5);
            if (maxFrequency <= MinFrequency)
                return false;

            // Inverse of GetFrequencyY: the displayed frequency axis is logarithmic.
            double position = (rect.Bottom - mousePoint.Y) / rect.Height;
            frequency = MinFrequency * Math.Exp(position *
                (Math.Log(maxFrequency) - Math.Log(MinFrequency)));
            return true;
        }

        public void AttachInput(SignalStream input)
        {
            DetachInput();
            if (input == null)
                return;
            lock (_sync)
            {
                if (_disposed)
                    return;
                _inputStream = input;
                int attachment = _attachmentId;
                _blockHandler = block => QueueInputBlock(input, attachment, block);
                _namesHandler = () => RequestImmediateInvalidate();
                input.BlockPublished += _blockHandler;
                input.ChannelNamesChanged += _namesHandler;
            }
            InvalidateOverlay();
            RequestImmediateInvalidate();
        }

        public void DetachInput()
        {
            lock (_sync)
            {
                if (_inputStream != null)
                {
                    _inputStream.BlockPublished -= _blockHandler;
                    _inputStream.ChannelNamesChanged -= _namesHandler;
                }
                _inputStream = null;
                _blockHandler = null;
                _namesHandler = null;
                _attachmentId++;
                ResetStreamingState_NoLock();
            }
            RequestImmediateInvalidate();
        }

        public void Configure(int channelsCount, int sampleRate, int timeBaseSeconds, float scaleVolts)
        {
            lock (_sync)
            {
                _inputChannelsCount = Math.Max(channelsCount, 0);
                _inputSampleRate = sampleRate > 0 ? sampleRate : 500;
                _timeBaseSeconds = Math.Max(timeBaseSeconds, 1);
                ResetStreamingState_NoLock();
            }
            _scaleVolts = scaleVolts > 0 && !float.IsInfinity(scaleVolts) ? scaleVolts : 10e-6f;
            _pagesDirty = true;
            InvalidateOverlay();
            RequestImmediateInvalidate();
        }

        public void SetTimeBaseSeconds(int timeBaseSeconds)
        {
            int value = Math.Max(timeBaseSeconds, 1);
            lock (_sync)
            {
                if (_timeBaseSeconds == value)
                    return;
                _timeBaseSeconds = value;
                TrimSlices_NoLock();
                _dataVersion++;
            }
            _samplesPerPage = (long)_sampleRate * value;
            _pagesDirty = true;
            InvalidateOverlay();
            RequestImmediateInvalidate();
        }

        public void ShowRecordedWindow(SignalBlock block, long firstSample)
        {
            if (block == null || block.Data == null || block.ChannelCount <= 0 ||
                block.SampleRate <= 1 || block.SampleCount < 0 || firstSample < 0 ||
                (long)block.ChannelCount * block.SampleCount > block.Data.Length)
                return;
            lock (_sync)
            {
                if (_disposed)
                    return;
                _inputChannelsCount = block.ChannelCount;
                _inputSampleRate = block.SampleRate;
                ResetStreamingState_NoLock();
                _keepRecordingChannelSelection = true;
                _totalReceivedSamples = firstSample + block.SampleCount;
                if (block.SampleCount > 0)
                {
                    // A complete file window is one job; the live latency limit
                    // must not discard its FFT history during a seek.
                    _inputQueue.Enqueue(new PendingBlock
                    {
                        Block = block.Clone(),
                        FirstSample = firstSample,
                        Generation = _generation
                    });
                    _queuedSamples = block.SampleCount;
                    _workAvailable.Set();
                }
            }
            RequestImmediateInvalidate();
        }

        public void SetScaleVolts(float scaleVolts)
        {
            if (scaleVolts <= 0 || float.IsNaN(scaleVolts) || float.IsInfinity(scaleVolts))
                return;
            _scaleVolts = scaleVolts;
            // Recolour the stored amplitudes instead of discarding their history.
            _pagesDirty = true;
            InvalidateOverlay();
            RequestImmediateInvalidate();
        }

        public void AddEventMarker(long qpcTimestamp, int classId, int state)
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _markers.Add(new EventMarker
                {
                    AbsoluteSample = _lastDataEndQpc > 0 ? GetMarkerSample_NoLock(qpcTimestamp) : -1,
                    QpcTimestamp = qpcTimestamp,
                    ClassId = classId,
                    State = state
                });
                _dataVersion++;
            }
            RequestImmediateInvalidate();
        }

        public void AddRejectedInterval(long startQpc, long endQpc)
        {
            lock (_sync)
            {
                if (_disposed || _lastDataEndQpc <= 0 || _inputSampleRate <= 0) return;
                AddRejectedSamples_NoLock(GetMarkerSample_NoLock(startQpc), GetMarkerSample_NoLock(endQpc));
            }
            RequestImmediateInvalidate();
        }

        public void AddRecordedRejectedInterval(long firstSample, long lastSample)
        {
            lock (_sync)
            {
                if (_disposed) return;
                AddRejectedSamples_NoLock(Math.Max(0, firstSample), lastSample);
            }
            RequestImmediateInvalidate();
        }

        private void AddRejectedSamples_NoLock(long first, long last)
        {
            if (last < first) return;
            for (int i = _markers.Count - 1; i >= 0; i--)
            {
                EventMarker marker = _markers[i];
                if (marker.ClassId != EogRejection.RejectedEventCode || marker.State >= 0) continue;
                long start = marker.AbsoluteSample + marker.State + 1;
                if (marker.AbsoluteSample + 1 < first || start > last + 1) continue;
                first = Math.Min(first, start); last = Math.Max(last, marker.AbsoluteSample);
                _markers.RemoveAt(i);
            }
            _markers.Add(new EventMarker { AbsoluteSample = last,
                ClassId = EogRejection.RejectedEventCode, State = -(int)Math.Min(int.MaxValue, last - first + 1) });
            _dataVersion++;
        }

        public void AddRecordedEventMarker(long absoluteSample, int classId, int state)
        {
            lock (_sync)
            {
                if (_disposed || absoluteSample < 0)
                    return;
                _markers.Add(new EventMarker { AbsoluteSample = absoluteSample, ClassId = classId, State = state });
                _dataVersion++;
            }
            RequestImmediateInvalidate();
        }

        private long GetMarkerSample_NoLock(long qpcTimestamp)
        {
            // Use the same timestamp-to-sample conversion as TimeSeries.
            return Math.Max(0, (long)Math.Round(_totalReceivedSamples - 1 -
                (double)(_lastDataEndQpc - qpcTimestamp) * _inputSampleRate / _qpcFrequency));
        }

        public void OnLiveRenderClockTick()
        {
            long now = Stopwatch.GetTimestamp();
            long previous = Interlocked.Read(ref _lastRenderClock);
            if (now - previous < Interlocked.Read(ref _minimumFrameTicks))
                return;
            Interlocked.Exchange(ref _lastRenderClock, now);
            EnqueueInvalidate();
        }

        private void ResetStreamingState_NoLock()
        {
            _generation++;
            _inputQueue.Clear();
            _queuedSamples = 0;
            _totalReceivedSamples = 0;
            _keepRecordingChannelSelection = false;
            _slices.Clear();
            _markers.Clear();
            _lastDataEndQpc = 0;
            _processingError = null;
            _dataVersion++;
        }

        private void QueueInputBlock(SignalStream source, int attachment, SignalBlock block)
        {
            if (block == null || block.Data == null || block.ChannelCount <= 0 ||
                block.SampleRate <= 0 || block.SampleCount <= 0 ||
                (long)block.ChannelCount * block.SampleCount > block.Data.Length)
                return;
            lock (_sync)
            {
                if (_disposed || attachment != _attachmentId || !ReferenceEquals(source, _inputStream))
                    return;
                if (_inputChannelsCount != block.ChannelCount || _inputSampleRate != block.SampleRate)
                {
                    EventMarker[] waitingMarkers = _lastDataEndQpc == 0 ? _markers.ToArray() : null;
                    _inputChannelsCount = block.ChannelCount;
                    _inputSampleRate = block.SampleRate;
                    ResetStreamingState_NoLock();
                    if (waitingMarkers != null)
                        _markers.AddRange(waitingMarkers);
                }
                PendingBlock pending = new PendingBlock
                {
                    // Publishers may reuse their arrays after returning from Publish.
                    Block = block.Clone(),
                    FirstSample = _totalReceivedSamples,
                    Generation = _generation
                };
                _totalReceivedSamples += block.SampleCount;
                _lastDataEndQpc = block.QpcTimestamp +
                    (long)((double)block.SampleCount * _qpcFrequency / block.SampleRate);
                // Events may arrive before the first signal block during playback.
                for (int i = 0; i < _markers.Count; i++)
                {
                    EventMarker marker = _markers[i];
                    if (marker.AbsoluteSample >= 0)
                        continue;
                    marker.AbsoluteSample = GetMarkerSample_NoLock(marker.QpcTimestamp);
                    _markers[i] = marker;
                    _dataVersion++;
                }
                _queuedSamples += block.SampleCount;
                _inputQueue.Enqueue(pending);
                // Bound latency. A dropped interval remains a gap on the time axis.
                long limit = Math.Max(2L * block.SampleRate, block.SampleCount);
                while (_queuedSamples > limit && _inputQueue.Count > 1)
                    _queuedSamples -= _inputQueue.Dequeue().Block.SampleCount;
                _workAvailable.Set();
            }
        }

        private void WorkerLoop()
        {
            TimeFrequencySpectrum spectrum = null;
            int engineGeneration = -1;
            while (true)
            {
                _workAvailable.WaitOne();
                while (true)
                {
                    PendingBlock pending;
                    lock (_sync)
                    {
                        if (_disposed)
                            return;
                        if (_inputQueue.Count == 0)
                            break;
                        pending = _inputQueue.Dequeue();
                        _queuedSamples -= pending.Block.SampleCount;
                    }
                    try
                    {
                        if (engineGeneration != pending.Generation)
                        {
                            spectrum = new TimeFrequencySpectrum(pending.Block.ChannelCount, pending.Block.SampleRate);
                            engineGeneration = pending.Generation;
                        }
                        List<SpectralSlice> completed = spectrum.Append(pending.Block, pending.FirstSample,
                            () => Volatile.Read(ref _disposed) || pending.Generation != Volatile.Read(ref _generation));
                        lock (_sync)
                        {
                            if (_disposed)
                                return;
                            if (pending.Generation != _generation)
                                continue;
                            if (completed.Count > 0)
                            {
                                _slices.AddRange(completed);
                                TrimSlices_NoLock();
                                _dataVersion++;
                            }
                            _processingError = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        engineGeneration = -1;
                        lock (_sync)
                        {
                            if (pending.Generation == _generation)
                            {
                                _processingError = "Spectrum: " + ex.Message;
                                _dataVersion++;
                            }
                        }
                    }
                }
            }
        }

        private void TrimSlices_NoLock()
        {
            long oldest = _totalReceivedSamples - (long)_inputSampleRate * Math.Max(5, _timeBaseSeconds);
            _markers.RemoveAll(marker => marker.AbsoluteSample >= 0 && marker.AbsoluteSample < oldest);
            int remove = 0;
            // Keep one preceding slice for interpolation at the left edge.
            while (remove + 1 < _slices.Count && _slices[remove + 1].EndAbsoluteSample < oldest)
                remove++;
            if (remove > 0)
                _slices.RemoveRange(0, remove);
        }

        private void UpdateSnapshot()
        {
            string status;
            lock (_sync)
            {
                if (_viewGeneration != _generation)
                {
                    _viewGeneration = _generation;
                    bool keepSelection = _keepRecordingChannelSelection && _channelsCount == _inputChannelsCount;
                    _channelsCount = _inputChannelsCount;
                    _sampleRate = _inputSampleRate;
                    _samplesPerPage = (long)_sampleRate * _timeBaseSeconds;
                    if (!keepSelection)
                        ResetChannelSelection_NoLock();
                    _pagesDirty = true;
                    _viewDataVersion = -1;
                    InvalidateOverlay();
                }
                if (_viewDataVersion != _dataVersion)
                {
                    _slicesUi = _slices.ToArray();
                    _markersUi = _markers.ToArray();
                    _viewDataVersion = _dataVersion;
                }
                _latestDataSample = _totalReceivedSamples;
                string[] names = _inputStream != null ? _inputStream.ChannelNames : new string[0];
                if (!ReferenceEquals(names, _channelNames))
                {
                    _channelNames = names;
                    InvalidateOverlay();
                }
                status = _processingError ?? (_latestDataSample == 0 ? "Waiting for data..." :
                    (_slicesUi.Length == 0 ? "Collecting spectrum..." : ""));
            }
            if (_statusText != status)
            {
                _statusText = status;
                InvalidateOverlay();
            }
        }

        private void InvalidateOnUiThread()
        {
            try
            {
                if (!_glCtrl.IsDisposed && _glCtrl.IsHandleCreated && _glCtrl.Visible)
                    _glCtrl.Invalidate();
            }
            finally { Interlocked.Exchange(ref _uiRefreshPending, 0); }
        }

        private void EnqueueInvalidate()
        {
            if (_glCtrl.IsDisposed || !_glCtrl.IsHandleCreated || !_glCtrl.Visible)
                return;
            if (Interlocked.Exchange(ref _uiRefreshPending, 1) != 0)
                return;
            try
            {
                if (_glCtrl.InvokeRequired)
                    _glCtrl.BeginInvoke(_invalidateOnUiThreadAction);
                else
                    _invalidateOnUiThreadAction();
            }
            catch (InvalidOperationException) { Interlocked.Exchange(ref _uiRefreshPending, 0); }
        }

        private void RequestImmediateInvalidate() { EnqueueInvalidate(); }

        private void EnsureGlInitialized()
        {
            if (_glInitialized)
                return;

            if (!_glCtrl.IsHandleCreated)
                return;

            _glCtrl.MakeCurrent();

            GL.ClearColor(Color.Black);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Lighting);
            GL.Disable(EnableCap.CullFace);
            GL.Enable(EnableCap.Texture2D);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Disable(EnableCap.LineSmooth);
            GL.Disable(EnableCap.PointSmooth);
            GL.ShadeModel(ShadingModel.Smooth);

            SetupViewport();
            _glInitialized = true;
        }

        private void SetupViewport()
        {
            int w = _glCtrl.Width > 0 ? _glCtrl.Width : 1;
            int h = _glCtrl.Height > 0 ? _glCtrl.Height : 1;

            GL.Viewport(0, 0, w, h);
            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadIdentity();
            GL.Ortho(0.0, w, h, 0.0, -1.0, 1.0);
            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadIdentity();
        }

        private void glCtrl_Paint(object sender, PaintEventArgs e)
        {
            RenderFrame();
        }

        private void glCtrl_Resize(object sender, EventArgs e)
        {
            InvalidateOverlay();
            RequestImmediateInvalidate();
        }

        private void glCtrl_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;

            RectangleF rect = GetPlotRectangle();
            if (e.X < 0 || e.X >= rect.Left || e.Y < rect.Top || e.Y >= rect.Bottom)
                return;

            lock (_sync)
            {
                if (_visibleChannels == null || _visibleChannels.Length == 0)
                    return;

                float bandHeight = rect.Height / _visibleChannels.Length;
                int visibleIndex = (int)((e.Y - rect.Top) / bandHeight);
                if (visibleIndex < 0 || visibleIndex >= _visibleChannels.Length)
                    return;

                int channelIndex = _visibleChannels[visibleIndex];
                if (channelIndex < 0 || channelIndex >= _selectedChannels.Length)
                    return;

                _selectedChannels[channelIndex] = !_selectedChannels[channelIndex];

                if (_showSelectedOnly)
                {
                    if (GetSelectedChannelCount_NoLock() == 0)
                        _showSelectedOnly = false;

                }

                RebuildVisibleChannels_NoLock();
            }

            InvalidateOverlay();
            RequestImmediateInvalidate();
        }

        private void glCtrl_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;

            RectangleF rect = GetPlotRectangle();
            if (e.X < rect.Left || e.X >= rect.Right || e.Y < rect.Top || e.Y >= rect.Bottom)
                return;

            lock (_sync)
            {
                if (GetSelectedChannelCount_NoLock() == 0)
                    return;

                _showSelectedOnly = !_showSelectedOnly;
                RebuildVisibleChannels_NoLock();
            }

            InvalidateOverlay();
            RequestImmediateInvalidate();
        }

        private void DestroySpectralTextures()
        {
            for (int ch = 0; ch < _spectralTex.Length; ch++)
                if (_spectralTex[ch] != 0)
                    GL.DeleteTexture(_spectralTex[ch]);
            _spectralTex = new int[0];
            _lastRenderedSlice = null;
        }

        private void EnsureSpectralTextures()
        {
            if (!_pagesDirty && _spectralTex.Length == _channelsCount)
                return;
            DestroySpectralTextures();
            _textureHopSamples = Math.Max(1, (int)Math.Round(_sampleRate * 0.04));
            _textureWindowSamples = TimeFrequencySpectrum.GetFftSize(_sampleRate);
            // Two extra columns keep interpolation neighbours outside the visible interval.
            _textureColumns = (int)Math.Ceiling(_samplesPerPage / (double)_textureHopSamples) + 2;
            _spectralTex = new int[_channelsCount];
            _channelColors = new Color[_channelsCount];
            byte[] empty = new byte[checked(_textureColumns * FrequencyRows * 4)];
            for (int i = 3; i < empty.Length; i += 4)
                empty[i] = 255;
            for (int i = 3; i < _blankColumn.Length; i += 4)
                _blankColumn[i] = 255;
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
            for (int ch = 0; ch < _channelsCount; ch++)
            {
                _channelColors[ch] = GlScope.GetChannelColor(ch);
                int texture = GL.GenTexture();
                _spectralTex[ch] = texture;
                GL.BindTexture(TextureTarget.Texture2D, texture);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                    _textureColumns, FrequencyRows, 0, OpenTK.Graphics.OpenGL.PixelFormat.Rgba,
                    PixelType.UnsignedByte, empty);
            }
            GL.BindTexture(TextureTarget.Texture2D, 0);
            _pagesDirty = false;
        }

        private long GetTextureColumn(long endSample)
        {
            return (endSample - _textureWindowSamples) / _textureHopSamples;
        }

        private void UploadBlankColumn(long column)
        {
            int x = (int)(column % _textureColumns);
            for (int ch = 0; ch < _spectralTex.Length; ch++)
            {
                GL.BindTexture(TextureTarget.Texture2D, _spectralTex[ch]);
                GL.TexSubImage2D(TextureTarget.Texture2D, 0, x, 0, 1, FrequencyRows,
                    OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, _blankColumn);
            }
        }

        private void UploadPendingSpectra()
        {
            long oldest = Math.Max(0, _latestDataSample - _samplesPerPage - 2L * _textureHopSamples);
            for (int i = 0; i < _slicesUi.Length; i++)
            {
                SpectralSlice slice = _slicesUi[i];
                if (slice.EndAbsoluteSample < oldest ||
                    (_lastRenderedSlice != null && slice.EndAbsoluteSample <= _lastRenderedSlice.EndAbsoluteSample))
                    continue;
                long column = GetTextureColumn(slice.EndAbsoluteSample);
                if (_lastRenderedSlice != null)
                {
                    long previousColumn = GetTextureColumn(_lastRenderedSlice.EndAbsoluteSample);
                    // Queue overruns remain black intervals; stale columns cannot reappear.
                    long firstBlank = Math.Max(previousColumn + 1, column - _textureColumns + 1);
                    for (long missing = firstBlank; missing < column; missing++)
                        UploadBlankColumn(missing);
                }
                int x = (int)(column % _textureColumns);
                for (int ch = 0; ch < _spectralTex.Length; ch++)
                {
                    Color color = _channelColors[ch];
                    int offset = ch * FrequencyRows;
                    for (int row = 0; row < FrequencyRows; row++)
                    {
                        float intensity = GetIntensity(slice.Amplitudes[offset + row], _scaleVolts);
                        _uploadColumn[row * 4] = (byte)Math.Round(color.R * intensity);
                        _uploadColumn[row * 4 + 1] = (byte)Math.Round(color.G * intensity);
                        _uploadColumn[row * 4 + 2] = (byte)Math.Round(color.B * intensity);
                        _uploadColumn[row * 4 + 3] = 255;
                    }
                    GL.BindTexture(TextureTarget.Texture2D, _spectralTex[ch]);
                    GL.TexSubImage2D(TextureTarget.Texture2D, 0, x, 0, 1, FrequencyRows,
                        OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, _uploadColumn);
                }
                _lastRenderedSlice = slice;
            }
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        internal static float GetIntensity(float amplitude, float scaleVolts)
        {
            return TimeFrequencySpectrum.GetIntensity(amplitude, scaleVolts);
        }

        private void RenderFrame()
        {
            if (!_glInitialized)
                EnsureGlInitialized();
            if (!_glInitialized || _glCtrl.IsDisposed || !_glCtrl.IsHandleCreated)
                return;
            _glCtrl.MakeCurrent();
            UpdateSnapshot();
            EnsureSpectralTextures();
            UploadPendingSpectra();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            SetupViewport();
            GL.DrawBuffer(DrawBufferMode.Back);
            GL.ClearColor(Color.Black);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            GL.Enable(EnableCap.Blend);
            DrawSpectralTextures();
            DrawEventMarkers();
            DrawOverlayTexture();
            _glCtrl.SwapBuffers();
        }

        private void DrawSpectralTextures()
        {
            if (_lastRenderedSlice == null || _visibleChannels.Length == 0)
                return;
            RectangleF rect = GetPlotRectangle();
            long leftSample = _latestDataSample - _samplesPerPage;
            long firstSample = Math.Max(leftSample, _textureWindowSamples - _textureHopSamples);
            long lastSample = Math.Min(_latestDataSample, _lastRenderedSlice.EndAbsoluteSample);
            if (lastSample <= firstSample)
                return;
            float left = rect.Left + (float)((firstSample - leftSample) * (double)rect.Width / _samplesPerPage);
            float right = rect.Left + (float)((lastSample - leftSample) * (double)rect.Width / _samplesPerPage);
            // A spectrum's timestamp maps exactly to the centre of its texture column.
            double u = (firstSample - _textureWindowSamples) / (double)_textureHopSamples + 0.5;
            u -= Math.Floor(u / _textureColumns) * _textureColumns;
            float uLeft = (float)(u / _textureColumns);
            float uRight = uLeft + (float)((lastSample - firstSample) /
                ((double)_textureColumns * _textureHopSamples));
            float vBottom = 0.5f / FrequencyRows;
            float vTop = 1.0f - vBottom;
            float bandHeight = rect.Height / _visibleChannels.Length;
            BeginPlotScissor(rect);
            GL.Disable(EnableCap.Blend);
            GL.Enable(EnableCap.Texture2D);
            GL.Color4(1.0f, 1.0f, 1.0f, 1.0f);
            for (int visible = 0; visible < _visibleChannels.Length; visible++)
            {
                int ch = _visibleChannels[visible];
                float top = rect.Top + visible * bandHeight;
                float bottom = top + bandHeight;
                GL.BindTexture(TextureTarget.Texture2D, _spectralTex[ch]);
                GL.Begin(PrimitiveType.Quads);
                GL.TexCoord2(uLeft, vTop); GL.Vertex2(left, top);
                GL.TexCoord2(uRight, vTop); GL.Vertex2(right, top);
                GL.TexCoord2(uRight, vBottom); GL.Vertex2(right, bottom);
                GL.TexCoord2(uLeft, vBottom); GL.Vertex2(left, bottom);
                GL.End();
            }
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.Enable(EnableCap.Blend);
            EndPlotScissor();
        }

        private void DrawEventMarkers()
        {
            if (_markersUi.Length == 0)
                return;
            RectangleF rect = GetPlotRectangle();
            long leftSample = _latestDataSample - _samplesPerPage;
            BeginPlotScissor(rect);
            GL.Disable(EnableCap.Texture2D);
            // Dim the spectrum first; original markers are drawn over it.
            for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < _markersUi.Length; i++)
            {
                EventMarker marker = _markersUi[i];
                bool rejectedRange = marker.ClassId == EogRejection.RejectedEventCode && marker.State < 0;
                if (rejectedRange != (pass == 0)) continue;
                if (rejectedRange)
                {
                    long firstSample = marker.AbsoluteSample + marker.State + 1;
                    if (marker.AbsoluteSample < leftSample || firstSample > _latestDataSample) continue;
                    float first = rect.Left + (float)((Math.Max(firstSample, leftSample) - leftSample) * (double)rect.Width / _samplesPerPage);
                    float last = rect.Left + (float)((Math.Min(marker.AbsoluteSample, _latestDataSample) - leftSample) * (double)rect.Width / _samplesPerPage);
                    bool blendWasEnabled = GL.IsEnabled(EnableCap.Blend);
                    GL.Enable(EnableCap.Blend);
                    GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                    GL.Color4(0.0f, 0.0f, 0.0f, 0.65f);
                    GL.Begin(PrimitiveType.Quads);
                    GL.Vertex2(first, rect.Top); GL.Vertex2(last, rect.Top);
                    GL.Vertex2(last, rect.Bottom); GL.Vertex2(first, rect.Bottom);
                    GL.End();
                    GlScope.DrawEventLabelCore("Rejected", last - 8.0f, rect.Top + 18.0f, rect.Left, rect.Right, Color.Gray);
                    if (!blendWasEnabled) GL.Disable(EnableCap.Blend);
                    continue;
                }
                if (marker.AbsoluteSample < 0 || marker.AbsoluteSample < leftSample ||
                    marker.AbsoluteSample > _latestDataSample)
                    continue;
                float x = (float)Math.Round(rect.Left +
                    (marker.AbsoluteSample - leftSample) * (double)rect.Width / _samplesPerPage);
                Color color = marker.State == 1 ? Color.Yellow : Color.OrangeRed;
                GL.Color3(color);
                GL.Begin(PrimitiveType.Quads);
                GL.Vertex2(x - 0.5f, rect.Top);
                GL.Vertex2(x + 1.5f, rect.Top);
                GL.Vertex2(x + 1.5f, rect.Bottom);
                GL.Vertex2(x - 0.5f, rect.Bottom);
                GL.End();
                GlScope.DrawEventLabelCore(marker.ClassId.ToString(), x - 0.5f - 8.0f,
                    rect.Top + 2.0f, rect.Left, rect.Right, color);
            }
            GL.Enable(EnableCap.Texture2D);
            EndPlotScissor();
        }

        private void BeginPlotScissor(RectangleF rect)
        {
            int x = Math.Max((int)Math.Floor(rect.Left), 0);
            int yTop = Math.Max((int)Math.Floor(rect.Top), 0);
            int width = Math.Max((int)Math.Ceiling(rect.Width), 1);
            int height = Math.Max((int)Math.Ceiling(rect.Height), 1);

            int glY = _glCtrl.Height - (yTop + height);
            if (glY < 0)
                glY = 0;

            GL.Enable(EnableCap.ScissorTest);
            GL.Scissor(x, glY, width, height);
        }

        private void EndPlotScissor()
        {
            GL.Disable(EnableCap.ScissorTest);
        }

        private RectangleF GetPlotRectangle()
        {
            float width = _glCtrl.Width - PlotLeft - PlotRight;
            float height = _glCtrl.Height - PlotTop - PlotBottom;

            if (width < 10.0f) width = 10.0f;
            if (height < 10.0f) height = 10.0f;

            return new RectangleF(PlotLeft, PlotTop, width, height);
        }

        private void ResetChannelSelection_NoLock()
        {
            _selectedChannels = new bool[Math.Max(_channelsCount, 0)];
            _showSelectedOnly = false;
            RebuildVisibleChannels_NoLock();
        }

        private int GetSelectedChannelCount_NoLock()
        {
            int count = 0;

            if (_selectedChannels == null)
                return 0;

            for (int i = 0; i < _selectedChannels.Length; i++)
            {
                if (_selectedChannels[i])
                    count++;
            }

            return count;
        }

        private void RebuildVisibleChannels_NoLock()
        {
            int channelsCount = Math.Max(_channelsCount, 0);
            _channelToVisibleIndex = new int[channelsCount];
            for (int i = 0; i < channelsCount; i++)
                _channelToVisibleIndex[i] = -1;

            if (!_showSelectedOnly)
            {
                _visibleChannels = new int[channelsCount];
                for (int ch = 0; ch < channelsCount; ch++)
                {
                    _visibleChannels[ch] = ch;
                    _channelToVisibleIndex[ch] = ch;
                }
                return;
            }

            int selectedCount = GetSelectedChannelCount_NoLock();
            if (selectedCount <= 0)
            {
                _showSelectedOnly = false;
                RebuildVisibleChannels_NoLock();
                return;
            }

            _visibleChannels = new int[selectedCount];
            int visibleIndex = 0;

            for (int ch = 0; ch < channelsCount; ch++)
            {
                if (!_selectedChannels[ch])
                    continue;

                _visibleChannels[visibleIndex] = ch;
                _channelToVisibleIndex[ch] = visibleIndex;
                visibleIndex++;
            }
        }

        private bool IsChannelSelected(int channelIndex)
        {
            lock (_sync)
            {
                return _selectedChannels != null &&
                       channelIndex >= 0 &&
                       channelIndex < _selectedChannels.Length &&
                       _selectedChannels[channelIndex];
            }
        }

        private void InvalidateOverlay()
        {
            _overlayDirty = true;
        }

        private Bitmap BuildOverlayBitmap()
        {
            int w = Math.Max(_glCtrl.Width, 1);
            int h = Math.Max(_glCtrl.Height, 1);

            Bitmap bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            using (Graphics g = Graphics.FromImage(bmp))
            using (Font font = new Font("Segoe UI", 8.0f, FontStyle.Regular, GraphicsUnit.Point))
            using (Font selectedFont = new Font("Segoe UI", 8.0f, FontStyle.Bold, GraphicsUnit.Point))
            using (Font frequencyFont = new Font("Segoe UI", 7.0f, FontStyle.Regular, GraphicsUnit.Point))
            using (Pen borderPen = new Pen(Color.FromArgb(45, 45, 45), 1.0f))
            using (Pen timeGridPen = new Pen(Color.FromArgb(22, 22, 22), 1.0f))
            using (Pen bandPen = new Pen(Color.FromArgb(18, 18, 18), 1.0f))
            using (Pen frequencyPen = new Pen(Color.FromArgb(16, 16, 16), 1.0f))
            using (Brush brushChannels = new SolidBrush(Color.LightGray))
            using (Brush brushSelectedChannels = new SolidBrush(Color.White))
            using (Brush brushTime = new SolidBrush(Color.Gray))
            using (Brush brushFrequency = new SolidBrush(Color.FromArgb(105, 105, 105)))
            using (StringFormat sfCenter = new StringFormat())
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.None;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;

                sfCenter.Alignment = StringAlignment.Center;
                sfCenter.LineAlignment = StringAlignment.Near;

                RectangleF rect = GetPlotRectangle();
                g.DrawRectangle(borderPen, rect.X, rect.Y, rect.Width, rect.Height);

                int timeStep = Math.Max(1, (int)Math.Ceiling(_timeBaseSeconds /
                    Math.Max(1.0, rect.Width / 65.0)));
                for (int sec = 0; sec <= _timeBaseSeconds; sec += timeStep)
                {
                    float x = rect.Right - rect.Width * sec / _timeBaseSeconds;
                    g.DrawLine(timeGridPen, x, rect.Top, x, rect.Bottom);
                }

                int[] visibleChannels = _visibleChannels;
                if (visibleChannels != null && visibleChannels.Length > 0)
                {
                    float bandHeight = rect.Height / visibleChannels.Length;

                    for (int visibleIndex = 0; visibleIndex < visibleChannels.Length; visibleIndex++)
                    {
                        int ch = visibleChannels[visibleIndex];
                        float bandTop = rect.Top + visibleIndex * bandHeight;
                        float bandBottom = bandTop + bandHeight;

                        if (visibleIndex > 0)
                            g.DrawLine(bandPen, rect.Left, bandTop, rect.Right, bandTop);

                        string channelName = "Ch " + (ch + 1).ToString();
                        if (ch < _channelNames.Length && !string.IsNullOrWhiteSpace(_channelNames[ch]))
                            channelName = _channelNames[ch];

                        bool selected = IsChannelSelected(ch);
                        Font channelFont = selected ? selectedFont : font;
                        Brush channelBrush = selected ? brushSelectedChannels : brushChannels;

                        using (Brush channelColorBrush = new SolidBrush(GlScope.GetChannelColor(ch)))
                            g.FillRectangle(channelColorBrush, rect.Left - 5.0f, bandTop + 1.0f, 3.0f,
                                Math.Max(1.0f, bandHeight - 2.0f));
                        using (StringFormat channelFormat = new StringFormat())
                        {
                            channelFormat.Trimming = StringTrimming.EllipsisCharacter;
                            channelFormat.FormatFlags = StringFormatFlags.NoWrap;
                            channelFormat.LineAlignment = StringAlignment.Center;
                            g.DrawString(channelName, channelFont, channelBrush,
                                new RectangleF(4.0f, bandTop, PlotLeft - 12.0f, bandHeight), channelFormat);
                        }

                        if (bandHeight >= 70.0f)
                            DrawFrequencyGuides(g, frequencyFont, frequencyPen, brushFrequency, rect, bandTop, bandBottom);
                    }
                }

                for (int sec2 = 0; sec2 <= _timeBaseSeconds; sec2 += timeStep)
                {
                    float x2 = rect.Right - rect.Width * sec2 / _timeBaseSeconds;
                    string text = (-sec2).ToString() + " s";

                    RectangleF textRect = new RectangleF(
                        x2 - 20.0f,
                        rect.Bottom + 2.0f,
                        40.0f,
                        font.Height + 2.0f);

                    g.DrawString(text, font, brushTime, textRect, sfCenter);
                }

/*                g.DrawString(
                    MinFrequency.ToString("0.#") + "-" + Math.Min(MaxFrequency, _sampleRate * 0.5).ToString("0.#") + " Hz",
                    font,
                    brushTime,
                    (float)Math.Round(rect.Left),
                    1.0f);*/
                if (!string.IsNullOrEmpty(_statusText))
                    g.DrawString(_statusText, font, brushTime,
                        new RectangleF(rect.Left, rect.Top + rect.Height * 0.5f, rect.Width, font.Height * 2), sfCenter);
/*                string scaleText = (_scaleVolts * 1e6).ToString("0.###") + " µV | ×" + DisplayGain.ToString("0");
                g.DrawString(scaleText, font, brushTime, rect.Right - g.MeasureString(scaleText, font).Width, 1.0f);*/
            }

            return bmp;
        }

        private void DrawFrequencyGuides(
            Graphics g,
            Font font,
            Pen pen,
            Brush brush,
            RectangleF rect,
            float bandTop,
            float bandBottom)
        {
            double[] frequencies = new double[] { 1.0, 2.0, 5.0, 10.0, 20.0, 50.0 };

            for (int i = 0; i < frequencies.Length; i++)
            {
                double frequency = frequencies[i];
                if (frequency < MinFrequency || frequency > Math.Min(MaxFrequency, _sampleRate * 0.5))
                    continue;

                float y = GetFrequencyY(frequency, bandTop, bandBottom);
                g.DrawLine(pen, rect.Left, y, rect.Right, y);
                g.DrawString(
                    frequency.ToString("0.#"),
                    font,
                    brush,
                    rect.Left + 2.0f,
                    y - font.Height * 0.5f);
            }
        }

        private float GetFrequencyY(double frequency, float top, float bottom)
        {
            double normalized =
                (Math.Log(frequency) - Math.Log(MinFrequency)) /
                (Math.Log(Math.Min(MaxFrequency, _sampleRate * 0.5)) - Math.Log(MinFrequency));

            if (normalized < 0.0) normalized = 0.0;
            if (normalized > 1.0) normalized = 1.0;

            return bottom - (float)normalized * (bottom - top);
        }

        private void RebuildOverlayTexture()
        {
            if (!_glInitialized)
                return;

            if (_overlayBitmap != null)
            {
                _overlayBitmap.Dispose();
                _overlayBitmap = null;
            }

            _overlayBitmap = BuildOverlayBitmap();

            if (_overlayTextureId == 0)
                _overlayTextureId = GL.GenTexture();

            GL.BindTexture(TextureTarget.Texture2D, _overlayTextureId);

            BitmapData data = _overlayBitmap.LockBits(
                new Rectangle(0, 0, _overlayBitmap.Width, _overlayBitmap.Height),
                ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            try
            {
                GL.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    PixelInternalFormat.Rgba,
                    _overlayBitmap.Width,
                    _overlayBitmap.Height,
                    0,
                    OpenTK.Graphics.OpenGL.PixelFormat.Bgra,
                    PixelType.UnsignedByte,
                    data.Scan0);
            }
            finally
            {
                _overlayBitmap.UnlockBits(data);
            }

            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Clamp);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Clamp);

            GL.BindTexture(TextureTarget.Texture2D, 0);
            _overlayDirty = false;
        }

        private void DrawOverlayTexture()
        {
            if (_overlayDirty || _overlayTextureId == 0)
                RebuildOverlayTexture();

            if (_overlayTextureId == 0)
                return;

            int w = Math.Max(_glCtrl.Width, 1);
            int h = Math.Max(_glCtrl.Height, 1);

            GL.Enable(EnableCap.Texture2D);
            GL.BindTexture(TextureTarget.Texture2D, _overlayTextureId);
            GL.Color4(1.0f, 1.0f, 1.0f, 1.0f);

            GL.Begin(PrimitiveType.Quads);
            GL.TexCoord2(0.0f, 0.0f); GL.Vertex2(0.0f, 0.0f);
            GL.TexCoord2(1.0f, 0.0f); GL.Vertex2(w, 0.0f);
            GL.TexCoord2(1.0f, 1.0f); GL.Vertex2(w, h);
            GL.TexCoord2(0.0f, 1.0f); GL.Vertex2(0.0f, h);
            GL.End();

            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }
            DetachInput();
            _workAvailable.Set();
            _worker.Join();
            _workAvailable.Dispose();

            _glCtrl.Paint -= glCtrl_Paint;
            _glCtrl.Resize -= glCtrl_Resize;
            _glCtrl.MouseClick -= glCtrl_MouseClick;
            _glCtrl.MouseDoubleClick -= glCtrl_MouseDoubleClick;

            if (_overlayTextureId != 0 && _glInitialized)
            {
                try
                {
                    _glCtrl.MakeCurrent();
                    GL.DeleteTexture(_overlayTextureId);
                    _overlayTextureId = 0;
                }
                catch
                {
                }
            }

            if (_overlayBitmap != null)
            {
                _overlayBitmap.Dispose();
                _overlayBitmap = null;
            }

            if (_glInitialized)
            {
                try
                {
                    _glCtrl.MakeCurrent();
                    DestroySpectralTextures();
                }
                catch
                {
                }
            }
        }
    }


    internal sealed class SpectralSlice
    {
        // Exclusive end of the causal FFT window, in samples since attachment.
        public long EndAbsoluteSample;
        public int ChannelsCount;
        public int HopSamples;
        public float[] Amplitudes;
    }

}
