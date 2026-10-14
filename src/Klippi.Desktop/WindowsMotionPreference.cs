using System.Runtime.InteropServices;

namespace Klippi.Desktop;

internal static class WindowsMotionPreference
{
    public static bool? ReadReducedMotion()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            // Read-only SPI_GETCLIENTAREAANIMATION; BOOL is a four-byte integer.
            // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow
            return SystemParametersInfoW(0x1042, 0, out var animationsEnabled, 0) != 0 ? animationsEnabled == 0 : null;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int SystemParametersInfoW(uint action, uint parameter, out int value, uint flags);
}
