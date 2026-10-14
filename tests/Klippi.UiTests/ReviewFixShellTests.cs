using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Klippi.Core;
using Klippi.Desktop;
using Xunit;

namespace Klippi.UiTests;

/// <summary>
/// Regression tests for the final review's shell findings: text contrast (13), the close prompt's
/// keys (14), closing while work runs (15), unreadable settings (16), Start minimized (18), links
/// arriving during fullscreen (19) and the retention limit (20).
/// </summary>
public sealed class ReviewFixShellTests
{
    // ----- 13. Small text reaches WCAG AA (4.5:1) on every surface it sits on -----

    private static readonly string[] Surfaces = ["AppBackground", "AppSidebar", "AppSurface", "AppSurfaceRaised"];
    private static readonly string[] TextTokens = ["AppText", "AppMuted", "AppFaint", "AppAccent", "AppAccentSubtleText", "AppWarmText", "AppWarning", "AppDanger", "AppInfo"];
    private static readonly (string Text, string Fill)[] Pairs =
    [
        ("AppWarmText", "AppWarmSubtle"), ("AppWarning", "AppWarningSubtle"), ("AppDanger", "AppDangerSubtle"), ("AppInfo", "AppInfoSubtle"),
        ("AppAccentSubtleText", "AppAccentSubtle"), ("AccentButtonForeground", "AccentButtonBackground"), ("AccentButtonForegroundPointerOver", "AccentButtonBackgroundPointerOver"),
        ("AccentText", "AppAccent"),
    ];

    [AvaloniaFact]
    public void Text_tokens_reach_aa_contrast_on_every_surface_and_chip_in_both_themes()
    {
        var failures = new List<string>();
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            foreach (var text in TextTokens)
                foreach (var surface in Surfaces)
                    if (Contrast(Token(text, theme), Token(surface, theme)) is var ratio and < 4.5) failures.Add($"{theme} {text} on {surface}: {ratio:0.00}:1");
            foreach (var (text, fill) in Pairs)
                if (Contrast(Token(text, theme), Token(fill, theme)) is var ratio and < 4.5) failures.Add($"{theme} {text} on {fill}: {ratio:0.00}:1");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [AvaloniaFact]
    public void Warm_chips_use_the_darker_warm_text_token_while_dots_keep_the_warm_fill()
    {
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var label = new TextBlock { Text = "0:12" };
            var chip = new Border { Classes = { "chip", "warm" }, Child = label };
            var dot = new Border { Classes = { "nav-dot" } };
            var window = new Window { RequestedThemeVariant = theme, Content = new StackPanel { Children = { chip, dot } } };
            window.Show(); window.UpdateLayout();
            try
            {
                Assert.Equal(Token("AppWarmText", theme), ((ISolidColorBrush)label.Foreground!).Color);
                Assert.Equal(Token("AppWarm", theme), ((ISolidColorBrush)dot.Background!).Color);
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaFact]
    public async Task Faint_hints_on_the_dark_video_backdrop_stay_readable_in_the_light_theme()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services) { Theme = "Light" }; await model.OpenEditorAsync("fixture.mp4");
        var window = new MainWindow { DataContext = model, RequestedThemeVariant = ThemeVariant.Light }; window.Show(); window.UpdateLayout();
        try
        {
            var hint = window.GetLogicalDescendants().OfType<TextBlock>().First(text => text.Text == "Press play to preview");
            Assert.True(hint.IsEffectivelyVisible);
            var backdrop = Token("AppVideoBackdrop", ThemeVariant.Light);
            var colour = ((ISolidColorBrush)hint.Foreground!).Color;
            Assert.Equal(Token("AppOnVideoFaint", ThemeVariant.Light), colour);
            // The hint is drawn at 75% opacity over the backdrop.
            Assert.True(Contrast(Blend(colour, backdrop, 0.75), backdrop) >= 4.5, $"{Contrast(Blend(colour, backdrop, 0.75), backdrop):0.00}:1");
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    // ----- 14 and 15. Closing asks first when something would be lost, with safe keys -----

    [AvaloniaFact]
    public async Task Exit_closes_at_once_when_nothing_is_running_or_unsaved()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        var window = new MainWindow { DataContext = model }; window.Show();
        var closed = false; window.Closed += (_, _) => closed = true;
        Assert.Equal(CloseBehavior.Exit, model.CloseBehavior);
        Assert.Empty(model.CloseWarnings);
        window.Close(); Dispatcher.UIThread.RunJobs();
        Assert.True(closed); Assert.Null(window.ClosePrompt);
    }

    [AvaloniaFact]
    public async Task Exit_with_unsaved_settings_asks_and_save_and_exit_keeps_them()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        var window = new MainWindow { DataContext = model }; window.Show();
        var closed = false; window.Closed += (_, _) => closed = true;
        try
        {
            model.RetentionDays = "14";
            Assert.Equal(MainWindow.CloseDecision.Ask, window.DecideClose(WindowCloseReason.WindowClosing));
            // Windows signing out or shutting down is never held up by the prompt.
            Assert.Equal(MainWindow.CloseDecision.Close, window.DecideClose(WindowCloseReason.OSShutdown));
            Assert.Equal(MainWindow.CloseDecision.Close, window.DecideClose(WindowCloseReason.ApplicationShutdown));

            window.Close(); Dispatcher.UIThread.RunJobs();
            Assert.False(closed);
            var prompt = window.ClosePrompt; Assert.NotNull(prompt);
            Assert.Contains("1 unsaved settings change", prompt!.FindNamed<TextBlock>("CloseWarningText")!.Text);
            Assert.Equal("Exit anyway", prompt.FindNamed<Button>("ExitButton")!.Content);

            prompt.FindNamed<Button>("SaveAndExitButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => closed);
            Assert.Equal(14, fake.Store.Settings.HistoryRetentionDays);
        }
        finally { if (!closed) { model.DiscardSettingsCommand.Execute(null); window.ClosePrompt?.Close(); window.Close(); } }
    }

    [AvaloniaFact]
    public async Task Exit_while_a_download_runs_names_it_and_keeps_the_app_open_until_confirmed()
    {
        var fake = new UiFakes(); fake.Downloads.WaitForCancellation = true;
        using var model = new MainViewModel(fake.Services) { Url = "https://example.com/video" }; await model.InitializeAsync();
        var window = new MainWindow { DataContext = model }; window.Show();
        var closed = false; window.Closed += (_, _) => closed = true;
        try
        {
            await model.QueueDownloadAsync(false); await Until(() => fake.Downloads.Requests.Count == 1);
            Assert.Contains("1 download", model.CloseWarnings);
            window.Close(); Dispatcher.UIThread.RunJobs();
            Assert.False(closed); Assert.False(fake.Downloads.Cancelled);
            var prompt = window.ClosePrompt!;
            Assert.Contains("stops 1 download", prompt.FindNamed<TextBlock>("CloseWarningText")!.Text);
            Assert.Null(prompt.FindNamed<Button>("SaveAndExitButton"));

            prompt.FindNamed<Button>("ExitButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => closed);
        }
        finally { if (!closed) { window.ClosePrompt?.Close(); model.CloseBehavior = CloseBehavior.Exit; foreach (var row in model.Queue.ToArray()) row.CancelCommand.Execute(null); window.Close(); } }
    }

    [AvaloniaFact]
    public async Task Close_prompt_focuses_keep_open_and_escape_or_enter_keep_the_app_open()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        model.CloseBehavior = CloseBehavior.Ask; await model.SettingsPreferenceSaveTask;
        var window = new MainWindow { DataContext = model }; window.Show();
        var closed = false; window.Closed += (_, _) => closed = true;
        try
        {
            foreach (var key in new[] { Key.Escape, Key.Enter })
            {
                window.Close(); Dispatcher.UIThread.RunJobs();
                var prompt = window.ClosePrompt; Assert.NotNull(prompt);
                var keepOpen = prompt!.FindNamed<Button>("KeepOpenButton")!;
                Assert.True(keepOpen.IsCancel); Assert.True(keepOpen.IsDefault);
                Assert.False(prompt.FindNamed<Button>("ExitButton")!.IsDefault);
                Assert.Same(keepOpen, prompt.FocusManager?.GetFocusedElement());

                // The key down closes the prompt, so there is no window left for the key up.
                prompt.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
                Dispatcher.UIThread.RunJobs();
                Assert.Null(window.ClosePrompt); Assert.False(closed); Assert.True(window.IsVisible);
            }
        }
        finally { window.ClosePrompt?.Close(); model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    // ----- 16. Unreadable settings can still be saved, and saving works normally afterwards -----

    [AvaloniaFact]
    public async Task Unreadable_settings_offer_save_apply_downloads_at_once_and_recover_after_saving()
    {
        var fake = new UiFakes(); fake.Store.SettingsLoadFailure = new IOException("settings.json is locked");
        using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        fake.Store.SettingsLoadFailure = null; var untouched = fake.Store.Settings;

        Assert.True(model.SettingsUnreadable);
        Assert.True(model.ShowSettingsSaveBar); Assert.True(model.CanSaveSettings);
        Assert.Equal("Saved settings could not be read", model.UnsavedSettingsTitle);

        // "Applies straight away" is true for Downloads at once.
        model.ConcurrentDownloads = 3;
        Assert.Equal(3, QueueLimit(model));
        // Instant choices wait in the save bar rather than overwriting the file automatically.
        model.Theme = "Light"; await model.SettingsPreferenceSaveTask;
        Assert.Same(untouched, fake.Store.Settings);
        Assert.Equal(2, model.UnsavedSettingsCount);

        await model.SaveSettingsChangesCommand.ExecuteAsync(null);
        Assert.False(model.SettingsUnreadable); Assert.False(model.ShowSettingsSaveBar);
        Assert.Equal(("Light", 3), (fake.Store.Settings.Theme, fake.Store.Settings.ConcurrentDownloads));

        // From here on instant choices save themselves again.
        model.CloseBehavior = CloseBehavior.Tray; await model.SettingsPreferenceSaveTask;
        Assert.Equal(CloseBehavior.Tray, fake.Store.Settings.CloseBehavior); Assert.False(model.HasUnsavedSettings);
        model.CloseBehavior = CloseBehavior.Exit; await model.SettingsPreferenceSaveTask;
    }

    // ----- 18. Start minimized is only offered together with Start with Windows -----

    [AvaloniaFact]
    public async Task Start_minimized_is_disabled_and_explained_without_start_with_windows()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        var window = new MainWindow { DataContext = model, Width = 1100, Height = 800 }; window.Show();
        try
        {
            model.SelectedTab = 4; window.UpdateLayout();
            var row = window.FindNamed<Grid>("StartMinimizedRow")!;
            Assert.False(model.StartWithWindows); Assert.False(row.IsEnabled);
            Assert.Contains("Turn on Start with Windows", window.FindNamed<TextBlock>("StartMinimizedHintText")!.Text);

            // On its own it changes nothing in Windows startup, so the bar does not say it will.
            model.StartMinimized = true;
            Assert.False(model.StartupChangePending);
            Assert.DoesNotContain("Windows startup", model.SettingsSaveBarDetail);

            model.StartWithWindows = true; window.UpdateLayout();
            Assert.True(row.IsEnabled); Assert.True(model.StartupChangePending);
            Assert.Contains("When KeaClip starts with Windows", window.FindNamed<TextBlock>("StartMinimizedHintText")!.Text);
        }
        finally { model.DiscardSettingsCommand.Execute(null); window.Close(); }
    }

    // ----- 19. A link handed over during fullscreen preview is visible -----

    [AvaloniaFact]
    public async Task A_link_handed_over_during_fullscreen_preview_leaves_fullscreen_and_shows_the_download_page()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services); await model.OpenEditorAsync("fixture.mp4");
        var window = new MainWindow { DataContext = model }; window.Show(); window.UpdateLayout();
        try
        {
            Assert.True(model.IsEditor);
            window.KeyPress(Key.F11, RawInputModifiers.None, PhysicalKey.F11, null); window.KeyRelease(Key.F11, RawInputModifiers.None, PhysicalKey.F11, null);
            Assert.True(model.PreviewFullscreen); Assert.Equal(WindowState.FullScreen, window.WindowState);

            model.AcceptLaunchArgument("https://example.com/video");
            window.UpdateLayout();
            Assert.Equal(0, model.SelectedTab); Assert.Equal("https://example.com/video", model.Url);
            Assert.False(model.PreviewFullscreen); Assert.NotEqual(WindowState.FullScreen, window.WindowState);
            Assert.False(window.FindNamed<Control>("FullscreenOverlay")!.IsVisible);
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    // ----- 20. The retention field enforces the same limit as Save -----

    [AvaloniaFact]
    public async Task Retention_above_the_store_limit_is_invalid_in_the_field_and_cannot_be_saved()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        model.RetentionDays = (ILocalStore.MaxHistoryRetentionDays + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(model.RetentionDaysInvalid); Assert.False(model.CanSaveSettings);
        model.RetentionDays = ILocalStore.MaxHistoryRetentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(model.RetentionDaysInvalid); Assert.True(model.CanSaveSettings);
        await model.SaveSettingsChangesCommand.ExecuteAsync(null);
        Assert.Equal(ILocalStore.MaxHistoryRetentionDays, fake.Store.Settings.HistoryRetentionDays);
    }

    // ----- Helpers -----

    private static int QueueLimit(MainViewModel model)
    {
        var queue = typeof(MainViewModel).GetField("_queue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
        return (int)queue.GetType().GetProperty("MaxConcurrent")!.GetValue(queue)!;
    }

    private static Color Token(string key, ThemeVariant theme)
    {
        Assert.True(Application.Current!.TryGetResource(key, theme, out var value), key);
        return value switch { ISolidColorBrush brush => brush.Color, Color color => color, _ => throw new InvalidOperationException(key) };
    }

    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte value) { var c = value / 255d; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        static double Luminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static Color Blend(Color top, Color bottom, double alpha)
    {
        byte Mix(byte t, byte b) => (byte)Math.Round(t * alpha + b * (1 - alpha));
        return Color.FromRgb(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not become true.");
            Dispatcher.UIThread.RunJobs(); await Task.Delay(20);
        }
    }
}
