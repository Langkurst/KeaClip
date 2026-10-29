using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Klippi.Core;

namespace Klippi.Desktop;

/// <summary>Where the app-update flow is.</summary>
public enum UpdatePhase { Idle, Checking, Available, Downloading, ReadyToRestart, PortableReady, Installing }

// App self-update. The feed is asked at most once a day in the background (only while "Check for
// updates automatically" is on and the settings really loaded), quietly; nothing downloads or
// installs until the user chooses Update now. The download is verified (size, SHA-256, signature if
// present) before anything runs. An installed copy then starts the installer's upgrade mode and
// exits; that waits for this process, installs, and starts KeaClip again. Running work or unsaved
// settings are named first, and the user decides whether to restart now.
public sealed partial class MainViewModel
{
    /// <summary>The Settings field key that the sidebar's "update available" button reveals.</summary>
    public const string UpdateSectionKey = "Updates";
    private bool _checkForUpdates = true;
    private UpdatePhase _updatePhase;
    private UpdateOffer? _updateOffer;
    private PreparedUpdate? _preparedUpdate;
    private string _updateStatus = "";
    private double? _updatePercent;
    private bool _updateDismissed, _updateRestartConfirmation;
    private CancellationTokenSource? _updateWork, _updateSchedule;
    private IAsyncRelayCommand? _checkForUpdatesCommand, _updateNowCommand, _restartToUpdateCommand;
    private IRelayCommand? _updateLaterCommand, _cancelUpdateCommand, _keepUpdateForLaterCommand, _showUpdateCommand, _revealPortableUpdateCommand;

    /// <summary>Asks the window to exit now so the started installer can replace the app.</summary>
    public event Action? ExitForUpdateRequested;
    /// <summary>Asks the window to show the update section on the Settings page.</summary>
    public event Action? UpdateRevealRequested;

    /// <summary>How long after startup the first background check waits (keeps startup light).</summary>
    internal TimeSpan UpdateStartupDelay { get; set; } = TimeSpan.FromSeconds(20);
    /// <summary>How often a long-running session asks whether the daily check is due.</summary>
    internal TimeSpan UpdatePollInterval { get; set; } = TimeSpan.FromHours(1);
    /// <summary>The background schedule, for tests.</summary>
    internal Task UpdateScheduleTask { get; private set; } = Task.CompletedTask;

    private IUpdateService? Updates => _services.Updates;

    public bool CheckForUpdates
    {
        get => _checkForUpdates;
        set
        {
            if (!SetProperty(ref _checkForUpdates, value)) return;
            if (!value) _updateSchedule?.Cancel();
            else if (_initialized && !_applyingSavedSettings) RestartUpdateSchedule(TimeSpan.FromSeconds(2));
        }
    }

    public bool UpdatesSupported => Updates is not null;
    public UpdatePhase UpdatePhase => _updatePhase;
    public bool IsCheckingForUpdates => _updatePhase == UpdatePhase.Checking;
    public bool AnimateUpdateRing => (IsCheckingForUpdates || IsDownloadingUpdate && !HasUpdatePercent) && !EffectiveReducedMotion;
    public bool IsDownloadingUpdate => _updatePhase == UpdatePhase.Downloading;
    public double UpdatePercent => _updatePercent ?? 0;
    public bool HasUpdatePercent => _updatePercent is not null;
    public string UpdateStatusText => _updateStatus.Length > 0 ? _updateStatus : Updates is null ? "Updates aren't available in this session." : LastCheckedText(Updates.LastChecked);

    /// <summary>A newer release is known and has not been put off with Later.</summary>
    public bool HasUpdateOffer => _updateOffer is not null && !_updateDismissed;
    /// <summary>The sidebar's quiet "update available" button.</summary>
    public bool ShowUpdateBadge => HasUpdateOffer && _updatePhase != UpdatePhase.Installing;
    public string UpdateBadgeText => _updateOffer is { } offer ? $"Update to {offer.Version}" : "";
    public string UpdateTitle => _updateOffer is { } offer ? $"KeaClip {offer.Version} is available" : "";
    public string UpdateNotes => _updateOffer?.Notes ?? "";
    public bool HasUpdateNotes => UpdateNotes.Length > 0;
    public string UpdateDetail
    {
        get
        {
            if (_updateOffer is not { } offer || Updates is null) return "";
            var date = offer.PublishedAt is { } published ? "Released " + published.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture) + ". " : "";
            return Updates.InstallKind switch
            {
                UpdateInstallKind.Installed => date + (offer.Installer is { } installer ? $"Installer, {Size(installer.Size)}. " : "") + "KeaClip closes, installs the verified update and opens again. Your settings and media are kept.",
                UpdateInstallKind.Portable => date + (offer.Portable is { } zip ? $"Portable ZIP, {Size(zip.Size)}. " : "") + "KeaClip downloads and verifies the new ZIP; you swap the folder yourself (one step, shown after the download).",
                _ => date + "This copy runs from a development build, so it can't update itself. Use the installer or the portable ZIP.",
            };
        }
    }
    public bool CanUpdateNow => _updateOffer is not null && Updates is { InstallKind: not UpdateInstallKind.Development } && _updatePhase is UpdatePhase.Available or UpdatePhase.Idle;
    public string UpdateNowLabel => Updates?.InstallKind == UpdateInstallKind.Portable ? "Download update" : "Update now";
    public string UpdateNowHint => Updates?.InstallKind switch
    {
        UpdateInstallKind.Development => "Development builds can't update themselves. Use the installer or the portable ZIP.",
        UpdateInstallKind.Portable => "Download and verify the new portable ZIP",
        _ => "Download, verify and install the update. KeaClip restarts by itself.",
    };
    public bool ShowUpdateActions => _updatePhase is UpdatePhase.Available or UpdatePhase.Idle;
    public bool IsUpdateReadyToRestart => _updatePhase == UpdatePhase.ReadyToRestart && !_updateRestartConfirmation;
    public bool NeedsUpdateRestartConfirmation => _updatePhase == UpdatePhase.ReadyToRestart && _updateRestartConfirmation;
    /// <summary>What restarting now would stop or lose, in the words the close prompt uses.</summary>
    public string UpdateRestartWarning
    {
        get
        {
            var work = RunningWorkDescriptions; var unsaved = UnsavedSettingsCount; var parts = new List<string>(2);
            if (work.Count > 0) parts.Add("stops " + JoinPlain(work));
            if (unsaved > 0) parts.Add("loses " + (unsaved == 1 ? "1 unsaved settings change" : $"{unsaved} unsaved settings changes"));
            return parts.Count == 0 ? "" : "Restarting to update now " + string.Join(", and ", parts) + ". Wait until it's done, or restart anyway.";
        }
    }
    public bool ShowPortableUpdateSteps => _updatePhase == UpdatePhase.PortableReady && _preparedUpdate is not null;
    public string PortableUpdateSteps => _preparedUpdate is { } prepared
        ? $"Close KeaClip, unzip {Path.GetFileName(prepared.FilePath)} to a new folder, then move the Data folder from this KeaClip folder into the new one. Your settings, history and components come along; nothing else changes."
        : "";

    public IAsyncRelayCommand CheckForUpdatesCommand => _checkForUpdatesCommand ??= new AsyncRelayCommand(() => CheckForUpdatesAsync(background: false), () => Updates is not null && _updatePhase is UpdatePhase.Idle or UpdatePhase.Available);
    public IAsyncRelayCommand UpdateNowCommand => _updateNowCommand ??= new AsyncRelayCommand(UpdateNowAsync, () => CanUpdateNow);
    public IAsyncRelayCommand RestartToUpdateCommand => _restartToUpdateCommand ??= new AsyncRelayCommand(() => { InstallPreparedUpdate(confirmed: _updateRestartConfirmation); return Task.CompletedTask; });
    public IRelayCommand UpdateLaterCommand => _updateLaterCommand ??= new RelayCommand(() =>
    {
        if (_updateOffer is not { } offer) return;
        _updateDismissed = true;
        SetUpdateStatus($"KeaClip {offer.Version} is available. Choose Check for updates when you're ready.");
        NotifyUpdateState();
    });
    public IRelayCommand CancelUpdateCommand => _cancelUpdateCommand ??= new RelayCommand(() => _updateWork?.Cancel());
    /// <summary>"Not yet" in the restart confirmation: keeps the verified installer for a later restart.</summary>
    public IRelayCommand KeepUpdateForLaterCommand => _keepUpdateForLaterCommand ??= new RelayCommand(() =>
    {
        _updateRestartConfirmation = false;
        SetUpdateStatus("The update is downloaded and verified. Choose Restart and update when your work is done.");
        NotifyUpdateState();
    });
    public IRelayCommand ShowUpdateCommand => _showUpdateCommand ??= new RelayCommand(() => { SelectedTab = 4; UpdateRevealRequested?.Invoke(); });
    public IRelayCommand RevealPortableUpdateCommand => _revealPortableUpdateCommand ??= new RelayCommand(() => TryAction(() => { if (_preparedUpdate is { } prepared) _services.Platform.RevealFile(prepared.FilePath); }));

    /// <summary>
    /// After a finished update, a card that stays until closed: what this version added (from the CHANGELOG.md beside
    /// the app) and a link to its release notes. Silent auto-updates would otherwise leave new features undiscovered.
    /// </summary>
    internal void ShowWhatsNew(string notice, string? highlights = null)
    {
        if (!notice.StartsWith("KeaClip was updated", StringComparison.Ordinal)) return;
        highlights ??= WhatsNew.Summary(AppIdentity.Version);
        Notice = new CompletionNote("KeaClip updated to " + AppIdentity.Version,
            highlights ?? "Your settings and media were kept.",
            new Uri("https://github.com/Langkurst/KeaClip/releases/tag/v" + AppIdentity.Version), "See what's new");
    }
    private IRelayCommand? _openNoticeLinkCommand;
    public IRelayCommand OpenNoticeLinkCommand => _openNoticeLinkCommand ??= new RelayCommand(() =>
    {
        if (Notice?.Link is not { } link) return;
        try { _services.Platform.OpenUri(link); Notice = null; }
        catch (Exception error) { CaptureDiagnostics(error); Status = "The release notes couldn't be opened in your browser."; }
    });

    // ----- Background schedule -----
    private void StartUpdates()
    {
        if (Updates is not { } updates || _disposed) return;
        if (!updates.TrySetFeed(_settings.UpdateFeedUrl)) SetUpdateStatus("The update feed in settings.json isn't a secure https link or a local keaclip-update.json file, so the default feed is used.");
        if (updates.TakeCompletedUpdateNotice() is { } notice) { Status = notice; SetUpdateStatus(notice); ShowWhatsNew(notice); }
        _ = Task.Run(updates.CleanupDownloads);
        // Only settings that really loaded can turn automatic checks on.
        if (_initialized && CheckForUpdates) RestartUpdateSchedule(UpdateStartupDelay);
        NotifyUpdateState();
    }

    private void RestartUpdateSchedule(TimeSpan firstDelay)
    {
        _updateSchedule?.Cancel();
        if (Updates is null || _disposed) return;
        var schedule = _updateSchedule = new CancellationTokenSource();
        UpdateScheduleTask = RunUpdateScheduleAsync(firstDelay, schedule.Token);
    }

    private async Task RunUpdateScheduleAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && !_disposed)
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
                delay = UpdatePollInterval;
                if (!CheckForUpdates) return;
                await CheckForUpdatesAsync(background: true, token);
            }
        }
        catch (OperationCanceledException) { /* Turned off, restarted or closing. */ }
    }

    /// <summary>One check. In the background it runs only when due, says nothing in the status line and never shows errors loudly.</summary>
    internal async Task CheckForUpdatesAsync(bool background, CancellationToken token = default)
    {
        if (Updates is not { } updates || _disposed) return;
        if (background && (!CheckForUpdates || !updates.IsBackgroundCheckDue)) return;
        if (_updatePhase is not (UpdatePhase.Idle or UpdatePhase.Available)) return;
        var before = _updatePhase;
        SetUpdatePhase(UpdatePhase.Checking);
        if (!background) SetUpdateStatus("Checking for updates…");
        UpdateCheckResult result;
        try { result = await updates.CheckAsync(background, token); }
        catch (OperationCanceledException) { SetUpdatePhase(before); if (!background) SetUpdateStatus(""); NotifyUpdateState(); return; }
        catch (Exception error) { CaptureDiagnostics(error); result = new(UpdateCheckOutcome.Failed, null, "The update check could not finish. Details are in Diagnostics.", updates.LastChecked, background); }
        if (_disposed) return;
        if (result.Outcome == UpdateCheckOutcome.Available && result.Offer is { } offer)
        {
            if (!background || _updateOffer?.Version != offer.Version) _updateDismissed = false;
            _updateOffer = offer;
            SetUpdatePhase(UpdatePhase.Available);
            SetUpdateStatus(result.Message);
            if (!background) Status = result.Message + " See Settings, About.";
        }
        else
        {
            // Up to date, offline or a feed problem: an offer found earlier stays (it is still newer).
            SetUpdatePhase(_updateOffer is null ? UpdatePhase.Idle : UpdatePhase.Available);
            var text = result.Outcome == UpdateCheckOutcome.UpToDate ? result.Message + " " + LastCheckedText(result.CheckedAt) : result.Message;
            if (!(background && result.Outcome == UpdateCheckOutcome.Skipped && _updateStatus.Length > 0)) SetUpdateStatus(text);
            if (!background) Status = text;
        }
        NotifyUpdateState();
    }

    // ----- Update now -----
    private async Task UpdateNowAsync()
    {
        if (Updates is not { } updates || _updateOffer is not { } offer || !CanUpdateNow) return;
        var work = _updateWork = new CancellationTokenSource();
        SetUpdatePhase(UpdatePhase.Downloading); _updatePercent = null;
        SetUpdateStatus($"Downloading KeaClip {offer.Version}…");
        NotifyUpdateState();
        PreparedUpdate prepared;
        try
        {
            var progress = new UpdateProgress(this, work);
            prepared = await Task.Run(() => updates.DownloadAsync(offer, progress, work.Token), work.Token);
            progress.Close();
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { FinishUpdateAttempt("Update download cancelled. Nothing was installed."); return; }
        catch (UpdateValidationException error) { FinishUpdateAttempt(error.Message); return; }
        catch (Exception error)
        {
            CaptureDiagnostics(error);
            FinishUpdateAttempt("The update couldn't be downloaded: " + MediaValidation.Redact(error.Message) + " Nothing was installed. Try again later."); return;
        }
        finally { if (ReferenceEquals(_updateWork, work)) _updateWork = null; work.Dispose(); }
        if (_disposed) return;
        _preparedUpdate = prepared; _updatePercent = null;
        SetUpdateStatus($"KeaClip {offer.Version} was downloaded and verified. {prepared.SignatureNote}");
        if (prepared.Kind == UpdateInstallKind.Portable)
        {
            SetUpdatePhase(UpdatePhase.PortableReady);
            Status = $"KeaClip {offer.Version} was downloaded and verified. Follow the one step in Settings, About.";
            NotifyUpdateState();
            TryAction(() => _services.Platform.RevealFile(prepared.FilePath));
            return;
        }
        SetUpdatePhase(UpdatePhase.ReadyToRestart);
        NotifyUpdateState();
        InstallPreparedUpdate(confirmed: false);
    }

    private void FinishUpdateAttempt(string message)
    {
        if (_disposed) return;
        _updatePercent = null; _preparedUpdate = null;
        SetUpdatePhase(_updateOffer is null ? UpdatePhase.Idle : UpdatePhase.Available);
        SetUpdateStatus(message); Status = message;
        NotifyUpdateState();
    }

    /// <summary>
    /// Starts the verified installer and exits. Running work or unsaved settings are named first
    /// (the close prompt's wording); only a confirmed restart goes ahead while they exist.
    /// </summary>
    private void InstallPreparedUpdate(bool confirmed)
    {
        if (Updates is not { } updates || _preparedUpdate is not { } prepared || _updatePhase != UpdatePhase.ReadyToRestart || _disposed) return;
        if (!confirmed && CloseWarnings.Count > 0)
        {
            _updateRestartConfirmation = true;
            SetUpdateStatus($"KeaClip {prepared.Offer.Version} is ready to install.");
            NotifyUpdateState();
            return;
        }
        try { updates.StartInstall(prepared); }
        catch (UpdateValidationException error) { _updateRestartConfirmation = false; FinishUpdateAttempt(error.Message); return; }
        catch (Exception error) { CaptureDiagnostics(error); _updateRestartConfirmation = false; FinishUpdateAttempt("The installer could not start. Nothing was changed."); return; }
        _updateRestartConfirmation = false;
        SetUpdatePhase(UpdatePhase.Installing);
        SetUpdateStatus($"Installing KeaClip {prepared.Offer.Version}. KeaClip closes now and opens again when the update is installed.");
        Status = UpdateStatusText;
        NotifyUpdateState();
        ExitForUpdateRequested?.Invoke();
    }

    private sealed class UpdateProgress(MainViewModel owner, CancellationTokenSource work) : IProgress<JobProgress>
    {
        private readonly IProgress<JobProgress> _dispatch = new Progress<JobProgress>(value => owner.ApplyUpdateProgress(work, value));
        private volatile bool _closed;
        public void Report(JobProgress value) { if (!_closed) _dispatch.Report(value); }
        public void Close() => _closed = true;
    }

    private void ApplyUpdateProgress(CancellationTokenSource work, JobProgress value)
    {
        if (_disposed || !ReferenceEquals(_updateWork, work) || _updatePhase != UpdatePhase.Downloading) return;
        var hadPercent = _updatePercent is not null;
        _updatePercent = value.Percent is { } percent && double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : null;
        if (!string.IsNullOrWhiteSpace(value.Message)) SetUpdateStatus((value.Stage == JobStage.Verifying ? "" : $"Downloading KeaClip {_updateOffer?.Version} · ") + value.Message);
        OnPropertyChanged(nameof(UpdatePercent));
        if (hadPercent != (_updatePercent is not null)) { OnPropertyChanged(nameof(HasUpdatePercent)); OnPropertyChanged(nameof(AnimateUpdateRing)); }
    }

    private static string LastCheckedText(DateTimeOffset? checkedAt)
    {
        if (checkedAt is not { } time) return "Not checked yet.";
        var local = time.ToLocalTime();
        var day = local.Date == DateTime.Today ? "today" : local.Date == DateTime.Today.AddDays(-1) ? "yesterday" : local.ToString("d MMMM", CultureInfo.CurrentCulture);
        return $"Last checked {day} at {local.ToString("t", CultureInfo.CurrentCulture)}.";
    }

    private void SetUpdateStatus(string text) { if (SetProperty(ref _updateStatus, text, nameof(UpdateStatusText))) { } }

    private void SetUpdatePhase(UpdatePhase phase)
    {
        if (_updatePhase == phase) return;
        _updatePhase = phase;
        NotifyUpdateState();
    }

    private void NotifyUpdateState()
    {
        foreach (var name in new[]
        {
            nameof(UpdatePhase), nameof(IsCheckingForUpdates), nameof(AnimateUpdateRing), nameof(IsDownloadingUpdate), nameof(UpdatePercent), nameof(HasUpdatePercent),
            nameof(UpdateStatusText), nameof(HasUpdateOffer), nameof(ShowUpdateBadge), nameof(UpdateBadgeText), nameof(UpdateTitle), nameof(UpdateNotes), nameof(HasUpdateNotes),
            nameof(UpdateDetail), nameof(CanUpdateNow), nameof(UpdateNowLabel), nameof(UpdateNowHint), nameof(ShowUpdateActions), nameof(IsUpdateReadyToRestart),
            nameof(NeedsUpdateRestartConfirmation), nameof(UpdateRestartWarning), nameof(ShowPortableUpdateSteps), nameof(PortableUpdateSteps), nameof(UpdatesSupported),
        }) OnPropertyChanged(name);
        CheckForUpdatesCommand.NotifyCanExecuteChanged(); UpdateNowCommand.NotifyCanExecuteChanged();
    }

    private void StopUpdates() { _updateSchedule?.Cancel(); _updateWork?.Cancel(); StopDownloaderUpdates(); }
}
