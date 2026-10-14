namespace Klippi.Infrastructure;

/// <summary>A recent video recording found on this PC (game clips, screen captures).</summary>
public sealed record RecentRecording(string Path, string Source, long SizeBytes, DateTimeOffset Modified);

/// <summary>
/// Finds the newest video recordings where Windows and common recorders save them: Xbox Game Bar (Videos\Captures),
/// AMD ReLive (Videos\Radeon ReLive\&lt;game&gt;), NVIDIA (Videos\&lt;game&gt;) and OBS (Videos). Only lists files on this PC;
/// it never opens, moves or uploads anything. The walk is shallow and bounded so a huge Videos folder stays quick.
/// </summary>
public static class RecentRecordings
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".mov", ".webm", ".avi" };
    private const int MaxEntriesVisited = 4000;
    /// <summary>Recordings smaller than this are still being written or are empty.</summary>
    private const long MinimumBytes = 100 * 1024;

    public static IReadOnlyList<RecentRecording> Find(string videosFolder, DateTimeOffset now, int max = 5, TimeSpan? within = null, CancellationToken cancellationToken = default)
    {
        var window = within ?? TimeSpan.FromDays(21);
        var found = new List<RecentRecording>();
        if (string.IsNullOrWhiteSpace(videosFolder) || !Directory.Exists(videosFolder)) return found;
        var visited = 0;
        // Videos itself, each folder in it, and one more level (AMD ReLive keeps a folder per game).
        var folders = new List<(string Path, int Depth)> { (videosFolder, 0) };
        for (var i = 0; i < folders.Count && visited < MaxEntriesVisited; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (folder, depth) = folders[i];
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(folder).EnumerateFileSystemInfos(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { continue; }
            try
            {
                foreach (var entry in entries)
                {
                    if (++visited > MaxEntriesVisited) break;
                    // Links and hidden or system items are skipped: only real folders and files the user can see.
                    if ((entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    if (entry is DirectoryInfo directory) { if (depth < 2) folders.Add((directory.FullName, depth + 1)); continue; }
                    if (entry is not FileInfo file || !VideoExtensions.Contains(file.Extension) || file.Length < MinimumBytes) continue;
                    var modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
                    if (now - modified > window || modified > now + TimeSpan.FromMinutes(5)) continue;
                    found.Add(new RecentRecording(file.FullName, SourceName(videosFolder, file.FullName), file.Length, modified));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* A folder that changes or locks mid-walk is skipped. */ }
        }
        return found.OrderByDescending(recording => recording.Modified).Take(max).ToArray();
    }

    /// <summary>Where a recording came from, in words people know.</summary>
    public static string SourceName(string videosFolder, string path)
    {
        var relative = System.IO.Path.GetRelativePath(videosFolder, path);
        var first = relative.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)[0];
        if (relative == System.IO.Path.GetFileName(path)) return "Videos";
        return first.ToLowerInvariant() switch
        {
            "captures" => "Xbox Game Bar",
            "radeon relive" => "AMD ReLive",
            _ => first,
        };
    }
}
