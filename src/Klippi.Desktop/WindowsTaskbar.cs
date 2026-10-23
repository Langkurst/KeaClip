using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Klippi.Desktop;

/// <summary>Flashes the window's taskbar button until it is brought to the front: Windows' usual "something finished".</summary>
internal static class WindowsTaskbar
{
    private const uint FlashAll = 0x3, FlashUntilForeground = 0xC;

    public static void Flash(Window window)
    {
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero) return;
        var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = handle, Flags = FlashAll | FlashUntilForeground, Count = 3, Timeout = 0 };
        try { FlashWindowEx(ref info); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo { public uint Size; public IntPtr Window; public uint Flags; public uint Count; public uint Timeout; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}
