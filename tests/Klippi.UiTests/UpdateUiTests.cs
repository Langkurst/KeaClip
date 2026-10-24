using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Klippi.Core;
using Klippi.Desktop;
using Xunit;

namespace Klippi.UiTests;

public sealed class UpdateUiTests
{
    private static readonly UpdateOffer Offer = new(ReleaseVersion.Parse("0.3.0"), "Faster exact clips.\nA calmer queue.", new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero),
        new UpdateAsset(new Uri("https://example.com/KeaClip.Setup.exe"), new string('a', 64), 150_000_000), new UpdateAsset(new Uri("https://example.com/KeaClip-0.3.0-win-x64.zip"), new string('b', 64), 140_000_000), null);

    private static (UiFakes Fake, FakeUpdates Updates, MainViewModel Model) Create(UpdateInstallKind kind = UpdateInstallKind.Installed, Action<UiFakes>? setup = null)
    {
        var fake = new UiFakes(); setup?.Invoke(fake);
        var updates = new FakeUpdates(kind);
        var model = new MainViewModel(fake.Services with { Updates = updates }) { UpdateStartupDelay = TimeSpan.FromHours(1) };
        return (fake, updates, model);
    }

    // ----- Settings: on by default, saved straight away, respected -----

    [AvaloniaFact]
    public async Task Automatic_checks_are_on_by_default_and_the_choice_is_saved_at_once()
    {
        var (fake, _, model) = Create(); using var _ = model; await model.InitializeAsync();
        Assert.True(new AppSettings().CheckForUpdates);
        Assert.True(model.CheckForUpdates);
        model.CheckForUpdates = false; await model.SettingsPreferenceSaveTask;
        Assert.False(fake.Store.Settings.CheckForUpdates);
        Assert.False(model.HasUnsavedSettings);
        model.CheckForUpdates = true; await model.SettingsPreferenceSaveTask;
        Assert.True(fake.Store.Settings.CheckForUpdates);
    }

    [AvaloniaFact]
    public async Task A_saved_off_choice_is_respected_and_the_daily_check_does_not_run()
    {
        var (_, updates, model) = Create(setup: f => f.Store.Settings = new AppSettings { CheckForUpdates = false });
        using var _ = model; model.UpdateStartupDelay = TimeSpan.Zero; await model.InitializeAsync();
        Assert.False(model.CheckForUpdates);
        await model.CheckForUpdatesAsync(background: true);
        await Task.Delay(100); Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, updates.Checks);
        // A manual check still works.
        await model.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.Equal(1, updates.Checks); Assert.False(updates.LastWasBackground);
    }

    [AvaloniaFact]
    public async Task Settings_that_could_not_be_read_never_start_automatic_checks()
    {
        var (_, updates, model) = Create(setup: f => f.Store.SettingsLoadFailure = new IOException("damaged"));
        using var _ = model; model.UpdateStartupDelay = TimeSpan.Zero; await model.InitializeAsync();
        await Task.Delay(150); Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, updates.Checks);
    }

    [AvaloniaFact]
    public async Task The_schedule_runs_the_background_check_after_startup_when_due()
    {
        var (_, updates, model) = Create(); updates.Result = Available();
        using var _ = model; model.UpdateStartupDelay = TimeSpan.Zero; await model.InitializeAsync();
        await Until(() => updates.Checks == 1);
        Assert.True(updates.LastWasBackground);
        await Until(() => model.HasUpdateOffer);
        Assert.Equal(1, updates.Cleanups);
    }

    [AvaloniaFact]
    public async Task A_background_check_is_quiet_skips_when_not_due_and_shows_a_newer_release_without_touching_the_status_line()
    {
        var (_, updates, model) = Create(); using var _ = model; await model.InitializeAsync();
        var status = model.Status;
        updates.Due = false;
        await model.CheckForUpdatesAsync(background: true);
        Assert.Equal(0, updates.Checks);

        updates.Due = true; updates.Result = new(UpdateCheckOutcome.Offline, null, "Offline, so the daily update check will run later.", null, Quiet: true);
        await model.CheckForUpdatesAsync(background: true);
        Assert.Equal("Offline, so the daily update check will run later.", model.UpdateStatusText);
        Assert.Equal(status, model.Status); Assert.False(model.HasUpdateOffer);

        updates.Result = Available();
        await model.CheckForUpdatesAsync(background: true);
        Assert.True(model.HasUpdateOffer); Assert.True(model.ShowUpdateBadge);
        Assert.Equal("KeaClip 0.3.0 is available", model.UpdateTitle);
        Assert.Equal("Update to 0.3.0", model.UpdateBadgeText);
        Assert.Contains("150 MB", model.UpdateDetail); Assert.Contains("settings and media are kept", model.UpdateDetail);
        Assert.Equal(status, model.Status);
    }

    [AvaloniaFact]
    public async Task A_manual_check_reports_up_to_date_or_offline_in_the_status_line()
    {
        var (_, updates, model) = Create(); using var _ = model; await model.InitializeAsync();
        updates.Result = new(UpdateCheckOutcome.UpToDate, null, "KeaClip 0.2.0 is up to date.", DateTimeOffset.Now, Quiet: true);
        await model.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.StartsWith("KeaClip 0.2.0 is up to date. Last checked today at", model.Status);
        Assert.Equal(model.Status, model.UpdateStatusText);
        updates.Result = new(UpdateCheckOutcome.Offline, null, "You appear to be offline. Connect to the internet and try again.", null);
        await model.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.Equal("You appear to be offline. Connect to the internet and try again.", model.Status);
        Assert.Equal(UpdatePhase.Idle, model.UpdatePhase);
    }

    [AvaloniaFact]
    public async Task Later_hides_the_offer_until_the_user_checks_again()
    {
        var (_, updates, model) = Create(); using var _ = model; await model.InitializeAsync();
        updates.Result = Available(); await model.CheckForUpdatesAsync(background: true);
        model.UpdateLaterCommand.Execute(null);
        Assert.False(model.HasUpdateOffer); Assert.False(model.ShowUpdateBadge);
        Assert.Contains("Check for updates", model.UpdateStatusText);
        // The next daily check of the same release stays quiet; a manual check shows it again.
        await model.CheckForUpdatesAsync(background: true); Assert.False(model.HasUpdateOffer);
        await model.CheckForUpdatesCommand.ExecuteAsync(null); Assert.True(model.HasUpdateOffer);
    }

    // ----- Update now -----

    [AvaloniaFact]
    public async Task Update_now_with_nothing_running_downloads_verifies_starts_the_installer_and_exits()
    {
        var (_, updates, model) = Create(); using var _ = model; await model.InitializeAsync();
        updates.Result = Available(); await model.CheckForUpdatesAsync(background: true);
        var exits = 0; model.ExitForUpdateRequested += () => exits++;
        Assert.True(model.UpdateNowCommand.CanExecute(null));
        await model.UpdateNowCommand.ExecuteAsync(null);
        Assert.Equal(1, updates.Downloads); Assert.Single(updates.Installs);
        Assert.Equal(1, exits);
        Assert.Equal(UpdatePhase.Installing, model.UpdatePhase);
        Assert.Contains("opens again", model.Status);
        Assert.False(model.ShowUpdateBadge);
    }

    [AvaloniaFact]
    public async Task Update_now_with_unsaved_settings_or_running_work_asks_before_restarting()
    {
        var (fake, updates, model) = Create(setup: f => f.Downloads.WaitForCancellation = true);
        using var _ = model; model.Url = "https://example.com/video"; await model.InitializeAsync();
        updates.Result = Available(); await model.CheckForUpdatesAsync(background: true);
        await model.QueueDownloadAsync(false); await Until(() => fake.Downloads.Requests.Count == 1);
        model.RetentionDays = "30";
        var exits = 0; model.ExitForUpdateRequested += () => exits++;
        try
        {
            await model.UpdateNowCommand.ExecuteAsync(null);
            Assert.Equal(1, updates.Downloads); Assert.Empty(updates.Installs); Assert.Equal(0, exits);
            Assert.True(model.NeedsUpdateRestartConfirmation);
            Assert.Equal("Restarting to update now stops 1 download, and loses 1 unsaved settings change. Wait until it's done, or restart anyway.", model.UpdateRestartWarning);
            Assert.False(fake.Downloads.Cancelled);

            // Not yet keeps the verified update; Restart and update asks again while work remains.
            model.KeepUpdateForLaterCommand.Execute(null);
            Assert.True(model.IsUpdateReadyToRestart); Assert.False(model.NeedsUpdateRestartConfirmation);
            await model.RestartToUpdateCommand.ExecuteAsync(null);
            Assert.True(model.NeedsUpdateRestartConfirmation); Assert.Empty(updates.Installs);

            // Restart anyway goes ahead.
            await model.RestartToUpdateCommand.ExecuteAsync(null);
            Assert.Single(updates.Installs); Assert.Equal(1, exits);
            Assert.Equal(1, updates.Downloads);
        }
        finally { model.DiscardSettingsCommand.Execute(null); foreach (var row in model.Queue.ToArray()) row.CancelCommand.Execute(null); }
    }

    [AvaloniaFact]
    public async Task Restart_and_update_goes_ahead_once_the_work_is_done()
    {
        var (_, updates, model) = Create(); using var _ = model; await model.InitializeAsync();
        updates.Result = Available(); await model.CheckForUpdatesAsync(background: true);
        model.RetentionDays = "30";
        await model.UpdateNowCommand.ExecuteAsync(null);
        model.KeepUpdateForLaterCommand.Execute(null);
        model.DiscardSettingsCommand.Execute(null);
        await model.RestartToUpdateCommand.ExecuteAsync(null);
        Assert.Single(updates.Installs); Assert.Equal(UpdatePhase.Installing, model.UpdatePhase);
    }

    [AvaloniaFact]
    public async Task A_refused_download_is_reported_and_nothing_is_installed()
    {
        var (_, updates, model) = Create(); using var _ = model; await model.InitializeAsync();
        updates.Result = Available(); await model.CheckForUpdatesAsync(background: true);
        updates.DownloadFailure = new UpdateValidationException("The download didn't match its published SHA-256, so it was deleted. Nothing was installed.");
        var exits = 0; model.ExitForUpdateRequested += () => exits++;
        await model.UpdateNowCommand.ExecuteAsync(null);
        Assert.Equal("The download didn't match its published SHA-256, so it was deleted. Nothing was installed.", model.Status);
        Assert.Equal(model.Status, model.UpdateStatusText);
        Assert.Empty(updates.Installs); Assert.Equal(0, exits);
        Assert.Equal(UpdatePhase.Available, model.UpdatePhase); Assert.True(model.UpdateNowCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task An_update_download_shows_progress_can_be_cancelled_and_counts_as_running_work()
    {
        var (_, updates, model) = Create(); using var _ = model; await model.InitializeAsync();
        updates.Result = Available(); await model.CheckForUpdatesAsync(background: true);
        updates.DownloadGate = new TaskCompletionSource();
        var running = model.UpdateNowCommand.ExecuteAsync(null);
        await Until(() => model.HasUpdatePercent);
        Assert.True(model.IsDownloadingUpdate); Assert.Equal(40, model.UpdatePercent);
        Assert.Contains("an app update download", model.CloseWarnings);
        Assert.False(model.CheckForUpdatesCommand.CanExecute(null));
        model.CancelUpdateCommand.Execute(null);
        await running;
        Assert.Equal("Update download cancelled. Nothing was installed.", model.Status);
        Assert.False(model.IsDownloadingUpdate); Assert.Empty(model.CloseWarnings); Assert.Empty(updates.Installs);
    }

    [AvaloniaFact]
    public async Task A_portable_copy_gets_the_verified_zip_and_the_one_manual_step()
    {
        var (fake, updates, model) = Create(UpdateInstallKind.Portable); using var _ = model; await model.InitializeAsync();
        updates.Result = Available(); await model.CheckForUpdatesAsync(background: true);
        Assert.Equal("Download update", model.UpdateNowLabel);
        Assert.Contains("140 MB", model.UpdateDetail);
        await model.UpdateNowCommand.ExecuteAsync(null);
        Assert.Empty(updates.Installs);
        Assert.True(model.ShowPortableUpdateSteps);
        Assert.Contains("KeaClip-0.3.0-win-x64.zip", model.PortableUpdateSteps);
        Assert.Contains("move the Data folder", model.PortableUpdateSteps);
        Assert.Equal(new[] { updates.PreparedPath }, fake.Platform.RevealedFiles);
    }

    [AvaloniaFact]
    public async Task A_development_build_shows_the_release_but_cannot_update_itself()
    {
        var (_, updates, model) = Create(UpdateInstallKind.Development); using var _ = model; await model.InitializeAsync();
        updates.Result = Available(); await model.CheckForUpdatesAsync(background: true);
        Assert.True(model.HasUpdateOffer);
        Assert.False(model.CanUpdateNow); Assert.False(model.UpdateNowCommand.CanExecute(null));
        Assert.Contains("development build", model.UpdateDetail);
        Assert.Contains("Development builds", model.UpdateNowHint);
    }

    [AvaloniaFact]
    public async Task The_finished_update_notice_is_shown_once_after_the_relaunch()
    {
        var (fake, updates, model) = Create(); updates.CompletedNotice = "KeaClip was updated to 0.3.0. Your settings and media were kept.";
        using var _ = model; await model.InitializeAsync();
        Assert.Equal("KeaClip was updated to 0.3.0. Your settings and media were kept.", model.Status);
        Assert.Null(updates.CompletedNotice);
        // A card that stays until closed says what's new and links to this version's release notes.
        Assert.Equal("KeaClip updated to " + AppIdentity.Version, model.Notice?.Heading);
        Assert.Equal(new Uri("https://github.com/Langkurst/KeaClip/releases/tag/v" + AppIdentity.Version), model.Notice?.Link);
        model.OpenNoticeLinkCommand.Execute(null);
        Assert.Equal(new Uri("https://github.com/Langkurst/KeaClip/releases/tag/v" + AppIdentity.Version), Assert.Single(fake.Platform.OpenedUris));
        Assert.Null(model.Notice); // opening the notes dismisses the card
    }

    [AvaloniaFact]
    public void An_update_that_did_not_finish_shows_no_whats_new_card_and_highlights_fill_the_card()
    {
        var (_, _, model) = Create(); using var _ = model;
        model.ShowWhatsNew("The update to KeaClip 9.9.9 didn't finish, so this version is still installed.");
        Assert.Null(model.Notice);
        model.ShowWhatsNew("KeaClip was updated to 0.4.6. Your settings and media were kept.", "Copy clip · Share any files or folders");
        Assert.Equal("New: Copy clip · Share any files or folders", model.Notice?.Detail);
        Assert.Equal("See what's new", model.Notice?.LinkText);
    }

    // ----- What the user sees -----

    [AvaloniaFact]
    public async Task The_settings_page_and_sidebar_show_the_offer_through_their_bindings()
    {
        var (_, updates, model) = Create(); using var _ = model; await model.InitializeAsync();
        var window = new MainWindow { DataContext = model }; window.Show();
        try
        {
            var settings = window.FindNamed<Control>("SettingsPage")!;
            var toggle = settings.FindNamed<ToggleSwitch>("CheckForUpdatesSwitch")!;
            Assert.True(toggle.IsChecked);
            toggle.IsChecked = false; Assert.False(model.CheckForUpdates);
            toggle.IsChecked = true; Assert.True(model.CheckForUpdates);
            Assert.False(settings.FindNamed<Border>("UpdateOfferNotice")!.IsVisible);
            Assert.False(window.FindNamed<Button>("UpdateAvailableButton")!.IsVisible);
            Assert.Equal(model.UpdateStatusText, settings.FindNamed<TextBlock>("UpdateStatusText")!.Text);

            updates.Result = Available(); await model.CheckForUpdatesAsync(background: true); Dispatcher.UIThread.RunJobs();
            var badge = window.FindNamed<Button>("UpdateAvailableButton")!;
            Assert.True(badge.IsVisible);
            Assert.Equal("Update to 0.3.0", window.FindNamed<TextBlock>("UpdateBadgeText")!.Text);
            Click(window, badge); Dispatcher.UIThread.RunJobs();
            Assert.Equal(4, model.SelectedTab);
            Assert.True(settings.FindNamed<Border>("UpdateOfferNotice")!.IsVisible);
            Assert.Equal("KeaClip 0.3.0 is available", settings.FindNamed<TextBlock>("UpdateTitleText")!.Text);
            Assert.Equal(model.UpdateDetail, settings.FindNamed<TextBlock>("UpdateDetailText")!.Text);
            Assert.Equal("Faster exact clips.\nA calmer queue.", settings.FindNamed<SelectableTextBlock>("UpdateNotesText")!.Text);
            var updateNow = settings.FindNamed<Button>("UpdateNowButton")!;
            Assert.True(updateNow.IsEffectivelyVisible); Assert.True(updateNow.IsEffectivelyEnabled);
            Assert.False(settings.FindNamed<Border>("UpdateRestartConfirm")!.IsVisible);

            model.RetentionDays = "30";
            await model.UpdateNowCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
            Assert.True(settings.FindNamed<Border>("UpdateRestartConfirm")!.IsVisible);
            Assert.Equal(model.UpdateRestartWarning, settings.FindNamed<TextBlock>("UpdateRestartWarningText")!.Text);
            Assert.False(updateNow.IsEffectivelyVisible);
            Click(window, settings.FindNamed<Button>("KeepUpdateForLaterButton")!); Dispatcher.UIThread.RunJobs();
            Assert.True(settings.FindNamed<Button>("RestartToUpdateButton")!.IsVisible);
        }
        finally { model.DiscardSettingsCommand.Execute(null); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Restart_anyway_closes_the_window_without_the_close_prompt()
    {
        var (_, updates, model) = Create(); using var _ = model; await model.InitializeAsync();
        // Reduced motion: the About section would otherwise still be rising in when Restart anyway is clicked.
        model.ReducedMotion = true;
        var window = new MainWindow { DataContext = model }; window.Show();
        var closed = false; window.Closed += (_, _) => closed = true;
        try
        {
            updates.Result = Available(); await model.CheckForUpdatesAsync(background: true);
            model.RetentionDays = "30";
            await model.UpdateNowCommand.ExecuteAsync(null);
            Assert.False(closed);
            model.SelectedTab = 4; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Click(window, window.FindNamed<Control>("SettingsPage")!.FindNamed<Button>("RestartAnywayButton")!);
            await Until(() => closed);
            Assert.True(window.ExitedForUpdate); Assert.Null(window.ClosePrompt);
            Assert.Single(updates.Installs);
        }
        finally { if (!closed) { model.DiscardSettingsCommand.Execute(null); window.Close(); } }
    }

    private static UpdateCheckResult Available() => new(UpdateCheckOutcome.Available, Offer, "KeaClip 0.3.0 is available.", DateTimeOffset.Now);

    private static void Click(Window window, Control control)
    {
        control.BringIntoView(); window.UpdateLayout();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); window.UpdateLayout();
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, timeout.Token); }
    }
}

public sealed class FakeUpdates(UpdateInstallKind kind) : IUpdateService
{
    public UpdateInstallKind InstallKind { get; } = kind;
    public ReleaseVersion CurrentVersion { get; } = ReleaseVersion.Parse(AppIdentity.Version);
    public Uri FeedUrl { get; private set; } = new("https://example.com/keaclip-update.json");
    public DateTimeOffset? LastChecked { get; private set; }
    public bool Due { get; set; } = true;
    public bool IsBackgroundCheckDue => Due;
    public UpdateCheckResult Result { get; set; } = new(UpdateCheckOutcome.UpToDate, null, "KeaClip 0.2.0 is up to date.", null, Quiet: true);
    public int Checks { get; private set; }
    public bool LastWasBackground { get; private set; }
    public int Downloads { get; private set; }
    public int Cleanups { get; private set; }
    public Exception? DownloadFailure { get; set; }
    public TaskCompletionSource? DownloadGate { get; set; }
    public List<PreparedUpdate> Installs { get; } = [];
    public string? CompletedNotice { get; set; }
    public string PreparedPath => Path.Combine(Path.GetTempPath(), "KeaClipUpdateUiTests", InstallKind == UpdateInstallKind.Portable ? "KeaClip-0.3.0-win-x64.zip" : "KeaClip.Setup.exe");

    public bool TrySetFeed(string? url) => true;
    public Task<UpdateCheckResult> CheckAsync(bool background, CancellationToken cancellationToken = default)
    {
        Checks++; LastWasBackground = background; LastChecked = DateTimeOffset.Now;
        return Task.FromResult(Result);
    }
    public async Task<PreparedUpdate> DownloadAsync(UpdateOffer offer, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Downloads++;
        progress?.Report(new(JobStage.Downloading, 40, "60 MB of 150 MB"));
        if (DownloadGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
        if (DownloadFailure is { } error) throw error;
        return new(offer, PreparedPath, InstallKind, offer.Installer!.Sha256, "Not digitally signed yet; verified against its published SHA-256.");
    }
    public void StartInstall(PreparedUpdate update) => Installs.Add(update);
    public void CleanupDownloads() => Cleanups++;
    public string? TakeCompletedUpdateNotice() { var notice = CompletedNotice; CompletedNotice = null; return notice; }
}
