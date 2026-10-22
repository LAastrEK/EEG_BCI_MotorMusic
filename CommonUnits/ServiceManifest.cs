using System.Collections.Generic;
using System.Runtime.Serialization;

namespace PolyHubIpc
{
    [DataContract]
    public sealed class ServiceManifest
    {
        [DataMember(Order = 1)]
        public string ServiceId { get; set; }

        [DataMember(Order = 2)]
        public string ServiceDisplayName { get; set; }

        [DataMember(Order = 3)]
        public string ServiceProgramFolderName { get; set; }

        [DataMember(Order = 4)]
        public string ServiceExeFileName { get; set; }

        [DataMember(Order = 5)]
        public string ServiceExeSubFolder { get; set; }

        [DataMember(Order = 6)]
        public string ServiceHostArguments { get; set; }

        [DataMember(Order = 7)]
        public bool ServiceSupportsHosted { get; set; }

        [DataMember(Order = 8)]
        public ServiceEvtLinkManifest ServiceEvtLinkManifest { get; set; }

        [DataMember(Order = 9)]
        public List<ServiceDataPortManifest> ServiceDataOutputManifestList { get; set; }

        [DataMember(Order = 10)]
        public List<ServiceDataPortManifest> ServiceDataInputManifestList { get; set; }
    }

    [DataContract]
    public sealed class ServiceEvtLinkManifest
    {
        [DataMember(Order = 1)]
        public string ServiceEvtLinkMapName { get; set; }

        [DataMember(Order = 2)]
        public string HubToServiceEvtName { get; set; }

        [DataMember(Order = 3)]
        public string ServiceToHubEvtName { get; set; }

        [DataMember(Order = 4)]
        public int ServiceEvtLinkCapacity { get; set; }
    }

    [DataContract]
    public sealed class ServiceDataPortManifest
    {
        [DataMember(Order = 1)]
        public string ServiceDataPortId { get; set; }

        [DataMember(Order = 2)]
        public string ServiceDataPortDisplayName { get; set; }

        [DataMember(Order = 3)]
        public string ServiceDataPortMapName { get; set; }

        [DataMember(Order = 4)]
        public string ServiceDataPortReadyEventName { get; set; }
    }
}