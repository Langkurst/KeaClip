using System.Globalization;
using System.Text.RegularExpressions;

namespace Klippi.Core;

/// <summary>
/// The moment a shared link points to: YouTube's ?t=90 / ?t=1m30s / &amp;start=90 / #t=1:30, Twitch's ?t=1h2m3s, and
/// the same forms elsewhere. KeaClip uses it to suggest saving just that moment.
/// </summary>
public static partial class LinkMoment
{
    private static readonly string[] Keys = ["t", "start", "time_continue"];
    private static readonly TimeSpan Longest = TimeSpan.FromHours(24);

    public static bool TryRead(string? link, out TimeSpan at)
    {
        at = default;
        if (!Uri.TryCreate(link?.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http")) return false;
        foreach (var part in (url.Query.TrimStart('?') + "&" + url.Fragment.TrimStart('#')).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2 || !Keys.Contains(Uri.UnescapeDataString(pair[0]), StringComparer.OrdinalIgnoreCase)) continue;
            if (TryParse(Uri.UnescapeDataString(pair[1]), out at) && at > TimeSpan.Zero && at < Longest) return true;
        }
        at = default;
        return false;
    }

    /// <summary>"90", "90s", "1m30s", "1h2m3s", "1:30" or "1:02:03".</summary>
    internal static bool TryParse(string text, out TimeSpan time)
    {
        time = default;
        text = text.Trim();
        if (Seconds().IsMatch(text) && double.TryParse(text.TrimEnd('s', 'S'), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        { time = TimeSpan.FromSeconds(seconds); return true; }
        if (Units().Match(text) is { Success: true } units && units.Length > 0)
        {
            int Part(string name) => units.Groups[name].Success ? int.Parse(units.Groups[name].Value, CultureInfo.InvariantCulture) : 0;
            time = new TimeSpan(Part("h"), Part("m"), Part("s")); return true;
        }
        if (Clock().Match(text) is { Success: true } clock)
        {
            var numbers = text.Split(':').Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            time = numbers.Length == 3 ? new TimeSpan(numbers[0], numbers[1], numbers[2]) : new TimeSpan(0, numbers[0], numbers[1]);
            return numbers[^1] < 60 && (numbers.Length == 2 || numbers[1] < 60);
        }
        return false;
    }

    [GeneratedRegex(@"^\d{1,6}(\.\d+)?[sS]?$")] private static partial Regex Seconds();
    [GeneratedRegex(@"^(?:(?<h>\d{1,2})h)?(?:(?<m>\d{1,3})m)?(?:(?<s>\d{1,5})s)?$", RegexOptions.IgnoreCase)] private static partial Regex Units();
    [GeneratedRegex(@"^\d{1,3}:\d{2}(:\d{2})?$")] private static partial Regex Clock();
}
