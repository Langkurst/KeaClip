namespace Klippi.Core;

/// <summary>
/// Finds the loudest stretch of a recording from the waveform the Editor already drew: in a game clip that's
/// usually the action (shots, explosions, shouting). The window with the most sound energy wins, so one short spike
/// doesn't beat a sustained fight.
/// </summary>
public static class LoudestMoment
{
    /// <summary>
    /// Up to <paramref name="count"/> loud stretches that don't overlap, loudest first: press Loudest again to hop to
    /// the next one. A stretch only counts if it is clearly louder than the recording's quiet parts.
    /// </summary>
    public static IReadOnlyList<(TimeSpan Start, TimeSpan End)> FindTop(IReadOnlyList<float> samples, TimeSpan duration, TimeSpan length, int count)
    {
        if (samples.Count < 8 || duration <= length || length <= TimeSpan.Zero || count < 1) return [];
        var perSample = duration.TotalSeconds / samples.Count;
        var window = Math.Clamp((int)Math.Round(length.TotalSeconds / perSample), 1, samples.Count);
        var energy = new double[samples.Count - window + 1];
        double running = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            var value = float.IsFinite(samples[i]) ? samples[i] : 0;
            running += value * value;
            if (i >= window) { var old = float.IsFinite(samples[i - window]) ? samples[i - window] : 0; running -= old * old; }
            if (i >= window - 1) energy[i - window + 1] = running;
        }
        var typical = energy.OrderBy(value => value).ElementAt(energy.Length / 2);
        var taken = new List<int>();
        foreach (var start in Enumerable.Range(0, energy.Length).OrderByDescending(index => energy[index]))
        {
            if (taken.Count == count) break;
            if (energy[start] <= 0 || (taken.Count > 0 && energy[start] < typical * 1.5)) break; // the rest is ordinary sound
            if (taken.Any(other => Math.Abs(other - start) < window)) continue;
            // Only a peak counts: the tail of a long fight is loud too, but louder windows sit right next to it.
            var from = Math.Max(0, start - window); var to = Math.Min(energy.Length - 1, start + window);
            var peak = true;
            for (var i = from; i <= to && peak; i++) if (energy[i] > energy[start] + 1e-9) peak = false;
            if (!peak) continue;
            taken.Add(start);
        }
        return taken.Select(start =>
        {
            var from = TimeSpan.FromSeconds(start * perSample); var to = from + length;
            return to > duration ? (duration - length, duration) : (from, to);
        }).ToArray();
    }

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
