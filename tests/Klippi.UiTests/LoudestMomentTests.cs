using Klippi.Core;
using Xunit;

namespace Klippi.UiTests;

// "Loudest 15 s" picks the stretch with the most sound energy from the waveform the Editor already drew.
public sealed class LoudestMomentTests
{
    private static float[] Quiet(int count) => Enumerable.Repeat(0.05f, count).ToArray();

    [Fact]
    public void A_sustained_loud_stretch_beats_a_single_spike()
    {
        // 120 s drawn as 600 points (5 per second). A lone spike at 10 s, a 20-second fight from 70 s to 90 s.
        var samples = Quiet(600);
        samples[50] = 1f;
        for (var i = 350; i < 450; i++) samples[i] = 0.8f;
        var loudest = LoudestMoment.Find(samples, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(15))!.Value;
        Assert.InRange(loudest.Start.TotalSeconds, 70, 75);
        Assert.Equal(TimeSpan.FromSeconds(15), loudest.End - loudest.Start);
    }

    [Fact]
    public void The_window_stays_inside_the_file_and_silence_finds_nothing()
    {
        var samples = Quiet(600);
        for (var i = 590; i < 600; i++) samples[i] = 1f; // loud right at the end
        var loudest = LoudestMoment.Find(samples, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(15))!.Value;
        Assert.Equal(TimeSpan.FromSeconds(120), loudest.End);
        Assert.Equal(TimeSpan.FromSeconds(105), loudest.Start);

        Assert.Null(LoudestMoment.Find(new float[600], TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(15)));
        Assert.Null(LoudestMoment.Find(Quiet(600), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15))); // shorter than the window
        Assert.Null(LoudestMoment.Find([], TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(15)));
    }
}
