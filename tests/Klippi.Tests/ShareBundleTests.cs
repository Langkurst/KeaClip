using System.Diagnostics;
using System.IO.Compression;
using Klippi.Core;
using Klippi.Infrastructure.Sharing;
using Xunit;

namespace Klippi.Tests;

public sealed class ShareBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "KeaClip-bundle-" + Guid.NewGuid().ToString("N"));
    private string Bundles => Path.Combine(_root, "bundles");

    [Fact]
    public async Task Same_named_files_from_different_items_both_arrive_and_video_is_stored_as_it_is()
    {
        var one = Write("one/clip.mp4", new byte[4096]); var two = Write("two/clip.mp4", new byte[4096]);
        var selection = ShareBundle.Describe([one, two]);
        Assert.Equal(2, selection.FileCount); Assert.Equal(8192, selection.Bytes); Assert.True(selection.NeedsPacking);
        var zip = await ShareBundle.PackAsync(selection, Bundles);
        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(["clip (2).mp4", "clip.mp4"], archive.Entries.Select(entry => entry.FullName).Order());
        Assert.All(archive.Entries, entry => Assert.Equal(entry.Length, entry.CompressedLength)); // zeros would shrink if compressed
    }

    [Fact]
    public void A_single_file_needs_no_packing_and_missing_or_empty_drops_are_explained()
    {
        Assert.False(ShareBundle.Describe([Write("tickets.pdf", [1])]).NeedsPacking);
        Assert.Contains("no longer available", Assert.Throws<MediaOperationException>(() => ShareBundle.Describe([Path.Combine(_root, "gone")])).Message);
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        Assert.Contains("no files", Assert.Throws<MediaOperationException>(() => ShareBundle.Describe([Path.Combine(_root, "empty")])).Message);
    }

    [Fact]
    public void Junctions_inside_a_folder_are_skipped_not_followed()
    {
        if (!OperatingSystem.IsWindows()) return;
        Write("shared/photo.jpg", [1, 2]); Write("elsewhere/secret.txt", [9]);
        var junction = Path.Combine(_root, "shared", "link");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{Path.Combine(_root, "elsewhere")}\"") { CreateNoWindow = true, UseShellExecute = false })!) mklink.WaitForExit();
        Assert.True(Directory.Exists(junction));
        var selection = ShareBundle.Describe([Path.Combine(_root, "shared")]);
        Assert.Equal(1, selection.FileCount);
    }

    [Fact]
    public async Task A_cancelled_pack_leaves_nothing_behind_and_discard_only_touches_its_own_folder()
    {
        Write("big/a.bin", new byte[1 << 20]);
        var selection = ShareBundle.Describe([Path.Combine(_root, "big")]);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ShareBundle.PackAsync(selection, Bundles, cancellationToken: cancelled.Token));
        Assert.Empty(Directory.EnumerateFiles(Bundles));

        var outside = Write("keep.txt", [1]);
        ShareBundle.Discard(outside, Bundles);
        Assert.True(File.Exists(outside));
        var zip = await ShareBundle.PackAsync(selection, Bundles);
        ShareBundle.Discard(zip, Bundles);
        Assert.False(File.Exists(zip));
    }

    private string Write(string relative, byte[] content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, content); return path;
    }

    public void Dispose()
    {
        // The junction is removed first so deleting the tree never reaches through it.
        var junction = Path.Combine(_root, "shared", "link"); if (Directory.Exists(junction)) Directory.Delete(junction);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
