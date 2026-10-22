using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public static class DisplayInfoProvider
{
    private const int ENUM_CURRENT_SETTINGS = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DEVMODE
    {
        private const int CCHDEVICENAME = 32;
        private const int CCHFORMNAME = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string dmDeviceName;

        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;

        public int dmFields;

        public int dmPositionX;
        public int dmPositionY;

        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;

        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;

        public short dmTTOption;
        public short dmCollation;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
        public string dmFormName;

        public short dmLogPixels;

        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;

        public int dmDisplayFlags;
        public int dmDisplayFrequency;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern int EnumDisplaySettings(
        string deviceName,
        int modeNum,
        ref DEVMODE devMode);

    public static int GetRefreshRate(Screen screen)
    {
        DEVMODE mode = new DEVMODE();
        mode.dmSize = (short)Marshal.SizeOf(mode);

        EnumDisplaySettings(
            screen.DeviceName,
            ENUM_CURRENT_SETTINGS,
            ref mode);

        return mode.dmDisplayFrequency;
    }
}
