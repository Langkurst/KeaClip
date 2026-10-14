using Klippi.Core;
using Xunit;

namespace Klippi.Tests;

// Export as GIF with the real FFmpeg and FFprobe (opt-in through KLIPPI_FFMPEG_DIR, like the other real-media checks).
[Collection(RealFfmpeg.Collection)]
public sealed class GifExportTests
{
    [RealFfmpegFact]
    public async Task A_gif_of_the_selection_is_silent_and_at_most_480_pixels_wide()
    {
        using var media = new RealFfmpeg();
        var source = await media.MakeAsync("wide.mp4", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=6", "-f", "lavfi", "-i", "sine=f=440:d=6", "-shortest", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac");
        var result = await media.Service.ExportAsync(new ExportOptions { InputPath = source, OutputPath = media.PathFor("clip.gif"), Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(3.5), Gif = true });
        Assert.Equal("gif", result.Probe.VideoCodec);
        Assert.False(result.Probe.HasAudio);
        Assert.Equal(480, result.Probe.Width);
        Assert.Equal(270, result.Probe.Height);
        Assert.InRange(result.Probe.Duration.TotalSeconds, 2.3, 2.7);
        Assert.False(result.OriginalDeleted);
        media.AssertOnlyFiles(source, result.FilePath);
    }

    [RealFfmpegFact]
    public async Task Audio_only_files_and_selections_over_a_minute_are_refused_and_nothing_is_written()
    {
        using var media = new RealFfmpeg();
        var song = await media.MakeAsync("song.m4a", "-f", "lavfi", "-i", "sine=f=440:d=3", "-c:a", "aac");
        var audioError = await Assert.ThrowsAsync<MediaOperationException>(() => media.Service.ExportAsync(new ExportOptions { InputPath = song, OutputPath = media.PathFor("song.gif"), Gif = true }));
        Assert.Contains("audio only", audioError.Message);
        var longVideo = await media.MakeAsync("long.mp4", "-f", "lavfi", "-i", "color=c=gray:s=64x64:r=5:d=61", "-c:v", "libx264", "-pix_fmt", "yuv420p");
        var lengthError = await Assert.ThrowsAsync<MediaOperationException>(() => media.Service.ExportAsync(new ExportOptions { InputPath = longVideo, OutputPath = media.PathFor("long.gif"), Gif = true }));
        Assert.Contains("60 seconds", lengthError.Message);
        media.AssertOnlyFiles(song, longVideo);
    }
}
