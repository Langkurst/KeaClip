using Avalonia;

namespace Klippi.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var relay=new SingleInstanceRelay();
        if(!relay.IsOwner)
        {
            // Let the running instance take the foreground when it receives this link;
            // Windows otherwise blocks a background process from activating its window.
            if(OperatingSystem.IsWindows())_=AllowSetForegroundWindow(-1);
            var link=args.FirstOrDefault(a=>a.StartsWith("keaclip:",StringComparison.OrdinalIgnoreCase)||a.StartsWith("https:",StringComparison.OrdinalIgnoreCase)||a.StartsWith("http:",StringComparison.OrdinalIgnoreCase));
            relay.ForwardAsync(link??"--show").GetAwaiter().GetResult();
            return;
        }
        App.Relay=relay;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
