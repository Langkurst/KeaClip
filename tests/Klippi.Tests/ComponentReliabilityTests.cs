using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Klippi.Core;
using Klippi.Infrastructure;
using Klippi.Infrastructure.Engines;
using Xunit;

namespace Klippi.Tests;

internal sealed class ComponentWindowsFactAttribute : FactAttribute
{
    public ComponentWindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Component installation and file-lock behaviour are Windows x64 only."; }
}
internal sealed class ComponentWindowsTheoryAttribute : TheoryAttribute
{
    public ComponentWindowsTheoryAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Component installation and file-lock behaviour are Windows x64 only."; }
}

public sealed class ComponentReliabilityTests
{
    // Finding: Find re-hashed large executables on every call.
    [Fact]
    public void Find_reuses_a_verified_hash_until_the_size_or_timestamp_changes()
    {
        using var f = new ComponentFixture();
        var entry = f.Install(f.Root, "deno", "deno 2.4.3", "original binary bytes");
        f.Record(f.Current, entry);
        var path = f.Owned(f.Root, entry);
        Assert.Equal(path, f.Service.Find("deno"));
        // Same size and restored timestamp: the cached verification is reused (documented trade-off).
        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "tampered binary bytes");
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal(path, f.Service.Find("deno"));
        // Any stamp change forces a new hash, which rejects the changed bytes.
        File.SetLastWriteTimeUtc(path, stamp.AddSeconds(5));
        Assert.Null(f.Service.Find("deno"));
        File.WriteAllText(path, "original binary bytes");
        Assert.Equal(path, f.Service.Find("deno"));
    }

    [Fact]
    public async Task Inspect_hashes_and_launches_off_the_calling_thread()
    {
        using var f = new ComponentFixture();
        f.Record(f.Current, f.Install(f.Root, "yt-dlp", "2026.08.19", "downloader"));
        Task<IReadOnlyList<ComponentInfo>>? running = null; var caller = 0;
        var thread = new Thread(() => { caller = Environment.CurrentManagedThreadId; running = f.Service.InspectAsync(); });
        thread.Start(); thread.Join();
        var results = await running!.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(results.Single(r => r.Name == "yt-dlp").Ready);
        Assert.NotEmpty(f.Runner.Threads);
        Assert.DoesNotContain(caller, f.Runner.Threads);
    }

    // Finding: a cleanup exception in InstallAsync's finally skipped _gate.Release().
    [ComponentWindowsFact]
    public async Task Locked_staging_cleanup_keeps_the_checksum_error_and_releases_the_gate()
    {
        using var f = new ComponentFixture();
        var body = "downloader 2026.10.01"u8.ToArray();
        f.Network.PublishDownloader("2026.10.01", body, hash: new string('a', 64));
        FileStream? held = null;
        // Simulates a scanner holding a staging file without delete sharing.
        f.Network.OnDownload = () => held ??= new FileStream(Path.Combine(Directory.GetDirectories(f.Root, ".staging-*").Single(), "scanner-held.tmp"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            var error = await Assert.ThrowsAsync<MediaOperationException>(() => f.Service.UpdateDownloaderAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Contains("checksum", error.Message);
        }
        finally { held?.Dispose(); }
        Assert.Single(Directory.GetDirectories(f.Root, ".staging-*"));
        var rollback = await Assert.ThrowsAsync<MediaOperationException>(() => f.Service.RollbackAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("No previous", rollback.Message);
        // The next install gets the gate and sweeps the leftover staging folder.
        f.Network.OnDownload = null;
        f.Network.PublishDownloader("2026.10.01", body);
        await f.Service.UpdateDownloaderAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Empty(Directory.GetDirectories(f.Root, ".staging-*"));
        Assert.StartsWith(f.Root + Path.DirectorySeparatorChar, f.Service.Find("yt-dlp"));
    }

    // Finding: an unreadable managed current.json disabled the verified shipped set.
    [Theory]
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{\"yt-dlp\":null,\"ffmpeg\":null}")]
    public void Unreadable_managed_record_falls_back_to_the_verified_shipped_set(string content)
    {
        using var f = new ComponentFixture();
        var shipped = f.ShipAll();
        File.WriteAllText(f.Current, content);
        foreach (var entry in shipped.Values) Assert.Equal(f.Owned(f.Shipped, entry), f.Service.Find(entry.Name));
    }

    [ComponentWindowsFact]
    public async Task Set_up_moves_an_unreadable_record_aside_and_repairs_without_network()
    {
        using var f = new ComponentFixture();
        var shipped = f.ShipAll();
        File.WriteAllText(f.Current, "");
        var progress = new EngineProgress();
        await f.Service.ProvisionAsync(progress).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(File.Exists(f.Current));
        Assert.Single(Directory.GetFiles(f.Root, "current.json.corrupt-*"));
        Assert.Empty(f.Network.Requests);
        Assert.Contains(progress.Values, p => p.Stage == JobStage.Finished && p.Message == "Components are already ready.");
        foreach (var entry in shipped.Values) Assert.Equal(f.Owned(f.Shipped, entry), f.Service.Find(entry.Name));
    }

    // Finding: Update always re-downloaded and rotated the real previous version away.
    [ComponentWindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_when_already_latest_downloads_nothing_and_keeps_the_rollback_record(bool shippedActive)
    {
        using var f = new ComponentFixture();
        const string latest = "downloader 2026.10.01";
        if (shippedActive) f.Record(Path.Combine(f.Shipped, "current.json"), f.Install(f.Shipped, "yt-dlp", "2026.10.01", latest));
        else { f.Record(f.Current, f.Install(f.Root, "yt-dlp", "2026.10.01", latest)); f.Record(f.Backup, f.Install(f.Root, "yt-dlp", "2026.09.01", "downloader 2026.09.01")); }
        var before = f.Snapshot();
        f.Network.PublishDownloader("2026.10.01", Encoding.UTF8.GetBytes(latest));
        var progress = new EngineProgress();
        await f.Service.UpdateDownloaderAsync(progress).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(new[] { "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest" }, f.Network.Requests.Select(u => u.AbsoluteUri));
        Assert.Contains(progress.Values, p => p.Stage == JobStage.Finished && p.Message == "yt-dlp is already up to date (2026.10.01).");
        Assert.Equal(before, f.Snapshot());
    }

    [ComponentWindowsFact]
    public async Task Repair_that_reacquires_the_same_binary_keeps_the_real_previous_version()
    {
        using var f = new ComponentFixture();
        var pair = Guid.NewGuid().ToString("N");
        foreach (var name in new[] { "ffmpeg", "ffprobe", "deno" }) f.Ship(name, name + " version 7.1.1", "shipped " + name, name == "deno" ? null : pair);
        const string body = "downloader 2026.10.01";
        var broken = f.Install(f.Root, "yt-dlp", "2026.10.01", body);
        var previous = f.Install(f.Root, "yt-dlp", "2026.09.01", "downloader 2026.09.01");
        File.Delete(f.Owned(f.Root, broken));
        f.Record(f.Current, broken); f.Record(f.Backup, previous);
        var backupBefore = File.ReadAllBytes(f.Backup);
        f.Network.PublishDownloader("2026.10.01", Encoding.UTF8.GetBytes(body));
        await f.Service.ProvisionAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(backupBefore, File.ReadAllBytes(f.Backup));
        Assert.StartsWith(f.Root + Path.DirectorySeparatorChar, f.Service.Find("yt-dlp"));
    }

    // Finding: the first update over the shipped set could never be rolled back.
    [ComponentWindowsFact]
    public async Task First_update_over_the_shipped_set_rolls_back_to_it_and_forward_again()
    {
        using var f = new ComponentFixture();
        var shipped = f.ShipAll();
        f.Network.PublishDownloader("2026.10.01", "downloader 2026.10.01"u8.ToArray());
        await f.Service.UpdateDownloaderAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var managed = f.Service.Find("yt-dlp");
        Assert.StartsWith(f.Root + Path.DirectorySeparatorChar, managed);
        Assert.False(File.Exists(f.Backup));

        var progress = new EngineProgress();
        await f.Service.RollbackAsync(progress).WaitAsync(TimeSpan.FromSeconds(20));
        var shippedPath = f.Owned(f.Shipped, shipped["yt-dlp"]);
        Assert.Equal(shippedPath, f.Service.Find("yt-dlp"));
        Assert.Contains(f.Runner.Calls, call => call.Executable == shippedPath && call.Arguments.SequenceEqual(new[] { "--version" }));
        Assert.False(File.Exists(f.Current));
        Assert.True(File.Exists(f.Backup));
        Assert.Contains(progress.Values, p => p.Stage == JobStage.Finished && p.Message!.Contains("shipped with KeaClip were restored"));

        await f.Service.RollbackAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(managed, f.Service.Find("yt-dlp"));
        Assert.True(File.Exists(f.Current));
        Assert.False(File.Exists(f.Backup));
    }

    [ComponentWindowsFact]
    public async Task Rollback_to_the_shipped_set_refuses_a_shipped_copy_that_fails_its_launch_check()
    {
        using var f = new ComponentFixture();
        var shipped = f.ShipAll();
        f.Network.PublishDownloader("2026.10.01", "downloader 2026.10.01"u8.ToArray());
        await f.Service.UpdateDownloaderAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var managed = f.Service.Find("yt-dlp");
        var currentBefore = File.ReadAllBytes(f.Current);
        f.Runner.Versions[f.Owned(f.Shipped, shipped["yt-dlp"])] = "something else";
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => f.Service.RollbackAsync().WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Contains("launch and version", error.Message);
        Assert.Equal(currentBefore, File.ReadAllBytes(f.Current));
        Assert.False(File.Exists(f.Backup));
        Assert.Equal(managed, f.Service.Find("yt-dlp"));
    }

    // Finding: a managed entry always overrode the shipped copy.
    [Theory]
    [InlineData("2026.08.19", "2027.01.05", true)]
    [InlineData("2027.01.05", "2026.08.19", false)]
    [InlineData("2026.10.01", "2026.10.01", false)]
    [InlineData("2026.10.01", "2026.10.01.1", true)]
    [InlineData("custom build", "2027.01.05", false)]
    public void A_strictly_newer_shipped_version_wins_over_an_older_managed_copy(string managedVersion, string shippedVersion, bool expectShipped)
    {
        using var f = new ComponentFixture();
        var managed = f.Install(f.Root, "yt-dlp", managedVersion, "managed downloader");
        f.Record(f.Current, managed);
        var shipped = f.Ship("yt-dlp", shippedVersion, "shipped downloader");
        Assert.Equal(expectShipped ? f.Owned(f.Shipped, shipped) : f.Owned(f.Root, managed), f.Service.Find("yt-dlp"));
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("missing")]
    [InlineData("outside")]
    public void Managed_copy_that_fails_verification_falls_back_to_the_verified_shipped_copy(string kind)
    {
        using var f = new ComponentFixture();
        var shipped = f.Ship("deno", "deno 2.4.3", "shipped runtime");
        var managed = f.Install(f.Root, "deno", "deno 2.5.0", "managed runtime");
        if (kind == "tampered") File.AppendAllText(f.Owned(f.Root, managed), "x");
        if (kind == "missing") File.Delete(f.Owned(f.Root, managed));
        if (kind == "outside") managed = managed with { RelativePath = "../outside.exe" };
        f.Record(f.Current, managed);
        Assert.Equal(f.Owned(f.Shipped, shipped), f.Service.Find("deno"));
    }

    // Review finding: ffmpeg and ffprobe resolved separately, so yt-dlp (given
    // ffmpeg's folder) could run an unverified ffprobe beside a verified ffmpeg.
    [Theory]
    [InlineData("tampered", true)]
    [InlineData("missing", true)]
    [InlineData("outside", true)]
    [InlineData("split", true)]
    [InlineData("tampered", false)]
    [InlineData("missing", false)]
    [InlineData("split", false)]
    public void Ffmpeg_and_ffprobe_resolve_only_as_one_verified_folder(string kind, bool shippedPair)
    {
        using var f = new ComponentFixture();
        var shipped = shippedPair ? f.ShipAll() : null;
        var managed = f.InstallPair(f.Root, "8.0", "managed");
        var probe = f.Owned(f.Root, managed[1]);
        if (kind == "tampered") File.AppendAllText(probe, "x");
        if (kind == "missing") File.Delete(probe);
        if (kind == "outside") managed[1] = managed[1] with { RelativePath = "../ffprobe.exe" };
        // A verified ffprobe from another folder is not the one yt-dlp would run.
        if (kind == "split") managed[1] = f.Install(f.Root, "ffprobe", "ffprobe version 8.0", "managed ffprobe");
        f.Record(f.Current, managed);
        var ffmpeg = f.Service.Find("ffmpeg"); var ffprobe = f.Service.Find("ffprobe");
        if (shipped is null) { Assert.Null(ffmpeg); Assert.Null(ffprobe); return; }
        Assert.Equal(f.Owned(f.Shipped, shipped["ffmpeg"]), ffmpeg);
        Assert.Equal(f.Owned(f.Shipped, shipped["ffprobe"]), ffprobe);
        Assert.Equal(Path.GetDirectoryName(ffmpeg), Path.GetDirectoryName(ffprobe));
    }

    [Fact]
    public void A_record_whose_entry_names_disagree_with_its_keys_never_makes_Find_throw()
    {
        using var f = new ComponentFixture();
        var managed = f.InstallPair(f.Root, "8.0", "managed");
        File.WriteAllText(f.Current, JsonSerializer.Serialize(new Dictionary<string, ManagedComponent> { ["ffmpeg"] = managed[0] with { Name = "other" }, ["ffprobe"] = managed[1] with { Name = "other" } }));
        Assert.Equal(f.Owned(f.Root, managed[0]), f.Service.Find("ffmpeg"));
        Assert.Equal(f.Owned(f.Root, managed[1]), f.Service.Find("ffprobe"));
    }

    [Fact]
    public void A_newer_shipped_pair_with_a_broken_member_leaves_the_whole_managed_pair_in_use()
    {
        using var f = new ComponentFixture();
        var managed = f.InstallPair(f.Root, "7.0", "managed");
        f.Record(f.Current, managed);
        var shipped = f.ShipAll();
        File.AppendAllText(f.Owned(f.Shipped, shipped["ffprobe"]), "x");
        Assert.Equal(f.Owned(f.Root, managed[0]), f.Service.Find("ffmpeg"));
        Assert.Equal(f.Owned(f.Root, managed[1]), f.Service.Find("ffprobe"));
    }

    // Review finding: a failed check was never remembered, so a corrupt managed
    // copy beside a working fallback was hashed again on every Find.
    [Fact]
    public void Find_remembers_a_failed_check_until_the_file_changes()
    {
        using var f = new ComponentFixture();
        var shipped = f.Owned(f.Shipped, f.Ship("deno", "deno 2.4.3", "shipped runtime"));
        var managed = f.Install(f.Root, "deno", "deno 2.4.3", "managed runtime");
        f.Record(f.Current, managed);
        var path = f.Owned(f.Root, managed);
        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "MANAGED RUNTIME"); File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal(shipped, f.Service.Find("deno"));
        // The good bytes are back with the same size and timestamp: only a new hash
        // would notice, so an unchanged result proves the failure was not re-hashed.
        File.WriteAllText(path, "managed runtime"); File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal(shipped, f.Service.Find("deno"));
        Assert.Equal(shipped, f.Service.Find("deno"));
        File.SetLastWriteTimeUtc(path, stamp.AddSeconds(5));
        Assert.Equal(path, f.Service.Find("deno"));
    }

    // Review finding: Set up reported "already ready" while a managed copy that
    // should be in use was broken, because the shipped fallback hid it.
    [ComponentWindowsFact]
    public async Task Set_up_repairs_a_broken_managed_media_pair_even_when_the_shipped_pair_works()
    {
        using var f = new ComponentFixture();
        f.ShipAll();
        var managed = f.InstallPair(f.Root, "8.0", "managed");
        var previous = f.InstallPair(f.Root, "7.9", "previous");
        f.Record(f.Current, managed); f.Record(f.Backup, previous);
        var backupBefore = File.ReadAllBytes(f.Backup);
        var good = managed.Select(e => File.ReadAllBytes(f.Owned(f.Root, e))).ToArray();
        File.AppendAllText(f.Owned(f.Root, managed[1]), "x");
        f.Network.PublishMediaTools(good[0], good[1]);
        var progress = new EngineProgress();
        await f.Service.ProvisionAsync(progress).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(new[] { FakeNetwork.MediaTools + ".sha256", FakeNetwork.MediaTools }, f.Network.Requests.Select(u => u.AbsoluteUri));
        Assert.Contains(progress.Values, p => p.Stage == JobStage.Finished && p.Message == "All set. Paste a link to start.");
        var ffmpeg = f.Service.Find("ffmpeg"); var ffprobe = f.Service.Find("ffprobe");
        Assert.StartsWith(Path.Combine(f.Root, "versions") + Path.DirectorySeparatorChar, ffmpeg);
        Assert.NotEqual(Path.GetDirectoryName(f.Owned(f.Root, managed[0])), Path.GetDirectoryName(ffmpeg));
        Assert.Equal(Path.GetDirectoryName(ffmpeg), Path.GetDirectoryName(ffprobe));
        Assert.Equal(good[1], File.ReadAllBytes(ffprobe!));
        // The same published binaries: the real previous version stays the rollback target.
        Assert.Equal(backupBefore, File.ReadAllBytes(f.Backup));
    }

    [ComponentWindowsFact]
    public async Task Set_up_repairs_a_tampered_managed_runtime_and_keeps_the_rollback_record()
    {
        using var f = new ComponentFixture();
        var shipped = f.ShipAll();
        var managed = f.Install(f.Root, "deno", "deno 2.4.3", "managed runtime");
        f.Record(f.Current, managed); f.Record(f.Backup, f.Install(f.Root, "deno", "deno 2.4.2", "previous runtime"));
        var backupBefore = File.ReadAllBytes(f.Backup);
        var good = File.ReadAllBytes(f.Owned(f.Root, managed));
        File.AppendAllText(f.Owned(f.Root, managed), "x");
        Assert.Equal(f.Owned(f.Shipped, shipped["deno"]), f.Service.Find("deno"));
        f.Network.PublishDeno(good);
        await f.Service.ProvisionAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(FakeNetwork.DenoRelease, f.Network.Requests.First().AbsoluteUri);
        Assert.Equal(2, f.Network.Requests.Count);
        var repaired = f.Service.Find("deno");
        Assert.StartsWith(Path.Combine(f.Root, "versions") + Path.DirectorySeparatorChar, repaired);
        Assert.NotEqual(f.Owned(f.Root, managed), repaired);
        Assert.Equal(good, File.ReadAllBytes(repaired!));
        Assert.Equal(backupBefore, File.ReadAllBytes(f.Backup));
    }

    [ComponentWindowsFact]
    public async Task Rollback_to_the_shipped_set_restores_ffmpeg_and_ffprobe_only_together()
    {
        using var f = new ComponentFixture();
        var shipped = f.ShipAll();
        File.AppendAllText(f.Owned(f.Shipped, shipped["ffprobe"]), "x");
        var downloader = f.Install(f.Root, "yt-dlp", "2026.10.01", "downloader 2026.10.01");
        var pair = f.InstallPair(f.Root, "8.0", "managed");
        f.Record(f.Current, [downloader, .. pair]);
        var progress = new EngineProgress();
        await f.Service.RollbackAsync(progress).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(f.Owned(f.Shipped, shipped["yt-dlp"]), f.Service.Find("yt-dlp"));
        Assert.Equal(f.Owned(f.Root, pair[0]), f.Service.Find("ffmpeg"));
        Assert.Equal(f.Owned(f.Root, pair[1]), f.Service.Find("ffprobe"));
        Assert.Equal(new[] { "ffmpeg", "ffprobe" }, ComponentFixture.Read(f.Current).Keys.Order(StringComparer.Ordinal));
        Assert.Contains(progress.Values, p => p.Stage == JobStage.Finished && p.Message!.Contains("restored and verified for yt-dlp.", StringComparison.Ordinal));
    }

    [ComponentWindowsFact]
    public async Task Rollback_refuses_a_previous_record_with_half_of_the_media_pair()
    {
        using var f = new ComponentFixture();
        f.Record(f.Current, f.Install(f.Root, "yt-dlp", "2026.10.01", "downloader 2026.10.01"));
        f.Record(f.Backup, f.InstallPair(f.Root, "7.1.1", "previous")[0]);
        var currentBefore = File.ReadAllBytes(f.Current);
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => f.Service.RollbackAsync().WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Contains("incomplete or invalid", error.Message);
        Assert.Equal(currentBefore, File.ReadAllBytes(f.Current));
        Assert.Empty(f.Runner.Calls);
    }

    // Review finding: repairing a corrupt record deleted the only rollback record.
    [ComponentWindowsFact]
    public async Task Repairing_a_corrupt_record_keeps_the_last_good_rollback_record()
    {
        using var f = new ComponentFixture();
        var previous = f.Install(f.Root, "yt-dlp", "2026.09.01", "downloader 2026.09.01");
        f.Record(f.Backup, previous);
        var backupBefore = File.ReadAllBytes(f.Backup);
        File.WriteAllText(f.Current, "{broken");
        f.Network.PublishDownloader("2026.10.01", "downloader 2026.10.01"u8.ToArray());
        await f.Service.UpdateDownloaderAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Single(Directory.GetFiles(f.Root, "current.json.corrupt-*"));
        Assert.Equal(backupBefore, File.ReadAllBytes(f.Backup));
        Assert.NotEqual(f.Owned(f.Root, previous), f.Service.Find("yt-dlp"));
        await f.Service.RollbackAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(f.Owned(f.Root, previous), f.Service.Find("yt-dlp"));
    }

    // Finding: the component download had a hard total deadline.
    [Fact]
    public async Task Slow_but_steady_download_is_not_cut_off_by_a_total_deadline()
    {
        using var f = new ComponentFixture();
        var chunks = Enumerable.Range(0, 8).Select(i => Encoding.ASCII.GetBytes($"chunk {i};")).ToArray();
        var all = chunks.SelectMany(c => c).ToArray();
        // Old behaviour: the 1 s client timeout capped the whole 2 s body transfer.
        using var client = new HttpClient(new StreamHandler(() => new TrickleStream(chunks, TimeSpan.FromMilliseconds(250)))) { Timeout = TimeSpan.FromSeconds(1) };
        var service = new ManagedComponentService(f.Runner, f.Root, client, f.Shipped);
        var target = Path.Combine(f.Root, "download.bin");
        await service.DownloadVerifiedAsync("https://example.com/tool", Sha(all), target, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(all, File.ReadAllBytes(target));
    }

    [Fact]
    public async Task Total_download_cap_still_bounds_a_drip_feeding_server()
    {
        using var f = new ComponentFixture();
        var chunks = Enumerable.Range(0, 40).Select(_ => "x"u8.ToArray()).ToArray();
        using var client = new HttpClient(new StreamHandler(() => new TrickleStream(chunks, TimeSpan.FromMilliseconds(100)))) { Timeout = TimeSpan.FromSeconds(5) };
        var service = new ManagedComponentService(f.Runner, f.Root, client, f.Shipped) { DownloadTotalLimit = TimeSpan.FromMilliseconds(700) };
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => service.DownloadVerifiedAsync("https://example.com/tool", new string('a', 64), Path.Combine(f.Root, "download.bin"), null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("timed out", error.Message);
    }

    // Finding: leftover staging folders and old versions were never cleaned.
    [ComponentWindowsFact]
    public async Task Install_sweeps_interrupted_leftovers_and_prunes_only_unreferenced_versions()
    {
        using var f = new ComponentFixture();
        var active = f.Install(f.Root, "yt-dlp", "2026.09.01", "downloader 2026.09.01");
        var previous = f.Install(f.Root, "yt-dlp", "2026.08.01", "downloader 2026.08.01");
        var orphan = f.Install(f.Root, "yt-dlp", "2026.07.01", "downloader 2026.07.01");
        f.Record(f.Current, active); f.Record(f.Backup, previous);
        var past = DateTime.UtcNow.AddHours(-1);
        var staging = Path.Combine(f.Root, ".staging-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "ffmpeg.zip"), "partial download"); Directory.SetCreationTimeUtc(staging, past);
        var manifest = Path.Combine(f.Root, ".manifest-" + Guid.NewGuid().ToString("N")); File.WriteAllText(manifest, "{}"); File.SetCreationTimeUtc(manifest, past);
        var foreignStaging = Path.Combine(f.Root, ".staging-not-ours"); Directory.CreateDirectory(foreignStaging); Directory.SetCreationTimeUtc(foreignStaging, past);
        var foreignVersion = Path.Combine(f.Root, "versions", "keep-me"); Directory.CreateDirectory(foreignVersion);
        f.Network.PublishDownloader("2026.10.01", "downloader 2026.10.01"u8.ToArray());

        await f.Service.UpdateDownloaderAsync().WaitAsync(TimeSpan.FromSeconds(20));

        Assert.False(Directory.Exists(staging)); Assert.False(File.Exists(manifest));
        Assert.True(Directory.Exists(foreignStaging)); Assert.True(Directory.Exists(foreignVersion));
        var current = ComponentFixture.Read(f.Current)["yt-dlp"];
        Assert.Equal(active.RelativePath, ComponentFixture.Read(f.Backup)["yt-dlp"].RelativePath);
        Assert.True(File.Exists(f.Owned(f.Root, current))); Assert.True(File.Exists(f.Owned(f.Root, active)));
        Assert.False(Directory.Exists(Path.GetDirectoryName(f.Owned(f.Root, previous))));
        Assert.False(Directory.Exists(Path.GetDirectoryName(f.Owned(f.Root, orphan))));
        await f.Service.RollbackAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(f.Owned(f.Root, active), f.Service.Find("yt-dlp"));
    }

    // Finding: common install failures reached the UI as raw exception text.
    [ComponentWindowsFact]
    public async Task Interrupted_download_reports_a_connection_message_and_keeps_details()
    {
        using var f = new ComponentFixture();
        f.Network.PublishDownloader("2026.10.01", "downloader 2026.10.01"u8.ToArray(), content: () => new StreamContent(new BrokenStream()));
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => f.Service.UpdateDownloaderAsync().WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Contains("interrupted", error.Message);
        Assert.Contains("remain active", error.Message);
        Assert.Contains("HttpIOException", error.Details);
        Assert.False(File.Exists(f.Current));
        Assert.Empty(Directory.GetDirectories(f.Root, ".staging-*"));
    }

    [ComponentWindowsTheory]
    [InlineData("{\"assets\":[]}")]
    [InlineData("{\"message\":\"Not Found\"}")]
    [InlineData("[]")]
    public async Task Incomplete_release_information_reports_a_plain_message(string release)
    {
        using var f = new ComponentFixture();
        f.Network.ReleaseJson = release;
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => f.Service.UpdateDownloaderAsync().WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Contains("release information", error.Message);
    }

    [Fact]
    public void Common_install_failures_map_to_plain_messages_with_redacted_details()
    {
        static MediaOperationException Map(Exception error) => ManagedComponentService.FriendlyInstallFailure(error, userCancelled: false) ?? throw new Xunit.Sdk.XunitException("No friendly message for " + error.GetType().Name);
        var disk = Map(new IOException(@"There is not enough space on the disk: C:\Users\someone\AppData\Local\KeaClip", unchecked((int)0x80070070)));
        Assert.Contains("disk space", disk.Message);
        Assert.Contains("There is not enough space", disk.Details);
        Assert.DoesNotContain("someone", disk.Details);
        Assert.Contains("in use", Map(new IOException("busy", unchecked((int)0x80070020))).Message);
        Assert.Contains("not allowed", Map(new UnauthorizedAccessException("Access is denied.")).Message);
        Assert.Contains("timed out", Map(new TaskCanceledException("client timeout")).Message);
        Assert.Contains("interrupted", Map(new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.")).Message);
        Assert.Contains("connection", Map(new HttpRequestException("No such host is known.")).Message);
        Assert.Contains("release information", Map(new JsonException("bad")).Message);
        Assert.Null(ManagedComponentService.FriendlyInstallFailure(new OperationCanceledException(), userCancelled: true));
        Assert.Null(ManagedComponentService.FriendlyInstallFailure(new MediaOperationException("Already plain."), userCancelled: false));
    }

    // Finding: ProcessRunner kept only the head of stderr.
    [ComponentWindowsFact]
    public async Task Process_output_keeps_the_closing_error_after_a_long_noisy_run()
    {
        const string script = "1..400 | ForEach-Object { [Console]::Error.WriteLine('warning: non monotonic dts in packet ' + $_) }; [Console]::Error.WriteLine('Error: No space left on device'); exit 1";
        var result = await new ProcessRunner().RunAsync(new("pwsh", ["-NoProfile", "-NonInteractive", "-NoLogo", "-Command", script], Timeout: TimeSpan.FromSeconds(60), OutputLimit: 4096));
        Assert.Equal(1, result.ExitCode);
        Assert.True(result.StandardError.Length <= 4096);
        Assert.StartsWith("warning: non monotonic dts in packet 1\n", result.StandardError);
        Assert.EndsWith("Error: No space left on device\n", result.StandardError);
        Assert.StartsWith("Not enough disk space", EngineSupport.ClassifyFailure(result, "Media export"));
    }

    [ComponentWindowsFact]
    public async Task Process_output_within_the_limit_is_returned_exactly()
    {
        const string script = "1..50 | ForEach-Object { [Console]::Out.WriteLine('line ' + $_) }";
        var result = await new ProcessRunner().RunAsync(new("pwsh", ["-NoProfile", "-NonInteractive", "-NoLogo", "-Command", script], Timeout: TimeSpan.FromSeconds(60), OutputLimit: 4096));
        Assert.Equal(string.Concat(Enumerable.Range(1, 50).Select(i => "line " + i + "\n")), result.StandardOutput);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class ComponentFixture : IDisposable
    {
        private readonly string _base = Path.Combine(Path.GetTempPath(), "KeaClipReliabilityTests-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http;
        private ManagedComponentService? _service;
        public string Root => Path.Combine(_base, "Components");
        public string Shipped => Path.Combine(_base, "App", "Components");
        public string Current => Path.Combine(Root, "current.json");
        public string Backup => Current + ".backup";
        public VersionRunner Runner { get; } = new();
        public FakeNetwork Network { get; } = new();
        public ManagedComponentService Service => _service ??= new ManagedComponentService(Runner, Root, _http, Shipped);
        public ComponentFixture() { Directory.CreateDirectory(Root); Directory.CreateDirectory(Shipped); _http = new HttpClient(Network); }
        public string Owned(string root, ManagedComponent entry) => Path.GetFullPath(Path.Combine(root, entry.RelativePath));
        public ManagedComponent Install(string root, string name, string version, string content, string? id = null)
        {
            var relative = "versions/" + (id ?? Guid.NewGuid().ToString("N")) + "/" + name + ".exe";
            var path = Path.GetFullPath(Path.Combine(root, relative));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content);
            Runner.Versions[path] = version;
            var source = name switch { "yt-dlp" => "https://github.com/yt-dlp/yt-dlp/releases/download/test/yt-dlp.exe", "deno" => "https://github.com/denoland/deno/releases/download/test/deno-x86_64-pc-windows-msvc.zip", _ => "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip" };
            return new(name, relative, version, Sha(File.ReadAllBytes(path)), source, "Fixture licence record");
        }
        public ManagedComponent Ship(string name, string version, string content, string? id = null)
        {
            var entry = Install(Shipped, name, version, content, id);
            var manifest = Path.Combine(Shipped, "current.json");
            var entries = File.Exists(manifest) ? Read(manifest) : [];
            entries[name] = entry;
            File.WriteAllText(manifest, JsonSerializer.Serialize(entries));
            return entry;
        }
        // ffmpeg and ffprobe always share one folder, as the installer and package lay them out.
        public Dictionary<string, ManagedComponent> ShipAll()
        {
            var pair = Guid.NewGuid().ToString("N");
            return new[] { ("yt-dlp", "2026.08.19"), ("ffmpeg", "ffmpeg version 7.1.1"), ("ffprobe", "ffprobe version 7.1.1"), ("deno", "deno 2.4.3") }
                .ToDictionary(x => x.Item1, x => Ship(x.Item1, x.Item2, "shipped " + x.Item1, x.Item1.StartsWith("ff", StringComparison.Ordinal) ? pair : null));
        }
        public ManagedComponent[] InstallPair(string root, string version, string content)
        {
            var id = Guid.NewGuid().ToString("N");
            return [Install(root, "ffmpeg", "ffmpeg version " + version, content + " ffmpeg", id), Install(root, "ffprobe", "ffprobe version " + version, content + " ffprobe", id)];
        }
        public void Record(string path, params ManagedComponent[] entries) => File.WriteAllText(path, JsonSerializer.Serialize(entries.ToDictionary(e => e.Name)));
        public static Dictionary<string, ManagedComponent> Read(string path) => JsonSerializer.Deserialize<Dictionary<string, ManagedComponent>>(File.ReadAllText(path))!;
        public string[] Snapshot() => Directory.GetFileSystemEntries(_base, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(_base, p) + (File.Exists(p) ? " " + Sha(File.ReadAllBytes(p)) : "/")).ToArray();
        public void Dispose()
        {
            _http.Dispose();
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var resolved = Path.GetFullPath(_base);
            if (!resolved.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("KeaClipReliabilityTests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup target.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }

    private sealed class VersionRunner : IProcessRunner
    {
        public ConcurrentDictionary<string, string> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentQueue<ProcessRequest> Calls { get; } = new();
        public ConcurrentQueue<int> Threads { get; } = new();
        public Task<ProcessResult> RunAsync(ProcessRequest request, Action<string>? stdout = null, Action<string>? stderr = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Enqueue(request); Threads.Enqueue(Environment.CurrentManagedThreadId);
            // Freshly downloaded copies are unknown paths and report the published release.
            return Task.FromResult(new ProcessResult(0, Versions.GetValueOrDefault(Path.GetFullPath(request.Executable), "2026.10.01") + "\n", ""));
        }
    }

    private sealed class FakeNetwork : HttpMessageHandler
    {
        public const string DownloaderRelease = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
        public const string DenoRelease = "https://api.github.com/repos/denoland/deno/releases/latest";
        public const string MediaTools = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
        private readonly ConcurrentDictionary<string, Func<HttpContent>> _files = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _releases = new(StringComparer.Ordinal);
        public ConcurrentQueue<Uri> Requests { get; } = new();
        public string? ReleaseJson { get => _releases.GetValueOrDefault(DownloaderRelease); set { if (value is null) _releases.TryRemove(DownloaderRelease, out _); else _releases[DownloaderRelease] = value; } }
        public Action? OnDownload { get; set; }
        public void PublishDeno(byte[] executable)
        {
            var zip = Zip(("deno.exe", executable));
            const string url = "https://github.com/denoland/deno/releases/download/v2.5.0/deno-x86_64-pc-windows-msvc.zip";
            _releases[DenoRelease] = JsonSerializer.Serialize(new { assets = new[] { new { name = "deno-x86_64-pc-windows-msvc.zip", browser_download_url = url, digest = "sha256:" + Sha(zip) } } });
            _files[url] = () => new ByteArrayContent(zip);
        }
        public void PublishMediaTools(byte[] ffmpeg, byte[] ffprobe)
        {
            const string folder = "ffmpeg-7.1.1-essentials_build/";
            var zip = Zip((folder + "bin/ffmpeg.exe", ffmpeg), (folder + "bin/ffprobe.exe", ffprobe), (folder + "LICENSE", "GPL-3.0"u8.ToArray()));
            _files[MediaTools + ".sha256"] = () => new StringContent(Sha(zip).ToLowerInvariant());
            _files[MediaTools] = () => new ByteArrayContent(zip);
        }
        private static byte[] Zip(params (string Name, byte[] Body)[] files)
        {
            using var memory = new MemoryStream();
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (name, body) in files) { using var entry = archive.CreateEntry(name).Open(); entry.Write(body); }
            return memory.ToArray();
        }
        public void PublishDownloader(string version, byte[] body, string? hash = null, Func<HttpContent>? content = null)
        {
            var url = "https://github.com/yt-dlp/yt-dlp/releases/download/" + version + "/yt-dlp.exe";
            ReleaseJson = JsonSerializer.Serialize(new { assets = new[] { new { name = "yt-dlp.exe", browser_download_url = url, digest = "sha256:" + (hash ?? Sha(body)) } } });
            _files[url] = content ?? (() => new ByteArrayContent(body));
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!; Requests.Enqueue(uri);
            if (_releases.TryGetValue(uri.AbsoluteUri, out var release))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(release, Encoding.UTF8, "application/json") });
            if (_files.TryGetValue(uri.AbsoluteUri, out var file)) { OnDownload?.Invoke(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = file() }); }
            throw new InvalidOperationException("Unexpected network request in a component test.");
        }
    }

    private sealed class StreamHandler(Func<Stream> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body()) });
    }

    private abstract class ReadOnlyTestStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TrickleStream(byte[][] chunks, TimeSpan gap) : ReadOnlyTestStream
    {
        private int _next;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_next >= chunks.Length) return 0;
            await Task.Delay(gap, cancellationToken);
            var chunk = chunks[_next++]; chunk.CopyTo(buffer); return chunk.Length;
        }
    }

    private sealed class BrokenStream : ReadOnlyTestStream
    {
        private bool _sent;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sent) throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
            _sent = true; "partial"u8.CopyTo(buffer.Span); return ValueTask.FromResult(7);
        }
    }
}
