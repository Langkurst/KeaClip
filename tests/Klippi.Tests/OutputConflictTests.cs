using Klippi.Core;
using Klippi.Infrastructure.Engines;
using Xunit;

namespace Klippi.Tests;

// The desktop's "Already saved" prompt and the engine's refusal share these paths, so an
// MP3 request is never stopped by a saved video of the same title (seen in a native run).
public sealed class OutputConflictTests
{
    private static DownloadOptions Options(DownloadMode mode) => new() { Url = new("https://example.com/v"), OutputDirectory = "out", Mode = mode };

    [Fact]
    public void An_mp3_download_can_only_collide_with_an_mp3()
    {
        var paths = DownloadService.PotentialOutputPaths("out", "Title [id]", Options(DownloadMode.Audio) with { AudioFormat = AudioFormat.Mp3 });
        Assert.Equal([Path.Combine("out", "Title [id].mp3")], paths);
    }

    [Fact]
    public void Video_paths_follow_the_chosen_container_and_archives_have_none()
    {
        Assert.Equal([Path.Combine("out", "T.mkv")], DownloadService.PotentialOutputPaths("out", "T", Options(DownloadMode.VideoAudio) with { Container = MediaContainer.Mkv }));
        Assert.Equal([".mp4", ".mkv", ".webm"], DownloadService.PotentialOutputPaths("out", "T", Options(DownloadMode.VideoOnly)).Select(Path.GetExtension));
        Assert.DoesNotContain(".webm", DownloadService.PotentialOutputPaths("out", "T", Options(DownloadMode.Audio)).Select(Path.GetExtension));
        Assert.Empty(DownloadService.PotentialOutputPaths("out", "T", Options(DownloadMode.Archive)));
    }

    [Fact]
    public async Task The_engine_still_refuses_an_existing_mp3_but_not_an_existing_video()
    {
        var folder = Directory.CreateTempSubdirectory("keaclip-conflict-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "T.webm"), "video");
            var mp3 = Options(DownloadMode.Audio) with { OutputDirectory = folder, AudioFormat = AudioFormat.Mp3 };
            Assert.DoesNotContain(DownloadService.PotentialOutputPaths(folder, "T", mp3), File.Exists);
            await File.WriteAllTextAsync(Path.Combine(folder, "T.mp3"), "audio");
            Assert.Contains(DownloadService.PotentialOutputPaths(folder, "T", mp3), File.Exists);
        }
        finally { Directory.Delete(folder, true); }
    }
}
