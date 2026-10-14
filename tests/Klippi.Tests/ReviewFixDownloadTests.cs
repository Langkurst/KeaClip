using System.Text.Json;
using Klippi.Core;
using Klippi.Infrastructure.Engines;
using Xunit;

namespace Klippi.Tests;

// Regression tests for the final review's download findings #4 (Replace writes only the saved
// file) and #12 (the archive reference follows the history link policy).
public sealed class ReviewFixDownloadTests
{
    private const string SignedManifest = "https://manifest.googlevideo.com/api/manifest/hls_playlist/expire/1693526400/ip/203.0.113.5/id/o-AbCdEf.1/sig/AOq0QJ8wRQIgSIGVALUEAAA/playlist/index.m3u8";

    [Theory]
    [InlineData(SignedManifest, "203.0.113.5")]
    [InlineData(SignedManifest, "SIGVALUE")]
    [InlineData("https://cdn.example.com/hdnts=exp=1~acl=*~hmac=HMACSECRET/video.mp4", "HMACSECRET")]
    [InlineData("https://media.example.com/watch?v=abc&p=hunter2", "hunter2")]
    public void The_archive_link_policy_never_keeps_what_the_history_policy_removes(string link, string secret)
    {
        var uri = new Uri(link);
        Assert.DoesNotContain(secret, MediaValidation.SafeSourceUrl(uri));
        var archived = MediaValidation.ScrubPublicSourceUrl(new Uri(EngineSupport.PublicSourceUrl(uri)));
        Assert.DoesNotContain(secret, archived);
        Assert.Equal(archived, MediaValidation.ScrubPublicSourceUrl(new Uri(archived)));
    }

    [Fact]
    public void Public_identifiers_survive_the_archive_link_policy()
    {
        Assert.Equal("https://example.com/watch?v=abc&p=12&ep=3", MediaValidation.ScrubPublicSourceUrl(new Uri("https://user:pw@example.com/watch?v=abc&p=12&ep=3#key")));
    }

    [Fact]
    public async Task An_archive_of_a_signed_direct_link_keeps_no_signature_or_viewer_address_in_its_reference()
    {
        using var files = new EngineTestFiles();
        var metadata = JsonSerializer.Serialize(new { id = "clip", title = "Clip", webpage_url = SignedManifest, duration = 60, extractor_key = "Generic", formats = new[] { new { format_id = "hls", ext = "mp4", height = 720, vcodec = "avc1", acodec = "aac" } } });
        var result = await new DownloadService(Download(metadata, "mp4"), new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri(SignedManifest), OutputDirectory = files.Root, Mode = DownloadMode.Archive });
        var archive = await File.ReadAllTextAsync(Path.Combine(result.ArchiveDirectory!, "archive.json"));
        var readable = await File.ReadAllTextAsync(Path.Combine(result.ArchiveDirectory!, "archive-info.txt"));
        foreach (var text in new[] { archive, readable })
        {
            Assert.DoesNotContain("203.0.113.5", text);
            Assert.DoesNotContain("SIGVALUE", text);
        }
        using var json = JsonDocument.Parse(archive);
        Assert.StartsWith("https://manifest.googlevideo.com/api/manifest/hls_playlist/", json.RootElement.GetProperty("SourceUrl").GetString());
    }

    [Fact]
    public async Task Replace_that_cannot_write_the_saved_file_stops_before_downloading()
    {
        using var files = new EngineTestFiles();
        var saved = Path.Combine(files.Root, "saved.webm"); await File.WriteAllTextAsync(saved, "original");
        var transfers = 0;
        var process = new EngineFakeProcess((request, output, token) =>
        {
            if (request.Arguments.Contains("--no-simulate")) transfers++;
            return Download(Metadata, "mp4").RunAsync(request, output, cancellationToken: token).GetAwaiter().GetResult();
        });
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => new DownloadService(process, new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/video"), OutputDirectory = files.Root, NamingTemplate = "saved", Container = MediaContainer.Mp4, ReplaceExisting = true, ReplacePath = saved }));
        Assert.Contains("saved file is unchanged", error.Message);
        Assert.Equal(0, transfers);
        Assert.Equal("original", await File.ReadAllTextAsync(saved));
        Assert.Equal([saved], Directory.GetFiles(files.Root));
    }

    [Fact]
    public async Task Replace_that_finishes_in_another_format_saves_nothing_next_to_the_saved_file()
    {
        // Automatic can finish as WebM, so the request may replace the saved WebM; the site then
        // delivers MP4, and a second file must not appear while the user believes it was replaced.
        using var files = new EngineTestFiles();
        var saved = Path.Combine(files.Root, "saved.webm"); await File.WriteAllTextAsync(saved, "original");
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => new DownloadService(Download(Metadata, "mp4"), new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/video"), OutputDirectory = files.Root, NamingTemplate = "saved", ReplaceExisting = true, ReplacePath = saved }));
        Assert.Contains("instead of .webm", error.Message);
        Assert.Equal("original", await File.ReadAllTextAsync(saved));
        Assert.Equal([saved], Directory.GetFiles(files.Root));
    }

    [Fact]
    public async Task Replace_of_the_same_file_overwrites_it()
    {
        using var files = new EngineTestFiles();
        var saved = Path.Combine(files.Root, "saved.mp4"); await File.WriteAllTextAsync(saved, "original");
        var result = await new DownloadService(Download(Metadata, "mp4"), new EngineFakeComponents()).DownloadAsync(new DownloadOptions { Url = new Uri("https://example.com/video"), OutputDirectory = files.Root, NamingTemplate = "saved", ReplaceExisting = true, ReplacePath = saved });
        Assert.Equal(saved, result.FilePath, ignoreCase: true);
        Assert.NotEqual("original", await File.ReadAllTextAsync(saved));
    }

    private const string Metadata = """{"id":"abcdefghijk","title":"Clip","webpage_url":"https://example.com/video","duration":60,"extractor_key":"Generic","formats":[{"format_id":"137","ext":"mp4","height":1080,"vcodec":"avc1","acodec":"aac"}]}""";

    private static EngineFakeProcess Download(string metadata, string extension) => new((request, output, _) =>
    {
        if (request.Arguments.Contains("--dump-single-json")) return new ProcessResult(0, metadata, "");
        if (request.Executable == "fake-ffprobe") return new ProcessResult(0, EngineFakeProcess.ProbeJson(), "");
        var path = Path.Combine(request.WorkingDirectory!, EngineFakeProcess.Argument(request, "--output").Replace("%(ext)s", extension, StringComparison.Ordinal));
        File.WriteAllBytes(path, Enumerable.Repeat((byte)19, 512).ToArray());
        output!("KC_FILE:" + JsonSerializer.Serialize(path));
        return new ProcessResult(0, "", "");
    });
}
