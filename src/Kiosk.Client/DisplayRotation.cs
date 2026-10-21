using System.Runtime.InteropServices;

namespace Kiosk.Client;

/// <summary>
/// Konfiguracja → Ekrany → "Obrót": a monitor standing on its side gets Windows' own orientation (0°, 90°, 180°, 270°),
/// set by the Kiosk at start and after saving, so nobody needs Windows' Settings on a locked-down kiosk.
/// Screens are numbered as everywhere in the Kiosk: 1 = the main screen, then Windows' order.
/// </summary>
internal static class DisplayRotation
{
    internal static readonly int[] Degrees = [0, 90, 180, 270];

    internal static Screen[] Ordered() => Screen.AllScreens.OrderByDescending(s => s.Primary).ToArray();

    /// <summary>The screen's orientation now, in degrees (0 when Windows does not say).</summary>
    internal static int Current(Screen screen)
    {
        var mode = NewMode();
        return EnumDisplaySettings(screen.DeviceName, EnumCurrentSettings, ref mode) ? Math.Clamp(mode.dmDisplayOrientation, 0, 3) * 90 : 0;
    }

    /// <summary>Turns every listed screen that is not turned so yet; true when anything changed.</summary>
    internal static bool Apply(IEnumerable<ScreenTurn> turns)
    {
        bool changed = false;
        var screens = Ordered();
        foreach (var turn in turns)
        {
            if (turn.Screen < 1 || turn.Screen > screens.Length) continue;
            var screen = screens[turn.Screen - 1];
            var mode = NewMode();
            if (!EnumDisplaySettings(screen.DeviceName, EnumCurrentSettings, ref mode)) { Audit.Write("screen_rotation_failed", screen.DeviceName + ": brak ustawień ekranu"); continue; }
            int wanted = Array.IndexOf(Degrees, turn.Degrees);
            if (wanted < 0 || mode.dmDisplayOrientation == wanted) continue;
            // From upright to sideways (or back) the width and the height swap.
            if ((mode.dmDisplayOrientation + wanted) % 2 == 1) (mode.dmPelsWidth, mode.dmPelsHeight) = (mode.dmPelsHeight, mode.dmPelsWidth);
            mode.dmDisplayOrientation = wanted;
            mode.dmFields = DisplayOrientation | PelsWidth | PelsHeight;
            int result = ChangeDisplaySettingsEx(screen.DeviceName, ref mode, IntPtr.Zero, UpdateRegistry, IntPtr.Zero);
            if (result == 0) { changed = true; Audit.Write("screen_rotated", "ekran " + turn.Screen + " (" + screen.DeviceName + "): " + turn.Degrees + "°"); }
            else Audit.Write("screen_rotation_failed", "ekran " + turn.Screen + " (" + screen.DeviceName + "): " + turn.Degrees + "°, kod " + result);
        }
        return changed;
    }

    private static DevMode NewMode() => new() { dmDeviceName = "", dmFormName = "", dmSize = (short)Marshal.SizeOf<DevMode>() };

    private const int EnumCurrentSettings = -1, UpdateRegistry = 0x01;
    private const int DisplayOrientation = 0x80, PelsWidth = 0x80000, PelsHeight = 0x100000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode mode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsEx(string deviceName, ref DevMode mode, IntPtr hwnd, int flags, IntPtr param);
}
