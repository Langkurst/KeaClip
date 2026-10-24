using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Klippi.Core;
using Klippi.Infrastructure.Updates;
using Xunit;

namespace Klippi.Tests;

public sealed class UpdateVersionAndFeedTests
{
    [Theory]
    [InlineData("0.2.0", 0, 2, 0)]
    [InlineData("v1.10.3", 1, 10, 3)]
    [InlineData(" 12.0.99999 ", 12, 0, 99999)]
    public void Release_versions_parse_strictly(string text, int major, int minor, int patch)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version));
        Assert.Equal(new ReleaseVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.3-beta")]
    [InlineData("1.02.3")]
    [InlineData("-1.2.3")]
    [InlineData("1.2.100000")]
    [InlineData("1. 2.3")]
    [InlineData("1.2.+3")]
    public void Malformed_versions_are_refused(string? text) => Assert.False(ReleaseVersion.TryParse(text, out _));

    [Fact]
    public void Versions_compare_numerically_not_as_text()
    {
        Assert.True(ReleaseVersion.Parse("0.10.0") > ReleaseVersion.Parse("0.9.9"));
        Assert.True(ReleaseVersion.Parse("1.0.0") > ReleaseVersion.Parse("0.99.99"));
        Assert.True(ReleaseVersion.Parse("0.2.1") > ReleaseVersion.Parse("0.2.0"));
        Assert.True(ReleaseVersion.Parse("0.2.0") <= ReleaseVersion.Parse("v0.2.0"));
        Assert.False(ReleaseVersion.Parse("0.2.0") > ReleaseVersion.Parse("0.2.0"));
        Assert.Equal("1.10.3", ReleaseVersion.Parse("v1.10.3").ToString());
    }

    [Fact]
    public void A_complete_feed_parses_with_plain_text_notes()
    {
        var offer = UpdateManifest.Parse(Feeds.Manifest("0.3.0", notes: "Faster clips.\r\n\u0007Fixes\tand more."));
        Assert.Equal(ReleaseVersion.Parse("0.3.0"), offer.Version);
        Assert.Equal("Faster clips.\nFixes\tand more.", offer.Notes);
        Assert.Equal(Feeds.Hash, offer.Installer!.Sha256);
        Assert.Equal(1234, offer.Installer.Size);
        Assert.Equal("https", offer.Installer.Url.Scheme);
        Assert.NotNull(offer.Portable);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), offer.PublishedAt);
    }

    public static TheoryData<string, string> InvalidFeeds => new()
    {
        { "not json", "{" },
        { "array", "[]" },
        { "other schema", Feeds.Manifest(schema: 2) },
        { "other product", Feeds.Manifest(product: "OtherApp") },
        { "bad version", Feeds.Manifest(version: "0.3") },
        { "missing hash", Feeds.Manifest(installerHash: null, portable: false) },
        { "short hash", Feeds.Manifest(installerHash: new string('a', 63), portable: false) },
        { "non-hex hash", Feeds.Manifest(installerHash: new string('g', 64), portable: false) },
        { "http link", Feeds.Manifest(installerUrl: "http://example.com/KeaClip.Setup.exe", portable: false) },
        { "userinfo link", Feeds.Manifest(installerUrl: "https://user:secret@example.com/KeaClip.Setup.exe", portable: false) },
        { "wrong extension", Feeds.Manifest(installerUrl: "https://example.com/KeaClip.Setup.msi", portable: false) },
        { "zero size", Feeds.Manifest(installerSize: 0, portable: false) },
        { "negative size", Feeds.Manifest(installerSize: -5, portable: false) },
        { "huge size", Feeds.Manifest(installerSize: UpdateManifest.MaxAssetBytes + 1, portable: false) },
        { "no assets", Feeds.Manifest(installer: false, portable: false) },
        { "http release page", Feeds.Manifest(releasePage: "http://example.com/release") },
        { "oversized", "{\"schema\":1,\"notes\":\"" + new string('x', UpdateManifest.MaxManifestBytes) + "\"}" },
    };

    [Theory]
    [MemberData(nameof(InvalidFeeds))]
    public void Invalid_feeds_are_refused_as_a_whole(string name, string json)
    {
        var error = Assert.Throws<UpdateValidationException>(() => UpdateManifest.Parse(json));
        Assert.StartsWith("The update information ", error.Message);
        Assert.False(string.IsNullOrEmpty(name));
    }

    [Fact]
    public void Plain_http_is_accepted_only_for_loopback_when_tests_allow_it()
    {
        var loopback = Feeds.Manifest(installerUrl: "http://127.0.0.1:5000/KeaClip.Setup.exe", portable: false);
        Assert.Throws<UpdateValidationException>(() => UpdateManifest.Parse(loopback));
        Assert.NotNull(UpdateManifest.Parse(loopback, allowLoopbackHttp: true).Installer);
        Assert.Throws<UpdateValidationException>(() => UpdateManifest.Parse(Feeds.Manifest(installerUrl: "http://example.com/KeaClip.Setup.exe", portable: false), allowLoopbackHttp: true));
    }

    [Fact]
    public void Long_notes_are_bounded()
    {
        var offer = UpdateManifest.Parse(Feeds.Manifest(notes: new string('n', UpdateManifest.MaxNotesLength + 500)));
        Assert.True(offer.Notes.Length <= UpdateManifest.MaxNotesLength + 1);
        Assert.EndsWith("…", offer.Notes);
    }
}

public sealed class UpdateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "KeaClipUpdateTests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFeedServer _server = new();
    private readonly FakeNetwork _network = new();
    private DateTimeOffset _now = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private readonly List<ProcessStartInfo> _started = [];
    private SignatureState _signature = SignatureState.Unsigned;

    public UpdateServiceTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        _server.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { }
    }

    private UpdateService Service(UpdateInstallKind kind = UpdateInstallKind.Installed, string current = "0.2.0", Uri? feed = null) => new(new UpdateServiceOptions
    {
        DataRoot = Path.Combine(_root, "Data"), InstallKind = kind, InstallRoot = kind == UpdateInstallKind.Installed ? Path.Combine(_root, "App") : null, CurrentVersion = current,
        FeedUrl = feed ?? _server.Url("keaclip-update.json"), AllowLoopbackHttp = true, Clock = () => _now, Network = _network,
        CheckSignature = _ => _signature, StartProcess = info => { _started.Add(info); return true; }, ProcessId = 4242,
        StallTimeout = TimeSpan.FromSeconds(5), ManifestTimeout = TimeSpan.FromSeconds(5),
    });

    private byte[] PublishRelease(string version, int size = 300_000, bool hashMatches = true)
    {
        var payload = RandomNumberGenerator.GetBytes(size);
        var hash = hashMatches ? Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant() : new string('0', 64);
        _server.Files["KeaClip.Setup.exe"] = payload;
        _server.Files[$"KeaClip-{version}-win-x64.zip"] = payload;
        _server.Files["keaclip-update.json"] = Encoding.UTF8.GetBytes(Feeds.Manifest(version, installerUrl: _server.Url("KeaClip.Setup.exe").AbsoluteUri, installerHash: hash, installerSize: payload.Length,
            portableUrl: _server.Url($"KeaClip-{version}-win-x64.zip").AbsoluteUri, portableHash: hash, portableSize: payload.Length));
        return payload;
    }

    [Fact]
    public async Task A_newer_release_is_offered_and_same_or_older_ones_are_not()
    {
        PublishRelease("0.3.0");
        var available = await Service().CheckAsync(background: false);
        Assert.Equal(UpdateCheckOutcome.Available, available.Outcome);
        Assert.Equal("KeaClip 0.3.0 is available.", available.Message);
        Assert.Equal(ReleaseVersion.Parse("0.3.0"), available.Offer!.Version);

        foreach (var version in new[] { "0.2.0", "0.1.9" })
        {
            PublishRelease(version);
            var result = await Service().CheckAsync(background: false);
            Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
            Assert.Null(result.Offer);
            Assert.Equal("KeaClip 0.2.0 is up to date.", result.Message);
        }
    }

    [Fact]
    public async Task Background_checks_run_at_most_once_a_day_and_the_time_survives_a_restart()
    {
        PublishRelease("0.3.0");
        Assert.Equal(UpdateCheckOutcome.Available, (await Service().CheckAsync(background: true)).Outcome);
        Assert.Equal(1, _server.Requests("keaclip-update.json"));

        _now = _now.AddHours(23);
        var restarted = Service();
        Assert.False(restarted.IsBackgroundCheckDue);
        var skipped = await restarted.CheckAsync(background: true);
        Assert.Equal(UpdateCheckOutcome.Skipped, skipped.Outcome); Assert.True(skipped.Quiet);
        Assert.Equal(1, _server.Requests("keaclip-update.json"));
        Assert.Equal(_now.AddHours(-23), restarted.LastChecked);

        // A manual check always asks.
        Assert.Equal(UpdateCheckOutcome.Available, (await restarted.CheckAsync(background: false)).Outcome);
        Assert.Equal(2, _server.Requests("keaclip-update.json"));

        _now = _now.AddHours(24);
        Assert.True(Service().IsBackgroundCheckDue);
        await Service().CheckAsync(background: true);
        Assert.Equal(3, _server.Requests("keaclip-update.json"));
    }

    [Fact]
    public async Task Offline_and_metered_background_checks_stay_quiet_and_do_not_use_the_daily_slot()
    {
        PublishRelease("0.3.0");
        _network.Online = false;
        var offline = await Service().CheckAsync(background: true);
        Assert.Equal(UpdateCheckOutcome.Offline, offline.Outcome); Assert.True(offline.Quiet);
        var manualOffline = await Service().CheckAsync(background: false);
        Assert.Equal(UpdateCheckOutcome.Offline, manualOffline.Outcome); Assert.False(manualOffline.Quiet);
        Assert.Contains("offline", manualOffline.Message);

        _network.Online = true; _network.Metered = true;
        var metered = await Service().CheckAsync(background: true);
        Assert.Equal(UpdateCheckOutcome.Skipped, metered.Outcome); Assert.True(metered.Quiet); Assert.Contains("metered", metered.Message);
        Assert.Equal(0, _server.Requests("keaclip-update.json"));
        Assert.True(Service().IsBackgroundCheckDue);

        // The user can still check on a metered connection.
        Assert.Equal(UpdateCheckOutcome.Available, (await Service().CheckAsync(background: false)).Outcome);
    }

    [Fact]
    public async Task An_unreachable_or_missing_feed_is_reported_honestly_and_quietly_in_the_background()
    {
        var closedPort = FakeFeedServer.UnusedPort();
        var unreachable = await Service(feed: new Uri($"http://127.0.0.1:{closedPort}/keaclip-update.json")).CheckAsync(background: true);
        Assert.Equal(UpdateCheckOutcome.Offline, unreachable.Outcome); Assert.True(unreachable.Quiet);
        Assert.Equal("Couldn't reach the update feed. KeaClip will try again later.", unreachable.Message);
        // The failed attempt still counts, so a broken feed is not asked again until tomorrow.
        Assert.False(Service().IsBackgroundCheckDue);

        var missing = await Service().CheckAsync(background: false);
        Assert.Equal(UpdateCheckOutcome.Failed, missing.Outcome); Assert.False(missing.Quiet);
        Assert.Equal("No update information was published at the release feed yet.", missing.Message);

        _server.Files["keaclip-update.json"] = Encoding.UTF8.GetBytes(Feeds.Manifest("0.3.0", installerHash: "not-a-hash", portable: false));
        var invalid = await Service().CheckAsync(background: false);
        Assert.Equal(UpdateCheckOutcome.Failed, invalid.Outcome);
        Assert.Contains("SHA-256", invalid.Message);
    }

    [Fact]
    public async Task A_verified_installer_is_downloaded_into_the_app_owned_folder()
    {
        var payload = PublishRelease("0.3.0");
        var service = Service();
        var offer = (await service.CheckAsync(false)).Offer!;
        var reports = new List<JobProgress>();
        var prepared = await service.DownloadAsync(offer, new SyncProgress(reports.Add));
        Assert.Equal(Path.Combine(_root, "Data", "Updates", "0.3.0", "KeaClip.Setup.exe"), prepared.FilePath);
        Assert.Equal(payload, File.ReadAllBytes(prepared.FilePath));
        Assert.Contains(reports, report => report.Stage == JobStage.Downloading && report.Percent == 100);
        Assert.Contains("Not digitally signed yet", prepared.SignatureNote);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(prepared.FilePath)!, "*.partial"));

        // A second request reuses the verified copy after checking it again.
        var again = await service.DownloadAsync(offer);
        Assert.Equal(prepared.FilePath, again.FilePath);
        Assert.Equal(1, _server.Requests("KeaClip.Setup.exe"));
    }

    [Fact]
    public async Task A_hash_mismatch_is_refused_and_nothing_is_kept_or_run()
    {
        PublishRelease("0.3.0", hashMatches: false);
        var service = Service();
        var offer = (await service.CheckAsync(false)).Offer!;
        var error = await Assert.ThrowsAsync<UpdateValidationException>(() => service.DownloadAsync(offer));
        Assert.Contains("didn't match its published SHA-256", error.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "Data", "Updates"), "*", SearchOption.AllDirectories));
        Assert.Empty(_started);
    }

    [Fact]
    public async Task Downloads_larger_or_smaller_than_the_release_lists_are_stopped()
    {
        var payload = PublishRelease("0.3.0");
        var service = Service();
        var offer = (await service.CheckAsync(false)).Offer!;
        // Bigger than listed without a Content-Length: stopped at the size cap.
        _server.Files["KeaClip.Setup.exe"] = [.. payload, .. new byte[50_000]];
        _server.OmitLength = true;
        var tooBig = await Assert.ThrowsAsync<UpdateValidationException>(() => service.DownloadAsync(offer));
        Assert.Contains("larger than the release lists", tooBig.Message);
        // Content-Length that differs from the release: refused before reading.
        _server.OmitLength = false;
        await Assert.ThrowsAsync<UpdateValidationException>(() => service.DownloadAsync(offer));
        // Shorter than listed.
        _server.Files["KeaClip.Setup.exe"] = payload[..1000]; _server.OmitLength = true;
        var tooSmall = await Assert.ThrowsAsync<UpdateValidationException>(() => service.DownloadAsync(offer));
        Assert.Contains("ended early", tooSmall.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "Data", "Updates"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Same_or_older_versions_are_never_downloaded_or_installed()
    {
        var payload = PublishRelease("0.3.0");
        var newer = (await Service().CheckAsync(false)).Offer!;
        var prepared = await Service().DownloadAsync(newer);
        foreach (var current in new[] { "0.3.0", "0.4.0" })
        {
            var service = Service(current: current);
            var error = await Assert.ThrowsAsync<UpdateValidationException>(() => service.DownloadAsync(newer));
            Assert.Contains("is not newer than this version", error.Message);
            Assert.Throws<UpdateValidationException>(() => service.StartInstall(prepared));
        }
        Assert.Empty(_started);
        Assert.Equal(payload, File.ReadAllBytes(prepared.FilePath));
    }

    [Fact]
    public async Task Installing_reverifies_the_file_and_starts_the_upgrade_mode_for_this_installation()
    {
        PublishRelease("0.3.0");
        var service = Service();
        var prepared = await service.DownloadAsync((await service.CheckAsync(false)).Offer!);
        service.StartInstall(prepared);
        var info = Assert.Single(_started);
        Assert.Equal(prepared.FilePath, info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.Equal(["--upgrade", "--path", Path.Combine(_root, "App"), "--wait-pid", "4242", "--relaunch"], info.ArgumentList);

        // The relaunched new version reports the finished update once.
        Assert.Equal("KeaClip was updated to 0.3.0. Your settings and media were kept.", Service(current: "0.3.0").TakeCompletedUpdateNotice());
        Assert.Null(Service(current: "0.3.0").TakeCompletedUpdateNotice());
    }

    [Fact]
    public async Task A_file_changed_after_download_or_with_a_broken_signature_is_not_run()
    {
        PublishRelease("0.3.0");
        var service = Service();
        var offer = (await service.CheckAsync(false)).Offer!;
        var prepared = await service.DownloadAsync(offer);
        var bytes = File.ReadAllBytes(prepared.FilePath); bytes[^1] ^= 0xFF; File.WriteAllBytes(prepared.FilePath, bytes);
        var tampered = Assert.Throws<UpdateValidationException>(() => service.StartInstall(prepared));
        Assert.Contains("changed after it was checked", tampered.Message);
        Assert.False(File.Exists(prepared.FilePath));

        _signature = SignatureState.Invalid;
        var signed = await Assert.ThrowsAsync<UpdateValidationException>(() => service.DownloadAsync(offer));
        Assert.Contains("digital signature", signed.Message);
        Assert.False(File.Exists(prepared.FilePath));
        Assert.Empty(_started);
        // A failed start leaves no pending-update note behind.
        Assert.Null(service.TakeCompletedUpdateNotice());
    }

    [Fact]
    public async Task Portable_copies_get_the_verified_zip_and_development_builds_cannot_update()
    {
        var payload = PublishRelease("0.3.0");
        var portable = Service(UpdateInstallKind.Portable);
        var prepared = await portable.DownloadAsync((await portable.CheckAsync(false)).Offer!);
        Assert.Equal("KeaClip-0.3.0-win-x64.zip", Path.GetFileName(prepared.FilePath));
        Assert.Equal(payload, File.ReadAllBytes(prepared.FilePath));
        Assert.Throws<UpdateValidationException>(() => portable.StartInstall(prepared));

        var development = Service(UpdateInstallKind.Development);
        var error = await Assert.ThrowsAsync<UpdateValidationException>(async () => await development.DownloadAsync((await development.CheckAsync(false)).Offer!));
        Assert.Contains("development build", error.Message);
        Assert.Empty(_started);
    }

    [Fact]
    public async Task A_redirect_to_an_insecure_address_is_refused()
    {
        var handler = new RedirectingHandler(new Uri("http://example.com/keaclip-update.json"), Feeds.Manifest("0.3.0"));
        using var service = new UpdateService(new UpdateServiceOptions { DataRoot = Path.Combine(_root, "Data"), FeedUrl = new Uri("https://example.com/keaclip-update.json"), Clock = () => _now, Network = _network }, handler);
        var result = await service.CheckAsync(false);
        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.Contains("not secure", result.Message);
    }

    [Fact]
    public void Feed_overrides_must_be_secure()
    {
        using var service = new UpdateService(new UpdateServiceOptions { DataRoot = Path.Combine(_root, "Data"), Network = _network });
        Assert.Equal(UpdateServiceOptions.DefaultFeed, service.FeedUrl.AbsoluteUri);
        Assert.False(service.TrySetFeed("http://example.com/feed.json"));
        Assert.False(service.TrySetFeed("file:///C:/feed.json"));
        Assert.Equal(UpdateServiceOptions.DefaultFeed, service.FeedUrl.AbsoluteUri);
        Assert.True(service.TrySetFeed("https://example.com/my/keaclip-update.json"));
        Assert.Equal("https://example.com/my/keaclip-update.json", service.FeedUrl.AbsoluteUri);
        Assert.True(service.TrySetFeed(null));
        Assert.Equal(UpdateServiceOptions.DefaultFeed, service.FeedUrl.AbsoluteUri);
    }

    // ----- Local feed (build/release-local.ps1): builds made on this PC, no hosting -----
    private Uri PublishLocal(string version, byte[] payload, string? installerName = null, string? hash = null)
    {
        var folder = Path.Combine(_root, "KeaClip-Releases");
        Directory.CreateDirectory(folder);
        var name = $"KeaClip-{version}-Setup.exe";
        File.WriteAllBytes(Path.Combine(folder, name), payload);
        var feed = Path.Combine(folder, UpdateManifest.FileName);
        File.WriteAllText(feed, Feeds.Manifest(version, installerUrl: installerName ?? name, installerHash: hash ?? Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            installerSize: payload.Length, portable: false, releasePage: null));
        return new Uri(feed);
    }

    [Fact]
    public async Task A_local_feed_offers_and_copies_a_verified_installer_without_any_network()
    {
        var payload = RandomNumberGenerator.GetBytes(200_000);
        using var service = Service(feed: PublishLocal("0.3.0", payload));
        _network.Online = false; _network.Metered = true;
        Assert.True(service.IsLocalFeed);
        var first = await service.CheckAsync(background: true);
        Assert.Equal(UpdateCheckOutcome.Available, first.Outcome);
        // Read again at every start: no daily wait for a feed on this PC.
        Assert.Equal(UpdateCheckOutcome.Available, (await service.CheckAsync(background: true)).Outcome);
        var prepared = await service.DownloadAsync(first.Offer!);
        Assert.Equal(Path.Combine(_root, "Data", "Updates", "0.3.0", "KeaClip.Setup.exe"), prepared.FilePath);
        Assert.Equal(payload, File.ReadAllBytes(prepared.FilePath));
        service.StartInstall(prepared);
        Assert.Contains("--upgrade", Assert.Single(_started).ArgumentList);
        Assert.Equal(0, _server.Requests("keaclip-update.json"));
    }

    [Fact]
    public async Task A_local_installer_that_does_not_match_its_hash_is_refused_and_removed()
    {
        using var service = Service(feed: PublishLocal("0.3.0", RandomNumberGenerator.GetBytes(50_000), hash: new string('0', 64)));
        var offer = (await service.CheckAsync(false)).Offer!;
        var error = await Assert.ThrowsAsync<UpdateValidationException>(() => service.DownloadAsync(offer));
        Assert.Contains("SHA-256", error.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "Data", "Updates", "0.3.0")));
    }

    [Theory]
    [InlineData(@"..\KeaClip-0.3.0-Setup.exe")]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData("sub/KeaClip-0.3.0-Setup.exe")]
    [InlineData("https://example.com/KeaClip.Setup.exe")]
    [InlineData("..")]
    public async Task A_local_feed_may_only_name_files_beside_it(string installerName)
    {
        using var service = Service(feed: PublishLocal("0.3.0", [1, 2, 3], installerName));
        var result = await service.CheckAsync(false);
        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.Contains("file name beside the feed", result.Message);
    }

    [Fact]
    public async Task A_missing_local_feed_reads_as_nothing_published_yet()
    {
        using var service = Service(feed: new Uri(Path.Combine(_root, "Nowhere", UpdateManifest.FileName)));
        var result = await service.CheckAsync(false);
        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.Contains("No update information", result.Message);
    }

    [Fact]
    public void Only_a_keaclip_update_file_on_a_local_drive_counts_as_a_local_feed()
    {
        using var service = new UpdateService(new UpdateServiceOptions { DataRoot = Path.Combine(_root, "Data"), Network = _network });
        Assert.True(service.TrySetFeed(new Uri(Path.Combine(_root, UpdateManifest.FileName)).AbsoluteUri));
        Assert.True(service.IsLocalFeed);
        Assert.False(service.TrySetFeed(@"file://server/share/keaclip-update.json"));
        Assert.False(service.TrySetFeed(@"\\server\share\keaclip-update.json"));
        Assert.False(service.TrySetFeed("file:///C:/other.json"));
    }

    [Fact]
    public void The_local_feed_is_preferred_only_when_it_exists()
    {
        var path = Path.Combine(_root, "KeaClip-Releases", UpdateManifest.FileName);
        Assert.Equal(UpdateServiceOptions.DefaultFeed, UpdateServiceOptions.PreferredFeed(path).AbsoluteUri);
        PublishLocal("0.3.0", [1]);
        Assert.Equal(new Uri(path), UpdateServiceOptions.PreferredFeed(path));
    }

    [Fact]
    public void The_profile_feed_wins_over_the_old_AppData_feed_which_still_works_alone()
    {
        var legacy = Path.Combine(_root, "KeaClip-Releases", UpdateManifest.FileName);
        var profile = Path.Combine(_root, ".keaclip-releases", UpdateManifest.FileName);
        PublishLocal("0.3.0", [1]); // writes the legacy-style folder
        Assert.Equal(new Uri(legacy), UpdateServiceOptions.PreferredFeed(profile, legacy));
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!); File.Copy(legacy, profile);
        Assert.Equal(new Uri(profile), UpdateServiceOptions.PreferredFeed(profile, legacy));
        Assert.EndsWith(Path.Combine(".keaclip-releases", UpdateManifest.FileName), UpdateServiceOptions.LocalFeedPath);
        Assert.DoesNotContain("AppData", UpdateServiceOptions.LocalFeedPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cleanup_removes_only_this_apps_downloads_for_installed_or_older_versions()
    {
        var updates = Path.Combine(_root, "Data", "Updates");
        string Write(string relative) { var path = Path.Combine(updates, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "x"); return path; }
        var old = Write("0.1.0/KeaClip.Setup.exe"); var current = Write("0.2.0/KeaClip-0.2.0-win-x64.zip"); var newer = Write("0.3.0/KeaClip.Setup.exe");
        var newerPartial = Write("0.3.0/KeaClip.Setup.exe.partial"); var userFile = Write("0.1.0/notes.txt"); var other = Write("misc/KeaClip.Setup.exe");
        Service().CleanupDownloads();
        Assert.False(File.Exists(old)); Assert.False(File.Exists(current)); Assert.False(Directory.Exists(Path.Combine(updates, "0.2.0")));
        Assert.True(File.Exists(newer)); Assert.False(File.Exists(newerPartial));
        Assert.True(File.Exists(userFile)); Assert.True(File.Exists(other));
    }

    [Fact]
    public void The_install_kind_is_read_from_the_files_beside_the_app()
    {
        var installed = Path.Combine(_root, "Installed"); Directory.CreateDirectory(installed);
        Assert.Equal(UpdateInstallKind.Development, UpdateEnvironment.Detect(installed).Kind);
        File.WriteAllText(Path.Combine(installed, "KeaClip.Uninstall.exe"), "x");
        File.WriteAllText(Path.Combine(installed, ".keaclip-install.json"), JsonSerializer.Serialize(new { ApplicationId = "KeaClip", Root = Path.Combine(_root, "Elsewhere") }));
        Assert.Equal(UpdateInstallKind.Development, UpdateEnvironment.Detect(installed).Kind);
        File.WriteAllText(Path.Combine(installed, ".keaclip-install.json"), JsonSerializer.Serialize(new { ApplicationId = "KeaClip", Root = installed }));
        Assert.Equal(new UpdateEnvironment(UpdateInstallKind.Installed, installed), UpdateEnvironment.Detect(installed + Path.DirectorySeparatorChar));
        var portable = Path.Combine(_root, "Portable"); Directory.CreateDirectory(portable); File.WriteAllText(Path.Combine(portable, "portable.marker"), "p");
        Assert.Equal(UpdateInstallKind.Portable, UpdateEnvironment.Detect(portable).Kind);
    }

    [Fact]
    public void Authenticode_tells_unsigned_signed_and_tampered_files_apart()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(SignatureState.Unsigned, Authenticode.Check(typeof(UpdateServiceTests).Assembly.Location));
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (!File.Exists(dotnet)) return; // No Microsoft-signed reference binary on this machine.
        Assert.Equal(SignatureState.Valid, Authenticode.Check(dotnet));
        var copy = Path.Combine(_root, "signed-copy.exe"); File.Copy(dotnet, copy);
        Assert.Equal(SignatureState.Valid, Authenticode.Check(copy));
        var bytes = File.ReadAllBytes(copy); bytes[bytes.Length / 3] ^= 0x5A; File.WriteAllBytes(copy, bytes);
        Assert.Equal(SignatureState.Invalid, Authenticode.Check(copy));
    }

    [Fact]
    public void The_windows_network_state_answers_without_throwing()
    {
        var network = new WindowsNetworkState();
        _ = network.IsOnline();
        _ = network.IsMetered();
    }

    private sealed class SyncProgress(Action<JobProgress> report) : IProgress<JobProgress> { public void Report(JobProgress value) => report(value); }
    private sealed class FakeNetwork : INetworkState { public bool Online { get; set; } = true; public bool? Metered { get; set; } = false; public bool IsOnline() => Online; public bool? IsMetered() => Metered; }
    private sealed class RedirectingHandler(Uri finalUrl, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = finalUrl; // what a followed redirect leaves behind
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body) });
        }
    }
}

internal static class Feeds
{
    public static readonly string Hash = new('a', 64);

    public static string Manifest(string version = "0.3.0", int schema = 1, string product = "KeaClip", string? notes = "Notes", bool installer = true, string installerUrl = "https://example.com/download/KeaClip.Setup.exe",
        string? installerHash = "default", long installerSize = 1234, bool portable = true, string portableUrl = "https://example.com/download/KeaClip-0.3.0-win-x64.zip", string? portableHash = "default", long portableSize = 2345, string? releasePage = "https://example.com/release")
    {
        var root = new Dictionary<string, object?> { ["schema"] = schema, ["product"] = product, ["version"] = version, ["publishedAt"] = "2026-11-02T00:00:00Z", ["notes"] = notes, ["releasePage"] = releasePage };
        if (installer) root["installer"] = Asset(installerUrl, installerHash, installerSize);
        if (portable) root["portable"] = Asset(portableUrl, portableHash, portableSize);
        return JsonSerializer.Serialize(root);
    }

    private static Dictionary<string, object> Asset(string url, string? hash, long size)
    {
        var asset = new Dictionary<string, object> { ["url"] = url, ["size"] = size };
        if (hash is not null) asset["sha256"] = hash == "default" ? Hash : hash;
        return asset;
    }
}

/// <summary>A minimal HTTP/1.1 file server on 127.0.0.1 for update tests: GET only, one request per connection.</summary>
internal sealed class FakeFeedServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, int> _requests = [];
    private readonly Task _loop;
    public Dictionary<string, byte[]> Files { get; } = [];
    public bool OmitLength { get; set; }

    public FakeFeedServer() { _listener.Start(); _loop = Task.Run(ServeAsync); }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public Uri Url(string name) => new($"http://127.0.0.1:{Port}/{name}");
    public int Requests(string name) { lock (_requests) return _requests.GetValueOrDefault(name); }

    public static int UnusedPort() { var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); return port; }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var header = new StringBuilder(); var one = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one) == 1) header.Append((char)one[0]);
                var path = header.ToString().Split(' ')[1].TrimStart('/');
                lock (_requests) _requests[path] = _requests.GetValueOrDefault(path) + 1;
                byte[]? body; bool omit;
                lock (Files) { body = Files.GetValueOrDefault(path); omit = OmitLength; }
                var head = body is null ? "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    : "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n" + (omit ? "" : $"Content-Length: {body.Length}\r\n") + "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                if (body is not null) await stream.WriteAsync(body);
                await stream.FlushAsync();
            }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException) { }
        }
    }

    public void Dispose() { _stop.Cancel(); _listener.Stop(); try { _loop.Wait(2000); } catch (AggregateException) { } _stop.Dispose(); }
}
