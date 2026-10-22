using PolyHubIpc;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PolyHubNet
{
    public sealed class ServiceSlot
    {
        public ServiceManifest ServiceManifest { get; set; }

        public Process ServiceProcess { get; set; }
        public IntPtr ServiceMainWindowHandle { get; set; }

        public ServiceEventLink ServiceEvtLink { get; set; }

        public List<ServiceDataOutputSlot> ServiceDataOutputSlotList { get; } = new List<ServiceDataOutputSlot>();

        public string ServiceStatusText { get; set; }

        public bool ServiceIsDiscovered { get; set; }
        public bool ServiceIsRunning { get; set; }
    }

    public sealed class ServiceDataOutputSlot
    {
        public ServiceDataPortManifest ServiceDataOutputManifest { get; set; }

        public ProcessingBlockRingReader ServiceDataOutputReader { get; set; }
        public SignalStream ServiceDataOutputStream { get; set; }
    }

    public enum ServiceTreeNodeKind
    {
        Service = 0,
        ServiceEvtLink = 1,
        ServiceOutputsRoot = 2,
        ServiceOutput = 3
    }

    public sealed class ServiceTreeNodeTag
    {
        public ServiceTreeNodeKind ServiceTreeNodeKind { get; set; }
        public string ServiceId { get; set; }
        public string ServiceDataOutputId { get; set; }
    }
}