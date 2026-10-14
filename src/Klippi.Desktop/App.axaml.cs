using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Klippi.Desktop;

public sealed partial class App : Application
{
    // Headless tests inject services and never initialise native playback or platform integration.
    public static Func<DesktopServices>? ServicesFactory { get; set; }
    internal static SingleInstanceRelay? Relay { get; set; }
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = ServicesFactory?.Invoke() ?? DesktopServices.CreateDefault();
            var model = new MainViewModel(services);
            var window = new MainWindow { DataContext = model };
            // The Windows startup entry passes --minimized only when "Start minimized" is on,
            // so a manual launch always opens normally. The decision is made before Show.
            var startMinimized = desktop.Args?.Contains("--minimized") == true;
            if (startMinimized)
            {
                window.ShowActivated = false;
                window.WindowState = WindowState.Minimized;
            }
            services.Platform.Attach(window);
            desktop.MainWindow = window;
            var closed = false;
            window.Closed += (_, _) => closed = true;
            Relay?.Listen(argument =>
            {
                // A relaunch during exit must not touch the closed window.
                if (closed) return;
                model.AcceptLaunchArgument(argument);
                window.BringToFront();
            });
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                // Keep the app alive for unexpected UI errors; details go to diagnostics.
                if (closed) return;
                model.ReportUnexpectedError(e.Exception);
                e.Handled = true;
            };
            // Raised for Windows sign-out/restart/shutdown (a normal exit closes the window
            // and shuts down without this event). Never veto it: cancel work now; child
            // processes are in kill-on-close jobs and end with this process.
            desktop.ShutdownRequested += (_, _) => model.Dispose();
            window.Opened += async (_, _) =>
            {
                await model.InitializeAsync();
                foreach (var argument in desktop.Args ?? []) model.AcceptLaunchArgument(argument);
                await model.DetectClipboardAsync();
                if (startMinimized && model.CloseBehavior == Core.CloseBehavior.Tray && window.WindowState == WindowState.Minimized) window.HideToTray();
            };
            desktop.Exit += (_, _) => model.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
