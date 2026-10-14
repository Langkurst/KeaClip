using System.Security.Cryptography;
using System.Text.Json;
using Klippi.Core;
using Klippi.Infrastructure;
using Xunit;

namespace Klippi.Tests;

public sealed class ComponentRollbackTests
{
    [Fact]
    public async Task Verified_rollback_atomically_swaps_manifests_and_can_restore_the_newer_set()
    {
        using var fixture = new RollbackFixture();
        var progress = new EngineProgress();
        var sourceHashes = fixture.BinaryHashes();
        await fixture.Service.RollbackAsync(progress);
        Assert.Equal(fixture.PreviousBytes, await File.ReadAllBytesAsync(fixture.Current));
        Assert.Equal(fixture.ActiveBytes, await File.ReadAllBytesAsync(fixture.Backup));
        foreach (var entry in fixture.Previous.Values) Assert.Equal(fixture.Owned(entry.RelativePath), fixture.Service.Find(entry.Name));
        Assert.Equal(4, fixture.Runner.Calls.Count);
        Assert.All(fixture.Runner.Calls, call => Assert.Equal(new[] { Path.GetFileName(call.Executable) is "ffmpeg.exe" or "ffprobe.exe" ? "-version" : "--version" }, call.Arguments));
        Assert.Contains(progress.Values, item => item.Stage == JobStage.Verifying);
        Assert.Contains(progress.Values, item => item.Stage == JobStage.Finished && item.Percent == 100);
        AssertNoTemporaryManifest(fixture);
        Assert.Equal(sourceHashes, fixture.BinaryHashes());

        await fixture.Service.RollbackAsync();
        Assert.Equal(fixture.ActiveBytes, await File.ReadAllBytesAsync(fixture.Current));
        Assert.Equal(fixture.PreviousBytes, await File.ReadAllBytesAsync(fixture.Backup));
        Assert.Equal(sourceHashes, fixture.BinaryHashes());
        AssertNoTemporaryManifest(fixture);
    }

    [Fact]
    public async Task Missing_previous_record_preserves_current_and_does_not_leave_the_operation_gate_locked()
    {
        using var fixture = new RollbackFixture();
        File.Delete(fixture.Backup);
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => fixture.Service.RollbackAsync());
        Assert.Contains("No previous", error.Message);
        Assert.Equal(fixture.ActiveBytes, await File.ReadAllBytesAsync(fixture.Current));
        Assert.Empty(fixture.Runner.Calls);
        await File.WriteAllBytesAsync(fixture.Backup, fixture.PreviousBytes);
        await fixture.Service.RollbackAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(fixture.PreviousBytes, await File.ReadAllBytesAsync(fixture.Current));
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("null-entry")]
    [InlineData("oversized")]
    public async Task Unreadable_or_incomplete_previous_record_cannot_deactivate_working_components(string kind)
    {
        using var fixture = new RollbackFixture();
        var content = kind switch { "broken" => "{invalid json", "empty" => "{}", "null" => "null", "null-entry" => "{\"yt-dlp\":null}", _ => new string(' ', 1_048_577) };
        await File.WriteAllTextAsync(fixture.Backup, content);
        var badBackup = await File.ReadAllBytesAsync(fixture.Backup);
        await Assert.ThrowsAsync<MediaOperationException>(() => fixture.Service.RollbackAsync());
        Assert.Equal(fixture.ActiveBytes, await File.ReadAllBytesAsync(fixture.Current));
        Assert.Equal(badBackup, await File.ReadAllBytesAsync(fixture.Backup));
        Assert.Empty(fixture.Runner.Calls);
        AssertNoTemporaryManifest(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_changed_binary_rejects_the_entire_prior_set_before_any_launch(bool changed)
    {
        using var fixture = new RollbackFixture();
        var path = fixture.Owned(fixture.Previous["deno"].RelativePath);
        if (changed) await File.AppendAllTextAsync(path, "tampered"); else File.Delete(path);
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => fixture.Service.RollbackAsync());
        Assert.Contains("checksum", error.Message);
        await AssertOriginalManifests(fixture);
        Assert.Empty(fixture.Runner.Calls);
        AssertNoTemporaryManifest(fixture);
    }

    [Theory]
    [InlineData("traversal")]
    [InlineData("rooted")]
    [InlineData("alternate-stream")]
    [InlineData("outside-versions")]
    [InlineData("wrong-name")]
    [InlineData("unknown-component")]
    [InlineData("untrusted-source")]
    [InlineData("source-traversal")]
    [InlineData("signed-source")]
    [InlineData("invalid-hash")]
    public async Task Invalid_previous_provenance_or_path_is_never_executed(string kind)
    {
        using var fixture = new RollbackFixture();
        var entry = fixture.Previous["yt-dlp"];
        var previous = new Dictionary<string, ManagedComponent>(fixture.Previous);
        previous["yt-dlp"] = kind switch
        {
            "traversal" => entry with { RelativePath = "../outside.exe" },
            "rooted" => entry with { RelativePath = fixture.Owned(entry.RelativePath) },
            "alternate-stream" => entry with { RelativePath = entry.RelativePath + ":payload" },
            "outside-versions" => entry with { RelativePath = "staging/" + Guid.NewGuid().ToString("N") + "/yt-dlp.exe" },
            "wrong-name" => entry with { Name = "deno" },
            "untrusted-source" => entry with { SourceUrl = "https://example.invalid/yt-dlp.exe" },
            "source-traversal" => entry with { SourceUrl = "https://github.com/yt-dlp/yt-dlp/releases/download/../../../../untrusted/yt-dlp.exe" },
            "signed-source" => entry with { SourceUrl = entry.SourceUrl + "?token=PRIVATE" },
            "invalid-hash" => entry with { Sha256 = new string('g', 64) },
            _ => entry
        };
        if (kind == "unknown-component") { previous.Remove("yt-dlp"); previous.Add("foreign-tool", entry with { Name = "foreign-tool" }); }
        await File.WriteAllTextAsync(fixture.Backup, JsonSerializer.Serialize(previous));
        var invalidBackup = await File.ReadAllBytesAsync(fixture.Backup);
        await Assert.ThrowsAsync<MediaOperationException>(() => fixture.Service.RollbackAsync());
        Assert.Equal(fixture.ActiveBytes, await File.ReadAllBytesAsync(fixture.Current));
        Assert.Equal(invalidBackup, await File.ReadAllBytesAsync(fixture.Backup));
        Assert.Empty(fixture.Runner.Calls);
        AssertNoTemporaryManifest(fixture);
    }

    [Theory]
    [InlineData("launch-failed")]
    [InlineData("wrong-version")]
    [InlineData("nonfree")]
    public async Task Failed_launch_version_or_licence_check_preserves_the_active_manifest(string kind)
    {
        using var fixture = new RollbackFixture();
        fixture.Runner.Result = request => kind switch
        {
            "launch-failed" => new ProcessResult(1, "", "private failure diagnostics"),
            "wrong-version" => new ProcessResult(0, "different version\n", ""),
            _ => new ProcessResult(0, fixture.VersionFor(request.Executable), "configuration: --enable-nonfree")
        };
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => fixture.Service.RollbackAsync());
        Assert.Contains("launch and version", error.Message);
        Assert.DoesNotContain("private", error.Message);
        await AssertOriginalManifests(fixture);
        AssertNoTemporaryManifest(fixture);
    }

    [Fact]
    public async Task Candidate_changed_during_launch_cannot_be_activated()
    {
        using var fixture = new RollbackFixture();
        fixture.Runner.Before = request => File.AppendAllText(request.Executable, "modified while checking");
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => fixture.Service.RollbackAsync());
        Assert.Contains("changed during verification", error.Message);
        await AssertOriginalManifests(fixture);
        AssertNoTemporaryManifest(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_before_or_during_verification_preserves_both_manifests(bool during)
    {
        using var fixture = new RollbackFixture();
        using var cancellation = new CancellationTokenSource();
        if (during) fixture.Runner.Before = _ => cancellation.Cancel(); else cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.RollbackAsync(cancellationToken: cancellation.Token));
        await AssertOriginalManifests(fixture);
        AssertNoTemporaryManifest(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Record_changed_by_another_writer_during_verification_is_preserved(bool backup)
    {
        using var fixture = new RollbackFixture();
        var path = backup ? fixture.Backup : fixture.Current;
        var content = (await File.ReadAllTextAsync(path)) + "\n";
        fixture.Runner.Before = _ => File.WriteAllText(path, content);
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => fixture.Service.RollbackAsync());
        Assert.Contains("records changed", error.Message);
        Assert.Equal(content, await File.ReadAllTextAsync(path));
        Assert.Equal(backup ? fixture.ActiveBytes : fixture.PreviousBytes, await File.ReadAllBytesAsync(backup ? fixture.Current : fixture.Backup));
        AssertNoTemporaryManifest(fixture);
    }

    [EngineWindowsFact]
    public async Task Locked_current_manifest_cannot_partially_swap_or_destroy_the_backup()
    {
        using var fixture = new RollbackFixture();
        FileStream? locked = null;
        try
        {
            fixture.Runner.Before = _ => locked ??= new FileStream(fixture.Current, FileMode.Open, FileAccess.Read, FileShare.Read);
            var error = await Assert.ThrowsAsync<MediaOperationException>(() => fixture.Service.RollbackAsync());
            Assert.Contains("could not be checked or activated", error.Message);
            await AssertOriginalManifests(fixture);
            AssertNoTemporaryManifest(fixture);
        }
        finally { locked?.Dispose(); }
    }

    private static void AssertNoTemporaryManifest(RollbackFixture fixture) => Assert.Empty(Directory.GetFiles(fixture.Root, ".rollback-*"));
    private static async Task AssertOriginalManifests(RollbackFixture fixture)
    {
        Assert.Equal(fixture.ActiveBytes, await File.ReadAllBytesAsync(fixture.Current));
        Assert.Equal(fixture.PreviousBytes, await File.ReadAllBytesAsync(fixture.Backup));
    }

    private sealed class RollbackFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "KeaClipRollbackTests-" + Guid.NewGuid().ToString("N"));
        public string Current => Path.Combine(Root, "current.json");
        public string Backup => Current + ".backup";
        public Dictionary<string, ManagedComponent> Active { get; }
        public Dictionary<string, ManagedComponent> Previous { get; }
        public byte[] ActiveBytes { get; }
        public byte[] PreviousBytes { get; }
        public RollbackRunner Runner { get; }
        public ManagedComponentService Service { get; }
        private readonly HttpClient http = new(new NoNetwork());
        public RollbackFixture()
        {
            Directory.CreateDirectory(Root);
            Active = CreateVersion("new"); Previous = CreateVersion("old");
            // An older unreferenced version must also survive activation.
            _ = CreateVersion("oldest");
            ActiveBytes = JsonSerializer.SerializeToUtf8Bytes(Active);
            PreviousBytes = JsonSerializer.SerializeToUtf8Bytes(Previous);
            File.WriteAllBytes(Current, ActiveBytes); File.WriteAllBytes(Backup, PreviousBytes);
            Runner = new RollbackRunner(request => new ProcessResult(0, VersionFor(request.Executable) + "\n", ""));
            Service = new ManagedComponentService(Runner, Root, http);
        }
        public string Owned(string relative) => Path.GetFullPath(Path.Combine(Root, relative));
        public string VersionFor(string path) => Active.Values.Concat(Previous.Values).Single(entry => Owned(entry.RelativePath) == path).Version;
        public KeyValuePair<string, string>[] BinaryHashes() => Directory.GetFiles(Path.Combine(Root, "versions"), "*.exe", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path => new KeyValuePair<string, string>(Path.GetRelativePath(Root, path), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))).ToArray();
        private Dictionary<string, ManagedComponent> CreateVersion(string version)
        {
            var id = Guid.NewGuid().ToString("N");
            var directory = Path.Combine(Root, "versions", id); Directory.CreateDirectory(directory);
            var result = new Dictionary<string, ManagedComponent>();
            foreach (var name in new[] { "yt-dlp", "ffmpeg", "ffprobe", "deno" })
            {
                var relative = "versions/" + id + "/" + name + ".exe";
                File.WriteAllText(Owned(relative), version + " fake owned binary " + name);
                var source = name switch { "yt-dlp" => "https://github.com/yt-dlp/yt-dlp/releases/download/test/yt-dlp.exe", "deno" => "https://github.com/denoland/deno/releases/download/test/deno-x86_64-pc-windows-msvc.zip", _ => "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip" };
                result[name] = new(name, relative, version + " " + name, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Owned(relative)))), source, "Fixture licence record");
            }
            return result;
        }
        public void Dispose()
        {
            http.Dispose();
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("KeaClipRollbackTests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup target.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }

    private sealed class RollbackRunner(Func<ProcessRequest, ProcessResult> result) : IProcessRunner
    {
        public List<ProcessRequest> Calls { get; } = [];
        public Action<ProcessRequest>? Before { get; set; }
        public Func<ProcessRequest, ProcessResult>? Result { get; set; }
        public Task<ProcessResult> RunAsync(ProcessRequest request, Action<string>? stdout = null, Action<string>? stderr = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls.Add(request); Before?.Invoke(request); cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult((Result ?? result)(request));
        }
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("Rollback must not perform network operations.");
    }
}
