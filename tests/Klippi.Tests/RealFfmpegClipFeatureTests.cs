using Klippi.Core;
using Xunit;

namespace Klippi.Tests;

// 0.5 features end to end with the real FFmpeg (set KLIPPI_FFMPEG_DIR; skipped otherwise).
[Collection(RealFfmpeg.Collection)]
public sealed class RealFfmpegClipFeatureTests
{
    [RealFfmpegFact]
    public async Task The_loudest_15_seconds_of_a_real_recording_are_where_the_noise_is()
    {
        using var media = new RealFfmpeg();
        // 60 s: quiet hiss throughout, a loud tone from 40 s to 50 s (the "fight").
        var source = await media.MakeAsync("replay.m4a",
            "-f", "lavfi", "-i", "anoisesrc=d=60:c=pink:a=0.02",
            "-f", "lavfi", "-i", "sine=f=440:d=60",
            "-filter_complex", "[1:a]volume='if(between(t,40,50),0.9,0)':eval=frame[tone];[0:a][tone]amix=inputs=2:normalize=0[out]",
            "-map", "[out]", "-c:a", "aac");
        var waveform = await media.Service.WaveformAsync(source, 600);
        var loudest = LoudestMoment.Find(waveform, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(15))!.Value;
        // Any 15-second window covering 40-50 s holds all of the tone.
        Assert.InRange(loudest.Start.TotalSeconds, 34.5, 40.5);
        Assert.InRange(loudest.End.TotalSeconds, 49.5, 55.5);
    }

    [RealFfmpegTheory]
    [InlineData(FrameShape.Vertical, 0.0, 404, 720)]
    [InlineData(FrameShape.Square, 1.0, 720, 720)]
    [InlineData(FrameShape.Original, 0.5, 1280, 720)]
    public async Task A_shaped_export_has_the_phone_shape(FrameShape shape, double position, int width, int height)
    {
        using var media = new RealFfmpeg();
        var source = await media.MakeAsync("game.mp4", "-f", "lavfi", "-i", "testsrc=size=1280x720:rate=30:duration=3", "-f", "lavfi", "-i", "sine=d=3", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest");
        var result = await media.Service.ExportAsync(new ExportOptions { InputPath = source, OutputPath = media.PathFor("shaped.mp4"), Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(2), Shape = shape, ShapePosition = position });
        Assert.Equal(width, result.Probe.Width);
        Assert.Equal(height, result.Probe.Height);
    }
}
