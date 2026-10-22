using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace PolyHubNet
{
    public sealed class RouteRuntimeController
    {
        private readonly RouteStore _routeStore;
        private readonly ServiceCatalog _serviceCatalog;
        private readonly SignalProcessorCatalog _signalProcessorCatalog;
        private readonly IList<ServiceSlot> _serviceSlotList;

        private readonly SignalStream _defaultViewerStream;
        private readonly GlScope _glScope;
        private readonly TimeFrequencyGlView _timeFrequencyGlView;

        private readonly Func<int> _getViewerTimeBaseSeconds;
        private readonly Func<float> _getViewerScaleVolts;
        private readonly Func<int> _getTimeFrequencyTimeBaseSeconds;
        private readonly Func<float> _getTimeFrequencyScaleVolts;

        private readonly Action<string> _setReaderStatusText;
        private readonly Action<string> _setProcessorStatusText;
        private readonly Func<string, string, string> _formatSourceNodePinText;

        private readonly string _viewerNodeId;
        private readonly string _viewerInputPinId;

        private SignalStream _viewerAttachedStream;

        public RouteRuntimeController(
            RouteStore routeStore,
            ServiceCatalog serviceCatalog,
            SignalProcessorCatalog signalProcessorCatalog,
            IList<ServiceSlot> serviceSlotList,
            SignalStream defaultViewerStream,
            GlScope glScope,
            TimeFrequencyGlView timeFrequencyGlView,
            Func<int> getViewerTimeBaseSeconds,
            Func<float> getViewerScaleVolts,
            Func<int> getTimeFrequencyTimeBaseSeconds,
            Func<float> getTimeFrequencyScaleVolts,
            Action<string> setReaderStatusText,
            Action<string> setProcessorStatusText,
            Func<string, string, string> formatSourceNodePinText,
            string viewerNodeId,
            string viewerInputPinId)
        {
            _routeStore = routeStore;
            _serviceCatalog = serviceCatalog;
            _signalProcessorCatalog = signalProcessorCatalog;
            _serviceSlotList = serviceSlotList;

            _defaultViewerStream = defaultViewerStream;
            _glScope = glScope;
            _timeFrequencyGlView = timeFrequencyGlView;

            _getViewerTimeBaseSeconds = getViewerTimeBaseSeconds;
            _getViewerScaleVolts = getViewerScaleVolts;
            _getTimeFrequencyTimeBaseSeconds = getTimeFrequencyTimeBaseSeconds;
            _getTimeFrequencyScaleVolts = getTimeFrequencyScaleVolts;

            _setReaderStatusText = setReaderStatusText;
            _setProcessorStatusText = setProcessorStatusText;
            _formatSourceNodePinText = formatSourceNodePinText;

            _viewerNodeId = viewerNodeId;
            _viewerInputPinId = viewerInputPinId;
        }

        public SignalStream ViewerAttachedStream
        {
            get { return _viewerAttachedStream; }
        }

        public ServiceDataOutputSlot ConnectedSourceServiceDataOutputSlot
        {
            get
            {
                PolyHubRouteSettings viewerRoute =
                    _routeStore.FindEnabledRouteTo(_viewerNodeId, _viewerInputPinId);

                if (viewerRoute == null)
                    return null;

                return FindSourceServiceOutputSlot(
                    viewerRoute.SourceNodeId,
                    viewerRoute.SourcePinId,
                    0);
            }
        }

        // Отключает runtime-входы всех зарегистрированных signal processors.
        public void DisconnectProcessorInputRoute()
        {
            IList<SignalProcessorSlot> slots = _signalProcessorCatalog.SignalProcessorSlotList;

            for (int i = 0; i < slots.Count; i++)
            {
                SignalProcessorSlot slot = slots[i];
                if (slot == null || slot.SignalProcessor == null)
                    continue;

                slot.SignalProcessor.DetachInput();
            }

            if (_setReaderStatusText != null)
                _setReaderStatusText("Reader: disconnected");

            if (_setProcessorStatusText != null)
                _setProcessorStatusText("Processor dropped latest blocks: N/A");
        }

        // Применяет сохранённые input-routes ко всем зарегистрированным signal processors.
        public void ApplyProcessorInputRoute(bool showErrorMessage)
        {
            IList<SignalProcessorSlot> slots = _signalProcessorCatalog.SignalProcessorSlotList;
            int connectedCount = 0;
            string singleConnectionText = null;

            for (int i = 0; i < slots.Count; i++)
            {
                SignalProcessorSlot slot = slots[i];
                if (slot == null || slot.SignalProcessor == null)
                    continue;

                PolyHubRouteSettings route =
                    _routeStore.FindEnabledRouteTo(slot.ProcessorNodeId, slot.InputPinId);

                if (route == null)
                {
                    slot.SignalProcessor.DetachInput();
                    continue;
                }

                SignalStream desiredStream = ResolveSignalOutputStream(
                    route.SourceNodeId,
                    route.SourcePinId,
                    showErrorMessage);

                if (desiredStream == null)
                {
                    slot.SignalProcessor.DetachInput();
                    continue;
                }

                if (!ReferenceEquals(slot.SignalProcessor.InputStream, desiredStream))
                    slot.SignalProcessor.AttachInput(desiredStream);

                connectedCount++;
                singleConnectionText =
                    _formatSourceNodePinText(route.SourceNodeId, route.SourcePinId) +
                    " -> " + slot.ProcessorDisplayName + "." + slot.InputPinId;
            }

            if (_setReaderStatusText != null)
            {
                if (connectedCount == 0)
                    _setReaderStatusText("Reader: disconnected");
                else if (connectedCount == 1)
                    _setReaderStatusText("Reader: " + singleConnectionText);
                else
                    _setReaderStatusText("Reader: " + connectedCount.ToString() + " processor inputs connected");
            }

            if (_setProcessorStatusText != null)
            {
                _setProcessorStatusText(
                    connectedCount > 0
                        ? "Processor dropped latest blocks: 0"
                        : "Processor dropped latest blocks: N/A");
            }
        }

        public void ApplyViewerInputRoute(bool showErrorMessage, bool viewerPlaybackMode, bool force = false)
        {
            PolyHubRouteSettings viewerRoute =
                _routeStore.FindEnabledRouteTo(_viewerNodeId, _viewerInputPinId);

            SignalStream desiredStream = null;
            int channels = 0;
            int sampleRate = 50;

            if (viewerRoute != null)
            {
                desiredStream = ResolveSignalOutputStream(
                    viewerRoute.SourceNodeId,
                    viewerRoute.SourcePinId,
                    showErrorMessage);

                ServiceDataOutputSlot sourceOutputSlot = FindSourceServiceOutputSlot(
                    viewerRoute.SourceNodeId,
                    viewerRoute.SourcePinId,
                    0);

                if (sourceOutputSlot != null && sourceOutputSlot.ServiceDataOutputReader != null)
                {
                    channels = sourceOutputSlot.ServiceDataOutputReader.GetChannelCount();
                    sampleRate = sourceOutputSlot.ServiceDataOutputReader.GetSampleRate();
                }
            }

            if (desiredStream == null)
                desiredStream = _defaultViewerStream;

            if (!force && !viewerPlaybackMode && ReferenceEquals(desiredStream, _viewerAttachedStream))
                return;

            _viewerAttachedStream = desiredStream;

            if (!viewerPlaybackMode)
            {
                if (_glScope != null)
                {
                    _glScope.DetachInput();
                    _glScope.AttachInput(desiredStream);
                    _glScope.Configure(
                        channels,
                        sampleRate,
                        _getViewerTimeBaseSeconds(),
                        _getViewerScaleVolts());
                }

                if (_timeFrequencyGlView != null)
                {
                    _timeFrequencyGlView.DetachInput();
                    _timeFrequencyGlView.AttachInput(desiredStream);
                    _timeFrequencyGlView.Configure(
                        channels,
                        sampleRate,
                        _getTimeFrequencyTimeBaseSeconds(),
                        _getTimeFrequencyScaleVolts());
                }
            }
        }

        // Для dynamic playback повторяет тот же processor path, который выбран у Viewer.
        // Если Viewer смотрит непосредственно на service output, playback идёт напрямую в Viewer.
        public SignalStream ApplyPlaybackInputRoute(SignalStream playbackStream)
        {
            if (playbackStream == null)
                throw new ArgumentNullException("playbackStream");

            DisconnectProcessorInputRoute();

            PolyHubRouteSettings viewerRoute =
                _routeStore.FindEnabledRouteTo(_viewerNodeId, _viewerInputPinId);

            if (viewerRoute == null)
                return playbackStream;

            SignalProcessorSlot processorSlot =
                _signalProcessorCatalog.FindByOutputPin(
                    viewerRoute.SourceNodeId,
                    viewerRoute.SourcePinId);

            if (processorSlot == null || processorSlot.SignalProcessor == null)
                return playbackStream;

            processorSlot.SignalProcessor.AttachInput(playbackStream);
            return processorSlot.ProcessorOutputStream ?? playbackStream;
        }

        public void ApplySavedRoutes(bool showErrorMessage, bool viewerPlaybackMode)
        {
            ApplyProcessorInputRoute(showErrorMessage);
            ApplyViewerInputRoute(showErrorMessage, viewerPlaybackMode);
        }

        public void DeactivateSavedRoutes(bool viewerPlaybackMode)
        {
            DisconnectProcessorInputRoute();
            DeactivateViewerRoute(viewerPlaybackMode);
            StopAllServiceOutputReaders();
        }

        private void StopAllServiceOutputReaders()
        {
            for (int i = 0; i < _serviceSlotList.Count; i++)
            {
                ServiceSlot serviceSlot = _serviceSlotList[i];
                if (serviceSlot == null)
                    continue;

                for (int j = 0; j < serviceSlot.ServiceDataOutputSlotList.Count; j++)
                {
                    ServiceDataOutputSlot outputSlot = serviceSlot.ServiceDataOutputSlotList[j];
                    if (outputSlot == null)
                        continue;

                    if (outputSlot.ServiceDataOutputReader != null)
                    {
                        outputSlot.ServiceDataOutputReader.Dispose();
                        outputSlot.ServiceDataOutputReader = null;
                    }
                }
            }
        }

        private void DeactivateViewerRoute(bool viewerPlaybackMode)
        {
            PolyHubRouteSettings viewerRoute =
                _routeStore.FindEnabledRouteTo(_viewerNodeId, _viewerInputPinId);

            int channels = 0;
            int sampleRate = 500;

            if (viewerRoute != null)
            {
                ServiceDataOutputSlot sourceOutputSlot = FindSourceServiceOutputSlot(
                    viewerRoute.SourceNodeId,
                    viewerRoute.SourcePinId,
                    0);

                if (sourceOutputSlot != null && sourceOutputSlot.ServiceDataOutputReader != null)
                {
                    channels = sourceOutputSlot.ServiceDataOutputReader.GetChannelCount();
                    sampleRate = sourceOutputSlot.ServiceDataOutputReader.GetSampleRate();
                }
            }

            _viewerAttachedStream = null;

            if (viewerPlaybackMode)
                return;

            if (_glScope != null)
            {
                _glScope.DetachInput();
                _glScope.ClearEventMarkers();
                _glScope.Configure(
                    channels,
                    sampleRate,
                    _getViewerTimeBaseSeconds(),
                    _getViewerScaleVolts());
            }

            if (_timeFrequencyGlView != null)
            {
                _timeFrequencyGlView.DetachInput();
                _timeFrequencyGlView.Configure(
                    channels,
                    sampleRate,
                    _getTimeFrequencyTimeBaseSeconds(),
                    _getTimeFrequencyScaleVolts());
            }
        }

        private bool EnsureServiceDataOutputReaderStarted(ServiceDataOutputSlot outputSlot, bool showErrorMessage)
        {
            if (outputSlot == null)
                return false;

            if (outputSlot.ServiceDataOutputReader != null)
                return true;

            outputSlot.ServiceDataOutputReader = new ProcessingBlockRingReader(
                outputSlot.ServiceDataOutputManifest.ServiceDataPortMapName,
                outputSlot.ServiceDataOutputManifest.ServiceDataPortReadyEventName,
                outputSlot.ServiceDataOutputStream);

            if (!outputSlot.ServiceDataOutputReader.Start(50, 200))
            {
                outputSlot.ServiceDataOutputReader.Dispose();
                outputSlot.ServiceDataOutputReader = null;

                if (showErrorMessage)
                    MessageBox.Show("Не удалось открыть ring: " + outputSlot.ServiceDataOutputManifest.ServiceDataPortDisplayName);

                return false;
            }

            return true;
        }

        private ServiceDataOutputSlot FindServiceOutputSlot(string sourceNodeId, string sourcePinId)
        {
            if (string.IsNullOrWhiteSpace(sourceNodeId) || string.IsNullOrWhiteSpace(sourcePinId))
                return null;

            if (!sourceNodeId.StartsWith("Service:", StringComparison.OrdinalIgnoreCase))
                return null;

            string serviceId = sourceNodeId.Substring("Service:".Length);
            ServiceSlot serviceSlot = _serviceCatalog.FindServiceSlot((List<ServiceSlot>)_serviceSlotList, serviceId);
            if (serviceSlot == null)
                return null;

            return _serviceCatalog.FindServiceDataOutputSlot(serviceSlot, sourcePinId);
        }

        private ServiceDataOutputSlot FindSourceServiceOutputSlot(
            string sourceNodeId,
            string sourcePinId,
            int depth)
        {
            if (depth > 16)
                return null;

            ServiceDataOutputSlot directServiceOutput =
                FindServiceOutputSlot(sourceNodeId, sourcePinId);

            if (directServiceOutput != null)
                return directServiceOutput;

            SignalProcessorSlot processorSlot =
                _signalProcessorCatalog.FindByOutputPin(sourceNodeId, sourcePinId);

            if (processorSlot == null)
                return null;

            PolyHubRouteSettings processorInputRoute =
                _routeStore.FindEnabledRouteTo(
                    processorSlot.ProcessorNodeId,
                    processorSlot.InputPinId);

            if (processorInputRoute == null)
                return null;

            return FindSourceServiceOutputSlot(
                processorInputRoute.SourceNodeId,
                processorInputRoute.SourcePinId,
                depth + 1);
        }

        private ServiceDataOutputSlot ResolveServiceOutputSlot(string sourceNodeId, string sourcePinId, bool showErrorMessage)
        {
            ServiceDataOutputSlot outputSlot = FindServiceOutputSlot(sourceNodeId, sourcePinId);
            if (outputSlot == null)
                return null;

            string serviceId = sourceNodeId.Substring("Service:".Length);
            ServiceSlot serviceSlot = _serviceCatalog.FindServiceSlot((List<ServiceSlot>)_serviceSlotList, serviceId);
            if (serviceSlot == null || !serviceSlot.ServiceIsRunning)
                return null;

            if (!EnsureServiceDataOutputReaderStarted(outputSlot, showErrorMessage))
                return null;

            return outputSlot;
        }

        private SignalStream ResolveSignalOutputStream(string sourceNodeId, string sourcePinId, bool showErrorMessage)
        {
            if (string.IsNullOrWhiteSpace(sourceNodeId) || string.IsNullOrWhiteSpace(sourcePinId))
                return null;

            ServiceDataOutputSlot serviceOutputSlot = ResolveServiceOutputSlot(sourceNodeId, sourcePinId, showErrorMessage);
            if (serviceOutputSlot != null)
                return serviceOutputSlot.ServiceDataOutputStream;

            SignalProcessorSlot processorSlot = _signalProcessorCatalog.FindByOutputPin(sourceNodeId, sourcePinId);
            if (processorSlot != null)
                return processorSlot.ProcessorOutputStream;

            return null;
        }
    }
}
