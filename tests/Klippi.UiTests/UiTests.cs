using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Styling;
using Klippi.Core;
using Klippi.Desktop;
using LibVLCSharp.Shared;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Klippi.UiTests.TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Klippi.UiTests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().WithInterFont().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class UiTests
{
    [AvaloniaFact]
    public async Task Clipboard_and_protocol_prefill_never_start_a_download()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        await model.InitializeAsync();
        // An ordinary page is not offered; a media link is filled in but never analysed or downloaded by itself.
        fake.Platform.Clipboard = "https://ko-fi.com/keaclip#setCreatorStatusModal";
        await model.DetectClipboardAsync();
        Assert.Equal("", model.Url ?? ""); Assert.DoesNotContain("Link detected", model.Status ?? "");
        fake.Platform.Clipboard = "https://www.youtube.com/watch?v=private1";
        await model.DetectClipboardAsync();
        Assert.Equal(fake.Platform.Clipboard, model.Url); Assert.Equal(0, fake.Downloads.Analyses); Assert.Empty(fake.Downloads.Requests);
        model.AcceptLaunchArgument("keaclip://download?url=https%3A%2F%2Fexample.com%2Fsecond");
        Assert.Equal("https://example.com/second", model.Url); Assert.Empty(fake.Downloads.Requests);
        model.Url = ""; model.DetectClipboard = false; await model.DetectClipboardAsync(); Assert.Equal("", model.Url);
    }

    [AvaloniaFact]
    public async Task Playlist_requires_explicit_entry_selection()
    {
        var fake = new UiFakes(); fake.Downloads.Info = UiFakes.Info with { Entries = [new("one", "One", new("https://example.com/one"), TimeSpan.FromSeconds(5)), new("two", "Two", new("https://example.com/two"), TimeSpan.FromSeconds(8))] };
        using var model = new MainViewModel(fake.Services) { Url = "https://example.com/list" };
        await model.AnalyseAsync(); Assert.All(model.Playlist, item => Assert.False(item.Selected));
        await model.QueueDownloadAsync(false); Assert.Empty(fake.Downloads.Requests); Assert.Contains("Select at least one", model.Status);
        model.Playlist[1].Selected = true; await model.QueueDownloadAsync(false); await WaitUntil(() => fake.Downloads.Requests.Count == 1);
        Assert.Equal("https://example.com/two", fake.Downloads.Requests.Single().Url.AbsoluteUri);
    }

    [AvaloniaFact]
    public async Task Invalid_urls_cannot_reach_the_downloader()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services) { Url = "file:///C:/secret.txt" };
        await model.QueueDownloadAsync(false); Assert.Equal(0, fake.Downloads.Analyses); Assert.Empty(fake.Downloads.Requests);
        Assert.Contains("complete http", model.Status);
    }

    [AvaloniaFact]
    public async Task Cancel_button_cancels_the_active_service_request()
    {
        var fake = new UiFakes(); fake.Downloads.WaitForCancellation = true;
        using var model = new MainViewModel(fake.Services) { Url = "https://example.com/video" };
        await model.QueueDownloadAsync(false); await WaitUntil(() => fake.Downloads.Requests.Count == 1);
        Assert.Single(model.Queue); model.Queue[0].CancelCommand.Execute(null); await WaitUntil(() => fake.Downloads.Cancelled);
        Assert.Empty(fake.Store.History);
    }

    [AvaloniaFact]
    public async Task Clearing_history_preserves_media_and_loaded_startup_is_not_reapplied()
    {
        var fake = new UiFakes(); fake.Store.Settings = fake.Store.Settings with { StartWithWindows = true };
        var path = Path.Combine(Path.GetTempPath(), "keaclip-ui-" + Guid.NewGuid().ToString("N") + ".mp4"); await File.WriteAllTextAsync(path, "owned test fixture");
        try
        {
            fake.Store.History.Add(new("history", "Sample", null, "Local", DateTimeOffset.UtcNow, path, "Video", "Original", 18, "Finished"));
            using var model = new MainViewModel(fake.Services); await model.InitializeAsync(); Assert.Empty(fake.Platform.StartupWrites);
            await model.ClearHistoryCommand.ExecuteAsync(null); Assert.Empty(fake.Store.History); Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task Export_requires_a_valid_range_and_keeps_original_by_default()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        await model.OpenEditorAsync("fixture.mp4"); Assert.False(model.DeleteOriginal); Assert.Equal(CompressionPreset.Off, model.Compression);
        model.StartTime = "00:00:08.000"; model.EndTime = "00:00:02.000";
        await model.ExportAsync(); Assert.Equal(0, fake.Platform.SaveDialogs); Assert.Empty(fake.Media.Exports);
        model.StartTime = "00:00:01.000"; model.EndTime = "00:00:03.000";
        fake.Platform.SavePath = "export.mp4"; await model.ExportAsync();
        var export = Assert.Single(fake.Media.Exports); Assert.False(export.DeleteOriginalAfterSuccess); Assert.Equal(TimeSpan.FromSeconds(1), export.Start); Assert.Equal(TimeSpan.FromSeconds(3), export.End);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Successful_export_and_chosen_deletion_stay_visible_when_history_fails(bool cancelled)
    {
        var fake = new UiFakes(); fake.Store.HistoryFailure = cancelled ? new OperationCanceledException() : new IOException("History storage is full."); fake.Media.OriginalDeleted = true; fake.Platform.SavePath = "export.mp4";
        using var model = new MainViewModel(fake.Services); await model.OpenEditorAsync("fixture.mp4"); model.DeleteOriginal = true;
        await model.ExportAsync();
        Assert.Contains("Export verified", model.Status); Assert.Contains("Original deleted as requested", model.Status); Assert.Contains("history could not", model.Status);
        Assert.False(model.HasEditor); Assert.Equal("", model.EditorPath); Assert.Equal(1, fake.Playback.CloseCalls); Assert.Empty(fake.Store.History);
        await model.OpenEditorAsync("different.mp4"); Assert.False(model.DeleteOriginal);
    }

    [AvaloniaFact]
    public async Task Share_is_explicit_and_the_full_secret_never_enters_history()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services); await model.OpenEditorAsync("fixture.mp4");
        Assert.Equal(0, fake.Sharing.Calls); await model.ShareFileAsync("fixture.mp4");
        Assert.Equal(1, fake.Sharing.Calls); Assert.Contains("#decryption-secret", model.ShareUrl); Assert.Empty(fake.Store.History);
        Assert.Equal("Shared media", fake.Sharing.LastTitle);
        await model.CopyShareCommand.ExecuteAsync(null); Assert.Equal(model.ShareUrl, fake.Platform.Clipboard); Assert.DoesNotContain("decryption-secret", model.Status);
        model.OpenShareCommand.Execute(null); Assert.Equal(model.ShareUrl, Assert.Single(fake.Platform.OpenedUris).AbsoluteUri);
    }

    [AvaloniaFact]
    public void Timeline_handles_drag_independently_and_scrubbing_follows_pointer()
    {
        var control = new WaveformControl { Samples = [.2f, .5f, .3f], StartRatio = .2, EndRatio = .8 };
        var window = new Window { Width = 600, Height = 90, Content = control }; window.Show(); window.UpdateLayout();
        try
        {
            var width = control.Bounds.Width; var height = control.Bounds.Height; var seek = -1d; control.SeekRequested += ratio => seek = ratio;
            window.MouseDown(new(width * .2, height / 2), MouseButton.Left); window.MouseMove(new(width * .3, height / 2)); window.MouseUp(new(width * .3, height / 2), MouseButton.Left);
            Assert.InRange(control.StartRatio, .29, .31); Assert.Equal(.8, control.EndRatio); Assert.Equal(-1, seek);
            window.MouseDown(new(width * .8, height / 2), MouseButton.Left); window.MouseMove(new(width * .7, height / 2)); window.MouseUp(new(width * .7, height / 2), MouseButton.Left);
            Assert.InRange(control.EndRatio, .69, .71);
            window.MouseDown(new(width * .5, height / 2), MouseButton.Left); window.MouseMove(new(width * .6, height / 2)); window.MouseUp(new(width * .6, height / 2), MouseButton.Left);
            Assert.InRange(seek, .59, .61); Assert.InRange(control.StartRatio, .29, .31); Assert.InRange(control.EndRatio, .69, .71);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Editing_timecodes_does_not_trigger_player_keyboard_shortcuts()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services); await model.OpenEditorAsync("fixture.mp4");
        var window = new MainWindow { DataContext = model }; window.Show(); window.UpdateLayout();
        try
        {
            window.FindNamed<TextBox>("StartTimeBox")!.Focus();
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " "); window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " "); window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null); window.KeyRelease(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            Assert.False(fake.Playback.IsPlaying); Assert.Equal(0, fake.Playback.Position);
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Completion_notice_requires_user_setting_and_is_not_duplicated()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services) { Url = "https://example.com/video", NotifyCompleted = true };
        var notices = new List<string>(); model.CompletionNotice += notices.Add;
        await model.QueueDownloadAsync(false); await WaitUntil(() => notices.Count > 0); await Task.Delay(30);
        Assert.Equal("A moment worth keeping", Assert.Single(notices));
    }

    [AvaloniaFact]
    public async Task Workspaces_render_at_laptop_width_and_four_scalings_without_native_playback()
    {
        var fake = new UiFakes(); fake.Downloads.Info = UiFakes.Info with { PublishedAt = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero) };
        using var model = new MainViewModel(fake.Services) { Url = "https://example.com/video" };
        var window = new MainWindow { DataContext = model, Width = 900, Height = 740 }; window.Show();
        try
        {
            await model.InitializeAsync(); await model.AnalyseAsync(); await model.QueueDownloadAsync(false); await WaitUntil(() => fake.Store.History.Count == 1); await model.OpenEditorAsync("fixture.mp4");
            var output = Environment.GetEnvironmentVariable("KEACLIP_UI_EVIDENCE_DIRECTORY");
            var names = new[] { "download", "queue", "library", "editor", "settings" };
            foreach (var theme in new[] { "Dark", "Light" })
            {
                model.Theme = theme;
                for (var tab = 0; tab < names.Length; tab++)
                {
                    model.SelectedTab = tab;
                    foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
                    {
                        window.SetRenderScaling(scale); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        using var image = window.CaptureRenderedFrame(); Assert.NotNull(image); Assert.True(image.PixelSize.Width >= 880);
                        Assert.Equal(900, window.Bounds.Width, 1); Assert.True(window.FindNamed<TextBlock>("StatusText")!.Bounds.Width > 0);
                        if (!string.IsNullOrWhiteSpace(output) && (scale == 1 || theme == "Dark" && tab >= 3)) { Directory.CreateDirectory(output); image.Save(Path.Combine(output, $"{names[tab]}-{theme.ToLowerInvariant()}-{scale * 100:0}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); }
                    }
                }
            }
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Window_renders_in_both_themes_and_navigation_remains_functional()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services);
        var window = new MainWindow { DataContext = model }; fake.Platform.Attach(window); window.Show();
        try
        {
            await model.InitializeAsync();
            window.FindNamed<Button>("SettingsNav")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.True(model.IsSettings);
            window.FindNamed<Button>("DownloadNav")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.True(model.IsDownload);
            Assert.NotNull(window.FindNamed<TextBox>("UrlInput")); Assert.NotNull(window.FindNamed<Button>("DownloadButton"));
            var output = Environment.GetEnvironmentVariable("KEACLIP_UI_EVIDENCE_DIRECTORY");
            foreach (var theme in new[] { "Dark", "Light" })
            {
                model.Theme = theme; window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var bitmap = window.CaptureRenderedFrame(); Assert.NotNull(bitmap); Assert.True(bitmap.PixelSize.Width >= 880);
                if (!string.IsNullOrWhiteSpace(output)) { Directory.CreateDirectory(output); bitmap.Save(Path.Combine(output, "download-" + theme.ToLowerInvariant() + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); }
            }
        }
        finally { model.CloseBehavior = CloseBehavior.Exit; window.Close(); }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}

public sealed class UiFakes
{
    public static MediaInfo Info => new("sample", "A moment worth keeping", new("https://example.com/video"), "Sample creator", "Sample source", TimeSpan.FromSeconds(10), null, null, "A deterministic preview fixture.", [new("1080", "mp4", 1920, 1080, 30, "h264", "aac", 2_000_000, 128, "en")], [], null);
    public FakeDownloads Downloads { get; } = new();
    public FakeMedia Media { get; } = new();
    public FakeSharing Sharing { get; } = new();
    public FakeStore Store { get; } = new();
    public FakePlatform Platform { get; } = new();
    public FakePlayback Playback { get; } = new();
    public FakeComponents Components { get; } = new();
    public DesktopServices Services => new(Downloads, Media, Sharing, Store, Components, Platform, Playback, "test-owned-data");
}

public sealed class FakeDownloads : IDownloadService
{
    public MediaInfo Info { get; set; } = UiFakes.Info;
    public int Analyses { get; private set; }
    public List<DownloadOptions> Requests { get; } = [];
    public bool WaitForCancellation { get; set; }
    public bool IndeterminateProgress { get; set; }
    /// <summary>The file every finished download reports.</summary>
    public string ResultPath { get; set; } = "test-output.mp4";
    public Func<DownloadOptions, string>? ResultPathFor { get; set; }
    /// <summary>Downloads wait for this gate (or cancellation) before they finish or fail.</summary>
    public TaskCompletionSource? DownloadGate { get; set; }
    public Exception? DownloadFailure { get; set; }
    private volatile bool _threw;
    /// <summary>A download has thrown <see cref="DownloadFailure"/> (set on the download's own thread).</summary>
    public bool Threw { get => _threw; private set => _threw = value; }
    public bool Cancelled { get; private set; }
    public Exception? AnalysisFailure { get; set; }
    /// <summary>
    /// When set, an analysis waits for it (or for cancellation) before answering. The gate is read
    /// before <see cref="Analyses"/> counts the call, so once a test sees the count it may clear the
    /// gate for later analyses without releasing that one.
    /// </summary>
    public TaskCompletionSource? AnalysisGate { get; set; }
    public Task<MediaInfo> AnalyseAsync(Uri url, string? browser = null, CancellationToken cancellationToken = default)
    {
        var gate = AnalysisGate;
        Analyses++;
        if (AnalysisFailure is { } error) return Task.FromException<MediaInfo>(error);
        return gate is not null ? GatedAnalysisAsync(gate, url, cancellationToken) : Task.FromResult(Info with { Url = url });
    }
    private async Task<MediaInfo> GatedAnalysisAsync(TaskCompletionSource gate, Uri url, CancellationToken cancellationToken) { await gate.Task.WaitAsync(cancellationToken); return Info with { Url = url }; }
    public async Task<DownloadResult> DownloadAsync(DownloadOptions options, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Requests.Add(options); progress?.Report(new(JobStage.Downloading, IndeterminateProgress ? null : 20, "Downloading"));
        if (WaitForCancellation) { try { await Task.Delay(Timeout.Infinite, cancellationToken); } catch (OperationCanceledException) { Cancelled = true; throw; } }
        if (DownloadGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
        if (DownloadFailure is { } failure) { Threw = true; throw failure; }
        return new(ResultPathFor?.Invoke(options) ?? ResultPath, Info, 2_000_000);
    }
    public Task<IReadOnlyList<string>> SupportedSitesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["sample"]);
}

public sealed class FakeMedia : IMediaService
{
    public bool OriginalDeleted { get; set; }
    /// <summary>FFprobe JSON for the next probes, for example an HDR (PQ) video stream.</summary>
    public string? TechnicalJson { get; set; }
    public List<ExportOptions> Exports { get; } = [];
    public Func<string, bool>? ProbeFails { get; set; }
    // Holds the next probes until released, so a test can look at the editor while a file opens.
    public TaskCompletionSource? ProbeGate { get; set; }
    // A progress report that reaches the app only after the export has returned (an engine
    // output line read after the process exit was seen).
    public TimeSpan? LateProgressDelay { get; set; }
    public async Task<ProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (ProbeGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
        return ProbeFails?.Invoke(path) == true ? throw new MediaOperationException("FFmpeg could not process this file.") : new ProbeInfo(path, TimeSpan.FromSeconds(10), 2_000_000, true, true, 1920, 1080, 30, "h264", "aac", 48000, 2, TechnicalJson);
    }
    public async Task<ExportResult> ExportAsync(ExportOptions options, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Exports.Add(options);
        if (LateProgressDelay is { } delay) _ = Task.Run(async () => { await Task.Delay(delay); progress?.Report(new(JobStage.Converting, 42, "Encoding")); });
        return new(options.OutputPath, await ProbeAsync(options.OutputPath, cancellationToken), OriginalDeleted);
    }
    public Task<IReadOnlyList<EncoderInfo>> EncodersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<EncoderInfo>>([new("libx264", "CPU H.264", false)]);
    public Func<string, bool>? WaveformFails { get; set; }
    public Task<IReadOnlyList<float>> WaveformAsync(string path, int points = 600, CancellationToken cancellationToken = default) => WaveformFails?.Invoke(path) == true ? Task.FromException<IReadOnlyList<float>>(new MediaOperationException("FFmpeg could not process this file.")) : Task.FromResult<IReadOnlyList<float>>(Enumerable.Range(0, points).Select(i => (float)Math.Abs(Math.Sin(i / 12d)) * .75f).ToArray());
    public ExportEstimate Estimate(ProbeInfo probe, ExportOptions options) => new((options.End ?? probe.Duration) - (options.Start ?? TimeSpan.Zero), 300_000, null, "Actual output is checked after export.");
}

public sealed class FakeSharing : ISharingService
{
    public int Calls { get; private set; }
    public string? LastTitle { get; private set; }
    public List<string> Paths { get; } = [];
    /// <summary>The next shares fail with this error (after counting the call).</summary>
    public Exception? Failure { get; set; }
    public TaskCompletionSource? Gate { get; set; }
    // Reported in order before the gate is awaited.
    public List<JobProgress> Reports { get; } = [];
    public async Task<ShareResult> ShareAsync(string path, string? title = null, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) { Calls++; LastTitle = title; Paths.Add(path); if (Failure is { } failure) throw failure; foreach (var report in Reports) progress?.Report(report); if (Gate is { } gate) await gate.Task.WaitAsync(cancellationToken); return await Task.FromResult(new ShareResult("https://file.kiwi/example#decryption-secret", "folder", "file", 48, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(48), "checksum", null)); }
}

public sealed class FakeStore : ILocalStore
{
    public AppSettings Settings { get; set; } = new();
    public List<HistoryEntry> History { get; } = [];
    public Exception? HistoryFailure { get; set; }
    public Exception? HistoryLoadFailure { get; set; }
    public Exception? SettingsFailure { get; set; }
    public Exception? SettingsLoadFailure { get; set; }
    public string? Warning { get; set; }
    public string? TakeWarning() { var warning = Warning; Warning = null; return warning; }
    public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => SettingsLoadFailure is { } error ? Task.FromException<AppSettings>(error) : Task.FromResult(Settings);
    public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) { if (SettingsFailure is { } error) return Task.FromException(error); Settings = settings; return Task.CompletedTask; }
    public Task<IReadOnlyList<HistoryEntry>> LoadHistoryAsync(CancellationToken cancellationToken = default) => HistoryLoadFailure is { } error ? Task.FromException<IReadOnlyList<HistoryEntry>>(error) : Task.FromResult<IReadOnlyList<HistoryEntry>>(History.ToArray());
    public Task AddHistoryAsync(HistoryEntry entry, CancellationToken cancellationToken = default) { if (HistoryFailure is { } error) return Task.FromException(error); History.Add(entry); return Task.CompletedTask; }
    public Task RemoveHistoryAsync(string id, CancellationToken cancellationToken = default) { History.RemoveAll(item => item.Id == id); return Task.CompletedTask; }
    public Task ClearHistoryAsync(CancellationToken cancellationToken = default) { History.Clear(); return Task.CompletedTask; }
    public Task PruneHistoryAsync(int? retentionDays, CancellationToken cancellationToken = default) => Task.CompletedTask;
    /// <summary>The saved queue: read at startup and replaced by every save.</summary>
    public SavedQueue? Queue { get; set; }
    public List<SavedQueue> QueueSaves { get; } = [];
    public Exception? QueueLoadFailure { get; set; }
    public Task<SavedQueue?> LoadQueueAsync(CancellationToken cancellationToken = default) => QueueLoadFailure is { } error ? Task.FromException<SavedQueue?>(error) : Task.FromResult(Queue);
    public Task SaveQueueAsync(SavedQueue queue, CancellationToken cancellationToken = default) { lock (QueueSaves) { QueueSaves.Add(queue); Queue = queue; } return Task.CompletedTask; }
}

public sealed class FakeComponents : IComponentService
{
    public string? Find(string name) => null;
    public bool Ready { get; set; } = true;
    public Task<IReadOnlyList<ComponentInfo>> InspectAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ComponentInfo>>([new("yt-dlp", null, "Test component", Ready), new("FFmpeg", null, "Test component", true)]);
    public Task ProvisionAsync(IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public string? UpdateMessage { get; set; }
    public Task UpdateDownloaderAsync(IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) { if (UpdateMessage is { } message) progress?.Report(new(JobStage.Finished, 100, message)); return Task.CompletedTask; }
}

public sealed class FakePlatform : IDesktopPlatform
{
    public bool? SystemReducedMotion { get; set; }
    public int PreferenceReads { get; private set; }
    public bool? GetReducedMotionPreference() { PreferenceReads++; return SystemReducedMotion; }
    public string? Clipboard { get; set; }
    public string? SavePath { get; set; }
    public int SaveDialogs { get; private set; }
    public List<bool> StartupWrites { get; } = [];
    public List<Uri> OpenedUris { get; } = [];
    public List<string> OpenedFiles { get; } = [];
    public void Attach(Window window) { }
    public Task<string?> PickMediaAsync() => Task.FromResult<string?>(null);
    public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    public string? SuggestedName { get; private set; }
    public Task<string?> SaveMediaAsync(string name, string extension) { SaveDialogs++; SuggestedName = name; return Task.FromResult(SavePath); }
    public Task<string?> ReadClipboardAsync() => Task.FromResult(Clipboard);
    public Exception? CopyFailure { get; set; }
    public Task CopyAsync(string value) { if (CopyFailure is { } error) return Task.FromException(error); Clipboard = value; return Task.CompletedTask; }
    // Like DesktopPlatform for a file that has been moved or deleted.
    public bool FilesMissing { get; set; }
    public void OpenFile(string path) { if (FilesMissing) throw new FileNotFoundException("The file is no longer available."); OpenedFiles.Add(path); }
    public List<string> RevealedFiles { get; } = [];
    public void RevealFile(string path) { if (FilesMissing) throw new FileNotFoundException("The file is no longer available."); RevealedFiles.Add(path); }
    public void OpenFolder(string path) { }
    public void OpenUri(Uri uri) => OpenedUris.Add(uri);
    public void SetStartup(bool enabled) => StartupWrites.Add(enabled);
    public List<(bool Enabled, bool Minimized)> StartupEntries { get; } = [];
    public void SetStartup(bool enabled, bool minimized) { StartupWrites.Add(enabled); StartupEntries.Add((enabled, minimized)); }
}

public sealed class FakePlayback : IPlaybackService
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public MediaPlayer? NativePlayer => null;
    public bool IsPlaying { get; private set; }
    public double Position { get; private set; }
    public TimeSpan Time => TimeSpan.FromSeconds(Position * 10);
    public int CloseCalls { get; private set; }
    public void Open(string path) { }
    public void Toggle() { IsPlaying = !IsPlaying; PropertyChanged?.Invoke(this, new(nameof(IsPlaying))); }
    public void Seek(double position) { Position = position; PropertyChanged?.Invoke(this, new(nameof(Position))); }
    public void SetVolume(int value) { }
    public void Stop() { IsPlaying = false; }
    public void Close() { IsPlaying = false; CloseCalls++; }
    public void Dispose() { }
}
