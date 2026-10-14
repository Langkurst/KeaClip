using Avalonia.Automation;
using Klippi.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Klippi.Core;
using Klippi.Desktop;
using Xunit;

namespace Klippi.UiTests;

public sealed class SettingsViewTests
{
    // ----- Save model: typed values and startup wait for Save -----

    [AvaloniaFact]
    public async Task Typed_values_and_startup_mark_one_change_each_and_reverting_clears_it()
    {
        var fake = Fakes(); using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        Assert.False(model.HasUnsavedSettings); Assert.False(model.ShowSettingsSaveBar);
        var notified = 0; model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.HasUnsavedSettings)) notified++; };
        var saved = fake.Store.Settings;
        var edits = new (string Name, Action Change, Action Revert)[]
        {
            ("download folder", () => model.DownloadDirectory = Folder("Elsewhere"), () => model.DownloadDirectory = saved.DownloadDirectory),
            ("download folder, other case and trailing slash", () => model.DownloadDirectory = Folder("Elsewhere"), () => model.DownloadDirectory = saved.DownloadDirectory.ToUpperInvariant() + Path.DirectorySeparatorChar),
            ("music folder", () => model.AudioDirectory = Folder("Music"), () => model.AudioDirectory = ""),
            ("clips folder", () => model.ClipsDirectory = Folder("Clips"), () => model.ClipsDirectory = "  "),
            ("archives folder", () => model.ArchivesDirectory = Folder("Archives"), () => model.ArchivesDirectory = ""),
            ("startup", () => model.StartWithWindows = true, () => model.StartWithWindows = false),
            ("retention", () => model.RetentionDays = "30", () => model.RetentionDays = ""),
            ("seconds", () => model.SoundboardSeconds = "4", () => model.SoundboardSeconds = "5.2"),
            ("bytes", () => model.SoundboardBytes = "400000", () => model.SoundboardBytes = "500000"),
        };
        foreach (var (name, change, revert) in edits)
        {
            change(); Assert.True(model.HasUnsavedSettings, name); Assert.Equal(1, model.UnsavedSettingsCount); Assert.Equal("1 unsaved change", model.UnsavedSettingsTitle); Assert.True(model.ShowSettingsSaveBar, name);
            Assert.Equal(name == "startup" ? "Windows startup changes when you save." : "Save to keep it after a restart.", model.SettingsSaveBarDetail);
            revert(); Assert.False(model.HasUnsavedSettings, name); Assert.False(model.ShowSettingsSaveBar, name);
        }
        Assert.True(notified >= edits.Length * 2, "Each edit and revert must notify the save bar.");
        Assert.Empty(fake.Platform.StartupWrites); Assert.Same(saved, fake.Store.Settings);
    }

    [AvaloniaFact]
    public async Task Instant_choices_apply_and_save_straight_away_but_never_write_pending_typed_values_or_startup()
    {
        var fake = Fakes(); using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        model.RetentionDays = "30"; model.StartWithWindows = true; // pending, must stay pending
        var edits = new (string Name, Action Change, Func<AppSettings, bool> Saved)[]
        {
            ("theme", () => model.Theme = "Light", s => s.Theme == "Light"),
            ("downloads at once", () => model.ConcurrentDownloads = 3, s => s.ConcurrentDownloads == 3),
            ("clipboard", () => model.DetectClipboard = false, s => !s.DetectClipboard),
            ("motion", () => model.ReducedMotion = false, s => !s.ReducedMotion),
            ("notices", () => model.NotifyCompleted = true, s => s.NotifyCompleted),
            ("close", () => model.CloseBehavior = CloseBehavior.Tray, s => s.CloseBehavior == CloseBehavior.Tray),
        };
        foreach (var (name, change, isSaved) in edits)
        {
            change(); await model.SettingsPreferenceSaveTask;
            Assert.True(isSaved(fake.Store.Settings), name);
            Assert.Equal(2, model.UnsavedSettingsCount); Assert.False(model.HasSettingsSaveError, name);
        }
        Assert.Null(fake.Store.Settings.HistoryRetentionDays); Assert.False(fake.Store.Settings.StartWithWindows); Assert.Empty(fake.Platform.StartupWrites);
        // Start minimized changes the Windows startup entry, so it waits for Save like Start with Windows.
        model.StartMinimized = true; await model.SettingsPreferenceSaveTask;
        Assert.False(fake.Store.Settings.StartMinimized); Assert.Equal(3, model.UnsavedSettingsCount); Assert.True(model.StartupChangePending);

        // With nothing else pending, an instant change shows only the short saved confirmation.
        model.DiscardSettingsCommand.Execute(null); Assert.False(model.HasUnsavedSettings);
        Assert.Equal("Light", model.Theme); Assert.Equal(CloseBehavior.Tray, model.CloseBehavior); // already saved, so Discard keeps them
        model.Theme = "Dark"; await model.SettingsPreferenceSaveTask;
        Assert.True(model.SettingsSavedVisible); Assert.Equal("Change saved", model.SettingsSavedTitle); Assert.False(model.ShowSettingsSaveBar);

        // A restart reads every instant choice back.
        using var restarted = new MainViewModel(fake.Services); await restarted.InitializeAsync();
        Assert.Equal("Dark", restarted.Theme); Assert.Equal(3, restarted.ConcurrentDownloads); Assert.Equal(CloseBehavior.Tray, restarted.CloseBehavior); Assert.True(restarted.NotifyCompleted);
    }

    [AvaloniaFact]
    public async Task A_failed_instant_save_stays_visible_until_save_or_discard_resolves_it()
    {
        var fake = Fakes(); using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        var saved = fake.Store.Settings;
        fake.Store.SettingsFailure = new IOException("Disk unavailable");
        model.Theme = "Light"; await model.SettingsPreferenceSaveTask;
        Assert.Equal("Light", model.Theme); Assert.Same(saved, fake.Store.Settings);
        Assert.True(model.HasSettingsSaveError); Assert.True(model.ShowSettingsSaveBar); Assert.Equal(1, model.UnsavedSettingsCount);
        Assert.Contains("could not be saved", model.SettingsSaveBarDetail); Assert.Contains("Disk unavailable", model.SettingsSaveBarDetail);
        Assert.Equal(nameof(MainViewModel.Theme), model.FirstSettingNeedingAttention);

        fake.Store.SettingsFailure = null;
        await model.SaveSettingsChangesCommand.ExecuteAsync(null);
        Assert.Equal("Light", fake.Store.Settings.Theme); Assert.False(model.HasSettingsSaveError); Assert.False(model.HasUnsavedSettings); Assert.True(model.SettingsSavedVisible);

        // Discard puts a choice that could not be saved back to the saved value, without writing anything.
        fake.Store.SettingsFailure = new IOException("Disk unavailable");
        model.CloseBehavior = CloseBehavior.Ask; await model.SettingsPreferenceSaveTask; Assert.True(model.HasSettingsSaveError);
        var before = fake.Store.Settings;
        model.DiscardSettingsCommand.Execute(null);
        Assert.Equal(CloseBehavior.Exit, model.CloseBehavior); Assert.False(model.HasSettingsSaveError); Assert.False(model.ShowSettingsSaveBar); Assert.Same(before, fake.Store.Settings);
    }

    [AvaloniaFact]
    public async Task Saving_clears_changes_confirms_and_writes_startup_only_on_save()
    {
        var fake = Fakes(); using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        model.StartWithWindows = true; model.RetentionDays = "14"; model.AudioDirectory = Folder("Music");
        Assert.Equal(3, model.UnsavedSettingsCount); Assert.Equal("3 unsaved changes", model.UnsavedSettingsTitle);
        Assert.True(model.StartupChangePending); Assert.Contains("Windows startup", model.SettingsSaveBarDetail);
        Assert.Empty(fake.Platform.StartupWrites);

        await model.SaveSettingsChangesCommand.ExecuteAsync(null);

        Assert.False(model.HasUnsavedSettings); Assert.False(model.StartupChangePending); Assert.False(model.HasSettingsSaveError);
        Assert.True(model.SettingsSavedVisible); Assert.Equal("Settings saved", model.SettingsSavedTitle); Assert.False(model.ShowSettingsSaveBar);
        Assert.Equal(new[] { true }, fake.Platform.StartupWrites);
        Assert.True(fake.Store.Settings.StartWithWindows); Assert.Equal(14, fake.Store.Settings.HistoryRetentionDays); Assert.Equal(Folder("Music"), fake.Store.Settings.AudioDirectory);

        model.SoundboardSeconds = "3"; // A new edit replaces the saved confirmation with the unsaved bar.
        Assert.False(model.SettingsSavedVisible); Assert.True(model.HasUnsavedSettings); Assert.True(model.ShowSettingsSaveBar);
    }

    [AvaloniaFact]
    public async Task Discard_restores_saved_values_without_touching_startup_or_storage()
    {
        var fake = Fakes(); using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        var saved = fake.Store.Settings;
        model.StartWithWindows = true; model.ClipsDirectory = Folder("Clips"); model.RetentionDays = "not a number"; model.SoundboardBytes = "250000";
        Assert.Equal(4, model.UnsavedSettingsCount);

        model.DiscardSettingsCommand.Execute(null);

        Assert.False(model.HasUnsavedSettings); Assert.Equal(0, model.InvalidSettingsCount);
        Assert.False(model.StartWithWindows); Assert.Equal("", model.ClipsDirectory); Assert.Equal("", model.RetentionDays); Assert.Equal("All", model.RetentionChoice); Assert.Equal("500000", model.SoundboardBytes);
        Assert.Contains("discarded", model.Status);
        Assert.Empty(fake.Platform.StartupWrites); Assert.Same(saved, fake.Store.Settings);
    }

    [AvaloniaFact]
    public async Task Leaving_settings_with_unsaved_changes_says_so_in_the_status_line()
    {
        var fake = Fakes(); using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        model.SelectedTab = 4; model.SoundboardSeconds = "4"; model.SelectedTab = 0;
        Assert.Contains("1 unsaved change", model.Status); Assert.Contains("Open Settings", model.Status);
        model.SelectedTab = 4; model.DiscardSettingsCommand.Execute(null); model.SelectedTab = 1;
        Assert.DoesNotContain("Open Settings", model.Status);
    }

    // ----- Validation mirrors SaveSettingsAsync -----

    [AvaloniaFact]
    public async Task Invalid_values_are_flagged_inline_block_saving_and_match_the_save_rules()
    {
        var fake = Fakes(); using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        var saved = fake.Store.Settings;
        model.DownloadDirectory = "relative\\folder"; model.AudioDirectory = "music"; model.RetentionDays = "abc"; model.SoundboardBytes = "12"; model.SoundboardSeconds = "301";
        Assert.True(model.DownloadDirectoryInvalid); Assert.True(model.AudioDirectoryInvalid); Assert.True(model.SeparateFoldersInvalid); Assert.True(model.RetentionDaysInvalid); Assert.True(model.SoundboardBytesInvalid); Assert.True(model.SoundboardSecondsInvalid);
        Assert.Equal(5, model.InvalidSettingsCount); Assert.False(model.CanSaveSettings); Assert.False(model.SaveSettingsChangesCommand.CanExecute(null));
        Assert.Equal("Fix the 5 highlighted fields to save.", model.SettingsSaveBarDetail); Assert.True(model.SettingsSaveBarNeedsAttention);
        Assert.Equal("Go to field", model.SettingsReviewLabel); Assert.Equal(nameof(MainViewModel.DownloadDirectory), model.FirstSettingNeedingAttention);

        // Each flagged value is also refused by the real save path, one at a time.
        foreach (var fix in new Action[] { () => model.DownloadDirectory = saved.DownloadDirectory, () => model.AudioDirectory = "", () => model.RetentionDays = "", () => model.SoundboardBytes = "500000", () => model.SoundboardSeconds = "5.2" })
        {
            await model.SaveSettingsAsync(); Assert.Same(saved, fake.Store.Settings);
            fix();
        }
        Assert.Equal(0, model.InvalidSettingsCount); Assert.True(model.CanSaveSettings);

        // Boundary values accepted inline are accepted by the save path too.
        model.SoundboardSeconds = "300"; model.SoundboardBytes = "1024"; model.RetentionDays = "1"; model.ArchivesDirectory = Folder("Archives");
        Assert.Equal(0, model.InvalidSettingsCount); Assert.True(model.SaveSettingsChangesCommand.CanExecute(null));
        await model.SaveSettingsChangesCommand.ExecuteAsync(null);
        Assert.Equal(300, fake.Store.Settings.SoundboardMaxSeconds); Assert.Equal(1024, fake.Store.Settings.SoundboardTargetBytes); Assert.Equal(1, fake.Store.Settings.HistoryRetentionDays); Assert.False(model.HasUnsavedSettings);

        // A comma decimal and digit grouping are accepted and saved as plain numbers.
        model.SoundboardSeconds = "4,5"; model.SoundboardBytes = "250,000";
        Assert.Equal(0, model.InvalidSettingsCount); Assert.Equal("About 250 KB", model.SoundboardBytesHint);
        await model.SaveSettingsChangesCommand.ExecuteAsync(null);
        Assert.Equal(4.5, fake.Store.Settings.SoundboardMaxSeconds); Assert.Equal(250_000, fake.Store.Settings.SoundboardTargetBytes); Assert.Equal("4.5", model.SoundboardSeconds); Assert.False(model.HasUnsavedSettings);
    }

    [AvaloniaFact]
    public async Task Save_failure_stays_beside_the_save_button_in_plain_words_and_retry_succeeds()
    {
        var fake = Fakes(); using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        model.StartWithWindows = true; fake.Store.SettingsFailure = new IOException("Disk unavailable");

        await model.SaveSettingsChangesCommand.ExecuteAsync(null);

        Assert.True(model.HasUnsavedSettings); Assert.True(model.HasSettingsSaveError); Assert.False(model.SettingsSavedVisible);
        Assert.StartsWith("Not saved.", model.SettingsSaveBarDetail); Assert.Contains("Disk unavailable", model.SettingsSaveBarDetail);
        Assert.Equal(new[] { true, false }, fake.Platform.StartupWrites);

        // A locked or read-only file gets a short reason and never shows a local path.
        fake.Store.SettingsFailure = new UnauthorizedAccessException(@"Access to the path 'C:\Users\someone\AppData\Local\KeaClip\settings.json' is denied.");
        await model.SaveSettingsChangesCommand.ExecuteAsync(null);
        Assert.Equal("Not saved. KeaClip can't write its settings file. Check that its folder isn't read-only.", model.SettingsSaveBarDetail);
        fake.Store.SettingsFailure = new IOException(@"Could not find a part of the path 'C:\Users\someone\settings.json'.");
        await model.SaveSettingsChangesCommand.ExecuteAsync(null);
        Assert.DoesNotContain("someone", model.SettingsSaveBarDetail);

        fake.Store.SettingsFailure = null;
        await model.SaveSettingsChangesCommand.ExecuteAsync(null);
        Assert.False(model.HasSettingsSaveError); Assert.False(model.HasUnsavedSettings); Assert.True(model.SettingsSavedVisible);
        Assert.Equal(new[] { true, false, true, false, true, false, true }, fake.Platform.StartupWrites);
    }

    // ----- Folders -----

    [AvaloniaFact]
    public async Task Separate_folders_use_the_picker_ignore_cancel_clear_back_to_default_and_open_when_needed()
    {
        var fake = Fakes(); var picker = new FolderPickerPlatform();
        using var model = new MainViewModel(fake.Services with { Platform = picker }); await model.InitializeAsync();
        Assert.False(model.SeparateFoldersExpanded); Assert.Equal("Music, clips and archives use the default folder.", model.SeparateFoldersSummary);
        var pairs = new (IAsyncRelayCommand Choose, IRelayCommand Clear, Func<string> Read, string Folder)[]
        {
            (model.ChooseAudioFolderCommand, model.ClearAudioFolderCommand, () => model.AudioDirectory, Folder("Music")),
            (model.ChooseClipsFolderCommand, model.ClearClipsFolderCommand, () => model.ClipsDirectory, Folder("Clips")),
            (model.ChooseArchivesFolderCommand, model.ClearArchivesFolderCommand, () => model.ArchivesDirectory, Folder("Archives")),
        };
        foreach (var (choose, clear, read, folder) in pairs)
        {
            picker.Next.Enqueue(folder); await choose.ExecuteAsync(null); Assert.Equal(folder, read());
            picker.Next.Enqueue(null); await choose.ExecuteAsync(null); Assert.Equal(folder, read());
            clear.Execute(null); Assert.Equal("", read());
        }
        Assert.Equal(6, picker.Picks);
        picker.Failure = new InvalidOperationException("Picker unavailable"); await model.ChooseAudioFolderCommand.ExecuteAsync(null);
        Assert.Equal("", model.AudioDirectory); Assert.Contains("Picker unavailable", model.Status);

        model.AudioDirectory = Folder("Music"); model.ArchivesDirectory = Folder("Archives");
        Assert.Equal("Music and archives use their own folders.", model.SeparateFoldersSummary);
        model.ClipsDirectory = "clips"; // invalid: the section opens so the problem is visible
        Assert.True(model.SeparateFoldersExpanded);

        // A restart opens the section when a separate folder is in use.
        model.ClipsDirectory = ""; await model.SaveSettingsChangesCommand.ExecuteAsync(null);
        using var restarted = new MainViewModel(fake.Services); await restarted.InitializeAsync();
        Assert.True(restarted.SeparateFoldersExpanded);
    }

    [AvaloniaFact]
    public async Task Default_folder_space_is_read_off_the_ui_thread_only_while_settings_is_shown()
    {
        var fake = Fakes(); using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        Assert.Equal("", model.DefaultFolderSpace); Assert.False(model.DefaultFolderSpacePending); // not on Settings: nothing is read
        model.SelectedTab = 4;
        await Until(() => !model.DefaultFolderSpacePending);
        Assert.Matches(@"(free on|unavailable)", model.DefaultFolderSpace);
        model.DownloadDirectory = "not a full path"; Assert.Equal("", model.DefaultFolderSpace); Assert.False(model.DefaultFolderSpacePending);
        model.DownloadDirectory = Folder("Elsewhere", "Deeper"); // a folder that does not exist yet reports its drive
        await Until(() => !model.DefaultFolderSpacePending);
        Assert.Matches(@"(free on|unavailable)", model.DefaultFolderSpace);
    }

    // ----- Stepper, retention, presets -----

    [AvaloniaFact]
    public async Task Downloads_at_once_stepper_stays_within_one_to_eight_and_saves_straight_away()
    {
        var fake = Fakes(); using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        Assert.False(model.DecreaseConcurrentDownloadsCommand.CanExecute(null));
        for (var i = 0; i < 10; i++) if (model.IncreaseConcurrentDownloadsCommand.CanExecute(null)) model.IncreaseConcurrentDownloadsCommand.Execute(null);
        Assert.Equal(8, model.ConcurrentDownloads); Assert.False(model.IncreaseConcurrentDownloadsCommand.CanExecute(null)); Assert.True(model.DecreaseConcurrentDownloadsCommand.CanExecute(null));
        model.DecreaseConcurrentDownloadsCommand.Execute(null); Assert.Equal(7, model.ConcurrentDownloads); Assert.Equal("7 downloads at the same time", model.ConcurrentDownloadsDescription);
        await model.SettingsPreferenceSaveTask; Assert.Equal(7, fake.Store.Settings.ConcurrentDownloads); Assert.False(model.HasUnsavedSettings);
    }

    [AvaloniaFact]
    public async Task Retention_choices_cover_keep_all_presets_and_custom_and_old_records_can_be_removed_now()
    {
        var fake = Fakes(); using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        Assert.Equal("All", model.RetentionChoice); Assert.False(model.IsCustomRetention); Assert.False(model.CanPruneHistoryNow);
        model.RetentionChoice = "90"; Assert.Equal("90", model.RetentionDays); Assert.True(model.RetentionDaysChanged); Assert.False(model.IsCustomRetention);
        model.RetentionChoice = "Custom"; Assert.True(model.IsCustomRetention); Assert.Equal("90", model.RetentionDays);
        model.RetentionDays = "30"; Assert.Equal("Custom", model.RetentionChoice); // typing a preset number keeps the custom box open
        model.RetentionChoice = "All"; Assert.Equal("", model.RetentionDays); Assert.False(model.IsCustomRetention); Assert.False(model.HasUnsavedSettings);
        model.RetentionChoice = "Custom"; Assert.Equal("365", model.RetentionDays);
        model.RetentionChoice = "30"; await model.SaveSettingsChangesCommand.ExecuteAsync(null);
        Assert.Equal(30, fake.Store.Settings.HistoryRetentionDays);
        Assert.True(model.CanPruneHistoryNow); Assert.Equal("Remove records older than 30 days now", model.PruneHistoryNowLabel);
        await model.PruneHistoryNowCommand.ExecuteAsync(null); Assert.Contains("Media files are never deleted", model.Status);

        // A saved custom value opens as Custom after a restart.
        model.RetentionChoice = "Custom"; model.RetentionDays = "45"; await model.SaveSettingsChangesCommand.ExecuteAsync(null);
        using var restarted = new MainViewModel(fake.Services); await restarted.InitializeAsync();
        Assert.Equal("Custom", restarted.RetentionChoice); Assert.Equal("45", restarted.RetentionDays); Assert.False(restarted.HasUnsavedSettings);
    }

    [AvaloniaFact]
    public async Task Soundboard_reset_and_hints_use_the_saved_preset_date()
    {
        var fake = Fakes(); using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        Assert.Contains("7 October 2026", model.SoundboardCheckedNote);
        Assert.Equal("About 500 KB", model.SoundboardBytesHint);
        model.SoundboardSeconds = "3"; model.SoundboardBytes = "250000"; Assert.Equal("About 250 KB", model.SoundboardBytesHint);
        model.ResetSoundboardDefaultsCommand.Execute(null);
        Assert.Equal("5.2", model.SoundboardSeconds); Assert.Equal("500000", model.SoundboardBytes); Assert.False(model.HasUnsavedSettings);
    }

    [AvaloniaFact]
    public async Task Values_the_page_cannot_show_are_normalised_without_a_phantom_change()
    {
        var fake = Fakes(); fake.Store.Settings = fake.Store.Settings with { Theme = "dark", ConcurrentDownloads = 12 };
        using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        Assert.Equal("Dark", model.Theme); Assert.Equal(8, model.ConcurrentDownloads); Assert.False(model.HasUnsavedSettings); Assert.False(model.ShowSettingsSaveBar);
    }

    // ----- Components -----

    [AvaloniaFact]
    public async Task Component_rows_stay_on_screen_while_checking_and_report_each_tool_honestly()
    {
        var fake = Fakes();
        var components = new ScriptedComponents { Items = [new("yt-dlp", null, "2026.08.19", true), new("ffmpeg", null, null, false, "Not installed or integrity check failed."), new("ffprobe", null, "ffprobe version 9.0.2", false, "Version check failed.")] };
        using var model = new MainViewModel(fake.Services with { Components = components });
        Assert.Equal("Not checked", model.SettingsComponentsSummary); Assert.False(model.SettingsComponentsNeedAttention);
        await model.InitializeAsync();
        Assert.True(model.SettingsComponentsNeedAttention); Assert.Equal("1 of 3 ready", model.SettingsComponentsSummary);
        var rows = model.SettingsComponents.ToArray();
        Assert.Equal(["Ready", "Missing", "Needs repair"], rows.Select(r => r.StatusLabel));
        Assert.Equal(["yt-dlp", "FFmpeg", "FFprobe"], rows.Select(r => r.Name));
        Assert.False(rows[1].ShowVersion); Assert.True(rows[2].ShowVersion); Assert.Equal("9.0.2", rows[2].VersionText);

        // While a check runs, the rows and the summary stay put and the page names the task itself.
        model.Status = "An unrelated older message.";
        components.InspectGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        components.Items = [new("yt-dlp", null, "2026.08.19", true), new("ffmpeg", null, "ffmpeg version 9.0.2-essentials_build-www.gyan.dev Copyright (c) 2000-2026 the FFmpeg developers", true), new("ffprobe", null, "ffprobe version 9.0.2", true)];
        var check = model.InspectComponentsCommand.ExecuteAsync(null);
        await Until(() => model.IsComponentTaskRunning);
        Assert.Empty(model.Components); // the shared list was cleared…
        Assert.Equal(3, model.SettingsComponents.Count); Assert.Equal("1 of 3 ready", model.SettingsComponentsSummary); // …but the page keeps its rows
        Assert.False(model.ShowComponentsNotChecked); Assert.False(model.ShowComponentSkeleton);
        Assert.Equal("Checking components…", model.ComponentTaskTitle); Assert.Equal("", model.ComponentTaskDetail);
        components.InspectGate.SetResult(); await check;
        Assert.Same(rows[1], model.SettingsComponents[1]); // updated in place
        Assert.False(model.SettingsComponentsNeedAttention); Assert.True(model.SettingsComponentsAllReady); Assert.Equal("All ready", model.SettingsComponentsSummary);
        Assert.Equal("9.0.2", rows[1].VersionText); Assert.Equal("Ready", rows[1].StatusLabel);
        Assert.Equal("", model.ComponentTaskTitle); Assert.False(model.IsComponentTaskRunning);
    }

    [AvaloniaFact]
    public async Task A_failed_downloader_update_offers_recovery_until_components_are_checked_again()
    {
        var fake = Fakes(); var components = new ScriptedComponents { Items = [new("yt-dlp", null, "2026.08.19", true)] };
        using var model = new MainViewModel(fake.Services with { Components = components }); model.ObserveSettingsChanges(); await model.InitializeAsync();
        // Earlier tasks refreshed the list; that must not count as a refresh by the update.
        await model.InspectComponentsCommand.ExecuteAsync(null);
        components.Release.SetResult(); await model.ProvisionCommand.ExecuteAsync(null);
        // Cancelling an update is not a failure.
        components.UpdateFailure = new OperationCanceledException(); await model.SettingsUpdateDownloaderCommand.ExecuteAsync(null);
        Assert.False(model.ComponentUpdateFailed);
        components.UpdateFailure = new MediaOperationException("The update could not be verified.");
        await model.SettingsUpdateDownloaderCommand.ExecuteAsync(null);
        Assert.True(model.ComponentUpdateFailed); Assert.Contains("could not be verified", model.Status);
        components.UpdateFailure = null;
        await model.InspectComponentsCommand.ExecuteAsync(null);
        Assert.False(model.ComponentUpdateFailed);
        await model.SettingsUpdateDownloaderCommand.ExecuteAsync(null);
        Assert.False(model.ComponentUpdateFailed);
    }

    [AvaloniaFact]
    public async Task Diagnostics_can_always_be_copied_and_never_include_local_paths()
    {
        var fake = Fakes(); using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        Assert.False(model.HasRecordedError); Assert.Contains("No errors recorded", model.SettingsDiagnosticsText); Assert.Contains("yt-dlp", model.SettingsDiagnosticsText);
        await model.CopySettingsDiagnosticsCommand.ExecuteAsync(null);
        Assert.Equal(model.SettingsDiagnosticsText, fake.Platform.Clipboard);
        fake.Downloads.AnalysisFailure = new IOException(@"Could not read C:\Users\someone\secret\file.json");
        model.Url = "https://example.com/video"; await model.AnalyseAsync();
        Assert.True(model.HasRecordedError); Assert.DoesNotContain("someone", model.SettingsDiagnosticsText);
        await model.CopySettingsDiagnosticsCommand.ExecuteAsync(null); Assert.Equal(model.Diagnostics, fake.Platform.Clipboard);
    }

    // ----- Rendered page interaction -----

    [AvaloniaFact]
    public async Task Tiles_and_switches_drive_the_model_and_the_save_bar_saves()
    {
        var (fake, model, window) = await OpenSettingsAsync("Dark", 900, 740);
        try
        {
            Assert.True(window.FindNamed<RadioButton>("ThemeDarkTile")!.IsChecked);
            Click(window, window.FindNamed<RadioButton>("ThemeLightTile")!);
            Assert.Equal("Light", model.Theme); Assert.False(window.FindNamed<RadioButton>("ThemeDarkTile")!.IsChecked);
            await model.SettingsPreferenceSaveTask; Assert.Equal("Light", fake.Store.Settings.Theme);
            window.UpdateLayout(); Assert.True(window.FindNamed<Border>("SettingsSavedToast")!.IsVisible); Assert.Equal("Change saved", window.FindNamed<TextBlock>("SettingsSavedText")!.Text);
            Assert.False(window.FindNamed<Border>("SettingsSaveBar")!.IsVisible);

            // Window behaviour lives under Advanced settings, closed until asked for.
            Assert.False(window.FindNamed<RadioButton>("CloseTrayTile")!.IsEffectivelyVisible);
            Click(window, window.FindNamed<Button>("AdvancedSettingsToggle")!); Assert.True(model.ShowAdvancedSettings); window.UpdateLayout();
            Click(window, window.FindNamed<RadioButton>("CloseTrayTile")!); Assert.Equal(CloseBehavior.Tray, model.CloseBehavior);
            await model.SettingsPreferenceSaveTask; Assert.Equal(CloseBehavior.Tray, fake.Store.Settings.CloseBehavior);
            model.CloseBehavior = CloseBehavior.Ask; window.UpdateLayout(); Assert.True(window.FindNamed<RadioButton>("CloseAskTile")!.IsChecked);

            Click(window, window.FindNamed<RadioButton>("Retention90Tile")!); Assert.Equal("90", model.RetentionDays);
            Click(window, window.FindNamed<ToggleSwitch>("StartWithWindowsSwitch")!);
            Assert.True(model.StartWithWindows); Assert.Empty(fake.Platform.StartupWrites);
            var bar = window.FindNamed<Border>("SettingsSaveBar")!; Assert.True(bar.IsVisible);
            Assert.Equal("2 unsaved changes", window.FindNamed<TextBlock>("UnsavedSettingsText")!.Text);

            Click(window, window.FindNamed<Button>("SaveSettingsButton")!);
            if (model.SaveSettingsChangesCommand.ExecutionTask is { } save) await save;
            Assert.Equal(new[] { true }, fake.Platform.StartupWrites); Assert.Equal(90, fake.Store.Settings.HistoryRetentionDays);
            window.UpdateLayout(); Assert.False(bar.IsVisible); Assert.Equal("Settings saved", window.FindNamed<TextBlock>("SettingsSavedText")!.Text);
            Assert.True(window.FindNamed<Button>("PruneHistoryButton")!.IsVisible);
        }
        finally { Close(model, window); }
    }

    // The only ask in the app: a Support button in About that opens the developer's Ko-fi page and nothing else.
    [AvaloniaFact]
    public async Task Support_button_opens_only_the_ko_fi_page()
    {
        var (fake, model, window) = await OpenSettingsAsync("Dark", 900, 740);
        try
        {
            var button = window.FindNamed<Button>("SupportButton")!;
            Assert.True(button.IsEffectivelyVisible);
            Assert.Equal("Support KeaClip on Ko-fi", AutomationProperties.GetName(button));
            Click(window, button);
            Assert.Equal([new Uri("https://ko-fi.com/keaclip")], fake.Platform.OpenedUris);
            Assert.Empty(fake.Downloads.Requests);
        }
        finally { Close(model, window); }
    }
    // Everyday choices first; the rest waits under Advanced settings and opens by itself when one of its settings
    // needs fixing, so the Review link never points at something hidden.
    [AvaloniaFact]
    public async Task Everyday_settings_show_first_and_review_opens_advanced_when_needed()
    {
        var (_, model, window) = await OpenSettingsAsync("Dark", 900, 740);
        try
        {
            Assert.False(model.ShowAdvancedSettings);
            foreach (var name in new[] { "AppearanceCard", "DownloadsCard", "AboutCard" }) Assert.True(window.FindNamed<Border>(name)!.IsEffectivelyVisible, name);
            foreach (var name in new[] { "WindowCard", "EditorPresetsCard", "PrivacyCard", "ComponentsCard", "StorageCard" }) Assert.False(window.FindNamed<Border>(name)!.IsEffectivelyVisible, name);
            Assert.Equal("Show advanced settings", AutomationProperties.GetName(window.FindNamed<Button>("AdvancedSettingsToggle")!));

            model.SoundboardSeconds = "not a number"; window.UpdateLayout();
            Assert.Equal(nameof(MainViewModel.SoundboardSeconds), model.FirstSettingNeedingAttention);
            var page = Assert.IsType<SettingsView>(window.FindNamed<Control>("SettingsPage"));
            Assert.True(page.RevealSetting(model, model.FirstSettingNeedingAttention!));
            window.UpdateLayout();
            Assert.True(model.ShowAdvancedSettings);
            Assert.True(window.FindNamed<TextBox>("SoundboardSecondsBox")!.IsEffectivelyVisible);
            Assert.Equal("Hide advanced settings", AutomationProperties.GetName(window.FindNamed<Button>("AdvancedSettingsToggle")!));
        }
        finally { Close(model, window); }
    }
    [AvaloniaFact]
    public async Task Discard_button_ctrl_s_and_the_stepper_keyboard_work_from_the_page()
    {
        var (fake, model, window) = await OpenSettingsAsync("Dark", 900, 740);
        try
        {
            model.ShowAdvancedSettings = true; model.RetentionDays = "45"; window.UpdateLayout();
            Assert.True(window.FindNamed<RadioButton>("RetentionCustomTile")!.IsChecked);
            var box = window.FindNamed<TextBox>("RetentionDaysBox")!; Assert.True(box.IsEffectivelyVisible);
            box.BringIntoView(); box.Focus(); window.UpdateLayout();
            window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, "s"); window.KeyRelease(Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            if (model.SaveSettingsChangesCommand.ExecutionTask is { } save) await save;
            Assert.Equal(45, fake.Store.Settings.HistoryRetentionDays); Assert.False(model.HasUnsavedSettings);

            model.SoundboardSeconds = "4"; window.UpdateLayout();
            Click(window, window.FindNamed<Button>("DiscardSettingsButton")!);
            Assert.Equal("5.2", model.SoundboardSeconds); Assert.False(model.HasUnsavedSettings);

            // The stepper value takes Up/Down keys and the − / + buttons; each change saves straight away.
            var stepper = window.FindNamed<NumericUpDown>("ConcurrentDownloadsBox")!;
            stepper.BringIntoView(); window.UpdateLayout();
            var input = stepper.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() ?? (Control)stepper; input.Focus(); window.UpdateLayout();
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null); window.KeyRelease(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null); window.KeyRelease(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Assert.Equal(3, model.ConcurrentDownloads);
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null); window.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Assert.Equal(2, model.ConcurrentDownloads);
            Click(window, FindVisual<Button>(window, "MoreDownloadsButton")); Assert.Equal(3, model.ConcurrentDownloads);
            Click(window, FindVisual<Button>(window, "FewerDownloadsButton")); Click(window, FindVisual<Button>(window, "FewerDownloadsButton")); Assert.Equal(1, model.ConcurrentDownloads);
            Assert.False(FindVisual<Button>(window, "FewerDownloadsButton").IsEffectivelyEnabled);
            await model.SettingsPreferenceSaveTask; Assert.Equal(1, fake.Store.Settings.ConcurrentDownloads);
        }
        finally { Close(model, window); }
    }

    [AvaloniaFact]
    public async Task Go_to_field_opens_the_right_section_and_focuses_the_first_problem()
    {
        var (_, model, window) = await OpenSettingsAsync("Dark", 900, 740);
        try
        {
            model.SoundboardSeconds = "4"; model.ClipsDirectory = "clips"; model.SeparateFoldersExpanded = false; window.UpdateLayout();
            Assert.Equal(nameof(MainViewModel.ClipsDirectory), model.FirstSettingNeedingAttention);
            var review = window.FindNamed<Button>("SettingsReviewButton")!; Assert.True(review.IsEffectivelyVisible); Assert.Equal("Go to field", review.Content);
            Click(window, review);
            Assert.True(model.SeparateFoldersExpanded); Assert.True(window.FindNamed<Expander>("SeparateFoldersExpander")!.IsExpanded);
            var clips = window.FindNamed<TextBox>("ClipsFolderBox")!; Assert.True(clips.IsFocused);
            var scroll = window.FindNamed<ScrollViewer>("SettingsScroll")!; var top = clips.TranslatePoint(new Point(0, 0), scroll)!.Value.Y;
            Assert.InRange(top, 0, scroll.Viewport.Height - clips.Bounds.Height);

            model.ClipsDirectory = ""; window.UpdateLayout();
            Assert.Equal("Show changes", review.Content);
            Click(window, review); Assert.True(window.FindNamed<TextBox>("SoundboardSecondsBox")!.IsFocused);
        }
        finally { Close(model, window); }
    }

    [AvaloniaFact]
    public async Task Existing_names_and_actions_remain_reachable()
    {
        var (_, model, window) = await OpenSettingsAsync("Dark", 900, 740);
        try
        {
            foreach (var name in new[] { "SaveSettingsButton", "DiscardSettingsButton", "CleanTemporaryFilesButton", "PrepareComponentsButton", "CheckComponentsButton", "UpdateDownloaderButton", "RestoreComponentsButton", "SupportedSitesButton", "CopyDiagnosticsButton", "OpenSettingsFolderButton", "ResetSoundboardButton" })
                Assert.NotNull(window.FindNamed<Button>(name));
            Assert.NotNull(window.FindNamed<TextBlock>("TemporaryCleanupStatusText"));
            Assert.NotNull(window.FindNamed<Grid>("ThemePicker"));
            Assert.Equal(model.TemporaryCleanupStatus, window.FindNamed<TextBlock>("TemporaryCleanupStatusText")!.Text);
            Assert.Equal(model.DataMode, window.FindNamed<TextBlock>("DataModeText")!.Text);
            Assert.Equal(model.SettingsLocation, ToolTip.GetTip(window.FindNamed<Button>("OpenSettingsFolderButton")!));
            var page = window.FindNamed<Control>("SettingsPage")!;
            Assert.NotNull(FindVisual<Button>(window, "MoreDownloadsButton")); // inside the stepper, so only in the visual tree
            foreach (var icon in page.GetVisualDescendants().OfType<Button>().Where(b => b.Content is Klippi.Desktop.Controls.StrokeIcon))
            {
                Assert.False(string.IsNullOrWhiteSpace(AutomationPropertiesName(icon)), "Icon-only buttons need an accessible name.");
                Assert.NotNull(ToolTip.GetTip(icon));
            }
            foreach (var box in page.GetLogicalDescendants().OfType<TextBox>().Where(b => !b.IsReadOnly && b.Name is { } name && !name.StartsWith("PART_", StringComparison.Ordinal)))
            {
                Assert.False(string.IsNullOrWhiteSpace(AutomationPropertiesName(box)), box.Name + " needs an accessible name.");
                Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetHelpText(box)), box.Name + " needs help text that says what is valid.");
            }
        }
        finally { Close(model, window); }
    }

    // ----- Visual evidence -----

    [AvaloniaFact]
    public async Task Settings_page_renders_its_states_in_both_themes()
    {
        foreach (var theme in new[] { "Dark", "Light" })
        {
            var key = theme.ToLowerInvariant();
            var (fake, model, window) = await OpenSettingsAsync(theme, 900, 740);
            try
            {
                await Capture(window, $"settings-top-{key}");
                ScrollTo(window, "DownloadsCard"); await Capture(window, $"settings-downloads-{key}");
                ScrollTo(window, "WindowCard"); await Capture(window, $"settings-window-{key}");
                ScrollTo(window, "PrivacyCard"); await Capture(window, $"settings-privacy-{key}");
                ScrollTo(window, "AboutCard"); await Capture(window, $"settings-bottom-{key}");

                // Separate folders open, one set and one changed.
                model.AudioDirectory = Folder("Music"); model.SeparateFoldersExpanded = true; ScrollTo(window, "DownloadsCard"); await Capture(window, $"settings-folders-{key}");
                model.DiscardSettingsCommand.Execute(null);
                // Unsaved, with a pending Windows startup change and a custom retention.
                model.StartWithWindows = true; model.RetentionDays = "45"; ScrollTo(window, "WindowCard"); await Capture(window, $"settings-dirty-{key}");
                ScrollTo(window, "PrivacyCard"); await Capture(window, $"settings-dirty-privacy-{key}");
                // Invalid input highlighted inline and in the bar.
                model.RetentionDays = "soon"; model.AudioDirectory = "music"; model.SoundboardBytes = "12"; ScrollTo(window, "EditorPresetsCard"); await Capture(window, $"settings-invalid-{key}");
                model.DiscardSettingsCommand.Execute(null); window.UpdateLayout();
                Assert.DoesNotContain("invalid", window.FindNamed<TextBox>("AudioFolderBox")!.Classes); Assert.DoesNotContain("invalid", window.FindNamed<TextBox>("RetentionDaysBox")!.Classes);
                // Saved confirmation after an instant choice.
                model.ConcurrentDownloads = 3; await model.SettingsPreferenceSaveTask; ScrollTo(window, "DownloadsCard"); await Capture(window, $"settings-saved-{key}");
                // Save failure stays visible, in plain words.
                model.SoundboardSeconds = "4"; fake.Store.SettingsFailure = new UnauthorizedAccessException(@"Access to the path 'C:\Users\someone\settings.json' is denied.");
                await model.SaveSettingsChangesCommand.ExecuteAsync(null); ScrollTo(window, "EditorPresetsCard"); await Capture(window, $"settings-save-error-{key}");
                fake.Store.SettingsFailure = null; model.DiscardSettingsCommand.Execute(null);

                // Full page in one frame for layout review.
                window.Height = 3600; window.UpdateLayout(); await Capture(window, $"settings-full-{key}");
            }
            finally { Close(model, window); }
        }
    }

    [AvaloniaFact]
    public async Task Components_render_missing_busy_checking_and_detail_states()
    {
        foreach (var theme in new[] { "Dark", "Light" })
        {
            var key = theme.ToLowerInvariant();
            var components = new ScriptedComponents
            {
                Items =
                [
                    new("yt-dlp", null, "2026.08.19", true),
                    new("ffmpeg", null, "ffmpeg version 9.0.2-essentials_build-www.gyan.dev Copyright (c) 2000-2026 the FFmpeg developers", true),
                    new("ffprobe", null, null, false, "Not installed or integrity check failed."),
                    new("deno", null, "deno 2.9.7 (stable, release, x86_64-pc-windows-msvc)", true),
                ],
            };
            var sites = new SitesDownloads();
            var (fake, model, window) = await OpenSettingsAsync(theme, 900, 740, services => services with { Components = components, Downloads = sites });
            try
            {
                ScrollTo(window, "ComponentsCard"); await Capture(window, $"settings-components-missing-{key}");

                // A component task with a known percentage: determinate bar, no spinning ring, its own label.
                model.ReducedMotion = false; fake.Platform.SystemReducedMotion = false; model.RefreshPlatformPreferences();
                model.Status = "Unsaved settings discarded. Your saved settings are back.";
                var running = model.ProvisionCommand.ExecuteAsync(null);
                await Until(() => model.HasOperationPercent);
                window.UpdateLayout(); Assert.True(window.FindNamed<Border>("ComponentProgress")!.IsVisible); Assert.False(window.FindNamed<Button>("CheckComponentsButton")!.IsEffectivelyEnabled);
                Assert.False(model.AnimateComponentRing); Assert.Equal("Preparing components…", window.FindNamed<TextBlock>("ComponentTaskTitleText")!.Text);
                Assert.Contains("Downloading verified FFprobe", model.ComponentTaskDetail);
                model.ReducedMotion = true; await model.SettingsPreferenceSaveTask; HideSavedConfirmation(model);
                ScrollTo(window, "ComponentsCard"); await Capture(window, $"settings-components-busy-{key}");
                components.Release.SetResult(); await running;
                window.UpdateLayout(); Assert.False(window.FindNamed<Border>("ComponentProgress")!.IsVisible);

                // Check status keeps the rows on screen while it runs.
                components.InspectGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var check = model.InspectComponentsCommand.ExecuteAsync(null); await Until(() => model.IsComponentTaskRunning);
                window.UpdateLayout(); Assert.True(window.FindNamed<ItemsControl>("ComponentList")!.IsVisible); Assert.False(window.FindNamed<TextBlock>("ComponentsNotCheckedText")!.IsVisible);
                ScrollTo(window, "ComponentsCard"); await Capture(window, $"settings-components-checking-{key}");
                components.InspectGate.SetResult(); await check;

                // A failed downloader update offers recovery.
                components.UpdateFailure = new MediaOperationException("The update could not be verified.");
                await model.SettingsUpdateDownloaderCommand.ExecuteAsync(null); components.UpdateFailure = null;
                window.UpdateLayout(); Assert.True(window.FindNamed<Border>("ComponentUpdateFailedNotice")!.IsVisible); Assert.True(window.FindNamed<Button>("RestoreAfterUpdateButton")!.IsEffectivelyEnabled);
                ScrollTo(window, "ComponentsCard"); await Capture(window, $"settings-components-update-failed-{key}");

                // Technical details with real content.
                await model.SupportedSitesCommand.ExecuteAsync(null);
                model.Url = "https://example.com/changed"; await model.AnalyseAsync();
                window.FindNamed<Expander>("TechnicalDetails")!.IsExpanded = true; window.UpdateLayout();
                ScrollTo(window, "TechnicalDetails"); await Capture(window, $"settings-components-details-{key}");
                Assert.Contains("ERROR", window.FindNamed<TextBox>("DiagnosticsBox")!.Text);
            }
            finally { Close(model, window); }
        }
    }

    [AvaloniaFact]
    public async Task Settings_page_renders_before_loading_and_lines_up_with_the_title_on_a_wide_window()
    {
        var fake = Fakes(); var slow = new ScriptedComponents { Items = [new("yt-dlp", null, "2026.08.19", true)], InspectGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using (var unloaded = new MainViewModel(fake.Services with { Components = slow }) { SelectedTab = 4 })
        {
            var early = new MainWindow { DataContext = unloaded, Width = 900, Height = 740 }; early.Show();
            try
            {
                ((Application)Application.Current!).RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
                fake.Platform.SystemReducedMotion = true; unloaded.RefreshPlatformPreferences();
                Assert.False(unloaded.HasUnsavedSettings);
                var loading = unloaded.InitializeAsync(); await Until(() => unloaded.IsBusy);
                early.UpdateLayout(); Assert.True(early.FindNamed<StackPanel>("ComponentSkeleton")!.IsVisible); Assert.False(early.FindNamed<ItemsControl>("ComponentList")!.IsVisible);
                ScrollTo(early, "ComponentsCard"); await Capture(early, "settings-unloaded-dark");
                slow.InspectGate.SetResult(); await loading;
                early.UpdateLayout(); Assert.False(early.FindNamed<StackPanel>("ComponentSkeleton")!.IsVisible); Assert.True(early.FindNamed<ItemsControl>("ComponentList")!.IsVisible);
            }
            finally { early.Close(); }
        }
        foreach (var theme in new[] { "Dark", "Light" })
        {
            var (_, model, window) = await OpenSettingsAsync(theme, 1400, 900);
            try
            {
                model.SoundboardSeconds = "4"; window.UpdateLayout();
                await Capture(window, $"settings-wide-{theme.ToLowerInvariant()}");
                var card = window.FindNamed<Border>("AppearanceCard")!; Assert.InRange(card.Bounds.Width, 1039.5, 1040.5);
                var title = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == model.PageTitle);
                var cardLeft = card.TranslatePoint(new Point(0, 0), window)!.Value.X; var titleLeft = title.TranslatePoint(new Point(0, 0), window)!.Value.X;
                Assert.InRange(cardLeft - titleLeft, -0.5, 0.5);
                var bar = window.FindNamed<Border>("SettingsSaveBar")!; Assert.InRange(bar.TranslatePoint(new Point(0, 0), window)!.Value.X - cardLeft, -0.5, 0.5); Assert.InRange(bar.Bounds.Width, 1039.5, 1040.5);
                model.DiscardSettingsCommand.Execute(null);
                ScrollTo(window, "ComponentsCard"); await Capture(window, $"settings-wide-components-{theme.ToLowerInvariant()}");
            }
            finally { Close(model, window); }
        }
        // At the narrowest supported window the column fills the space.
        var (_, narrowModel, narrow) = await OpenSettingsAsync("Dark", 880, 640);
        try
        {
            var column = (Control)narrow.FindNamed<StackPanel>("SettingsColumn")!.Parent!; var card = narrow.FindNamed<Border>("AppearanceCard")!;
            Assert.True(column.Bounds.Width < 1040); Assert.InRange(column.Bounds.Width - card.Bounds.Width, -0.5, 0.5);
            narrowModel.StartWithWindows = true; narrowModel.SoundboardSeconds = "x"; narrow.UpdateLayout();
            ScrollTo(narrow, "WindowCard"); await Capture(narrow, "settings-narrow-dark");
        }
        finally { Close(narrowModel, narrow); }
    }

    [AvaloniaFact]
    public async Task Settings_page_bindings_resolve_in_every_state()
    {
        var sink = new SettingsBindingSink(); var previous = Logger.Sink; Logger.Sink = sink; string[]? problems = null;
        try
        {
            var components = new ScriptedComponents { Items = [new("yt-dlp", null, "2026.08.19", true), new("ffprobe", null, "ffprobe version 9.0.2", false, "Version check failed."), new("deno", null, null, false, "Not installed or integrity check failed.")] };
            var (fake, model, window) = await OpenSettingsAsync("Dark", 900, 740, services => services with { Components = components, Downloads = new SitesDownloads() });
            try
            {
                // The sink must see a binding that cannot resolve, or an empty result proves nothing.
                var column = window.FindNamed<StackPanel>("SettingsColumn")!;
                var probe = new TextBlock { [!TextBlock.TextProperty] = new Avalonia.Data.Binding("NoSuchSettingsProperty") };
                column.Children.Add(probe); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Assert.Contains(sink.OnSettingsPage(), error => error.Contains("NoSuchSettingsProperty", StringComparison.Ordinal));
                column.Children.Remove(probe); sink.Clear();

                void Settle() { window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
                model.AudioDirectory = Folder("Music"); model.SeparateFoldersExpanded = true; Settle();
                model.StartWithWindows = true; model.RetentionChoice = "Custom"; model.RetentionDays = "45"; Settle();
                model.RetentionDays = "soon"; model.ClipsDirectory = "clips"; model.SoundboardBytes = "12"; model.SoundboardSeconds = "0"; model.DownloadDirectory = "relative"; Settle();
                model.DiscardSettingsCommand.Execute(null); Settle();
                model.SoundboardSeconds = "4"; fake.Store.SettingsFailure = new IOException("Disk unavailable");
                await model.SaveSettingsChangesCommand.ExecuteAsync(null); Settle();
                fake.Store.SettingsFailure = null; await model.SaveSettingsChangesCommand.ExecuteAsync(null); Settle();
                model.ConcurrentDownloads = 4; model.Theme = "Light"; await model.SettingsPreferenceSaveTask; Settle();

                var running = model.ProvisionCommand.ExecuteAsync(null); await Until(() => model.HasOperationPercent); Settle();
                components.Release.SetResult(); await running; Settle();
                components.UpdateFailure = new MediaOperationException("The update could not be verified.");
                await model.SettingsUpdateDownloaderCommand.ExecuteAsync(null); Settle();
                await model.SupportedSitesCommand.ExecuteAsync(null);
                window.FindNamed<Expander>("TechnicalDetails")!.IsExpanded = true; Settle();
                await model.CopySettingsDiagnosticsCommand.ExecuteAsync(null); Settle();
                window.Height = 3600; Settle();
                problems = sink.OnSettingsPage();
            }
            finally { Close(model, window); }
        }
        finally { Logger.Sink = previous; }
        Assert.NotNull(problems); Assert.Empty(problems);
    }

    // ----- Helpers -----

    // Editing a typed value hides the saved confirmation; putting it back leaves nothing changed.
    private static void HideSavedConfirmation(MainViewModel model) { var seconds = model.SoundboardSeconds; model.SoundboardSeconds = seconds + "0"; model.SoundboardSeconds = seconds; }

    private static string Folder(params string[] parts) => Path.Combine([Path.GetPathRoot(Path.GetTempPath())!, "Media", .. parts]);

    private static UiFakes Fakes(string theme = "Dark")
    {
        var fake = new UiFakes();
        fake.Store.Settings = new AppSettings { DownloadDirectory = Folder("KeaClip"), Theme = theme, ReducedMotion = true };
        return fake;
    }

    private static async Task<(UiFakes Fake, MainViewModel Model, MainWindow Window)> OpenSettingsAsync(string theme, double width, double height, Func<DesktopServices, DesktopServices>? configure = null)
    {
        var fake = Fakes(theme);
        var services = configure is null ? fake.Services : configure(fake.Services);
        var model = new MainViewModel(services);
        var window = new MainWindow { DataContext = model, Width = width, Height = height }; window.Show();
        await model.InitializeAsync(); model.SelectedTab = 4;
        window.Width = width; window.Height = height; window.UpdateLayout();
        await Until(() => !model.DefaultFolderSpacePending);
        Assert.False(model.HasUnsavedSettings);
        return (fake, model, window);
    }

    private static void Close(MainViewModel model, Window window)
    {
        model.CloseBehavior = CloseBehavior.Exit; window.Close(); model.Dispose();
    }

    private static void ScrollTo(Window window, string name)
    {
        window.UpdateLayout();
        var target = window.FindNamed<Control>(name)!; var scroll = window.FindNamed<ScrollViewer>("SettingsScroll")!;
        var top = target.TranslatePoint(new Point(0, 0), (Control)scroll.Content!)!.Value.Y;
        scroll.Offset = new Vector(0, Math.Max(0, top - 8)); window.UpdateLayout();
    }

    private static void Click(Window window, Control control)
    {
        control.BringIntoView(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        Assert.InRange(point.Y, 0, window.Bounds.Height);
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); window.UpdateLayout();
    }

    private static async Task Capture(Window window, string name)
    {
        // Colour transitions (theme switches, enabled and invalid states) run on wall-clock
        // time, so let them finish before capturing the final frame.
        window.UpdateLayout(); await Task.Delay(260); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        var output = Environment.GetEnvironmentVariable("KEACLIP_UI_EVIDENCE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output); frame!.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    // Controls hosted inside another control's template (the stepper's − and + buttons) are only in the visual tree.
    private static T FindVisual<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().First(control => control.Name == name);

    private static string? AutomationPropertiesName(Control control) => Avalonia.Automation.AutomationProperties.GetName(control);

    private static async Task Until(Func<bool> condition) { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(10, deadline.Token); }
}

internal sealed class FolderPickerPlatform : IDesktopPlatform
{
    public Queue<string?> Next { get; } = new();
    public int Picks { get; private set; }
    public Exception? Failure { get; set; }
    public void Attach(Window window) { }
    public Task<string?> PickMediaAsync() => Task.FromResult<string?>(null);
    public Task<string?> PickFolderAsync() { if (Failure is { } error) return Task.FromException<string?>(error); Picks++; return Task.FromResult(Next.Count > 0 ? Next.Dequeue() : null); }
    public Task<string?> SaveMediaAsync(string name, string extension) => Task.FromResult<string?>(null);
    public Task<string?> ReadClipboardAsync() => Task.FromResult<string?>(null);
    public Task CopyAsync(string value) => Task.CompletedTask;
    public void OpenFile(string path) { }
    public void RevealFile(string path) { }
    public void OpenFolder(string path) { }
    public void OpenUri(Uri uri) { }
    public void SetStartup(bool enabled) { }
}

internal sealed class ScriptedComponents : IComponentService
{
    public IReadOnlyList<ComponentInfo> Items { get; set; } = [];
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>When set, inspections wait for it, so a test can look at the page mid-check.</summary>
    public TaskCompletionSource? InspectGate { get; set; }
    public Exception? UpdateFailure { get; set; }
    public string? Find(string name) => null;
    public async Task<IReadOnlyList<ComponentInfo>> InspectAsync(CancellationToken cancellationToken = default)
    {
        if (InspectGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
        return Items;
    }
    public async Task ProvisionAsync(IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(new(JobStage.Downloading, 42, "Downloading verified FFprobe"));
        await Release.Task.WaitAsync(cancellationToken);
        Items = Items.Select(item => item with { Ready = true, Version = item.Version ?? "ffprobe version 9.0.2-essentials_build-www.gyan.dev", Error = null }).ToArray();
    }
    public Task UpdateDownloaderAsync(IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => UpdateFailure is { } error ? Task.FromException(error) : Task.CompletedTask;
}

/// <summary>
/// Collects binding warnings and errors (reflection bindings fail silently otherwise). A control
/// reports a failed binding while it is being attached, before its parent is set, so the page
/// it belongs to is checked afterwards with <see cref="OnSettingsPage"/> while the window is open.
/// </summary>
internal sealed class SettingsBindingSink : ILogSink
{
    private readonly List<(object? Source, string Text)> _entries = [];
    public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area == LogArea.Binding;
    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Log(level, area, source, messageTemplate, []);
    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        if (!IsEnabled(level, area)) return;
        var values = string.Join(" | ", propertyValues.Select(value => value?.ToString()));
        lock (_entries) _entries.Add((source, $"{source?.GetType().Name} ({(source as Control)?.Name}): {messageTemplate} => {values}"));
    }

    /// <summary>Binding problems whose control is (still) part of the Settings page.</summary>
    public string[] OnSettingsPage() { lock (_entries) return _entries.Where(entry => IsOnSettingsPage(entry.Source)).Select(entry => entry.Text).Distinct().ToArray(); }
    public void Clear() { lock (_entries) _entries.Clear(); }

    private static bool IsOnSettingsPage(object? source) => source is Control control
        && (control is Klippi.Desktop.Views.SettingsView || control.FindAncestorOfType<Klippi.Desktop.Views.SettingsView>() is not null || control.FindLogicalAncestorOfType<Klippi.Desktop.Views.SettingsView>() is not null);
}

internal sealed class SitesDownloads : IDownloadService
{
    private static readonly string[] Sites = ["Bandcamp", "BBC", "Bilibili", "Dailymotion", "Internet Archive", "Mixcloud", "NZ Herald", "Odysee", "PeerTube", "RNZ", "SoundCloud", "Streamable", "TikTok", "Twitch", "Vimeo", "YouTube"];
    public Task<MediaInfo> AnalyseAsync(Uri url, string? browser = null, CancellationToken cancellationToken = default) => Task.FromException<MediaInfo>(new MediaOperationException("This website changed and the downloader needs an update.", "yt-dlp: ERROR: Unsupported URL " + url));
    public Task<DownloadResult> DownloadAsync(DownloadOptions options, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromException<DownloadResult>(new MediaOperationException("Downloads are not part of this settings test."));
    public Task<IReadOnlyList<string>> SupportedSitesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(Sites);
}
