using Avalonia.Headless.XUnit;
using Klippi.Core;
using Klippi.Desktop;
using Xunit;

namespace Klippi.UiTests;

// Regression tests for the final review's download-queue findings #1-#4 and the clipboard
// finding #11.
public sealed class ReviewFixQueueDownloadTests
{
    private const string Link = "https://example.com/video";

    // ----- #1 the queue's "already in your queue" check -----

    [AvaloniaFact]
    public async Task Another_audio_format_of_a_link_still_downloading_is_queued()
    {
        var fake = new UiFakes(); fake.Downloads.WaitForCancellation = true;
        using var model = new MainViewModel(fake.Services) { Url = Link, DownloadMode = DownloadMode.Audio, AudioFormat = AudioFormat.Mp3 };
        await model.QueueDownloadAsync(false); await WaitUntil(() => fake.Downloads.Requests.Count == 1);
        model.AudioFormat = AudioFormat.Flac; await model.QueueDownloadAsync(false);
        Assert.Equal(2, model.Queue.Count);
        Assert.StartsWith("Added to your queue", model.Status);
        // The same request again is still never fetched twice.
        await model.QueueDownloadAsync(false);
        Assert.Equal(2, model.Queue.Count); Assert.Contains("already in your queue", model.Status);
    }

    [AvaloniaFact]
    public async Task Two_files_of_one_download_page_are_both_queued()
    {
        // ?file= is not kept in the stored link, so both links share one stored form.
        var fake = new UiFakes(); fake.Downloads.WaitForCancellation = true;
        using var model = new MainViewModel(fake.Services) { Url = "https://files.example.com/download.php?file=a.mp4" };
        await model.QueueDownloadAsync(false); await WaitUntil(() => fake.Downloads.Requests.Count == 1);
        model.Url = "https://files.example.com/download.php?file=b.mp4"; await model.QueueDownloadAsync(false);
        Assert.Equal(2, model.Queue.Count);
        Assert.StartsWith("Added to your queue", model.Status);
    }

    [AvaloniaFact]
    public async Task Other_settings_that_write_the_same_file_are_held_back_with_a_true_reason()
    {
        var fake = new UiFakes(); fake.Downloads.WaitForCancellation = true;
        using var model = new MainViewModel(fake.Services) { Url = Link };
        await model.QueueDownloadAsync(false); await WaitUntil(() => fake.Downloads.Requests.Count == 1);
        model.Quality = "Smallest available"; await model.QueueDownloadAsync(false);
        Assert.Single(model.Queue);
        Assert.Contains("saves to the same file", model.Status);
        Assert.DoesNotContain("already in your queue", model.Status);
    }

    // ----- #2 a waiting duplicate card follows the latest request -----

    [AvaloniaFact]
    public async Task Asking_again_with_other_options_updates_the_waiting_card()
    {
        var root = TempFolder();
        try
        {
            var saved = Path.Combine(root, "saved.mp4"); await File.WriteAllTextAsync(saved, "kept");
            var fake = new UiFakes(); fake.Store.History.Add(Record("existing", saved, "VideoAudio"));
            using var model = new MainViewModel(fake.Services) { Url = Link, DownloadDirectory = root }; await model.InitializeAsync();
            await model.QueueDownloadAsync(false);
            Assert.False(Assert.Single(model.DuplicateChoices).Options.SmallestFile);
            model.Quality = "Smallest available"; await model.QueueDownloadAsync(true);
            var card = Assert.Single(model.DuplicateChoices);
            Assert.True(card.Options.SmallestFile); Assert.True(card.EditAfter);
            model.ResolveDuplicate(card, "again"); await WaitUntil(() => fake.Downloads.Requests.Count == 1);
            Assert.True(Assert.Single(fake.Downloads.Requests).SmallestFile);
            Assert.Equal("kept", await File.ReadAllTextAsync(saved));
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task A_saved_mp3_does_not_stop_a_flac_and_its_old_card_goes_away()
    {
        var root = TempFolder();
        try
        {
            var saved = Path.Combine(root, "song.mp3"); await File.WriteAllTextAsync(saved, "kept");
            var fake = new UiFakes(); fake.Store.History.Add(Record("existing", saved, "Audio"));
            using var model = new MainViewModel(fake.Services) { Url = Link, DownloadDirectory = root, DownloadMode = DownloadMode.Audio, AudioFormat = AudioFormat.Mp3 }; await model.InitializeAsync();
            await model.QueueDownloadAsync(false); Assert.Single(model.DuplicateChoices);
            model.AudioFormat = AudioFormat.Flac; await model.QueueDownloadAsync(false);
            Assert.Empty(model.DuplicateChoices);
            await WaitUntil(() => fake.Downloads.Requests.Count == 1);
            Assert.Equal(AudioFormat.Flac, Assert.Single(fake.Downloads.Requests).AudioFormat);
            Assert.StartsWith("Added to your queue", model.Status);
            Assert.Equal("kept", await File.ReadAllTextAsync(saved));
        }
        finally { Directory.Delete(root, true); }
    }

    // ----- #3 an old card does not decide a later request's status -----

    [AvaloniaFact]
    public async Task An_old_duplicate_card_does_not_hijack_a_new_links_status()
    {
        var root = TempFolder();
        try
        {
            var saved = Path.Combine(root, "saved.mp4"); await File.WriteAllTextAsync(saved, "kept");
            var fake = new UiFakes(); fake.Store.History.Add(Record("existing", saved, "VideoAudio"));
            using var model = new MainViewModel(fake.Services) { Url = Link, DownloadDirectory = root }; await model.InitializeAsync();
            await model.QueueDownloadAsync(false); Assert.Single(model.DuplicateChoices); Assert.Equal(0, model.SelectedTab);
            model.Url = "https://example.com/another"; await model.QueueDownloadAsync(false);
            await WaitUntil(() => fake.Downloads.Requests.Count == 1);
            Assert.StartsWith("Added to your queue", model.Status);
            Assert.Equal(1, model.SelectedTab);
            Assert.Single(model.DuplicateChoices);
        }
        finally { Directory.Delete(root, true); }
    }

    // ----- #4 Replace only overwrites the saved file itself -----

    [AvaloniaFact]
    public async Task Replace_is_not_offered_when_the_engine_cannot_write_the_saved_name()
    {
        // The engine never writes a percent sign, so Replace would save "50_ off.mp4" next to
        // "50% off.mp4" while the card promised to overwrite it.
        var root = TempFolder();
        try
        {
            var saved = Path.Combine(root, "50% off.mp4"); await File.WriteAllTextAsync(saved, "kept");
            var fake = new UiFakes(); fake.Store.History.Add(Record("existing", saved, "VideoAudio"));
            using var model = new MainViewModel(fake.Services) { Url = Link, DownloadDirectory = root }; await model.InitializeAsync();
            await model.QueueDownloadAsync(false);
            var card = Assert.Single(model.DuplicateChoices);
            Assert.False(card.CanReplace); Assert.Contains("Download again", card.ReplaceTip);
            model.ResolveDuplicate(card, "replace"); await Task.Delay(50);
            Assert.Empty(fake.Downloads.Requests); Assert.Single(model.DuplicateChoices);
            Assert.Contains("Download again", model.Status);
            Assert.Equal([saved], Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task A_saved_webm_is_not_offered_for_replacement_by_an_mp4_request()
    {
        // Replace is never the answer to another format: the MP4 is simply a different download.
        var root = TempFolder();
        try
        {
            var saved = Path.Combine(root, "saved.webm"); await File.WriteAllTextAsync(saved, "kept");
            var fake = new UiFakes(); fake.Store.History.Add(Record("existing", saved, "VideoAudio"));
            using var model = new MainViewModel(fake.Services) { Url = Link, DownloadDirectory = root, Container = MediaContainer.Mp4 }; await model.InitializeAsync();
            await model.QueueDownloadAsync(false);
            Assert.Empty(model.DuplicateChoices);
            await WaitUntil(() => fake.Downloads.Requests.Count == 1);
            Assert.False(Assert.Single(fake.Downloads.Requests).ReplaceExisting);
            Assert.Equal("kept", await File.ReadAllTextAsync(saved));
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task A_finished_replace_leaves_one_library_record_for_the_file()
    {
        var root = TempFolder();
        try
        {
            var saved = Path.Combine(root, "saved.mp4"); await File.WriteAllTextAsync(saved, "kept");
            var fake = new UiFakes(); fake.Store.History.Add(Record("existing", saved, "VideoAudio"));
            var downloads = new NamedOutputDownloads();
            var services = new DesktopServices(downloads, fake.Media, fake.Sharing, fake.Store, fake.Components, fake.Platform, fake.Playback, "test-owned-data");
            using var model = new MainViewModel(services) { Url = Link, DownloadDirectory = root }; await model.InitializeAsync();
            await model.QueueDownloadAsync(false);
            var card = Assert.Single(model.DuplicateChoices); Assert.True(card.CanReplace);
            model.ResolveDuplicate(card, "replace");
            await WaitUntil(() => fake.Store.History.Count == 1 && fake.Store.History[0].Id != "existing");
            Assert.Equal(saved, Assert.Single(downloads.Requests).ReplacePath);
            Assert.Equal(saved, fake.Store.History[0].FilePath, ignoreCase: true);
            // Removing the old record never touches the file (the fake engine writes nothing).
            Assert.Equal("kept", await File.ReadAllTextAsync(saved));
        }
        finally { Directory.Delete(root, true); }
    }

    // ----- #11 the app's own copied links are not offered back -----

    [AvaloniaFact]
    public async Task A_copied_share_link_never_returns_to_the_download_box()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        await model.ShareFileAsync("fixture.mp4"); await model.CopyShareCommand.ExecuteAsync(null);
        Assert.Contains("#decryption-secret", fake.Platform.Clipboard);
        await model.DetectClipboardAsync();
        Assert.Equal("", model.Url ?? "");
        Assert.StartsWith("Share link copied", model.Status);
    }

    [AvaloniaFact]
    public async Task A_share_link_already_on_the_clipboard_is_not_detected()
    {
        var fake = new UiFakes(); fake.Platform.Clipboard = "https://file.kiwi/folder#secret-key";
        using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        await model.DetectClipboardAsync();
        Assert.Equal("", model.Url ?? "");
        Assert.DoesNotContain("Link detected", model.Status ?? "");
    }

    [AvaloniaFact]
    public async Task A_copied_source_link_never_returns_to_the_download_box()
    {
        var fake = new UiFakes(); using var model = new MainViewModel(fake.Services); await model.InitializeAsync();
        await model.CopySourceAsync(Record("saved", "missing.mp4", "VideoAudio"));
        Assert.Equal(Link, fake.Platform.Clipboard);
        await model.DetectClipboardAsync();
        Assert.Equal("", model.Url ?? "");
    }

    private static HistoryEntry Record(string id, string path, string mode) => new(id, "Saved media", Link, "Example", DateTimeOffset.Now, path, mode, "Best available", 4, "Finished");

    private static string TempFolder() { var root = Path.Combine(Path.GetTempPath(), "KeaClip-ReviewFix-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }

    private static async Task WaitUntil(Func<bool> condition) { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(10, deadline.Token); }

    // Reports the file the request names (folder, template and .mp4), as the engine does, without writing it.
    private sealed class NamedOutputDownloads : IDownloadService
    {
        public List<DownloadOptions> Requests { get; } = [];
        public Task<MediaInfo> AnalyseAsync(Uri url, string? browser = null, CancellationToken cancellationToken = default) => Task.FromResult(UiFakes.Info with { Url = url });
        public Task<DownloadResult> DownloadAsync(DownloadOptions options, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(options);
            return Task.FromResult(new DownloadResult(Path.Combine(options.OutputDirectory, (options.NamingTemplate ?? "output") + ".mp4"), UiFakes.Info with { Url = options.Url }, 2_000_000));
        }
        public Task<IReadOnlyList<string>> SupportedSitesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["sample"]);
    }
}
