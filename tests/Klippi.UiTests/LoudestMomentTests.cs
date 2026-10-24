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
    public void Separate_loud_moments_come_back_loudest_first_and_never_overlap()
    {
        // 120 s as 600 points: a big fight at 70-90 s, a smaller one at 20-30 s, ordinary sound elsewhere.
        var samples = Quiet(600);
        for (var i = 350; i < 450; i++) samples[i] = 0.9f;
        for (var i = 100; i < 150; i++) samples[i] = 0.5f;
        var moments = LoudestMoment.FindTop(samples, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(15), 3);
        Assert.Equal(2, moments.Count); // nothing else stands out from the background
        Assert.InRange(moments[0].Start.TotalSeconds, 70, 75);
        Assert.InRange(moments[1].Start.TotalSeconds, 15, 25);
        Assert.True(moments[0].Start >= moments[1].End || moments[1].Start >= moments[0].End);
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
