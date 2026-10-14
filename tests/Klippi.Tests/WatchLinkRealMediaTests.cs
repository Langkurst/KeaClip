using System.Security.Cryptography;
using System.Text.Json;
using Klippi.Core;
using Klippi.Infrastructure.Sharing;
using Xunit;

namespace Klippi.Tests;

// The browser copy with the real FFmpeg and FFprobe (set KLIPPI_FFMPEG_DIR, as for the other real-media checks). Small,
// short synthetic clips keep the CPU load low. With KLIPPI_WATCH_FIXTURES also set to a folder, the last test runs the
// whole watch link pipeline and writes the encrypted objects plus their links there for web/watch/test/e2e.mjs.
[Collection(RealFfmpeg.Collection)]
public sealed class WatchLinkRealMediaTests
{
    private static async Task<string> H264Async(RealFfmpeg media, string name, int seconds, int gop, string size = "320x180")
        => await media.MakeAsync(name, "-f", "lavfi", "-i", $"testsrc2=s={size}:r=30:d={seconds}", "-f", "lavfi", "-i", $"sine=f=440:d={seconds}",
            "-c:v", "libx264", "-preset", "ultrafast", "-g", gop.ToString(System.Globalization.CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "96k", "-shortest");

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [RealFfmpegFact]
    public async Task H264_with_AAC_is_repackaged_into_fragments_that_start_at_keyframes()
    {
        using var media = new RealFfmpeg();
        string source = await H264Async(media, "camera.mp4", 12, 60);
        string before = Hash(source);
        string copyPath;
        using (var copy = await media.Service.PrepareWatchCopyAsync(source))
        {
            copyPath = copy.FilePath;
            Assert.False(copy.Reencoded);
            Assert.StartsWith("avc1.", copy.Layout.Codecs);
            Assert.EndsWith(",mp4a.40.2", copy.Layout.Codecs);
            Assert.Equal(0, copy.Layout.Fragments[0].Time, 1);
            Assert.InRange(copy.Layout.Fragments.Count, 5, 7); // A keyframe every 2 s over 12 s.
            Assert.InRange(copy.Layout.LongestFragmentSeconds(copy.Probe.Duration.TotalSeconds), 1.9, 2.1);
            Assert.InRange(copy.Probe.Duration.TotalSeconds, 11.5, 12.5);
            Assert.Equal((320, 180), (copy.Layout.Width, copy.Layout.Height));
            Assert.NotEqual(Path.GetDirectoryName(source), Path.GetDirectoryName(copy.FilePath));
        }
        Assert.False(File.Exists(copyPath), "Disposing the copy deletes it.");
        Assert.Equal(before, Hash(source));
    }

    [RealFfmpegFact]
    public async Task Other_codecs_and_long_keyframe_gaps_are_converted_to_H264_with_2_second_fragments()
    {
        using var media = new RealFfmpeg();
        string webm = await media.MakeAsync("phone.webm", "-f", "lavfi", "-i", "testsrc2=s=160x120:r=24:d=6", "-f", "lavfi", "-i", "sine=f=300:d=6",
            "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-c:a", "libopus", "-shortest");
        using (var copy = await media.Service.PrepareWatchCopyAsync(webm))
        {
            Assert.True(copy.Reencoded);
            Assert.StartsWith("avc1.64", copy.Layout.Codecs); // libx264 High profile.
            Assert.EndsWith("mp4a.40.2", copy.Layout.Codecs);
            Assert.InRange(copy.Layout.LongestFragmentSeconds(copy.Probe.Duration.TotalSeconds), 1.9, 2.1);
        }
        // One keyframe in 20 s would make a seek fetch the whole file, so the repackaged copy is replaced by a converted one.
        string sparse = await H264Async(media, "screen.mp4", 20, 1000);
        using var converted = await media.Service.PrepareWatchCopyAsync(sparse);
        Assert.True(converted.Reencoded);
        Assert.True(converted.Layout.LongestFragmentSeconds(20) <= 2.1, $"Longest fragment {converted.Layout.LongestFragmentSeconds(20)} s");
    }

    [RealFfmpegFact]
    public async Task Audio_only_files_become_AAC_in_2_second_fragments_without_cover_art()
    {
        using var media = new RealFfmpeg();
        string cover = await media.MakeAsync("cover.png", "-f", "lavfi", "-i", "color=c=orange:s=64x64", "-frames:v", "1");
        string mp3 = await media.MakeAsync("song.mp3", "-f", "lavfi", "-i", "sine=f=500:d=9", "-i", cover, "-map", "0:a", "-map", "1:v", "-c:a", "libmp3lame", "-c:v", "png", "-disposition:v", "attached_pic");
        using var copy = await media.Service.PrepareWatchCopyAsync(mp3);
        Assert.False(copy.Layout.HasVideo);
        Assert.Equal("mp4a.40.2", copy.Layout.Codecs);
        Assert.InRange(copy.Layout.Fragments.Count, 4, 6);
        Assert.InRange(copy.Probe.Duration.TotalSeconds, 8.5, 9.5);
    }

    [RealFfmpegFact]
    public async Task Exports_encrypted_fixtures_for_the_local_browser_check()
    {
        string? folder = Environment.GetEnvironmentVariable("KLIPPI_WATCH_FIXTURES");
        if (string.IsNullOrWhiteSpace(folder)) return; // Only when the local browser check asks for fixtures.
        using var media = new RealFfmpeg();
        var sources = new Dictionary<string, string>
        {
            ["video"] = await H264Async(media, "Kea at Arthur's Pass.mp4", 90, 60, "640x360"),
            ["converted"] = await media.MakeAsync("phone clip.webm", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=24:d=10", "-f", "lavfi", "-i", "sine=f=330:d=10",
                "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-c:a", "libopus", "-shortest"),
            ["audio"] = await media.MakeAsync("Dawn chorus.mp3", "-f", "lavfi", "-i", "sine=f=660:d=20", "-c:a", "libmp3lame"),
        };
        // Start from an empty fixture folder so links.json matches the objects next to it.
        if (Directory.Exists(Path.Combine(folder, "w"))) Directory.Delete(Path.Combine(folder, "w"), true);
        Directory.CreateDirectory(folder);
        var settings = new WatchLinkSettingsStore(Path.Combine(media.PathFor("data")), new WatchLinkSettingsStoreTests.PlainProtector());
        await settings.SaveAsync(new WatchLinkSetup(new Uri("http://127.0.0.1:9/"), "local-test", "LOCALTESTKEY0000", new Uri("http://127.0.0.1:8787/v/"), 7), "local-test-secret-0000");
        var store = new FolderObjectStore(folder);
        var service = new WatchLinkService(media.Service, settings, (_, _) => store);
        var links = new Dictionary<string, object>();
        foreach (var (name, source) in sources)
        {
            var result = await service.ShareAsync(source);
            links[name] = new { fragment = new Uri(result.ShareUrl).Fragment, sha256 = result.Sha256, source = Path.GetFileName(source) };
        }
        await File.WriteAllTextAsync(Path.Combine(folder, "links.json"), JsonSerializer.Serialize(links, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Stores objects as files under a folder, like the bucket the local test server serves.</summary>
    private sealed class FolderObjectStore(string root) : IWatchObjectStore
    {
        private readonly Dictionary<string, List<byte[]>> _parts = [];
        public int MaximumSinglePutBytes => 5 * 1024 * 1024;
        public int PartBytes => 5 * 1024 * 1024;
        private string PathFor(string key) { string path = Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); return path; }
        public Task PutObjectAsync(string key, ReadOnlyMemory<byte> body, CancellationToken cancellationToken) => File.WriteAllBytesAsync(PathFor(key), body.ToArray(), cancellationToken);
        public Task<string> StartMultipartAsync(string key, CancellationToken cancellationToken) { _parts[key] = []; return Task.FromResult(key); }
        public Task<string> UploadPartAsync(string key, string uploadId, int partNumber, ReadOnlyMemory<byte> body, CancellationToken cancellationToken) { _parts[uploadId].Add(body.ToArray()); return Task.FromResult("e" + partNumber); }
        public Task CompleteMultipartAsync(string key, string uploadId, IReadOnlyList<string> partETags, CancellationToken cancellationToken) => File.WriteAllBytesAsync(PathFor(key), _parts[uploadId].SelectMany(p => p).ToArray(), cancellationToken);
        public Task AbortMultipartAsync(string key, string uploadId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteObjectAsync(string key, CancellationToken cancellationToken) { File.Delete(PathFor(key)); return Task.CompletedTask; }
        public void Dispose() { }
    }
}
