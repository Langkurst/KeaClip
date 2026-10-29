using System.Text.RegularExpressions;

namespace Klippi.Desktop;

/// <summary>
/// The highlights of one version, read from the CHANGELOG.md that ships beside the app: the bold titles of that
/// version's entries ("**Copy clip**: ..."), without "Fixed" lines. Nothing to maintain by hand at release time.
/// </summary>
internal static partial class WhatsNew
{
    public static IReadOnlyList<string> Highlights(string changelog, string version)
    {
        var lines = changelog.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, line => line.StartsWith("## " + version + " ", StringComparison.Ordinal) || line == "## " + version);
        if (start < 0) return [];
        var titles = new List<string>();
        for (var i = start + 1; i < lines.Length && !lines[i].StartsWith("## ", StringComparison.Ordinal); i++)
            if (EntryTitle().Match(lines[i]) is { Success: true } match && !match.Groups[1].Value.StartsWith("Fixed", StringComparison.OrdinalIgnoreCase))
                titles.Add(match.Groups[1].Value.Trim());
        return titles;
    }

    /// <summary>True when the version's entry lists fixes ("**Fixed**: …").</summary>
    public static bool HasFixes(string changelog, string version)
    {
        var lines = changelog.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, line => line.StartsWith("## " + version + " ", StringComparison.Ordinal) || line == "## " + version);
        if (start < 0) return false;
        for (var i = start + 1; i < lines.Length && !lines[i].StartsWith("## ", StringComparison.Ordinal); i++)
            if (EntryTitle().Match(lines[i]) is { Success: true } match && match.Groups[1].Value.StartsWith("Fixed", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>"New: Copy clip · Share any files or folders · …", "Bug fixes and polish." for a fixes-only version,
    /// or null when the changelog is missing or says nothing.</summary>
    public static string? Summary(string version)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md");
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            var titles = Highlights(text, version);
            if (titles.Count > 0) return "New: " + string.Join(" · ", titles.Take(3));
            return HasFixes(text, version) ? "Bug fixes and polish." : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    [GeneratedRegex(@"^- \*\*([^*]+)\*\*")]
    private static partial Regex EntryTitle();
}
