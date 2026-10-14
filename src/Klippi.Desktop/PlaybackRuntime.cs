using System.Runtime.InteropServices;

namespace Klippi.Desktop;

public static class PlaybackRuntime
{
    // The native format test reproduced D3D11.DLL_unloaded access violations
    // during successive VLC media changes. Hold one system DLL reference for
    // the process lifetime so driver callbacks cannot outlive its module.
    // Loading the module does not create a graphics device or start decoding.
    private static readonly Lazy<nint> GraphicsModule = new(() => OperatingSystem.IsWindows()
        ? NativeLibrary.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "d3d11.dll")) : 0);
    public static void Prepare() => _ = GraphicsModule.Value;
}
