using PolyHubIpc;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace PolyHubNet
{
    public sealed class PolyHubSettingsStore
    {
        private readonly string _startupPath;
        private readonly string _settingsFilePath;

        public PolyHubSettingsStore(string startupPath)
        {
            _startupPath = startupPath;
            _settingsFilePath = Path.Combine(_startupPath, "PolyHub.json");
        }

        public string SettingsFilePath
        {
            get { return _settingsFilePath; }
        }

        public string FindPolyHubRootFolder()
        {
            DirectoryInfo dir = new DirectoryInfo(_startupPath);

            while (dir != null)
            {
                string servicesDir = Path.Combine(dir.FullName, "Services");
                if (Directory.Exists(servicesDir))
                    return dir.FullName;

                dir = dir.Parent;
            }

            return _startupPath;
        }

        public PolyHubSettings LoadOrCreate()
        {
            PolyHubSettings settings = JsonFileStore.LoadOrCreate(
                _settingsFilePath,
                CreateDefaultPolyHubSettings);

            if (settings.EogRejectionSettings == null)
                settings.EogRejectionSettings = EogRejectionSettings.CreateDefault();
            settings.EogRejectionSettings.Normalize();

            return settings;
        }

        public void Save(PolyHubSettings settings)
        {
            if (settings == null)
                return;

            JsonFileStore.Save(_settingsFilePath, settings);
        }

        public void ApplyWindowSettings(Form form, PolyHubSettings settings)
        {
            if (form == null || settings == null || settings.PolyHubUiSettings == null)
                return;

            form.StartPosition = FormStartPosition.Manual;
            form.Left = settings.PolyHubUiSettings.WindowLeft;
            form.Top = settings.PolyHubUiSettings.WindowTop;
            form.Width = settings.PolyHubUiSettings.WindowWidth;
            form.Height = settings.PolyHubUiSettings.WindowHeight;
        }

        public void CaptureWindowSettings(Form form, PolyHubSettings settings, string selectedServiceId)
        {
            if (form == null || settings == null)
                return;

            if (settings.PolyHubUiSettings == null)
                settings.PolyHubUiSettings = new PolyHubUiSettings();

            settings.PolyHubUiSettings.WindowLeft = form.Left;
            settings.PolyHubUiSettings.WindowTop = form.Top;
            settings.PolyHubUiSettings.WindowWidth = form.Width;
            settings.PolyHubUiSettings.WindowHeight = form.Height;
            settings.PolyHubUiSettings.SelectedServiceId = selectedServiceId;
        }

        private static PolyHubSettings CreateDefaultPolyHubSettings()
        {
            return new PolyHubSettings
            {
                PolyHubServiceSettingsList = new List<PolyHubServiceSettings>(),
                PolyHubRouteSettingsList = new List<PolyHubRouteSettings>(),
                PolyHubUiSettings = new PolyHubUiSettings
                {
                    WindowLeft = 100,
                    WindowTop = 100,
                    WindowWidth = 1500,
                    WindowHeight = 900,
                    SelectedServiceId = null
                },
                PolyHubViewerSettings = new PolyHubViewerSettings
                {
                    ViewerTimeBaseSeconds = 5,
                    ViewerScaleVolts = 100e-6f,
                    TimeFrequencyTimeBaseSeconds = 5,
                    TimeFrequencyScaleVolts = 10e-6f
                },
                PolyHubRecorderSettings = new PolyHubRecorderSettings
                {
                    RecorderSourceNodeId = null
                },
                EogRejectionSettings = EogRejectionSettings.CreateDefault()
            };
        }
    }
}
