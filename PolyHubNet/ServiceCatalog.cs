using PolyHubIpc;
using System;
using System.Collections.Generic;
using System.IO;

namespace PolyHubNet
{
    public sealed class ServiceCatalog
    {
        private readonly string _polyHubRootFolder;
        private readonly string _servicesFolderPath;

        public ServiceCatalog(string polyHubRootFolder)
        {
            _polyHubRootFolder = polyHubRootFolder;
            _servicesFolderPath = Path.Combine(_polyHubRootFolder, "Services");
        }

        public string ServicesFolderPath
        {
            get { return _servicesFolderPath; }
        }

        public List<ServiceSlot> LoadServiceSlots(PolyHubSettings settings)
        {
            List<ServiceSlot> result = new List<ServiceSlot>();

            if (!Directory.Exists(_servicesFolderPath))
                Directory.CreateDirectory(_servicesFolderPath);

            string[] manifestFiles = Directory.GetFiles(
                _servicesFolderPath,
                "*.service.json",
                SearchOption.TopDirectoryOnly);

            for (int i = 0; i < manifestFiles.Length; i++)
            {
                ServiceManifest serviceManifest = null;

                try
                {
                    serviceManifest = JsonFileStore.Load<ServiceManifest>(manifestFiles[i]);
                }
                catch
                {
                }

                if (serviceManifest == null || string.IsNullOrWhiteSpace(serviceManifest.ServiceId))
                    continue;

                if (FindServiceSlot(result, serviceManifest.ServiceId) != null)
                    continue;

                ServiceSlot serviceSlot = new ServiceSlot
                {
                    ServiceManifest = serviceManifest,
                    ServiceIsDiscovered = true,
                    ServiceIsRunning = false,
                    ServiceStatusText = serviceManifest.ServiceDisplayName + ": discovered"
                };

                if (serviceManifest.ServiceDataOutputManifestList != null)
                {
                    for (int j = 0; j < serviceManifest.ServiceDataOutputManifestList.Count; j++)
                    {
                        ServiceDataPortManifest outputManifest = serviceManifest.ServiceDataOutputManifestList[j];

                        ServiceDataOutputSlot outputSlot = new ServiceDataOutputSlot
                        {
                            ServiceDataOutputManifest = outputManifest,
                            ServiceDataOutputStream = new SignalStream(
                                serviceManifest.ServiceId + "." + outputManifest.ServiceDataPortId)
                        };

                        serviceSlot.ServiceDataOutputSlotList.Add(outputSlot);
                    }
                }

                EnsurePolyHubServiceSettingsEntry(settings, serviceSlot);
                result.Add(serviceSlot);
            }

            return result;
        }

        public void EnsurePolyHubServiceSettingsEntry(PolyHubSettings settings, ServiceSlot serviceSlot)
        {
            if (settings == null || serviceSlot == null || serviceSlot.ServiceManifest == null)
                return;

            if (settings.PolyHubServiceSettingsList == null)
                settings.PolyHubServiceSettingsList = new List<PolyHubServiceSettings>();

            string serviceId = serviceSlot.ServiceManifest.ServiceId;

            for (int i = 0; i < settings.PolyHubServiceSettingsList.Count; i++)
            {
                if (string.Equals(settings.PolyHubServiceSettingsList[i].ServiceId, serviceId, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            settings.PolyHubServiceSettingsList.Add(new PolyHubServiceSettings
            {
                ServiceId = serviceId,
                ServiceIsEnabled = true,
                ServiceUseHostedMode = serviceSlot.ServiceManifest.ServiceSupportsHosted
            });
        }

        public PolyHubServiceSettings FindPolyHubServiceSettings(PolyHubSettings settings, string serviceId)
        {
            if (settings == null || settings.PolyHubServiceSettingsList == null)
                return null;

            for (int i = 0; i < settings.PolyHubServiceSettingsList.Count; i++)
            {
                PolyHubServiceSettings item = settings.PolyHubServiceSettingsList[i];
                if (string.Equals(item.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase))
                    return item;
            }

            return null;
        }

        public ServiceSlot FindServiceSlot(List<ServiceSlot> serviceSlotList, string serviceId)
        {
            if (serviceSlotList == null)
                return null;

            for (int i = 0; i < serviceSlotList.Count; i++)
            {
                ServiceSlot slot = serviceSlotList[i];
                if (slot != null &&
                    slot.ServiceManifest != null &&
                    string.Equals(slot.ServiceManifest.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return slot;
                }
            }

            return null;
        }

        public ServiceDataOutputSlot FindServiceDataOutputSlot(ServiceSlot serviceSlot, string outputId)
        {
            if (serviceSlot == null)
                return null;

            for (int i = 0; i < serviceSlot.ServiceDataOutputSlotList.Count; i++)
            {
                ServiceDataOutputSlot slot = serviceSlot.ServiceDataOutputSlotList[i];
                if (slot != null &&
                    slot.ServiceDataOutputManifest != null &&
                    string.Equals(slot.ServiceDataOutputManifest.ServiceDataPortId, outputId, StringComparison.OrdinalIgnoreCase))
                {
                    return slot;
                }
            }

            return null;
        }
    }
}