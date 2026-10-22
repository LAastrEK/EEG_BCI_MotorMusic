using PolyHubIpc;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PolyHubNet
{
    public sealed class ServiceRuntimeController
    {
        private readonly string _polyHubRootFolder;
        private readonly Panel _hostPanel;
        private readonly PolyHubSettings _polyHubSettings;
        private readonly ServiceCatalog _serviceCatalog;
        private readonly IList<ServiceSlot> _serviceSlotList;
        private readonly Action<ServiceSlot, ServiceEventPacket> _serviceEventHandler;

        private ServiceSlot _activeHostedServiceSlot;

        private const int GWL_STYLE = -16;

        private const int WS_CHILD = 0x40000000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_MAXIMIZEBOX = 0x00010000;
        private const int WS_SYSMENU = 0x00080000;

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public ServiceRuntimeController(
            string polyHubRootFolder,
            Panel hostPanel,
            PolyHubSettings polyHubSettings,
            ServiceCatalog serviceCatalog,
            IList<ServiceSlot> serviceSlotList,
            Action<ServiceSlot, ServiceEventPacket> serviceEventHandler)
        {
            _polyHubRootFolder = polyHubRootFolder;
            _hostPanel = hostPanel;
            _polyHubSettings = polyHubSettings;
            _serviceCatalog = serviceCatalog;
            _serviceSlotList = serviceSlotList;
            _serviceEventHandler = serviceEventHandler;
        }

        public bool IsServiceHostedModeEnabled(ServiceSlot serviceSlot)
        {
            if (serviceSlot == null || serviceSlot.ServiceManifest == null)
                return false;

            PolyHubServiceSettings serviceSettings =
                _serviceCatalog.FindPolyHubServiceSettings(
                    _polyHubSettings,
                    serviceSlot.ServiceManifest.ServiceId);

            return serviceSettings != null &&
                   serviceSettings.ServiceUseHostedMode &&
                   serviceSlot.ServiceManifest.ServiceSupportsHosted;
        }

        public void EnsureServiceEvtLinkCreated(ServiceSlot serviceSlot)
        {
            if (serviceSlot == null)
                return;

            if (serviceSlot.ServiceEvtLink != null)
                return;

            if (serviceSlot.ServiceManifest == null || serviceSlot.ServiceManifest.ServiceEvtLinkManifest == null)
                return;

            ServiceEvtLinkManifest m = serviceSlot.ServiceManifest.ServiceEvtLinkManifest;

            serviceSlot.ServiceEvtLink = new ServiceEventLink(
                m.ServiceEvtLinkMapName,
                m.HubToServiceEvtName,
                m.ServiceToHubEvtName,
                ServiceEventSide.Hub,
                m.ServiceEvtLinkCapacity);

            if (_serviceEventHandler != null)
            {
                serviceSlot.ServiceEvtLink.EventReceived +=
                    delegate (ServiceEventPacket evt)
                    {
                        _serviceEventHandler(serviceSlot, evt);
                    };
            }
        }

        public void StartService(ServiceSlot serviceSlot)
        {
            if (serviceSlot == null || serviceSlot.ServiceManifest == null)
                return;

            EnsureServiceEvtLinkCreated(serviceSlot);

            bool useHosted = IsServiceHostedModeEnabled(serviceSlot);
            bool processAlreadyRunning =
                serviceSlot.ServiceProcess != null &&
                !serviceSlot.ServiceProcess.HasExited;

            if (processAlreadyRunning && serviceSlot.ServiceIsRunning)
            {
                if (useHosted)
                    ShowServiceInHostPanel(serviceSlot);

                return;
            }

            if (!processAlreadyRunning)
            {
                string exePath = GetServiceExePath(serviceSlot);
                string workingDir = GetServiceWorkingDirectory(serviceSlot);

                if (!File.Exists(exePath))
                {
                    MessageBox.Show("Файл сервиса не найден: " + exePath);
                    return;
                }

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = useHosted ? serviceSlot.ServiceManifest.ServiceHostArguments : string.Empty,
                    UseShellExecute = false,
                    WorkingDirectory = workingDir
                };

                serviceSlot.ServiceProcess = Process.Start(psi);
                if (serviceSlot.ServiceProcess == null)
                    return;

                serviceSlot.ServiceMainWindowHandle = WaitForMainWindowHandle(serviceSlot.ServiceProcess);
            }

            if (useHosted)
                ShowServiceInHostPanel(serviceSlot);

            if (serviceSlot.ServiceEvtLink != null)
                serviceSlot.ServiceEvtLink.SendEvent(ServiceEventCode.Start, 1, 0, 0, "Start");
        }

        public void StopService(ServiceSlot serviceSlot)
        {
            if (serviceSlot == null || serviceSlot.ServiceEvtLink == null)
                return;

            serviceSlot.ServiceEvtLink.SendEvent(ServiceEventCode.Stop, 0, 0, 0, "Stop");
        }

        public void PingService(ServiceSlot serviceSlot)
        {
            if (serviceSlot == null || serviceSlot.ServiceEvtLink == null)
                return;

            serviceSlot.ServiceEvtLink.SendEvent(ServiceEventCode.Ping, 0, 0, 0, "Ping");
        }

        public void StartAllDiscoveredServices()
        {
            if (_serviceSlotList == null)
                return;

            for (int i = 0; i < _serviceSlotList.Count; i++)
            {
                ServiceSlot serviceSlot = _serviceSlotList[i];
                if (serviceSlot == null || serviceSlot.ServiceManifest == null)
                    continue;

                PolyHubServiceSettings settings =
                    _serviceCatalog.FindPolyHubServiceSettings(
                        _polyHubSettings,
                        serviceSlot.ServiceManifest.ServiceId);

                if (settings != null && !settings.ServiceIsEnabled)
                    continue;

                StartService(serviceSlot);
            }
        }

        public void ShowServiceInHostPanel(ServiceSlot serviceSlot)
        {
            if (serviceSlot == null)
                return;

            if (!IsServiceHostedModeEnabled(serviceSlot))
                return;

            if (serviceSlot.ServiceMainWindowHandle == IntPtr.Zero)
                return;

            PrepareHostedWindow(serviceSlot.ServiceMainWindowHandle);

            if (_activeHostedServiceSlot != null &&
                _activeHostedServiceSlot != serviceSlot &&
                _activeHostedServiceSlot.ServiceMainWindowHandle != IntPtr.Zero)
            {
                ShowWindow(_activeHostedServiceSlot.ServiceMainWindowHandle, SW_HIDE);
            }

            ShowWindow(serviceSlot.ServiceMainWindowHandle, SW_SHOW);

            SetWindowPos(
                serviceSlot.ServiceMainWindowHandle,
                IntPtr.Zero,
                0,
                0,
                _hostPanel.ClientSize.Width,
                _hostPanel.ClientSize.Height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);

            _activeHostedServiceSlot = serviceSlot;
        }

        public void ResizeActiveHostedService()
        {
            if (_activeHostedServiceSlot == null)
                return;

            if (_activeHostedServiceSlot.ServiceMainWindowHandle == IntPtr.Zero)
                return;

            SetWindowPos(
                _activeHostedServiceSlot.ServiceMainWindowHandle,
                IntPtr.Zero,
                0,
                0,
                _hostPanel.ClientSize.Width,
                _hostPanel.ClientSize.Height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        }

        public void ShutdownAllServiceSlots()
        {
            if (_serviceSlotList == null)
                return;

            for (int i = 0; i < _serviceSlotList.Count; i++)
            {
                ServiceSlot serviceSlot = _serviceSlotList[i];
                if (serviceSlot == null)
                    continue;

                for (int j = 0; j < serviceSlot.ServiceDataOutputSlotList.Count; j++)
                {
                    ServiceDataOutputSlot outputSlot = serviceSlot.ServiceDataOutputSlotList[j];

                    if (outputSlot.ServiceDataOutputReader != null)
                    {
                        outputSlot.ServiceDataOutputReader.Dispose();
                        outputSlot.ServiceDataOutputReader = null;
                    }
                }

                if (serviceSlot.ServiceEvtLink != null)
                {
                    serviceSlot.ServiceEvtLink.Dispose();
                    serviceSlot.ServiceEvtLink = null;
                }

                try
                {
                    if (serviceSlot.ServiceProcess != null && !serviceSlot.ServiceProcess.HasExited)
                    {
                        serviceSlot.ServiceProcess.CloseMainWindow();

                        if (!serviceSlot.ServiceProcess.WaitForExit(2000))
                            serviceSlot.ServiceProcess.Kill();
                    }
                }
                catch
                {
                }
            }

            _activeHostedServiceSlot = null;
        }

        private string GetServiceExePath(ServiceSlot serviceSlot)
        {
            if (serviceSlot == null || serviceSlot.ServiceManifest == null)
                return string.Empty;

            string serviceFolder = Path.Combine(
                _polyHubRootFolder,
                serviceSlot.ServiceManifest.ServiceProgramFolderName);

            string exeSubFolder = serviceSlot.ServiceManifest.ServiceExeSubFolder;

            if (!string.IsNullOrWhiteSpace(exeSubFolder))
                serviceFolder = Path.Combine(serviceFolder, exeSubFolder);

            return Path.Combine(serviceFolder, serviceSlot.ServiceManifest.ServiceExeFileName);
        }

        private string GetServiceWorkingDirectory(ServiceSlot serviceSlot)
        {
            if (serviceSlot == null || serviceSlot.ServiceManifest == null)
                return string.Empty;

            string serviceFolder = Path.Combine(
                _polyHubRootFolder,
                serviceSlot.ServiceManifest.ServiceProgramFolderName);

            string exeSubFolder = serviceSlot.ServiceManifest.ServiceExeSubFolder;

            if (!string.IsNullOrWhiteSpace(exeSubFolder))
                serviceFolder = Path.Combine(serviceFolder, exeSubFolder);

            return serviceFolder;
        }

        private IntPtr WaitForMainWindowHandle(Process process)
        {
            if (process == null)
                return IntPtr.Zero;

            try
            {
                process.WaitForInputIdle();
            }
            catch
            {
            }

            IntPtr hwnd = IntPtr.Zero;
            for (int i = 0; i < 100; i++)
            {
                try
                {
                    process.Refresh();
                    hwnd = process.MainWindowHandle;
                }
                catch
                {
                    hwnd = IntPtr.Zero;
                }

                if (hwnd != IntPtr.Zero)
                    break;

                Thread.Sleep(50);
            }

            return hwnd;
        }

        private void PrepareHostedWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return;

            int style = GetWindowLong(hwnd, GWL_STYLE);
            style &= ~WS_POPUP;
            style &= ~WS_CAPTION;
            style &= ~WS_THICKFRAME;
            style &= ~WS_MINIMIZEBOX;
            style &= ~WS_MAXIMIZEBOX;
            style &= ~WS_SYSMENU;
            style |= WS_CHILD;

            SetWindowLong(hwnd, GWL_STYLE, style);
            SetParent(hwnd, _hostPanel.Handle);

            SetWindowPos(
                hwnd,
                IntPtr.Zero,
                0,
                0,
                _hostPanel.ClientSize.Width,
                _hostPanel.ClientSize.Height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        }
    }
}