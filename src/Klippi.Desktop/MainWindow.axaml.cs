using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Rendering;
using Avalonia.VisualTree;

namespace Klippi.Desktop;

public sealed partial class MainWindow : Window
{
    private static readonly string[] NavigationButtons = ["DownloadNav", "QueueNav", "LibraryNav", "EditorNav", "SettingsNav"];
    // Nav buttons are a fixed 42px with 4px spacing, so the indicator offset is exact
    // without waiting for layout.
    private const double NavigationPitch = 46;
    private TrayIcon? _tray;
    private bool _explicitExit;
    private WindowState _beforeFullscreen = WindowState.Normal;
    private WindowNotificationManager? _notifications;
    private MainViewModel? _model;
    private bool _swallowSpaceRelease;

    public MainWindow()
    {
        InitializeComponent();
        NavIndicator.RenderTransform = TransformOperations.Parse("translateY(0px)");
        // Fit the default size to the work area BEFORE the first show, so CenterScreen
        // centres a window that fits (laptops at 125-200% scaling have short work areas).
        if (Screens.Primary is { } primary) FitToWorkArea(primary);
        Opened += (_, _) =>
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen is null || WindowState != WindowState.Normal) return;
            FitToWorkArea(screen);
            // Backstop for multi-monitor/DPI changes: keep the title bar on screen.
            var area = screen.WorkingArea; var frame = FrameSize ?? ClientSize;
            var width = (int)(frame.Width * screen.Scaling); var height = (int)(frame.Height * screen.Scaling);
            var x = Math.Clamp(Position.X, area.X, Math.Max(area.X, area.Right - width));
            var y = Math.Clamp(Position.Y, area.Y, Math.Max(area.Y, area.Bottom - height));
            if (x != Position.X || y != Position.Y) Position = new PixelPoint(x, y);
        };
        Activated += async (_, _) => { if (Model is { } model) { model.RefreshPlatformPreferences(); await model.DetectClipboardAsync(); } };
        Opened += (_, _) => UpdateDisplayInfo();
        ScalingChanged += (_, _) => UpdateDisplayInfo();
        DataContextChanged += (_, _) => AttachModel(Model);
        Closing += OnClosing;
        Closed += (_, _) => { _tray?.Dispose(); _tray = null; AttachModel(null); };
        // Tunnel routing: shortcuts run before a focused Button (Space would click it) or
        // Slider (arrows would jump by 1% of the file) can consume the key.
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        // Buttons click on Space release, so a consumed Space press must also consume
        // its release or a focused button would still fire.
        AddHandler(KeyUpEvent, (_, e) => { if (e.Key == Key.Space && _swallowSpaceRelease) { _swallowSpaceRelease = false; e.Handled = true; } }, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(this, true); AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private MainViewModel? Model => DataContext as MainViewModel;

    // For the beta report: how this monitor is scaled and how big it is. Only while the window is open: a closed
    // window's platform screens are disposed.
    private void UpdateDisplayInfo()
    {
        if (Model is not { } model || !IsVisible) return;
        model.DisplayScaling = RenderScaling;
        try { if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is { } screen) model.ScreenSize = screen.Bounds.Width + "x" + screen.Bounds.Height; }
        catch (ObjectDisposedException) { }
    }

    private void FitToWorkArea(Screen screen)
    {
        Width = Math.Max(MinWidth, Math.Min(Width, screen.WorkingArea.Width / screen.Scaling - 24));
        Height = Math.Max(MinHeight, Math.Min(Height, screen.WorkingArea.Height / screen.Scaling - 64));
    }

    private void AttachModel(MainViewModel? model)
    {
        if (ReferenceEquals(model, _model)) return;
        if (_model is { } previous) { previous.PropertyChanged -= ModelChanged; previous.CompletionNotice -= ShowCompletion; previous.ExitForUpdateRequested -= ExitForUpdate; previous.UpdateRevealRequested -= RevealUpdate; }
        _model = model;
        if (model is not null)
        {
            model.RefreshPlatformPreferences();
            model.PropertyChanged += ModelChanged;
            model.CompletionNotice += ShowCompletion;
            model.ExitForUpdateRequested += ExitForUpdate;
            model.UpdateRevealRequested += RevealUpdate;
        }
        UpdateNavigation(); UpdateWindowClasses();
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SelectedTab):
                // The fullscreen preview belongs to the editor. Anything that moves to another page
                // (a link handed over by a second launch or keaclip:, a dropped link, the tray's
                // Downloads item) must leave fullscreen, or that page would sit hidden under the video.
                if (Model is { PreviewFullscreen: true, IsEditor: false }) SetPreviewFullscreen(false);
                UpdateNavigation(); break;
            case nameof(MainViewModel.EffectiveReducedMotion):
            case nameof(MainViewModel.HasActiveQueue):
            case nameof(MainViewModel.IsBusy): UpdateWindowClasses(); break;
        }
    }

    private void UpdateWindowClasses()
    {
        Classes.Set("motion", Model is { EffectiveReducedMotion: false });
        Classes.Set("busy", Model is { IsBusy: true } || Model is { HasActiveQueue: true });
        Classes.Set("downloading", Model is { HasActiveQueue: true });
    }

    private void ShowCompletion(string title)
    {
        _notifications ??= new WindowNotificationManager(this) { Position = NotificationPosition.BottomRight, MaxItems = 3 };
        _notifications.Show(new Notification("Download finished", title, NotificationType.Success));
    }

    private void Navigate(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var index) && Model is { } model) model.SelectedTab = index;
    }

    private void UpdateNavigation()
    {
        var selected = Model?.SelectedTab ?? 0;
        for (var i = 0; i < NavigationButtons.Length; i++) this.FindControl<Button>(NavigationButtons[i])?.Classes.Set("selected", i == selected);
        // The beta checklist lives in the sidebar footer, outside the indicator's track.
        BetaTestButton.Classes.Set("selected", selected == 5);
        NavIndicator.IsVisible = selected < NavigationButtons.Length;
        NavIndicator.RenderTransform = TransformOperations.Parse($"translateY({selected * NavigationPitch}px)");
    }

    private void MinimizeWindow(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void ToggleMaximize(object? sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            var maximized = WindowState == WindowState.Maximized;
            MaximizeGlyph.IsVisible = !maximized; RestoreGlyph.IsVisible = maximized;
            // Fullscreen preview owns the whole screen; the custom title bar steps aside.
            SidebarTitleBar.IsVisible = ContentTitleBar.IsVisible = WindowState != WindowState.FullScreen;
        }
    }

    private void ToggleFullscreen(object? sender, RoutedEventArgs e) => SetPreviewFullscreen(Model?.PreviewFullscreen != true);
    internal void TogglePreviewFullscreen() => SetPreviewFullscreen(Model?.PreviewFullscreen != true);

    private void SetPreviewFullscreen(bool fullscreen)
    {
        if (Model is not { } model) return;
        var normal = EditorPage.PreviewHost; var expanded = FullscreenPreviewHost;
        if (fullscreen)
        {
            if (!model.HasEditor || !model.IsEditor || model.PreviewFullscreen) return;
            _beforeFullscreen = WindowState; var video = normal.Child; normal.Child = null; expanded.Child = video; model.PreviewFullscreen = true; WindowState = WindowState.FullScreen;
        }
        else
        {
            if (!model.PreviewFullscreen) return;
            var video = expanded.Child; expanded.Child = null; if (video is not null) normal.Child = video; model.PreviewFullscreen = false; WindowState = _beforeFullscreen;
        }
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key == Key.Escape && Model?.PreviewFullscreen == true) { SetPreviewFullscreen(false); e.Handled = true; return; }
        // Audio has no picture, so F11 never opens an empty fullscreen window for it. Fullscreen
        // opens from the editor page only, so leaving that page always leaves fullscreen too.
        if (e.Key == Key.F11 && Model is { HasEditor: true } fullscreen && (fullscreen.PreviewFullscreen || fullscreen.IsEditor && !fullscreen.IsAudioOnlyMedia)) { SetPreviewFullscreen(!fullscreen.PreviewFullscreen); e.Handled = true; return; }
        // Developer aid: Ctrl+Shift+F12 shows the FPS counter and layout/render frame-time
        // graphs, to measure animation smoothness on real hardware. Off by default.
        if (e.Key == Key.F12 && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            RendererDiagnostics.DebugOverlays = RendererDiagnostics.DebugOverlays == RendererDebugOverlays.None
                ? RendererDebugOverlays.Fps | RendererDebugOverlays.LayoutTimeGraph | RendererDebugOverlays.RenderTimeGraph
                : RendererDebugOverlays.None;
            e.Handled = true; return;
        }
        if (e.KeyModifiers == KeyModifiers.Control && Model is { } shortcuts)
        {
            if (e.Key is >= Key.D1 and <= Key.D5 && !shortcuts.PreviewFullscreen) { shortcuts.SelectedTab = e.Key - Key.D1; e.Handled = true; return; }
            if (e.Key == Key.O && shortcuts.CanEdit) { shortcuts.OpenMediaCommand.Execute(null); e.Handled = true; return; }
        }
        if (Model is not { IsEditor: true, HasEditor: true } model || e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift)) return;
        // Yield only to text entry and open drop-downs; buttons and sliders keep the
        // player shortcuts working after a mouse click.
        var focused = FocusManager?.GetFocusedElement() as Control;
        if (focused is not null && focused.GetVisualAncestors().Prepend(focused).Any(c => c is TextBox || c is ComboBox { IsDropDownOpen: true })) return;
        if (e.Key == Key.Space) { model.PlayCommand.Execute(null); e.Handled = true; _swallowSpaceRelease = true; return; }
        // The editor page owns the editing keys (I/O marks, +/- zoom, arrow nudges on the
        // timeline's step); forwarding from the tunnel keeps them working after a click.
        EditorPage.HandleShortcut(e);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (Model is not { } model) return;
        var files = e.DataTransfer.TryGetFiles(); var file = files?.FirstOrDefault()?.TryGetLocalPath();
        if (file is not null) { await model.OpenEditorAsync(file); return; }
        var text = e.DataTransfer.TryGetText(); if (text is not null) model.AcceptLaunchArgument(text);
    }

    internal enum CloseDecision { Close, HideToTray, Ask }

    /// <summary>
    /// What a close request does. Windows sign-out, restart or shutdown and the app's own shutdown
    /// are never delayed or vetoed. Otherwise "Exit" closes at once only when nothing would be lost;
    /// running downloads, an export, an upload, a settings task or unsaved settings ask first.
    /// </summary>
    internal CloseDecision DecideClose(WindowCloseReason reason)
    {
        if (reason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown) return CloseDecision.Close;
        if (_explicitExit || Model is not { } model) return CloseDecision.Close;
        return model.CloseBehavior switch
        {
            Core.CloseBehavior.Tray => CloseDecision.HideToTray,
            Core.CloseBehavior.Ask => CloseDecision.Ask,
            _ => model.CloseWarnings.Count > 0 ? CloseDecision.Ask : CloseDecision.Close,
        };
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        var decision = DecideClose(e.CloseReason);
        if (decision == CloseDecision.Close) return;
        e.Cancel = true;
        if (decision == CloseDecision.HideToTray) { HideToTray(); return; }
        await AskBeforeClosingAsync();
    }

    /// <summary>The open "Close KeaClip?" prompt, if any.</summary>
    internal Window? ClosePrompt => _closePrompt;
    private Window? _closePrompt;

    private async Task AskBeforeClosingAsync()
    {
        if (Model is not { } model) return;
        if (_closePrompt is { } open) { open.Activate(); return; }
        var losesSomething = model.CloseWarnings.Count > 0;
        var unsaved = model.HasUnsavedSettings;
        var dialog = new Window { Title = "Close KeaClip", Width = 460, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        // The safe choice is the default: Enter and Escape both keep the app open, and it has
        // focus when the prompt opens. Exit is never the default.
        var stay = new Button { Name = "KeepOpenButton", Content = "Keep open", IsDefault = true, IsCancel = true, Classes = { losesSomething ? "primary" : "ghost" } };
        var tray = new Button { Name = "KeepInTrayButton", Content = "Keep in tray" };
        var exit = new Button { Name = "ExitButton", Content = losesSomething ? "Exit anyway" : "Exit" };
        if (losesSomething) exit.Classes.Add("danger"); else exit.Classes.Add("primary");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 14, 0, 0), Children = { stay, tray } };
        if (unsaved && model.CanSaveSettings)
        {
            var saveExit = new Button { Name = "SaveAndExitButton", Content = "Save and exit" };
            saveExit.Click += async (_, _) =>
            {
                dialog.Close();
                await model.SaveSettingsChangesCommand.ExecuteAsync(null);
                // A failed save stays open on Settings, where the save bar says why.
                if (model.HasUnsavedSettings || model.HasSettingsSaveError) { model.SelectedTab = 4; BringToFront(); return; }
                ExitNow();
            };
            buttons.Children.Add(saveExit);
        }
        buttons.Children.Add(exit);
        var message = new TextBlock { Name = "CloseWarningText", Text = model.CloseWarningText, Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        AutomationProperties.SetName(dialog, "Close KeaClip?");
        AutomationProperties.SetHelpText(dialog, message.Text);
        dialog.Content = new StackPanel
        {
            Spacing = 8, Margin = new(24, 22),
            Children = { new TextBlock { Text = "Close KeaClip?", Classes = { "section" } }, message, buttons }
        };
        stay.Click += (_, _) => dialog.Close();
        tray.Click += (_, _) => { dialog.Close(); HideToTray(); };
        exit.Click += (_, _) => { dialog.Close(); ExitNow(); };
        dialog.Opened += (_, _) => stay.Focus(NavigationMethod.Tab);
        dialog.Closed += (_, _) => { if (ReferenceEquals(_closePrompt, dialog)) _closePrompt = null; };
        _closePrompt = dialog;
        await dialog.ShowDialog(this);
    }

    // The user chose to exit: close without asking again.
    private void ExitNow() { _explicitExit = true; Close(); }

    // Update now already named any running work and the user chose to restart, so the window closes
    // without asking again; the started installer waits for this process to exit.
    internal bool ExitedForUpdate { get; private set; }
    private void ExitForUpdate() { ExitedForUpdate = true; ExitNow(); }

    private void RevealUpdate() => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (Model is { } model) SettingsPage.RevealSetting(model, MainViewModel.UpdateSectionKey); }, Avalonia.Threading.DispatcherPriority.Loaded);

    // The tray's Exit asks first too when work would stop or settings would be lost.
    private void ExitFromTray()
    {
        if (Model is { } model && model.CloseWarnings.Count > 0) { BringToFront(); _ = AskBeforeClosingAsync(); return; }
        ExitNow();
    }

    internal void HideToTray()
    {
        if (_tray is null)
        {
            var menu = new NativeMenu();
            var open = new NativeMenuItem("Open KeaClip"); open.Click += (_, _) => RestoreFromTray();
            var queue = new NativeMenuItem("Downloads"); queue.Click += (_, _) => { if (Model is { } model) model.SelectedTab = 1; RestoreFromTray(); };
            var pause = new NativeMenuItem("Pause / resume new starts"); pause.Click += (_, _) => Model?.PauseQueueCommand.Execute(null);
            var folder = new NativeMenuItem("Open downloads folder"); folder.Click += (_, _) => { if (Model is { } model && Directory.Exists(model.DownloadDirectory)) new DesktopPlatform().OpenFolder(model.DownloadDirectory); };
            var exit = new NativeMenuItem("Exit"); exit.Click += (_, _) => ExitFromTray();
            menu.Items.Add(open); menu.Items.Add(queue); menu.Items.Add(pause); menu.Items.Add(folder); menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(exit);
            using var icon = AssetLoader.Open(new Uri("avares://KeaClip/Assets/kea.ico"));
            _tray = new TrayIcon { ToolTipText = "KeaClip", Icon = new WindowIcon(icon), Menu = menu, IsVisible = true };
            _tray.Clicked += (_, _) => RestoreFromTray();
        }
        if (_tray.IsVisible) Hide(); else if (Model is { } model) model.Status = "A tray icon is unavailable. The window stayed open.";
    }

    private void RestoreFromTray() => BringToFront();

    /// <summary>Shows the window without un-maximizing it or leaving preview fullscreen.</summary>
    internal void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
}
