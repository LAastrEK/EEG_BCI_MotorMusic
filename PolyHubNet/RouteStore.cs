using System;
using System.Collections.Generic;

namespace PolyHubNet
{
    public sealed class RouteStore
    {
        private readonly PolyHubSettings _settings;

        public RouteStore(PolyHubSettings settings)
        {
            _settings = settings;
            EnsureRouteList();
        }

        public void EnsureRouteList()
        {
            if (_settings == null)
                return;

            if (_settings.PolyHubRouteSettingsList == null)
                _settings.PolyHubRouteSettingsList = new List<PolyHubRouteSettings>();
        }

        public string MakeServiceNodeId(string serviceId)
        {
            return "Service:" + serviceId;
        }

        public PolyHubRouteSettings FindEnabledRouteTo(string targetNodeId, string targetPinId)
        {
            if (_settings == null || _settings.PolyHubRouteSettingsList == null)
                return null;

            for (int i = 0; i < _settings.PolyHubRouteSettingsList.Count; i++)
            {
                PolyHubRouteSettings route = _settings.PolyHubRouteSettingsList[i];

                if (!route.RouteIsEnabled)
                    continue;

                if (string.Equals(route.TargetNodeId, targetNodeId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(route.TargetPinId, targetPinId, StringComparison.OrdinalIgnoreCase))
                {
                    return route;
                }
            }

            return null;
        }

        public List<PolyHubRouteSettings> FindEnabledRoutesFrom(string sourceNodeId, string sourcePinId)
        {
            List<PolyHubRouteSettings> list = new List<PolyHubRouteSettings>();

            if (_settings == null || _settings.PolyHubRouteSettingsList == null)
                return list;

            for (int i = 0; i < _settings.PolyHubRouteSettingsList.Count; i++)
            {
                PolyHubRouteSettings route = _settings.PolyHubRouteSettingsList[i];

                if (!route.RouteIsEnabled)
                    continue;

                if (string.Equals(route.SourceNodeId, sourceNodeId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(route.SourcePinId, sourcePinId, StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(route);
                }
            }

            return list;
        }

        public void SaveOrReplaceRoute(
            string sourceNodeId,
            string sourcePinId,
            string targetNodeId,
            string targetPinId,
            string routeDataType)
        {
            EnsureRouteList();

            if (_settings == null || _settings.PolyHubRouteSettingsList == null)
                return;

            for (int i = _settings.PolyHubRouteSettingsList.Count - 1; i >= 0; i--)
            {
                PolyHubRouteSettings route = _settings.PolyHubRouteSettingsList[i];

                if (string.Equals(route.TargetNodeId, targetNodeId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(route.TargetPinId, targetPinId, StringComparison.OrdinalIgnoreCase))
                {
                    _settings.PolyHubRouteSettingsList.RemoveAt(i);
                }
            }

            _settings.PolyHubRouteSettingsList.Add(new PolyHubRouteSettings
            {
                SourceNodeId = sourceNodeId,
                SourcePinId = sourcePinId,
                TargetNodeId = targetNodeId,
                TargetPinId = targetPinId,
                RouteDataType = routeDataType,
                RouteIsEnabled = true
            });
        }
    }
}