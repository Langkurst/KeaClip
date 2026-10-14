using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klippi.Core;

namespace Klippi.Desktop;

// Settings page state.
//
// Save model: choices that already take effect across the app the moment they change
// (theme, reduced motion, close behaviour, copied-link detection, completion notices,
// start minimized and downloads at once) are written to the settings file straight away,
// so they can never silently revert after a restart. Typed values (folders, history
// retention, soundboard limits) and the Windows startup entry wait for Save: they are
// tracked against the last loaded or saved _settings snapshot, validated with the same
// rules SaveSettingsAsync uses, and shown in the save bar. Windows startup registration
// still changes only inside SaveSettingsAsync.
public sealed partial class MainViewModel
{
    private static readonly HashSet<string> InstantSettingNames =
    [
        nameof(Theme), nameof(ReducedMotion), nameof(CloseBehavior), nameof(DetectClipboard), nameof(NotifyCompleted), nameof(ConcurrentDownloads),
        nameof(ShareWhenFinishedDefault), nameof(CheckForUpdates), nameof(ShowRecentRecordings),
    ];
    private static readonly HashSet<string> ReviewedSettingNames =
    [
        // Start minimized is saved with Start with Windows: both change the Windows startup entry.
        nameof(DownloadDirectory), nameof(AudioDirectory), nameof(ClipsDirectory), nameof(ArchivesDirectory), nameof(StartWithWindows), nameof(StartMinimized),
        nameof(RetentionDays), nameof(SoundboardSeconds), nameof(SoundboardBytes),
    ];
    // Page order, used to find the first field that needs attention.
    private static readonly string[] SettingsFieldOrder =
    [
        nameof(Theme), nameof(ReducedMotion), nameof(DownloadDirectory), nameof(AudioDirectory), nameof(ClipsDirectory), nameof(ArchivesDirectory),
        nameof(ConcurrentDownloads), nameof(DetectClipboard), nameof(CloseBehavior), nameof(StartWithWindows), nameof(StartMinimized), nameof(NotifyCompleted),
        nameof(ShareWhenFinishedDefault), nameof(SoundboardSeconds), nameof(SoundboardBytes), nameof(ShowRecentRecordings), nameof(RetentionDays), nameof(CheckForUpdates),
    ];
    private static readonly string[] SettingsBarProperties =
    [
        nameof(HasUnsavedSettings), nameof(UnsavedSettingsCount), nameof(UnsavedSettingsTitle), nameof(SettingsSaveBarDetail), nameof(SettingsNeedAttention),
        nameof(SettingsSaveBarNeedsAttention), nameof(HasSettingsSaveError), nameof(ShowSettingsSaveBar), nameof(SettingsSavedVisible), nameof(SettingsSavedTitle),
        nameof(SettingsReviewLabel), nameof(FirstSettingNeedingAttention), nameof(StartupChangePending), nameof(InvalidSettingsCount), nameof(CanSaveSettings),
        nameof(DownloadDirectoryChanged), nameof(AudioDirectoryChanged), nameof(ClipsDirectoryChanged), nameof(ArchivesDirectoryChanged),
        nameof(RetentionDaysChanged), nameof(SoundboardSecondsChanged), nameof(SoundboardBytesChanged), nameof(CanPruneHistoryNow), nameof(PruneHistoryNowLabel),
        nameof(SettingsUnreadable),
    ];
    private static readonly string[] RetentionPresets = ["30", "90", "180"];
    private const int SettingsMaxConcurrentDownloads = 8;
    private static readonly TimeSpan FolderSpaceCacheAge = TimeSpan.FromSeconds(5);

    private bool _settingsObserved, _wasOnSettings;
    private AppSettings? _settingsBaseline;
    private bool _settingsJustSaved, _settingsSavedAutomatically;
    private int _settingsSavedGeneration;
    private string? _settingsSaveError, _preferenceSaveError;
    private Task? _preferenceSave;
    private bool _preferenceSaveAgain, _applyingSavedSettings;
    // The settings file could not be read at startup, so defaults are shown. Nothing is written
    // until the user chooses Save (the unreadable file may still be recoverable); after that
    // save the session behaves exactly as if the settings had loaded.
    private bool _settingsUnreadable;
    private bool _retentionCustom;
    private bool _separateFoldersExpanded;
    private string _defaultFolderSpace = "";
    private bool _defaultFolderSpacePending;
    private CancellationTokenSource? _folderSpaceLookup;
    private readonly Dictionary<string, (DateTime Checked, string Text)> _folderSpaceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<string>> _folderSpaceReads = new(StringComparer.OrdinalIgnoreCase);
    private bool _componentUpdateFailed;
    private int _componentChecks;
    private string _componentTaskStatusAtStart = "";
    private IAsyncRelayCommand? _saveSettingsChanges, _chooseAudioFolder, _chooseClipsFolder, _chooseArchivesFolder, _pruneHistoryNow, _copySettingsDiagnostics, _settingsUpdateDownloader;
    private IRelayCommand? _clearAudioFolder, _clearClipsFolder, _clearArchivesFolder, _discardSettings, _resetSoundboard, _moreConcurrent, _fewerConcurrent, _openSettingsFolder, _openLicenses;

    /// <summary>Starts change notifications for the settings page. Safe to call repeatedly.</summary>
    public void ObserveSettingsChanges()
    {
        if (_settingsObserved || _disposed) return;
        _settingsObserved = true;
        _settingsBaseline ??= _settings;
        _wasOnSettings = IsSettings;
        PropertyChanged += OnSettingsPageModelChanged;
        foreach (var command in SettingsTaskCommands) command.PropertyChanged += OnSettingsTaskChanged;
    }

    partial void OnSettingsLoaded()
    {
        ObserveSettingsChanges();
        // Hand-edited or older files can hold values the page cannot show. Adopt the
        // normalised value as the baseline so it is written back on the next save instead
        // of appearing as a change the user never made.
        var theme = Themes.FirstOrDefault(t => string.Equals(t, _settings.Theme, StringComparison.OrdinalIgnoreCase)) ?? "System";
        var normalised = _settings with { Theme = theme, ConcurrentDownloads = Math.Clamp(_settings.ConcurrentDownloads, 1, SettingsMaxConcurrentDownloads) };
        if (normalised != _settings)
        {
            _settings = normalised;
            _applyingSavedSettings = true;
            try { if (Theme != theme) Theme = theme; }
            finally { _applyingSavedSettings = false; }
        }
        _settingsBaseline = _settings; _settingsSaveError = null; _preferenceSaveError = null; _settingsJustSaved = false;
        // InitializeAsync calls this after every load attempt; _initialized is false only when the file could not be read.
        _settingsUnreadable = !_initialized;
        _retentionCustom = IsCustomRetentionValue(RetentionDays);
        // Separate folders stay collapsed until one is in use (or needs fixing).
        SeparateFoldersExpanded = HasSeparateFolder || SeparateFoldersInvalid;
        SyncSettingsComponents();
        foreach (var name in ReviewedSettingNames) NotifySettingField(name);
        NotifySettingField(nameof(ConcurrentDownloads));
        OnPropertyChanged(nameof(SoundboardCheckedNote)); OnPropertyChanged(nameof(SeparateFoldersHint)); OnPropertyChanged(nameof(RetentionChoice)); OnPropertyChanged(nameof(IsCustomRetention));
        NotifyComponentTaskState();
        NotifySettingsBar();
        if (IsSettings) RefreshDefaultFolderSpace(immediate: true);
    }

    // ----- Instant choices -----
    private AppSettings WithInstantPreferences(AppSettings saved) => saved with
    {
        Theme = Theme, ReducedMotion = ReducedMotion, CloseBehavior = CloseBehavior, DetectClipboard = DetectClipboard,
        NotifyCompleted = NotifyCompleted, ConcurrentDownloads = ConcurrentDownloads, ShareWhenFinished = ShareWhenFinishedDefault, CheckForUpdates = CheckForUpdates,
        ShowRecentRecordings = ShowRecentRecordings,
    };

    private int CountPreferenceChanges()
    {
        var saved = _settings; var changes = 0;
        if (!string.Equals(Theme, saved.Theme, StringComparison.Ordinal)) changes++;
        if (ReducedMotion != saved.ReducedMotion) changes++;
        if (CloseBehavior != saved.CloseBehavior) changes++;
        if (DetectClipboard != saved.DetectClipboard) changes++;
        if (ShowRecentRecordings != saved.ShowRecentRecordings) changes++;
        if (NotifyCompleted != saved.NotifyCompleted) changes++;
        if (ConcurrentDownloads != saved.ConcurrentDownloads) changes++;
        if (ShareWhenFinishedDefault != saved.ShareWhenFinished) changes++;
        if (CheckForUpdates != saved.CheckForUpdates) changes++;
        return changes;
    }

    /// <summary>Completes when any in-flight save of an instantly applied choice has finished.</summary>
    public Task SettingsPreferenceSaveTask => _preferenceSave ?? Task.CompletedTask;

    private void QueuePreferenceSave()
    {
        // Not before settings loaded, and not while they are unreadable: those choices wait in
        // the save bar instead, so an automatic write never replaces a file that could still be
        // recovered. A successful Save sets _initialized (see AdoptSavedSettings).
        if (!_initialized || _disposed || _applyingSavedSettings) return;
        if (_preferenceSave is { IsCompleted: false }) { _preferenceSaveAgain = true; return; }
        _preferenceSave = SavePreferencesAsync();
    }

    private async Task SavePreferencesAsync()
    {
        do
        {
            _preferenceSaveAgain = false;
            // A full save writes these values as well; the IsBusy handler checks again when it ends.
            if (SaveSettingsCommand.IsRunning) return;
            var saved = _settings; var next = WithInstantPreferences(saved);
            if (next == saved) break;
            try { await _services.Store.SaveSettingsAsync(next, CancellationToken.None); }
            catch (Exception error)
            {
                if (_disposed) return;
                CaptureDiagnostics(error);
                _preferenceSaveError = SettingsFailureText(MediaValidation.Redact(error.Message));
                Status = "Your choice applies now, but it could not be saved: " + _preferenceSaveError;
                NotifySettingsBar(); return;
            }
            if (_disposed) return;
            // If a full save replaced the snapshot meanwhile, it holds the newest values; check again.
            if (ReferenceEquals(_settings, saved)) { _settings = next; _settingsBaseline = next; }
            else _preferenceSaveAgain = true;
            _preferenceSaveError = null;
            FlashSettingsSaved(automatic: true);
        }
        while (_preferenceSaveAgain && !_disposed);
    }

    // ----- Save bar -----
    public int UnsavedSettingsCount
    {
        get
        {
            var changes = (DownloadDirectoryChanged ? 1 : 0) + (AudioDirectoryChanged ? 1 : 0) + (ClipsDirectoryChanged ? 1 : 0) + (ArchivesDirectoryChanged ? 1 : 0)
                + (StartWithWindows != _settings.StartWithWindows ? 1 : 0) + (StartMinimized != _settings.StartMinimized ? 1 : 0) + (RetentionDaysChanged ? 1 : 0) + (SoundboardSecondsChanged ? 1 : 0) + (SoundboardBytesChanged ? 1 : 0);
            // Instant choices only count while they cannot be saved automatically.
            return PreferencesWaitForSave ? changes + CountPreferenceChanges() : changes;
        }
    }
    // Instant choices wait for Save after their automatic save failed, or while the settings file is unreadable.
    private bool PreferencesWaitForSave => _preferenceSaveError is not null || _settingsUnreadable;
    /// <summary>True when the settings file could not be read at startup and nothing has been saved since.</summary>
    public bool SettingsUnreadable => _settingsUnreadable;
    public bool HasUnsavedSettings => UnsavedSettingsCount > 0;
    /// <summary>True while the save bar has something to say: changes, a saved value that needs fixing, a failed save or unreadable settings.</summary>
    public bool SettingsNeedAttention => HasUnsavedSettings || InvalidSettingsCount > 0 || HasSettingsSaveError || _settingsUnreadable;
    public bool ShowSettingsSaveBar => SettingsNeedAttention;
    /// <summary>The short-lived "saved" confirmation shown after a save, automatic or manual.</summary>
    public bool SettingsSavedVisible => _settingsJustSaved && !SettingsNeedAttention;
    public string SettingsSavedTitle => _settingsSavedAutomatically ? "Change saved" : "Settings saved";
    public string UnsavedSettingsTitle => UnsavedSettingsCount is var count and > 0 ? (count == 1 ? "1 unsaved change" : $"{count} unsaved changes")
        : InvalidSettingsCount is var invalid and > 0 ? (invalid == 1 ? "A saved setting needs fixing" : $"{invalid} saved settings need fixing")
        : _settingsUnreadable ? "Saved settings could not be read"
        : "Settings not saved";
    // Mirrors when SaveSettingsAsync rewrites the startup entry: Start minimized only reaches
    // Windows through that entry, so on its own it changes nothing there.
    public bool StartupChangePending => StartWithWindows != _settings.StartWithWindows || StartWithWindows && StartMinimized != _settings.StartMinimized;
    /// <summary>What Start minimized does now; it only works with Start with Windows.</summary>
    public string StartMinimizedHint => StartWithWindows
        ? "When KeaClip starts with Windows, it opens minimised so it stays out of the way. Opening it yourself always shows the window."
        : "Turn on Start with Windows to use this. Opening KeaClip yourself always shows the window.";
    public bool HasSettingsSaveError => _settingsSaveError is not null || _preferenceSaveError is not null;
    public bool SettingsSaveBarNeedsAttention => HasSettingsSaveError || InvalidSettingsCount > 0 || _settingsUnreadable;
    public bool CanSaveSettings => CanRunSettingsTask && InvalidSettingsCount == 0;

    // The page's one busy gate and progress source. With per-area operation lanes these map to
    // CanMaintain and MaintenancePercent / HasMaintenancePercent (see the change handler).
    /// <summary>Whether a settings or component task (save, check, prepare, update, restore, prune) can start now.</summary>
    public bool CanRunSettingsTask => CanMaintain;
    public double SettingsTaskPercent => MaintenancePercent;
    public bool HasSettingsTaskPercent => HasMaintenancePercent;
    public string SettingsSaveBarDetail => _settingsSaveError is { } error ? "Not saved. " + error
        : _preferenceSaveError is { } preference ? "Some choices apply now but could not be saved: " + preference + " Save to try again."
        : InvalidSettingsCount is var invalid and > 0 ? (invalid == 1 ? "Fix the highlighted field to save." : $"Fix the {invalid} highlighted fields to save.")
        : !CanRunSettingsTask ? "Finish the current task to save."
        : _settingsUnreadable ? "Defaults are in use. Save to keep what you see here; it replaces the settings file that could not be read."
        : StartupChangePending ? "Windows startup changes when you save."
        : UnsavedSettingsCount == 1 ? "Save to keep it after a restart." : "Save to keep them after a restart.";
    public string SettingsReviewLabel => InvalidSettingsCount > 0 ? "Go to field" : "Show changes";

    /// <summary>The first setting, in page order, that is invalid or else changed; null when none.</summary>
    public string? FirstSettingNeedingAttention
    {
        get
        {
            foreach (var name in SettingsFieldOrder) if (IsSettingInvalid(name)) return name;
            foreach (var name in SettingsFieldOrder) if (IsSettingChanged(name)) return name;
            return null;
        }
    }

    private bool IsSettingInvalid(string name) => name switch
    {
        nameof(DownloadDirectory) => DownloadDirectoryInvalid, nameof(AudioDirectory) => AudioDirectoryInvalid, nameof(ClipsDirectory) => ClipsDirectoryInvalid,
        nameof(ArchivesDirectory) => ArchivesDirectoryInvalid, nameof(RetentionDays) => RetentionDaysInvalid, nameof(SoundboardSeconds) => SoundboardSecondsInvalid,
        nameof(SoundboardBytes) => SoundboardBytesInvalid, _ => false,
    };

    private bool IsSettingChanged(string name) => name switch
    {
        nameof(DownloadDirectory) => DownloadDirectoryChanged, nameof(AudioDirectory) => AudioDirectoryChanged, nameof(ClipsDirectory) => ClipsDirectoryChanged,
        nameof(ArchivesDirectory) => ArchivesDirectoryChanged, nameof(StartWithWindows) => StartWithWindows != _settings.StartWithWindows, nameof(RetentionDays) => RetentionDaysChanged,
        nameof(SoundboardSeconds) => SoundboardSecondsChanged, nameof(SoundboardBytes) => SoundboardBytesChanged,
        nameof(Theme) => PreferencesWaitForSave && Theme != _settings.Theme,
        nameof(ReducedMotion) => PreferencesWaitForSave && ReducedMotion != _settings.ReducedMotion,
        nameof(CloseBehavior) => PreferencesWaitForSave && CloseBehavior != _settings.CloseBehavior,
        nameof(DetectClipboard) => PreferencesWaitForSave && DetectClipboard != _settings.DetectClipboard,
        nameof(ShowRecentRecordings) => PreferencesWaitForSave && ShowRecentRecordings != _settings.ShowRecentRecordings,
        nameof(NotifyCompleted) => PreferencesWaitForSave && NotifyCompleted != _settings.NotifyCompleted,
        nameof(StartMinimized) => StartMinimized != _settings.StartMinimized,
        nameof(ConcurrentDownloads) => PreferencesWaitForSave && ConcurrentDownloads != _settings.ConcurrentDownloads,
        nameof(ShareWhenFinishedDefault) => PreferencesWaitForSave && ShareWhenFinishedDefault != _settings.ShareWhenFinished,
        nameof(CheckForUpdates) => PreferencesWaitForSave && CheckForUpdates != _settings.CheckForUpdates,
        _ => false,
    };

    /// <summary>
    /// Saves every setting through <see cref="SaveSettingsCommand"/> (also on a clean page, which
    /// re-applies history retention and rewrites the settings file) and keeps a failure visible
    /// beside the Save button.
    /// </summary>
    public IAsyncRelayCommand SaveSettingsChangesCommand => _saveSettingsChanges ??= new AsyncRelayCommand(SaveSettingsChangesAsync, () => CanSaveSettings);

    private async Task SaveSettingsChangesAsync()
    {
        NormaliseTypedSettings();
        if (_preferenceSave is { IsCompleted: false } pending) await pending;
        var before = _settings;
        _settingsSaveError = null; _preferenceSaveError = null;
        await SaveSettingsCommand.ExecuteAsync(null);
        if (_disposed) return;
        if (ReferenceEquals(before, _settings)) _settingsSaveError = SettingsFailureText(Status);
        // Usually adopted already when the operation ended; this also covers a save that ends
        // while other work keeps the app busy.
        else AdoptSavedSettings();
        NotifySettingsBar();
        // A choice made while the file was being written is saved now.
        QueuePreferenceSave();
    }

    // The save path parses with the invariant culture; accept a comma decimal and digit
    // grouping here and hand it plain numbers.
    private void NormaliseTypedSettings()
    {
        if (TrySoundboardSeconds(out var seconds) && seconds.ToString(CultureInfo.InvariantCulture) is var secondsText && secondsText != SoundboardSeconds) SoundboardSeconds = secondsText;
        if (TrySoundboardBytes(out var bytes) && bytes.ToString(CultureInfo.InvariantCulture) is var bytesText && bytesText != SoundboardBytes) SoundboardBytes = bytesText;
        if (TryRetentionDays(out var days) && days is { } value && value.ToString(CultureInfo.InvariantCulture) is var daysText && daysText != RetentionDays) RetentionDays = daysText;
    }

    // A short, plain reason for the save bar. Common file-system failures get a friendly
    // sentence; anything else is shown redacted (local paths removed), with the full
    // details in the diagnostics.
    private static string SettingsFailureText(string message)
    {
        const string prefix = "Could not finish: ";
        var text = (message.StartsWith(prefix, StringComparison.Ordinal) ? message[prefix.Length..] : message).Trim();
        if (text.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)) return "Another program is using the settings file. Close it and try again.";
        if (text.Contains("read-only", StringComparison.OrdinalIgnoreCase) || text.Contains("access", StringComparison.OrdinalIgnoreCase) && text.Contains("denied", StringComparison.OrdinalIgnoreCase))
            return "KeaClip can't write its settings file. Check that its folder isn't read-only.";
        if (text.Contains("not enough space", StringComparison.OrdinalIgnoreCase) || text.Contains("disk is full", StringComparison.OrdinalIgnoreCase)) return "The drive is full. Free some space and try again.";
        text = MediaValidation.RedactDiagnostics(text).Trim();
        return text.Length > 0 && !".!?".Contains(text[^1]) ? text + "." : text;
    }

    public IRelayCommand DiscardSettingsCommand => _discardSettings ??= new RelayCommand(DiscardSettingChanges);

    private void DiscardSettingChanges()
    {
        var saved = _settings;
        // Put every value back first, then check once, so no half-restored state is written.
        _applyingSavedSettings = true;
        try
        {
            DownloadDirectory = saved.DownloadDirectory; AudioDirectory = saved.AudioDirectory ?? ""; ClipsDirectory = saved.ClipsDirectory ?? ""; ArchivesDirectory = saved.ArchivesDirectory ?? "";
            StartWithWindows = saved.StartWithWindows;
            RetentionDays = saved.HistoryRetentionDays?.ToString(CultureInfo.InvariantCulture) ?? "";
            SoundboardSeconds = saved.SoundboardMaxSeconds.ToString(CultureInfo.InvariantCulture); SoundboardBytes = saved.SoundboardTargetBytes.ToString(CultureInfo.InvariantCulture);
            // Choices whose automatic save failed go back too, so what you see matches the file.
            Theme = saved.Theme; ConcurrentDownloads = saved.ConcurrentDownloads; DetectClipboard = saved.DetectClipboard; ShowRecentRecordings = saved.ShowRecentRecordings; StartMinimized = saved.StartMinimized;
            ReducedMotion = saved.ReducedMotion; NotifyCompleted = saved.NotifyCompleted; CloseBehavior = saved.CloseBehavior; ShareWhenFinishedDefault = saved.ShareWhenFinished; CheckForUpdates = saved.CheckForUpdates;
        }
        finally { _applyingSavedSettings = false; }
        _retentionCustom = IsCustomRetentionValue(RetentionDays);
        _settingsSaveError = null; _preferenceSaveError = null;
        OnPropertyChanged(nameof(RetentionChoice)); OnPropertyChanged(nameof(IsCustomRetention));
        NotifySettingsBar();
        Status = _settingsUnreadable ? "Unsaved changes discarded. Defaults stay in use until you save." : "Unsaved settings discarded. Your saved settings are back.";
    }

    // ----- Per-field change markers -----
    public bool DownloadDirectoryChanged => !SameFolder(DownloadDirectory, _settings.DownloadDirectory);
    public bool AudioDirectoryChanged => !SameFolder(Empty(AudioDirectory), Empty(_settings.AudioDirectory ?? ""));
    public bool ClipsDirectoryChanged => !SameFolder(Empty(ClipsDirectory), Empty(_settings.ClipsDirectory ?? ""));
    public bool ArchivesDirectoryChanged => !SameFolder(Empty(ArchivesDirectory), Empty(_settings.ArchivesDirectory ?? ""));
    public bool RetentionDaysChanged => TryRetentionDays(out var days) ? days != _settings.HistoryRetentionDays : !SameText(RetentionDays, _settings.HistoryRetentionDays?.ToString(CultureInfo.InvariantCulture));
    public bool SoundboardSecondsChanged => TrySoundboardSeconds(out var seconds) ? seconds != _settings.SoundboardMaxSeconds : !SameText(SoundboardSeconds, _settings.SoundboardMaxSeconds.ToString(CultureInfo.InvariantCulture));
    public bool SoundboardBytesChanged => TrySoundboardBytes(out var bytes) ? bytes != _settings.SoundboardTargetBytes : !SameText(SoundboardBytes, _settings.SoundboardTargetBytes.ToString(CultureInfo.InvariantCulture));

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static bool SameText(string? current, string? saved) => string.Equals((current ?? "").Trim(), (saved ?? "").Trim(), StringComparison.Ordinal);
    private static bool SameFolder(string? current, string? saved)
    {
        if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(saved)) return string.IsNullOrWhiteSpace(current) == string.IsNullOrWhiteSpace(saved);
        // Two complete paths are compared as folders; anything else (a relative or broken
        // path) is compared as typed, so a saved value that needs fixing is not a change.
        if (Path.IsPathFullyQualified(current) && Path.IsPathFullyQualified(saved) && SettingsFullPathOrNull(current) is { } a && SettingsFullPathOrNull(saved) is { } b)
            return string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), PathComparison);
        return string.Equals(current.Trim(), saved.Trim(), PathComparison);
    }

    // ----- Folders -----
    public IAsyncRelayCommand ChooseAudioFolderCommand => _chooseAudioFolder ??= new AsyncRelayCommand(() => PickSettingsFolderAsync(folder => AudioDirectory = folder));
    public IAsyncRelayCommand ChooseClipsFolderCommand => _chooseClipsFolder ??= new AsyncRelayCommand(() => PickSettingsFolderAsync(folder => ClipsDirectory = folder));
    public IAsyncRelayCommand ChooseArchivesFolderCommand => _chooseArchivesFolder ??= new AsyncRelayCommand(() => PickSettingsFolderAsync(folder => ArchivesDirectory = folder));
    public IRelayCommand ClearAudioFolderCommand => _clearAudioFolder ??= new RelayCommand(() => AudioDirectory = "");
    public IRelayCommand ClearClipsFolderCommand => _clearClipsFolder ??= new RelayCommand(() => ClipsDirectory = "");
    public IRelayCommand ClearArchivesFolderCommand => _clearArchivesFolder ??= new RelayCommand(() => ArchivesDirectory = "");

    private async Task PickSettingsFolderAsync(Action<string> apply)
    {
        try { if (await _services.Platform.PickFolderAsync() is { Length: > 0 } folder) apply(folder); }
        catch (Exception error) { Status = FriendlyError(error); }
    }

    public bool DownloadDirectoryInvalid => !IsCompleteSettingsFolder(DownloadDirectory);
    public bool AudioDirectoryInvalid => !IsOptionalSettingsFolder(AudioDirectory);
    public bool ClipsDirectoryInvalid => !IsOptionalSettingsFolder(ClipsDirectory);
    public bool ArchivesDirectoryInvalid => !IsOptionalSettingsFolder(ArchivesDirectory);
    public bool SeparateFoldersInvalid => AudioDirectoryInvalid || ClipsDirectoryInvalid || ArchivesDirectoryInvalid;
    public string SeparateFoldersHint => DownloadDirectoryInvalid ? "A blank folder uses your default download folder."
        : "A blank folder uses your default: " + DownloadDirectory.Trim();
    private bool HasSeparateFolder => !string.IsNullOrWhiteSpace(AudioDirectory) || !string.IsNullOrWhiteSpace(ClipsDirectory) || !string.IsNullOrWhiteSpace(ArchivesDirectory);
    /// <summary>Whether the Separate folders section is open. It opens by itself when a folder is set or needs fixing.</summary>
    public bool SeparateFoldersExpanded { get => _separateFoldersExpanded; set => SetProperty(ref _separateFoldersExpanded, value); }
    /// <summary>
    /// Settings shows the everyday choices first; startup, notifications, history, editor presets, watch links,
    /// components and storage sit under Advanced settings, which opens by itself when one of them needs attention.
    /// </summary>
    public bool ShowAdvancedSettings
    {
        get => _showAdvancedSettings;
        set { if (SetProperty(ref _showAdvancedSettings, value)) OnPropertyChanged(nameof(AdvancedSettingsLabel)); }
    }
    private bool _showAdvancedSettings;
    public string AdvancedSettingsLabel => ShowAdvancedSettings ? "Hide advanced settings" : "Show advanced settings";
    private IRelayCommand? _toggleAdvancedSettingsCommand;
    public IRelayCommand ToggleAdvancedSettingsCommand => _toggleAdvancedSettingsCommand ??= new RelayCommand(() => ShowAdvancedSettings = !ShowAdvancedSettings);
    public string SeparateFoldersSummary
    {
        get
        {
            var names = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(AudioDirectory)) names.Add("Music");
            if (!string.IsNullOrWhiteSpace(ClipsDirectory)) names.Add(names.Count == 0 ? "Clips" : "clips");
            if (!string.IsNullOrWhiteSpace(ArchivesDirectory)) names.Add(names.Count == 0 ? "Archives" : "archives");
            return names.Count switch
            {
                0 => "Music, clips and archives use the default folder.",
                1 => names[0] == "Music" ? "Music uses its own folder." : names[0] + " use their own folder.",
                2 => $"{names[0]} and {names[1]} use their own folders.",
                _ => "Music, clips and archives use their own folders.",
            };
        }
    }

    /// <summary>Free space where the default folder lives. Read off the UI thread, so a slow or offline drive never blocks typing.</summary>
    public string DefaultFolderSpace => _defaultFolderSpace;
    public bool DefaultFolderSpacePending => _defaultFolderSpacePending;

    private void RefreshDefaultFolderSpace(bool immediate, bool bypassCache = false)
    {
        _folderSpaceLookup?.Cancel(); _folderSpaceLookup = null;
        if (_disposed || !IsSettings || DownloadDirectoryInvalid) { SetFolderSpace("", pending: false); return; }
        string folder, root;
        try { folder = Path.GetFullPath(DownloadDirectory.Trim()); root = Path.GetPathRoot(folder) ?? ""; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException) { folder = root = ""; }
        if (root.Length == 0) { SetFolderSpace("Drive space is unavailable for this folder.", pending: false); return; }
        if (!bypassCache && _folderSpaceCache.TryGetValue(root, out var cached) && DateTime.UtcNow - cached.Checked < FolderSpaceCacheAge) { SetFolderSpace(cached.Text, pending: false); return; }
        var lookup = new CancellationTokenSource(); _folderSpaceLookup = lookup;
        // Keep the last reading on screen while typing within the same drive.
        SetFolderSpace(_folderSpaceCache.TryGetValue(root, out var previous) ? previous.Text : "Checking free space…", pending: true);
        _ = ReadFolderSpaceAsync(folder, root, immediate ? TimeSpan.Zero : TimeSpan.FromMilliseconds(250), lookup);
    }

    private async Task ReadFolderSpaceAsync(string folder, string root, TimeSpan delay, CancellationTokenSource lookup)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, lookup.Token);
            // A read cannot be cancelled and an offline drive can take a long time to answer,
            // so keep at most one read per drive in flight and share its answer.
            // Runs on the UI thread before and after each await, so the dictionary needs no lock.
            if (!_folderSpaceReads.TryGetValue(root, out var read)) _folderSpaceReads[root] = read = Task.Run(() => ReadFreeSpace(folder, root), CancellationToken.None);
            string text;
            try { text = await read; }
            finally { if (_folderSpaceReads.TryGetValue(root, out var current) && ReferenceEquals(current, read)) _folderSpaceReads.Remove(root); }
            if (_disposed) return;
            _folderSpaceCache[root] = (DateTime.UtcNow, text); // still a valid reading for the next lookup on this drive
            if (lookup.IsCancellationRequested) return;
            SetFolderSpace(text, pending: false);
        }
        catch (OperationCanceledException) { /* A newer lookup replaced this one. */ }
        finally { if (ReferenceEquals(_folderSpaceLookup, lookup)) _folderSpaceLookup = null; lookup.Dispose(); }
    }

    // Asks for the free space of the nearest existing folder, so mapped and UNC (\\server\share)
    // folders work as well as local drives. Runs on a worker thread.
    private static string ReadFreeSpace(string folder, string root)
    {
        try
        {
            var probe = folder;
            while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe)) probe = Path.GetDirectoryName(probe);
            if (string.IsNullOrEmpty(probe)) return "Drive space is unavailable right now.";
            var label = root.Length > 1 ? Path.TrimEndingDirectorySeparator(root) : root;
            if (OperatingSystem.IsWindows())
            {
                return GetDiskFreeSpaceEx(Path.EndsInDirectorySeparator(probe) ? probe : probe + Path.DirectorySeparatorChar, out var available, out _, out _)
                    ? $"{Size((long)Math.Min(available, long.MaxValue))} free on {label}" : "Drive space is unavailable right now.";
            }
            var drive = new DriveInfo(root);
            return drive.IsReady ? $"{Size(drive.AvailableFreeSpace)} free on {label}" : "Drive space is unavailable right now.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { return "Drive space is unavailable for this folder."; }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);

    private void SetFolderSpace(string text, bool pending)
    {
        SetProperty(ref _defaultFolderSpace, text, nameof(DefaultFolderSpace));
        if (_defaultFolderSpacePending != pending) { _defaultFolderSpacePending = pending; OnPropertyChanged(nameof(DefaultFolderSpacePending)); }
    }

    // ----- Downloads at once -----
    public IRelayCommand IncreaseConcurrentDownloadsCommand => _moreConcurrent ??= new RelayCommand(() => ConcurrentDownloads++, () => ConcurrentDownloads < SettingsMaxConcurrentDownloads);
    public IRelayCommand DecreaseConcurrentDownloadsCommand => _fewerConcurrent ??= new RelayCommand(() => ConcurrentDownloads--, () => ConcurrentDownloads > 1);
    public string ConcurrentDownloadsDescription => ConcurrentDownloads == 1 ? "1 download at a time" : $"{ConcurrentDownloads} downloads at the same time";

    // ----- History retention -----
    public bool RetentionDaysInvalid => !TryRetentionDays(out _);
    /// <summary>"All", "30", "90", "180" or "Custom"; the days box shows only for Custom.</summary>
    public string RetentionChoice
    {
        get => _retentionCustom || IsCustomRetentionValue(RetentionDays) ? "Custom" : string.IsNullOrWhiteSpace(RetentionDays) ? "All" : RetentionDays.Trim();
        set
        {
            if (value == "Custom")
            {
                if (_retentionCustom) return;
                _retentionCustom = true;
                if (string.IsNullOrWhiteSpace(RetentionDays)) RetentionDays = "365";
            }
            else if (value == "All" || RetentionPresets.Contains(value))
            {
                if (!_retentionCustom && value == RetentionChoice) return;
                _retentionCustom = false;
                RetentionDays = value == "All" ? "" : value;
            }
            else return;
            OnPropertyChanged(); OnPropertyChanged(nameof(IsCustomRetention));
        }
    }
    public bool IsCustomRetention => RetentionChoice == "Custom";
    private static bool IsCustomRetentionValue(string value) => !string.IsNullOrWhiteSpace(value) && !RetentionPresets.Contains(value.Trim());

    public bool CanPruneHistoryNow => _settings.HistoryRetentionDays is > 0 && !RetentionDaysChanged;
    public string PruneHistoryNowLabel => _settings.HistoryRetentionDays is { } days ? $"Remove records older than {days} days now" : "";
    public IAsyncRelayCommand PruneHistoryNowCommand => _pruneHistoryNow ??= new AsyncRelayCommand(() => RunAsync(OperationLane.Maintenance, async ct =>
    {
        if (_settings.HistoryRetentionDays is not { } days) return;
        var before = _history.Count;
        await _services.Store.PruneHistoryAsync(days, ct); await ReloadHistoryAsync(ct);
        var removed = Math.Max(0, before - _history.Count);
        Status = removed == 0 ? $"No history records were older than {days} days. Media files are never deleted."
            : $"Removed {removed:N0} history {(removed == 1 ? "record" : "records")} older than {days} days. Your media files were kept.";
    }));

    // ----- Soundboard -----
    public bool SoundboardSecondsInvalid => !TrySoundboardSeconds(out _);
    public bool SoundboardBytesInvalid => !TrySoundboardBytes(out _);
    public string SoundboardBytesHint => TrySoundboardBytes(out var bytes) ? "About " + Size(bytes) : "";
    public string SoundboardCheckedNote => (DateTime.TryParseExact(_settings.PresetCheckedDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? $"Discord soundboard limits were last checked on {date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}." : "Discord soundboard limits were checked when this preset was added.") + " Discord can change them, so edit these values when needed.";
    public IRelayCommand ResetSoundboardDefaultsCommand => _resetSoundboard ??= new RelayCommand(() =>
    {
        var defaults = new AppSettings();
        SoundboardSeconds = defaults.SoundboardMaxSeconds.ToString(CultureInfo.InvariantCulture);
        SoundboardBytes = defaults.SoundboardTargetBytes.ToString(CultureInfo.InvariantCulture);
    });

    // ----- Validation (mirrors SaveSettingsAsync, after NormaliseTypedSettings) -----
    public int InvalidSettingsCount => (DownloadDirectoryInvalid ? 1 : 0) + (AudioDirectoryInvalid ? 1 : 0) + (ClipsDirectoryInvalid ? 1 : 0) + (ArchivesDirectoryInvalid ? 1 : 0)
        + (RetentionDaysInvalid ? 1 : 0) + (SoundboardSecondsInvalid ? 1 : 0) + (SoundboardBytesInvalid ? 1 : 0);

    private static bool IsCompleteSettingsFolder(string value) => !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value) && SettingsFullPathOrNull(value) is not null;
    private static bool IsOptionalSettingsFolder(string value) => string.IsNullOrWhiteSpace(value) || Path.IsPathFullyQualified(value);
    private static string? SettingsFullPathOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return Path.GetFullPath(value); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException) { return null; }
    }
    private bool TryRetentionDays(out int? days)
    {
        days = null;
        if (string.IsNullOrWhiteSpace(RetentionDays)) return true;
        // The same limit SaveSettingsAsync and the settings store enforce, so the field never
        // looks valid for a value that is certain to fail on Save.
        if (!int.TryParse(RetentionDays, out var value) || value < 1 || value > ILocalStore.MaxHistoryRetentionDays) return false;
        days = value; return true;
    }
    private bool TrySoundboardSeconds(out double seconds)
    {
        var text = SoundboardSeconds.Trim();
        if (text.Contains(',') && !text.Contains('.')) text = text.Replace(',', '.');
        return double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out seconds) && double.IsFinite(seconds) && seconds > 0 && seconds <= 300;
    }
    private bool TrySoundboardBytes(out long bytes)
    {
        var digits = new string(SoundboardBytes.Where(c => c is not (',' or '_' or '\'' or ' ' or ' ' or ' ')).ToArray());
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out bytes) && bytes is >= 1024 and <= 100_000_000;
    }

    // ----- Components -----
    // The page shows its own copy of the component list and updates it in place once a check
    // has finished, so rows never disappear while a check runs and chips can animate.
    public ObservableCollection<SettingsComponentRow> SettingsComponents { get; } = [];
    public bool HasSettingsComponents => SettingsComponents.Count > 0;
    public bool SettingsComponentsAllReady => SettingsComponents.Count > 0 && SettingsComponents.All(c => c.Ready);
    public bool SettingsComponentsNeedAttention => SettingsComponents.Count > 0 && !SettingsComponentsAllReady;
    public string SettingsComponentsSummary => SettingsComponents.Count == 0 ? "Not checked" : SettingsComponentsAllReady ? "All ready" : $"{SettingsComponents.Count(c => c.Ready)} of {SettingsComponents.Count} ready";
    public bool IsComponentTaskRunning => ProvisionCommand.IsRunning || UpdateDownloaderCommand.IsRunning || RollbackComponentsCommand.IsRunning || InspectComponentsCommand.IsRunning || SupportedSitesCommand.IsRunning;
    /// <summary>Spins only for a component task with no known percentage, while Settings is shown and motion is allowed.</summary>
    public bool AnimateComponentRing => IsComponentTaskRunning && AnimateBusyIndicator && !HasSettingsTaskPercent && IsSettings;
    public bool ShowComponentSkeleton => SettingsComponents.Count == 0 && (IsComponentTaskRunning || (!CanRunSettingsTask && !_initialized));
    public bool ShowComponentsNotChecked => SettingsComponents.Count == 0 && !ShowComponentSkeleton;
    public bool ComponentUpdateFailed => _componentUpdateFailed;
    /// <summary>What the running component task is doing, named by the page rather than the shared status line.</summary>
    public string ComponentTaskTitle => ProvisionCommand.IsRunning ? "Preparing components…" : UpdateDownloaderCommand.IsRunning ? "Updating the downloader…"
        : RollbackComponentsCommand.IsRunning ? "Restoring the previous components…" : SupportedSitesCommand.IsRunning ? "Reading the supported sites list…"
        : InspectComponentsCommand.IsRunning ? "Checking components…" : "";
    /// <summary>Progress reported by the task itself; empty until it reports, so older messages never show here.</summary>
    public string ComponentTaskDetail => IsComponentTaskRunning && !string.Equals(Status, _componentTaskStatusAtStart, StringComparison.Ordinal) ? Status : "";
    public bool HasComponentTaskDetail => ComponentTaskDetail.Length > 0;
    public bool IsCleaningTemporaryFiles => CleanTemporaryFilesCommand.IsRunning;
    public bool AnimateCleanupRing => IsCleaningTemporaryFiles && AnimateBusyIndicator && IsSettings;

    private IEnumerable<IAsyncRelayCommand> SettingsTaskCommands => [ProvisionCommand, UpdateDownloaderCommand, RollbackComponentsCommand, InspectComponentsCommand, SupportedSitesCommand, CleanTemporaryFilesCommand];

    private void SyncSettingsComponents()
    {
        var items = Components.ToArray();
        for (var index = 0; index < items.Length; index++)
        {
            var key = SettingsComponentRow.KeyOf(items[index].Name);
            var existing = -1;
            for (var search = index; search < SettingsComponents.Count; search++) if (SettingsComponents[search].Key == key) { existing = search; break; }
            if (existing < 0) { SettingsComponents.Insert(index, new SettingsComponentRow(items[index])); continue; }
            if (existing != index) SettingsComponents.Move(existing, index);
            SettingsComponents[index].Update(items[index]);
        }
        while (SettingsComponents.Count > items.Length) SettingsComponents.RemoveAt(SettingsComponents.Count - 1);
        OnPropertyChanged(nameof(HasSettingsComponents)); OnPropertyChanged(nameof(SettingsComponentsAllReady)); OnPropertyChanged(nameof(SettingsComponentsNeedAttention));
        OnPropertyChanged(nameof(SettingsComponentsSummary)); OnPropertyChanged(nameof(ShowComponentSkeleton)); OnPropertyChanged(nameof(ShowComponentsNotChecked));
        OnPropertyChanged(nameof(SettingsDiagnosticsText));
    }

    private void SetComponentUpdateFailed(bool failed)
    {
        if (_componentUpdateFailed == failed) return;
        _componentUpdateFailed = failed; OnPropertyChanged(nameof(ComponentUpdateFailed));
    }

    private void OnSettingsTaskChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IAsyncRelayCommand.IsRunning) || _disposed) return;
        if (sender is IAsyncRelayCommand { IsRunning: true } started && !ReferenceEquals(started, CleanTemporaryFilesCommand)) _componentTaskStatusAtStart = Status;
        NotifyComponentTaskState();
    }

    /// <summary>
    /// Runs <see cref="MainViewModel.UpdateDownloaderCommand"/> and notes whether it finished. The
    /// component list is checked again only after a successful update, so no new check means the
    /// update failed; the page then offers to restore the previous components.
    /// </summary>
    public IAsyncRelayCommand SettingsUpdateDownloaderCommand => _settingsUpdateDownloader ??= new AsyncRelayCommand(async () =>
    {
        var checks = _componentChecks;
        await UpdateDownloaderCommand.ExecuteAsync(null);
        if (_disposed) return;
        if (_componentChecks == checks && !IsCancelledOrRefused(Status)) SetComponentUpdateFailed(true);
    });

    // A cancelled update, or one refused because another task was running, is not a failure.
    // Covers the wording of the single busy gate and of per-area operation lanes.
    private static bool IsCancelledOrRefused(string status) =>
        status.Contains("Operation cancelled", StringComparison.Ordinal) || status.Contains("Cancelled.", StringComparison.Ordinal)
        || status.Contains("Finish or cancel the current operation", StringComparison.Ordinal) || status.Contains("task is still running", StringComparison.Ordinal);

    private void NotifyComponentTaskState()
    {
        OnPropertyChanged(nameof(IsComponentTaskRunning)); OnPropertyChanged(nameof(AnimateComponentRing)); OnPropertyChanged(nameof(ShowComponentSkeleton));
        OnPropertyChanged(nameof(ShowComponentsNotChecked)); OnPropertyChanged(nameof(IsCleaningTemporaryFiles)); OnPropertyChanged(nameof(AnimateCleanupRing));
        OnPropertyChanged(nameof(ComponentTaskTitle)); OnPropertyChanged(nameof(ComponentTaskDetail)); OnPropertyChanged(nameof(HasComponentTaskDetail));
    }

    // ----- Diagnostics -----
    public bool HasRecordedError => !string.IsNullOrEmpty(Diagnostics);
    /// <summary>The last redacted error report, or a baseline report (app, OS and component versions) when nothing failed.</summary>
    public string SettingsDiagnosticsText => HasRecordedError ? Diagnostics : VersionDiagnostics + "\nNo errors recorded in this session.";
    private string VersionDiagnostics => MediaValidation.RedactDiagnostics(
        $"{AppIdentity.DisplayName} {AppIdentity.Version}\nOS: {RuntimeInformation.OSDescription}\n"
        + (Components.Count == 0 ? "Components: not checked yet" : string.Join("\n", Components.Select(c => c.Name + ": " + (c.Version ?? (c.Ready ? "version unavailable" : "missing"))))));
    public IAsyncRelayCommand CopySettingsDiagnosticsCommand => _copySettingsDiagnostics ??= new AsyncRelayCommand(async () =>
    {
        try
        {
            if (HasRecordedError) await CopyDiagnosticsCommand.ExecuteAsync(null);
            else { await _services.Platform.CopyAsync(SettingsDiagnosticsText); Status = "Diagnostics copied: app, Windows and component versions only."; }
        }
        catch (Exception error) { Status = FriendlyError(error); }
    });

    // ----- About -----
    public string SettingsLocation { get { try { return Path.GetFullPath(_services.DataRoot); } catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return _services.DataRoot; } } }
    public IRelayCommand OpenSettingsFolderCommand => _openSettingsFolder ??= new RelayCommand(() => TryAction(() =>
    {
        if (!Directory.Exists(SettingsLocation)) throw new MediaOperationException("The settings folder is created the first time something is saved.");
        _services.Platform.OpenFolder(SettingsLocation);
    }));
    /// <summary>The Support KeaClip page (Ko-fi). Only an https link is offered; an empty AppIdentity.SupportUrl hides the button.</summary>
    public bool HasSupportLink => Uri.TryCreate(AppIdentity.SupportUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0;
    private IRelayCommand? _openSupportPage;
    public IRelayCommand OpenSupportPageCommand => _openSupportPage ??= new RelayCommand(() => TryAction(() => { if (HasSupportLink) _services.Platform.OpenUri(new Uri(AppIdentity.SupportUrl)); }));
    private static string LicensesFolder => Path.Combine(AppContext.BaseDirectory, "Licenses");
    public bool HasLicensesFolder => Directory.Exists(LicensesFolder);
    public IRelayCommand OpenLicensesCommand => _openLicenses ??= new RelayCommand(() => TryAction(() =>
    {
        if (!Directory.Exists(LicensesFolder)) throw new MediaOperationException("Licence texts ship with packaged builds of KeaClip.");
        _services.Platform.OpenFolder(LicensesFolder);
    }));

    // ----- Change notifications -----
    private void OnSettingsPageModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        var name = e.PropertyName;
        if (name is null || _disposed) return;
        if (ReviewedSettingNames.Contains(name))
        {
            _settingsSaveError = null; _settingsJustSaved = false;
            NotifySettingField(name); NotifySettingsBar();
        }
        else if (InstantSettingNames.Contains(name))
        {
            // "Applies straight away" holds once startup has finished, also when the settings file was unreadable.
            if (name == nameof(ConcurrentDownloads)) { NotifySettingField(name); if (_initialized || _settingsUnreadable) _queue.MaxConcurrent = ConcurrentDownloads; }
            QueuePreferenceSave();
            if (PreferencesWaitForSave) NotifySettingsBar();
        }
        else if (name == nameof(IsBusy))
        {
            OnPropertyChanged(nameof(CanRunSettingsTask));
            if (CanRunSettingsTask)
            {
                // A completed full save replaces the snapshot.
                AdoptSavedSettings();
                SyncSettingsComponents();
                QueuePreferenceSave();
            }
            NotifyComponentTaskState();
            NotifySettingsBar();
        }
        else if (name == nameof(ComponentsReady))
        {
            _componentChecks++; SetComponentUpdateFailed(false);
            SyncSettingsComponents();
        }
        else if (name == nameof(SelectedTab))
        {
            if (_wasOnSettings && !IsSettings && SettingsNeedAttention)
                Status = HasUnsavedSettings ? $"Settings has {UnsavedSettingsTitle}. Open Settings to save or discard {(UnsavedSettingsCount == 1 ? "it" : "them")}."
                    : InvalidSettingsCount == 0 && !HasSettingsSaveError && _settingsUnreadable ? "Your saved settings could not be read, so defaults are in use. Open Settings and choose Save to keep your choices."
                    : "A saved setting needs fixing. Open Settings to review it.";
            _wasOnSettings = IsSettings;
            if (IsSettings) RefreshDefaultFolderSpace(immediate: true); else _folderSpaceLookup?.Cancel();
            OnPropertyChanged(nameof(AnimateComponentRing)); OnPropertyChanged(nameof(AnimateCleanupRing));
        }
        else if (name == nameof(MaintenancePercent)) OnPropertyChanged(nameof(SettingsTaskPercent));
        else if (name is nameof(AnimateBusyIndicator) or nameof(HasMaintenancePercent))
        {
            if (name == nameof(HasMaintenancePercent)) OnPropertyChanged(nameof(HasSettingsTaskPercent));
            OnPropertyChanged(nameof(AnimateComponentRing)); OnPropertyChanged(nameof(AnimateCleanupRing));
        }
        else if (name == nameof(Status)) { if (IsComponentTaskRunning) { OnPropertyChanged(nameof(ComponentTaskDetail)); OnPropertyChanged(nameof(HasComponentTaskDetail)); } }
        else if (name == nameof(TemporaryCleanupStatus)) RefreshDefaultFolderSpace(immediate: true, bypassCache: true);
        else if (name == nameof(Diagnostics)) { OnPropertyChanged(nameof(HasRecordedError)); OnPropertyChanged(nameof(SettingsDiagnosticsText)); }
    }

    private void NotifySettingField(string name)
    {
        switch (name)
        {
            case nameof(DownloadDirectory): OnPropertyChanged(nameof(DownloadDirectoryInvalid)); OnPropertyChanged(nameof(SeparateFoldersHint)); RefreshDefaultFolderSpace(immediate: false); break;
            case nameof(AudioDirectory): OnPropertyChanged(nameof(AudioDirectoryInvalid)); SeparateFolderChanged(); break;
            case nameof(ClipsDirectory): OnPropertyChanged(nameof(ClipsDirectoryInvalid)); SeparateFolderChanged(); break;
            case nameof(ArchivesDirectory): OnPropertyChanged(nameof(ArchivesDirectoryInvalid)); SeparateFolderChanged(); break;
            case nameof(RetentionDays): OnPropertyChanged(nameof(RetentionDaysInvalid)); OnPropertyChanged(nameof(RetentionChoice)); OnPropertyChanged(nameof(IsCustomRetention)); break;
            case nameof(StartWithWindows): OnPropertyChanged(nameof(StartMinimizedHint)); break;
            case nameof(SoundboardSeconds): OnPropertyChanged(nameof(SoundboardSecondsInvalid)); break;
            case nameof(SoundboardBytes): OnPropertyChanged(nameof(SoundboardBytesInvalid)); OnPropertyChanged(nameof(SoundboardBytesHint)); break;
            case nameof(ConcurrentDownloads):
                IncreaseConcurrentDownloadsCommand.NotifyCanExecuteChanged(); DecreaseConcurrentDownloadsCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(ConcurrentDownloadsDescription)); break;
        }
    }

    private void SeparateFolderChanged()
    {
        OnPropertyChanged(nameof(SeparateFoldersInvalid)); OnPropertyChanged(nameof(SeparateFoldersSummary));
        // A folder that needs fixing must be on screen.
        if (SeparateFoldersInvalid) SeparateFoldersExpanded = true;
    }

    // A full save (from the page or anywhere else) replaced _settings: it becomes the new
    // baseline, clears any earlier failure and shows the saved confirmation once.
    private void AdoptSavedSettings()
    {
        if (ReferenceEquals(_settings, _settingsBaseline)) return;
        _settingsBaseline = _settings; _settingsSaveError = null; _preferenceSaveError = null;
        // A full save wrote every value the page shows, so a settings file that could not be read
        // has now been replaced: from here on the session works as if it had loaded (instant
        // choices save themselves again and copied-link detection may run).
        if (_settingsUnreadable) { _settingsUnreadable = false; _initialized = true; }
        if (_initialized) FlashSettingsSaved(automatic: false);
        OnPropertyChanged(nameof(SoundboardCheckedNote));
    }

    private void NotifySettingsBar()
    {
        foreach (var name in SettingsBarProperties) OnPropertyChanged(name);
        SaveSettingsChangesCommand.NotifyCanExecuteChanged();
    }

    private void FlashSettingsSaved(bool automatic)
    {
        _settingsJustSaved = true; _settingsSavedAutomatically = automatic;
        var generation = ++_settingsSavedGeneration;
        NotifySettingsBar();
        // One-shot: the confirmation hides itself; nothing keeps running while idle.
        DispatcherTimer.RunOnce(() =>
        {
            if (generation != _settingsSavedGeneration || !_settingsJustSaved || _disposed) return;
            _settingsJustSaved = false; NotifySettingsBar();
        }, TimeSpan.FromSeconds(2.4));
    }
}

/// <summary>One managed media component on the Settings page, updated in place after each check.</summary>
public sealed partial class SettingsComponentRow : ObservableObject
{
    private string _rawName;
    private string? _version, _error;
    private bool _ready;

    public SettingsComponentRow(ComponentInfo info)
    {
        Key = KeyOf(info.Name); _rawName = info.Name;
        Update(info);
    }

    public static string KeyOf(string name) => name.Trim().ToLowerInvariant();
    public string Key { get; }
    public string Name => Key switch { "yt-dlp" => "yt-dlp", "ffmpeg" => "FFmpeg", "ffprobe" => "FFprobe", "deno" => "Deno", _ => _rawName };
    public string Role => Key switch
    {
        "yt-dlp" => "Reads and downloads media from websites",
        "ffmpeg" => "Converts, clips and compresses media",
        "ffprobe" => "Reads media details for the editor",
        "deno" => "Runs the site scripts some downloads need",
        _ => "Media component",
    };
    /// <summary>Icon resource key: each tool gets its own glyph.</summary>
    public string IconKey => Key switch { "yt-dlp" => "IconDownload", "ffmpeg" => "IconFilm", "ffprobe" => "IconSearch", "deno" => "IconSparkle", _ => "IconPackage" };
    public bool Ready => _ready;
    public bool NeedsAttention => !_ready;
    /// <summary>"Ready", "Missing" when nothing is installed, or "Needs repair" when an installed copy failed its check.</summary>
    public string StatusLabel => _ready ? "Ready" : string.IsNullOrWhiteSpace(_version) ? "Missing" : "Needs repair";
    public string? Error => _error;
    public bool HasError => !string.IsNullOrEmpty(_error);
    public string? FullVersion => _version;
    public string VersionText => FriendlyVersion(Key, _version, _ready);
    /// <summary>A missing tool shows no version slot: the chip and the reason already say it.</summary>
    public bool ShowVersion => _ready || !string.IsNullOrWhiteSpace(_version);

    internal void Update(ComponentInfo info)
    {
        _rawName = info.Name;
        if (SetProperty(ref _version, info.Version, nameof(FullVersion))) { OnPropertyChanged(nameof(VersionText)); OnPropertyChanged(nameof(ShowVersion)); OnPropertyChanged(nameof(StatusLabel)); }
        if (SetProperty(ref _ready, info.Ready, nameof(Ready))) { OnPropertyChanged(nameof(NeedsAttention)); OnPropertyChanged(nameof(VersionText)); OnPropertyChanged(nameof(ShowVersion)); OnPropertyChanged(nameof(StatusLabel)); }
        var error = string.IsNullOrWhiteSpace(info.Error) ? null : MediaValidation.Redact(info.Error);
        if (SetProperty(ref _error, error, nameof(Error))) OnPropertyChanged(nameof(HasError));
    }

    // "ffmpeg version 9.0.2-essentials_build-www.gyan.dev Copyright (c) ..." becomes "9.0.2" and
    // "deno 2.9.7 (stable, release, x86_64-pc-windows-msvc)" becomes "2.9.7"; the tooltip keeps the full text.
    public static string FriendlyVersion(string name, string? raw, bool ready)
    {
        var version = raw?.Trim();
        if (string.IsNullOrEmpty(version)) return ready ? "Version unavailable" : "Not installed";
        var copyright = version.IndexOf(" Copyright", StringComparison.OrdinalIgnoreCase);
        if (copyright > 0) version = version[..copyright];
        foreach (var prefix in new[] { name + " version ", name + " " })
            if (version.Length > prefix.Length && version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { version = version[prefix.Length..]; break; }
        version = BuildSuffix().Replace(version, "").Trim();
        return version.Length > 40 ? version[..40] + "…" : version;
    }

    [GeneratedRegex(@"(\s*\([^)]*\)\s*$)|([-_](essentials|full|shared)?_?build.*$)", RegexOptions.IgnoreCase)]
    private static partial Regex BuildSuffix();
}
