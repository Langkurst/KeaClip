namespace Klippi.Core;

/// <summary>
/// Finds the loudest stretch of a recording from the waveform the Editor already drew: in a game clip that's
/// usually the action (shots, explosions, shouting). The window with the most sound energy wins, so one short spike
/// doesn't beat a sustained fight.
/// </summary>
public static class LoudestMoment
{
    public static (TimeSpan Start, TimeSpan End)? Find(IReadOnlyList<float> samples, TimeSpan duration, TimeSpan length)
    {
        if (samples.Count < 8 || duration <= length || length <= TimeSpan.Zero) return null;
        var perSample = duration.TotalSeconds / samples.Count;
        var window = Math.Clamp((int)Math.Round(length.TotalSeconds / perSample), 1, samples.Count);
        double energy = 0, best = -1; var bestStart = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            var value = float.IsFinite(samples[i]) ? samples[i] : 0;
            energy += value * value;
            if (i >= window) { var old = float.IsFinite(samples[i - window]) ? samples[i - window] : 0; energy -= old * old; }
            if (i >= window - 1 && energy > best + 1e-9) { best = energy; bestStart = i - window + 1; }
        }
        if (best <= 0) return null; // silence everywhere: nothing stands out
        var start = TimeSpan.FromSeconds(bestStart * perSample);
        var end = start + length;
        if (end > duration) { end = duration; start = duration - length; }
        return (start, end);
    }
}
