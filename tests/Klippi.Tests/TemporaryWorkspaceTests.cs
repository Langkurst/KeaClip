using System.Text.Json.Nodes;
using System.Text;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Klippi.Core;
using Klippi.Infrastructure;
using Klippi.Infrastructure.Engines;
using Xunit;

namespace Klippi.Tests;

public sealed class TemporaryWorkspaceTests
{
    [Fact]
    public async Task RunningLeaseIsSkippedAndRestartDeletesDeclaredPayloadOnly()
    {
        using var files = new Files(); var service = files.Service();
        var lease = service.Create(files.Output); lease.RegisterPayload("clip.mp4");
        var media = files.Payload(lease, "clip.mp4", "owned bytes");
        var active = await files.Service().CleanAsync();
        Assert.Equal(1, active.ActiveFolders); Assert.Equal(0, active.DeletedFolders); Assert.True(File.Exists(media));
        Assert.Throws<IOException>(() => Directory.Move(files.Registry, files.Registry + "-moved"));
        lease.Dispose();
        var restarted = await files.Service().CleanAsync();
        Assert.Equal(1, restarted.DeletedFolders); Assert.Equal(11, restarted.DeletedBytes); Assert.False(Directory.Exists(lease.Path));
        Assert.Empty(Directory.EnumerateFiles(files.Registry, "*.lock"));
    }

    [Fact]
    public async Task UnregisteredLookalikeAndUnrelatedUserMediaArePreserved()
    {
        using var files = new Files(); var unregistered = Path.Combine(files.Output, ".keaclip-work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unregistered); var unrelated = Path.Combine(unregistered, "user.mp4"); File.WriteAllText(unrelated, "user");
        var lease = files.Service().Create(files.Output); lease.RegisterPayload("clip.mp4");
        var owned = files.Payload(lease, "clip.mp4"); var user = files.Payload(lease, "unrelated-user.mp4", "user"); lease.Dispose();
        var result = await files.Service().CleanAsync();
        Assert.Equal(0, result.DeletedFolders); Assert.Equal(1, result.SkippedFolders);
        Assert.True(File.Exists(owned)); Assert.Equal("user", File.ReadAllText(user)); Assert.Equal("user", File.ReadAllText(unrelated));
    }

    [Fact]
    public async Task GenericWorkspaceDeclaresNoPayloadOwnership()
    {
        using var files = new Files(); var lease = files.Service().Create(files.Output); var payload = files.Payload(lease, "clip.mp4"); lease.Dispose();
        Assert.Equal(1, (await files.Service().CleanAsync()).SkippedFolders); Assert.True(File.Exists(payload));
    }

    [Fact]
    public async Task RecoveryDispositionAndUnresolvedBackupsSurviveRestart()
    {
        using var files = new Files(); var service = files.Service();
        var recovery = service.Create(files.Output); recovery.RegisterPayload("clip.mp4"); files.Payload(recovery, "clip.mp4"); recovery.PreserveForRecovery(); recovery.Dispose();
        var backups = service.Create(files.Output); backups.RegisterPayload("clip.mp4"); files.Payload(backups, "clip.mp4"); files.Payload(backups, ".restore-" + Guid.NewGuid().ToString("N"), "original"); backups.Dispose();
        var result = await files.Service().CleanAsync(); Assert.Equal(2, result.RecoveryFolders); Assert.Equal(0, result.DeletedFolders);
        Assert.True(Directory.Exists(recovery.Path)); Assert.True(Directory.Exists(backups.Path));
    }

    [Fact]
    public void CompletedEngineCleanupRemovesOnlyItsSuccessfulReplacementBackups()
    {
        using var files = new Files(); using var lease = files.Service().Create(files.Output); lease.RegisterPayload("clip.mp4");
        var backup = ".restore-" + Guid.NewGuid().ToString("N"); lease.RegisterPayload(backup);
        files.Payload(lease, "clip.mp4"); files.Payload(lease, backup);
        EngineSupport.Cleanup(lease, files.Output); Assert.False(Directory.Exists(lease.Path));
    }

    [Fact]
    public async Task UnregisteredValidBackupPreventsNormalAndRestartCleanupOfWholeFolder()
    {
        using var files = new Files(); var lease = files.Service().Create(files.Output); lease.RegisterPayload("clip.mp4");
        var media = files.Payload(lease, "clip.mp4"); var backup = files.Payload(lease, ".restore-" + Guid.NewGuid().ToString("N"), "user backup");
        EngineSupport.Cleanup(lease, files.Output); Assert.True(File.Exists(media)); Assert.Equal("user backup", File.ReadAllText(backup));
        lease.Dispose(); var result = await files.Service().CleanAsync(); Assert.Equal(1, result.RecoveryFolders); Assert.Equal(0, result.DeletedFolders);
        Assert.True(File.Exists(media)); Assert.Equal("user backup", File.ReadAllText(backup));
    }

    [Theory]
    [InlineData("unfamiliar.exe")]
    [InlineData("nested-folder")]
    public async Task UnfamiliarStructuresPreventAnyPayloadDeletion(string unknown)
    {
        using var files = new Files(); var lease = files.Service().Create(files.Output); lease.RegisterPayload("clip.mp4"); var media = files.Payload(lease, "clip.mp4");
        if (unknown == "nested-folder") Directory.CreateDirectory(Path.Combine(lease.Path, unknown)); else files.Payload(lease, unknown);
        lease.Dispose(); var result = await files.Service().CleanAsync();
        Assert.Equal(1, result.SkippedFolders); Assert.Equal(0, result.DeletedBytes); Assert.True(File.Exists(media));
    }

    [Fact]
    public async Task LockedPayloadPreservesEveryFileAndCanBeRetried()
    {
        using var files = new Files(); var lease = files.Service().Create(files.Output); lease.RegisterPayload("clip.mp4"); lease.RegisterPayload("audio.wav");
        var first = files.Payload(lease, "clip.mp4"); var second = files.Payload(lease, "audio.wav"); lease.Dispose();
        using (var writer = new FileStream(second, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await files.Service().CleanAsync(); Assert.Equal(0, result.DeletedFolders); Assert.Equal(0, result.DeletedBytes);
            Assert.True(File.Exists(first)); Assert.True(File.Exists(second));
        }
        Assert.Equal(1, (await files.Service().CleanAsync()).DeletedFolders);
    }

    [Fact]
    public async Task CancellationKeepsDurableRecordsAndPayloadForRetry()
    {
        using var files = new Files(); var lease = files.Service().Create(files.Output); lease.RegisterPayload("clip.mp4"); var media = files.Payload(lease, "clip.mp4"); lease.Dispose();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var result = await files.Service().CleanAsync(cancel.Token); Assert.True(result.Cancelled); Assert.Equal(0, result.DeletedFolders);
        Assert.True(File.Exists(media)); Assert.True(File.Exists(files.Record(lease)));
        Assert.Equal(1, (await files.Service().CleanAsync()).DeletedFolders);
    }

    [Theory]
    [InlineData("Schema", "0")]
    [InlineData("Nonce", "\"bad\"")]
    [InlineData("DirectoryIdentity", "\"000000000000000000000000\"")]
    [InlineData("WriterJobs", "\"untrusted-global-job\"")]
    [InlineData("ExpectedFiles", "\"[\\\"../user.mp4\\\"]\"")]
    [InlineData("DownloadBaseName", "\"../unsafe\"")]
    public async Task InvalidOrAlteredRegistryRecordsNeverAuthorizeDeletion(string property, string jsonValue)
    {
        using var files = new Files(); var lease = files.Service().Create(files.Output); lease.RegisterPayload("clip.mp4"); var media = files.Payload(lease, "clip.mp4"); lease.Dispose();
        var record = files.Record(lease); var json = JsonNode.Parse(File.ReadAllText(record))!.AsObject(); json[property] = JsonNode.Parse(jsonValue); File.WriteAllText(record, json.ToJsonString());
        var result = await files.Service().CleanAsync(); Assert.Equal(0, result.DeletedFolders); Assert.Equal(1, result.SkippedFolders); Assert.True(File.Exists(media));
        Assert.All(result.Warnings, warning => Assert.DoesNotContain(files.Root, warning, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("oversized")]
    [InlineData("foreign-property")]
    public async Task MalformedOrAmbiguousReceiptsFailClosed(string kind)
    {
        using var files = new Files(); var lease = files.Service().Create(files.Output); lease.RegisterPayload("clip.mp4"); var media = files.Payload(lease, "clip.mp4"); lease.Dispose();
        var record = files.Record(lease); var original = File.ReadAllText(record);
        File.WriteAllText(record, kind switch { "duplicate" => "{\"Schema\":1," + original[1..], "oversized" => new string(' ', 70_000), _ => "{\"unexpected\":1," + original[1..] });
        Assert.Equal(1, (await files.Service().CleanAsync()).SkippedFolders); Assert.True(File.Exists(media));
    }

    [Theory]
    [InlineData("marker")]
    [InlineData("record")]
    [InlineData("owner")]
    public async Task MissingOwnershipEvidencePreservesMedia(string missing)
    {
        using var files = new Files(); var lease = files.Service().Create(files.Output); lease.RegisterPayload("clip.mp4"); var media = files.Payload(lease, "clip.mp4"); lease.Dispose();
        File.Delete(missing switch { "marker" => Path.Combine(lease.Path, OwnedTemporaryWorkspaceService.ReceiptName), "record" => files.Record(lease), _ => Path.Combine(files.Registry, "owner.json") });
        Assert.Equal(0, (await files.Service().CleanAsync()).DeletedFolders); Assert.True(File.Exists(media));
    }

    [Fact]
    public async Task DirectoryIdentityPreventsCopiedReceiptFromAuthorizingReplacementFolder()
    {
        using var files = new Files(); var lease = files.Service().Create(files.Output); lease.RegisterPayload("clip.mp4"); files.Payload(lease, "clip.mp4", "original"); lease.Dispose();
        var original = lease.Path + "-original"; Directory.Move(lease.Path, original); Directory.CreateDirectory(lease.Path);
        File.Copy(Path.Combine(original, OwnedTemporaryWorkspaceService.ReceiptName), Path.Combine(lease.Path, OwnedTemporaryWorkspaceService.ReceiptName)); File.WriteAllText(Path.Combine(lease.Path, OwnedTemporaryWorkspaceService.LockName), "");
        var replacement = files.Payload(lease, "clip.mp4", "user replacement");
        Assert.Equal(1, (await files.Service().CleanAsync()).SkippedFolders); Assert.Equal("user replacement", File.ReadAllText(replacement)); Assert.Equal("original", File.ReadAllText(Path.Combine(original, "clip.mp4")));
    }

    [Fact]
    public async Task IndependentCleanersCannotDeleteTwiceOrEscapeRegistry()
    {
        using var files = new Files(); var lease = files.Service().Create(files.Output); lease.RegisterPayload("clip.mp4"); files.Payload(lease, "clip.mp4"); lease.Dispose();
        var results = await Task.WhenAll(Task.Run(() => files.Service().CleanAsync()), Task.Run(() => files.Service().CleanAsync()));
        Assert.Equal(1, results.Sum(result => result.DeletedFolders)); Assert.False(Directory.Exists(lease.Path));
    }

    [Theory]
    [InlineData("video.mp4")]
    [InlineData("video.f137.mp4.part-Frag2")]
    [InlineData("video.en.vtt")]
    [InlineData("video.info.json")]
    // Named format IDs (HLS, DASH variants), the fragment's own .part, and the chapter file.
    [InlineData("video.fhls-1080p.mp4")]
    [InlineData("video.f251-drc.webm.part")]
    [InlineData("video.fdash_video.v1.mp4.ytdl")]
    [InlineData("video.f137.mp4.part-Frag2.part")]
    [InlineData("video.fhls-720p.mp4.part-Frag12.part")]
    [InlineData("video.meta")]
    public async Task DeclaredDownloadBasenameAdmitsGeneratedMediaAndSidecars(string name)
    {
        using var files = new Files(); var lease = files.Service().Create(files.Output); lease.RegisterDownloadBaseName("video"); files.Payload(lease, name); lease.Dispose();
        Assert.Equal(1, (await files.Service().CleanAsync()).DeletedFolders);
    }

    [Theory]
    [InlineData("other.mp4")]
    [InlineData("video.user.mp4")]
    [InlineData("video.mp4.part-Fragunsafe")]
    [InlineData("video.mp4.part-Frag+2")]
    [InlineData("video.mp4.part-Frag 2")]
    [InlineData("video.mp4.part-Frag-0")]
    [InlineData("video.mp4.part-Frag2147483648")]
    [InlineData("video.f.mp4")]
    [InlineData("video.fhls 1080p.mp4")]
    [InlineData("video.fhls+1080p.mp4")]
    [InlineData("video.f137.mp4.part-Frag2.part.part")]
    [InlineData("video.mp4.part-Frag.part")]
    [InlineData("video.meta.json")]
    [InlineData("video.f137.exe")]
    [InlineData("other.meta")]
    public async Task SimilarButUndeclaredDownloadFilesArePreserved(string name)
    {
        using var files = new Files(); var lease = files.Service().Create(files.Output); lease.RegisterDownloadBaseName("video"); var media = files.Payload(lease, name); lease.Dispose();
        Assert.Equal(1, (await files.Service().CleanAsync()).SkippedFolders); Assert.True(File.Exists(media));
    }

    [Fact]
    public void FormatIdMiddlesAreBoundedToSixtyFourSafeCharacters()
    {
        Assert.True(OwnedTemporaryWorkspaceService.DownloadPayload("video.f" + new string('a', 64) + ".mp4", "video"));
        Assert.False(OwnedTemporaryWorkspaceService.DownloadPayload("video.f" + new string('a', 65) + ".mp4", "video"));
        Assert.False(OwnedTemporaryWorkspaceService.DownloadPayload("video.f137.mp4", ""));
        Assert.False(OwnedTemporaryWorkspaceService.DownloadPayload("video.fé.mp4", "video"));
    }

    [Fact]
    public async Task ArchivePromotionRetainsExclusiveControlWhileRemovingPayloadControls()
    {
        using var files = new Files(); var lease = files.Service().Create(files.Output); lease.RegisterPayload("clip.mp4"); files.Payload(lease, "clip.mp4");
        lease.PrepareForPromotion(); Assert.False(File.Exists(Path.Combine(lease.Path, OwnedTemporaryWorkspaceService.ReceiptName))); Assert.False(File.Exists(Path.Combine(lease.Path, OwnedTemporaryWorkspaceService.LockName)));
        Assert.Equal(1, (await files.Service().CleanAsync()).ActiveFolders);
        var destination = Path.Combine(files.Output, "Final Archive"); Directory.Move(lease.Path, destination); EngineSupport.Cleanup(lease, files.Output); lease.Dispose();
        Assert.True(File.Exists(Path.Combine(destination, "clip.mp4"))); Assert.False(File.Exists(files.Record(lease))); Assert.Equal(0, (await files.Service().CleanAsync()).DeletedFolders);
    }

    [Fact]
    public async Task InterruptedArchivePromotionIsPreservedForReview()
    {
        using var files = new Files(); var lease = files.Service().Create(files.Output); lease.RegisterPayload("clip.mp4"); var media = files.Payload(lease, "clip.mp4"); lease.PrepareForPromotion(); lease.Dispose();
        var result = await files.Service().CleanAsync(); Assert.Equal(1, result.RecoveryFolders); Assert.True(File.Exists(media));
    }

    [Theory]
    [InlineData("child")]
    [InlineData("folder")]
    [InlineData("registry")]
    [InlineData("ancestor")]
    public async Task ReparsePathsNeverFollowOrDeleteExternalData(string kind)
    {
        using var files = new Files(); var lease = files.Service().Create(files.Output); lease.RegisterPayload("clip.mp4"); files.Payload(lease, "clip.mp4"); lease.Dispose();
        var outside = Path.Combine(files.Root, "external"); Directory.CreateDirectory(outside); var user = Path.Combine(outside, "clip.mp4"); File.WriteAllText(user, "user");
        if (kind == "child") { var media = Path.Combine(lease.Path, "clip.mp4"); File.Delete(media); files.LinkDirectory(media, outside); }
        else
        {
            var path = kind switch { "folder" => lease.Path, "registry" => files.Registry, _ => files.Output };
            Directory.Move(path, path + "-saved"); files.LinkDirectory(path, outside);
        }
        Assert.Equal(0, (await files.Service().CleanAsync()).DeletedFolders); Assert.Equal("user", File.ReadAllText(user));
    }

    [Fact]
    public async Task EngineWriterJobIsDurableAndNormalCompletionAllowsCleanup()
    {
        using var files = new Files(); var lease = files.Service().Create(files.Output); lease.RegisterPayload("clip.mp4"); files.Payload(lease, "clip.mp4");
        await new ProcessRunner().RunAsync(new ProcessRequest("dotnet", ["--version"], Timeout: TimeSpan.FromSeconds(15)));
        var json = JsonNode.Parse(File.ReadAllText(files.Record(lease)))!; Assert.StartsWith("KeaClip.WorkspaceJob.", json["WriterJobs"]!.GetValue<string>());
        lease.Dispose(); Assert.Equal(1, (await files.Service().CleanAsync()).DeletedFolders);
    }

    [Fact]
    public async Task RegisteredJobObjectPreventsCleanupEvenAfterHostLeaseIsReleased()
    {
        using var files = new Files(); var lease = files.Service().Create(files.Output); lease.RegisterPayload("clip.mp4"); var media = files.Payload(lease, "clip.mp4");
        var name = "KeaClip.WorkspaceJob." + Guid.NewGuid().ToString("N"); using var job = CreateJobObjectW(IntPtr.Zero, name); Assert.False(job.IsInvalid);
        OwnedTemporaryWorkspaceService.RegisterWriterJob(name); lease.Dispose();
        var active = await files.Service().CleanAsync(); Assert.Equal(1, active.ActiveFolders); Assert.True(File.Exists(media));
        job.Dispose(); Assert.Equal(1, (await files.Service().CleanAsync()).DeletedFolders);
    }

    [Fact]
    public async Task NestedWorkspacesRecordEveryWriterBeforeRelease()
    {
        using var files = new Files(); var outer = files.Service().Create(files.Output); var inner = files.Service().Create(outer.Path);
        inner.RegisterPayload("audio.wav"); files.Payload(inner, "audio.wav");
        var name = "KeaClip.WorkspaceJob." + Guid.NewGuid().ToString("N"); OwnedTemporaryWorkspaceService.RegisterWriterJob(name);
        foreach (var lease in new[] { outer, inner }) Assert.Equal(name, JsonNode.Parse(File.ReadAllText(files.Record(lease)))!["WriterJobs"]!.GetValue<string>());
        inner.Dispose(); outer.Dispose(); var result = await files.Service().CleanAsync(); Assert.Equal(2, result.DeletedFolders); Assert.False(Directory.Exists(outer.Path));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObjectW(IntPtr security, string name);

    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "KeaClipTemporaryTests", Guid.NewGuid().ToString("N"));
        public string Data => Path.Combine(Root, "data"); public string Output => Path.Combine(Root, "output"); public string Registry => Path.Combine(Data, "TemporaryWorkspaces");
        private readonly List<(string Path, bool Directory)> _links = [];
        public Files() => Directory.CreateDirectory(Output);
        public OwnedTemporaryWorkspaceService Service() => new(Data);
        public string Payload(ITemporaryWorkspace lease, string name, string contents = "owned") { var path = Path.Combine(lease.Path, name); File.WriteAllText(path, contents); return path; }
        public string Record(ITemporaryWorkspace lease) => Path.Combine(Registry, Path.GetFileName(lease.Path)[".keaclip-work-".Length..] + ".json");
        public void LinkDirectory(string path, string target)
        {
            if (!OperatingSystem.IsWindows()) Directory.CreateSymbolicLink(path, target);
            else
            {
                // Junctions exercise real reparse paths without changing the
                // machine's developer-mode or symbolic-link privilege settings.
                Directory.CreateDirectory(path);
                using var handle = CreateFileW(path, 0x40000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) throw new IOException("Test junction handle failed.");
                var substitute = Encoding.Unicode.GetBytes("\\??\\" + target); var printed = Encoding.Unicode.GetBytes(target); var buffer = new byte[20 + substitute.Length + printed.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xa0000003); BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)(buffer.Length - 8));
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), (ushort)substitute.Length); BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), (ushort)(substitute.Length + 2)); BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), (ushort)printed.Length);
                substitute.CopyTo(buffer, 16); printed.CopyTo(buffer, 18 + substitute.Length);
                if (!DeviceIoControl(handle, 0x000900a4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero)) throw new IOException("Test junction creation failed.");
            }
            _links.Add((path, true));
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out uint returned, IntPtr overlapped);
        public void Dispose()
        {
            foreach (var link in _links) { if (link.Directory) Directory.Delete(link.Path, false); else File.Delete(link.Path); }
            var resolved = Path.GetFullPath(Root); var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KeaClipTemporaryTests")) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected test workspace path.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
