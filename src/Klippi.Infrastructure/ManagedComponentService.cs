using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Klippi.Core;
namespace Klippi.Infrastructure;

public sealed record ManagedComponent(string Name, string RelativePath, string Version, string Sha256, string SourceUrl, string Licence);
public sealed partial class ManagedComponentService(IProcessRunner runner, string componentRoot, HttpClient? http = null, string? bundledRoot = null) : IComponentService
{
    private readonly string _root = Path.GetFullPath(componentRoot);
    private readonly string _bundled = Path.GetFullPath(bundledRoot ?? Path.Combine(AppContext.BaseDirectory,"Components"));
    private readonly HttpClient _http = http ?? NewClient();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (long Length, DateTime WriteUtc, string Sha256, bool Valid)> _verified = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] Names = ["yt-dlp", "ffmpeg", "ffprobe", "deno"];
    // yt-dlp is given ffmpeg's folder and runs the ffprobe beside it, so these two
    // only ever resolve, install and restore together, from one folder.
    private static readonly string[] MediaPair = ["ffmpeg", "ffprobe"];
    private static string[] Group(string name) => MediaPair.Contains(name) ? MediaPair : [name];
    private readonly record struct Located(string Path, ManagedComponent Entry, bool Bundled);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private const string KeptActive = " The previous components remain active.";
    private static HttpClient NewClient() { var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) }; client.DefaultRequestHeaders.UserAgent.ParseAdd("KeaClip/" + AppIdentity.Version); return client; }
    /// <summary>Longest wait for any single network read; a slow but steady download is never cut off by it.</summary>
    internal TimeSpan DownloadIdleTimeout { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>Overall bound for one component download, so a drip-feeding server cannot hold an install forever.</summary>
    internal TimeSpan DownloadTotalLimit { get; set; } = TimeSpan.FromHours(4);

    public string? Find(string name) => Resolve(name)?.Path;
    private Located? Resolve(string name) => Resolution(name).Found;

    // Managed copies exist only because of an explicit Set up, Update or Restore,
    // so they normally win. A verified copy shipped with the app wins when it is a
    // strictly newer version (an app upgrade that ships newer tools), and is the
    // fallback when the managed record is unreadable or its copy fails verification.
    // A group (the ffmpeg/ffprobe pair) is usable only when every member verifies.
    // Broken: a managed copy that should be in use is missing, changed or invalid,
    // so Set up must acquire it again even though a fallback works.
    private (Located? Found, bool Broken) Resolution(string name)
    {
        if (!Names.Contains(name)) return (null, false);
        var group = Group(name);
        var managedEntries = Entries(_root);
        var managed = Source(_root, managedEntries, group, bundled: false);
        var shipped = Source(_bundled, Entries(_bundled), group, bundled: true);
        var listed = managedEntries is not null && group.Any(managedEntries.ContainsKey);
        var shippedFirst = shipped is not null && managedEntries?.GetValueOrDefault(group[0]) is { } preferred && IsNewer(shipped[0].Entry.Version, preferred.Version);
        foreach (var source in shippedFirst ? new[] { shipped, managed } : new[] { managed, shipped })
            if (source is not null && source.All(c => Verified(c.Path, c.Entry.Sha256)))
                return (source[Array.IndexOf(group, name)], listed && !shippedFirst && source != managed);
        return (null, listed && !shippedFirst);
    }
    private static Located[]? Source(string root, Dictionary<string, ManagedComponent>? entries, string[] group, bool bundled)
    {
        if (entries is null) return null;
        try
        {
            var found = new Located[group.Length];
            for (var i = 0; i < group.Length; i++)
            {
                if (!entries.TryGetValue(group[i], out var entry) || entry.Sha256 is null || Contained(root, entry.RelativePath) is not { } path) return null;
                if (group.Length > 1 && !Path.GetFileName(path).Equals(group[i] + ".exe", StringComparison.OrdinalIgnoreCase)) return null;
                found[i] = new(path, entry, bundled);
            }
            return found.Select(c => Path.GetDirectoryName(c.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? found : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }
    private static Dictionary<string, ManagedComponent>? Entries(string root)
    {
        try { return ReadManifest(Path.Combine(root, "current.json")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }
    private static string? Contained(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) return null;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? path : null;
    }
    private static bool IsNewer(string? candidate, string? current) => ParseVersion(candidate) is { } newer && ParseVersion(current) is { } older && newer > older;
    private static Version? ParseVersion(string? text) => text is not null && VersionText().Match(text) is { Success: true } match && Version.TryParse(match.Value, out var version) ? version : null;
    [GeneratedRegex(@"\d+(?:\.\d+){1,3}", RegexOptions.CultureInvariant)] private static partial Regex VersionText();

    // Hashing 20-105 MB executables on every Find froze callers, some of them on
    // the UI thread. A file is hashed again only when its path, size, last-write
    // time or expected hash changes. A mismatch is remembered too, so a corrupt
    // managed copy beside a working fallback is not hashed again on every call; a
    // read error is not remembered and is retried. Trade-off: a same-size rewrite
    // that also restores the timestamp is not noticed until the app restarts or an
    // install or rollback clears the cache. Doing that needs write access to this
    // per-user folder, which already means control of the user's account, and the
    // check always had a gap before the process launches. Install and rollback
    // still hash every candidate.
    private bool Verified(string path, string expected)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                if (_verified.TryGetValue(path, out var known) && known.Length == info.Length && known.WriteUtc == info.LastWriteTimeUtc && known.Sha256.Equals(expected, StringComparison.OrdinalIgnoreCase)) return known.Valid;
                // Writers are excluded while hashing, so the stamp matches the hashed bytes.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
                var stamp = (Length: stream.Length, WriteUtc: File.GetLastWriteTimeUtc(path));
                var valid = Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase);
                _verified[path] = (stamp.Length, stamp.WriteUtc, expected, valid);
                return valid;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _verified.TryRemove(path, out _);
        return false;
    }

    // Callers include the UI thread; first-time verification hashes large files.
    public Task<IReadOnlyList<ComponentInfo>> InspectAsync(CancellationToken cancellationToken = default) => Task.Run(() => InspectCoreAsync(cancellationToken), cancellationToken);
    private async Task<IReadOnlyList<ComponentInfo>> InspectCoreAsync(CancellationToken cancellationToken)
    {
        var results = new List<ComponentInfo>();
        foreach (var name in Names)
        {
            var path = Find(name);
            if (path is null) { results.Add(new(name, null, null, false, "Not installed or integrity check failed.")); continue; }
            try
            {
                var result = await runner.RunAsync(new(path, [name is "ffmpeg" or "ffprobe" ? "-version" : "--version"], Timeout: TimeSpan.FromSeconds(20), Interactive: true), cancellationToken: cancellationToken).ConfigureAwait(false);
                var first = (result.StandardOutput + result.StandardError).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                results.Add(new(name, path, first, result.ExitCode == 0, result.ExitCode == 0 ? null : "Version check failed."));
            }
            catch (MediaOperationException ex) { results.Add(new(name, path, null, false, ex.Message)); }
        }
        return results;
    }
    public Task ProvisionAsync(IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => Task.Run(() => InstallAsync(false, progress, cancellationToken), cancellationToken);
    public Task UpdateDownloaderAsync(IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => Task.Run(() => InstallAsync(true, progress, cancellationToken), cancellationToken);
    public Task RollbackAsync(IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default) => Task.Run(() => RollbackCoreAsync(progress, cancellationToken), cancellationToken);

    private async Task RollbackCoreAsync(IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        var activated = false;
        try
        {
            var current = Path.Combine(_root, "current.json");
            var backup = current + ".backup";
            var hasCurrent = File.Exists(current);
            if (!File.Exists(backup))
            {
                if (!hasCurrent) throw new MediaOperationException("No previous managed component version is available to roll back to.");
                // The first managed update over the shipped set leaves no backup
                // record: the previous version is the one shipped with the app.
                var restoredMessage = await RestoreShippedAsync(current, backup, progress, cancellationToken).ConfigureAwait(false);
                activated = true;
                _verified.Clear();
                progress?.Report(new(JobStage.Finished, 100, restoredMessage));
                return;
            }
            var activeBytes = hasCurrent ? await ReadRollbackManifestAsync(current, cancellationToken).ConfigureAwait(false) : null;
            var previousBytes = await ReadRollbackManifestAsync(backup, cancellationToken).ConfigureAwait(false);
            var previous = JsonSerializer.Deserialize<Dictionary<string, ManagedComponent>>(previousBytes);
            if (previous is null || previous.Count is < 1 or > 4) throw new MediaOperationException("The previous component record is incomplete. Current components remain active.");

            progress?.Report(new(JobStage.Verifying, Message: "Checking the previous component versions…"));
            var candidates = new List<(ManagedComponent Entry, string Path)>();
            foreach (var (name, entry) in previous)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateRollbackEntry(name, entry);
                var path = ResolveRollbackPath(entry);
                if (!File.Exists(path) || !string.Equals(await HashFileAsync(path, cancellationToken).ConfigureAwait(false), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new MediaOperationException("A previous component is missing or its checksum changed. Current components remain active.");
                candidates.Add((entry, path));
            }
            // Half of the ffmpeg/ffprobe pair, or a pair split across folders, would never be used (see Resolution).
            if (!WholePair(candidates)) throw new MediaOperationException("The previous component record is incomplete or invalid. Current components remain active.");
            await LaunchCheckAsync(candidates, "A previous component did not pass its launch and version check. Current components remain active.", cancellationToken).ConfigureAwait(false);
            // A candidate changed while being checked must never become active.
            foreach (var (entry, path) in candidates)
            {
                ResolveRollbackPath(entry);
                if (!string.Equals(await HashFileAsync(path, cancellationToken).ConfigureAwait(false), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new MediaOperationException("A previous component changed during verification. Current components remain active.");
            }

            string message;
            if (activeBytes is not null)
            {
                temporary = await WriteDurableAsync(".rollback-", previousBytes, cancellationToken).ConfigureAwait(false);
                var activeNow = await ReadRollbackManifestAsync(current, cancellationToken).ConfigureAwait(false);
                var previousNow = await ReadRollbackManifestAsync(backup, cancellationToken).ConfigureAwait(false);
                if (!activeBytes.AsSpan().SequenceEqual(activeNow) || !previousBytes.AsSpan().SequenceEqual(previousNow))
                    throw new MediaOperationException("The component records changed during verification. Current components remain active; refresh and try again.");
                cancellationToken.ThrowIfCancellationRequested();
                // One atomic activation; the former active manifest becomes the new
                // backup, making this switch reversible. Version folders are untouched.
                File.Replace(temporary, current, backup, ignoreMetadataErrors: true);
                temporary = null;
                message = "Previous components restored and verified. The newer versions remain available for another rollback.";
            }
            else
            {
                // No active record means the shipped versions were in use. Restoring
                // the record makes them the rollback target again (no backup record).
                var previousNow = await ReadRollbackManifestAsync(backup, cancellationToken).ConfigureAwait(false);
                if (File.Exists(current) || !previousBytes.AsSpan().SequenceEqual(previousNow))
                    throw new MediaOperationException("The component records changed during verification. Current components remain active; refresh and try again.");
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(backup, current);
                message = "Managed components restored and verified. The versions shipped with KeaClip remain available for another rollback.";
            }
            activated = true;
            _verified.Clear();
            var shadowed = candidates.Select(c => c.Entry.Name).Where(n => Resolve(n) is { Bundled: true }).ToList();
            if (shadowed.Count > 0) message += " A newer version shipped with KeaClip stays in use for " + string.Join(", ", shadowed) + ".";
            progress?.Report(new(JobStage.Finished, 100, message));
        }
        catch (JsonException) when (!activated) { throw new MediaOperationException("The previous component record could not be read. Current components remain active."); }
        catch (Exception error) when (!activated && (error is IOException or UnauthorizedAccessException))
        { throw new MediaOperationException("The previous components could not be checked or activated. Current components remain active. Close component operations and try again."); }
        finally
        {
            try { if (temporary is not null) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            finally { _gate.Release(); }
        }
    }

    // Removes managed overrides whose shipped copy verifies (hash, launch and
    // version), so Find uses the shipped copy again. The removed record becomes
    // the backup, so another rollback restores the newer managed versions.
    private async Task<string> RestoreShippedAsync(string current, string backup, IProgress<JobProgress>? progress, CancellationToken token)
    {
        var activeBytes = await ReadRollbackManifestAsync(current, token).ConfigureAwait(false);
        Dictionary<string, ManagedComponent?> active;
        try { active = JsonSerializer.Deserialize<Dictionary<string, ManagedComponent?>>(activeBytes) ?? []; }
        catch (JsonException) { throw new MediaOperationException("The active component record could not be read. Set up components to repair it."); }
        var shipped = ReadShippedManifest();
        progress?.Report(new(JobStage.Verifying, Message: "Checking the component versions shipped with KeaClip…"));
        var candidates = new List<(ManagedComponent Entry, string Path)>();
        // The ffmpeg/ffprobe pair is restored together or not at all.
        foreach (var name in active.Keys.Where(Names.Contains).SelectMany(Group).Distinct(StringComparer.Ordinal).ToList())
        {
            token.ThrowIfCancellationRequested();
            if (!shipped.TryGetValue(name, out var entry) || entry.Name != name || entry.Sha256 is null || string.IsNullOrWhiteSpace(entry.Version) || Contained(_bundled, entry.RelativePath) is not { } path) continue;
            RejectLinks(_bundled, path, "The shipped component path contains a file link. Rollback was stopped.");
            // A shipped copy that is missing or changed cannot be a rollback target; its managed copy stays.
            if (File.Exists(path) && string.Equals(await HashFileAsync(path, token).ConfigureAwait(false), entry.Sha256, StringComparison.OrdinalIgnoreCase)) candidates.Add((entry, path));
        }
        if (!WholePair(candidates)) candidates.RemoveAll(c => MediaPair.Contains(c.Entry.Name));
        if (candidates.Count == 0) throw new MediaOperationException("No previous managed component version is available to roll back to.");
        await LaunchCheckAsync(candidates, "A component shipped with KeaClip did not pass its launch and version check. Current components remain active.", token).ConfigureAwait(false);
        foreach (var (entry, path) in candidates)
            if (!string.Equals(await HashFileAsync(path, token).ConfigureAwait(false), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new MediaOperationException("A shipped component changed during verification. Current components remain active.");
        var restored = candidates.Select(c => c.Entry.Name).Where(active.ContainsKey).ToHashSet(StringComparer.Ordinal);
        var remaining = active.Where(e => e.Value is not null && !restored.Contains(e.Key)).ToDictionary(e => e.Key, e => e.Value!, StringComparer.Ordinal);
        string? temporary = null;
        try
        {
            if (remaining.Count > 0) temporary = await WriteDurableAsync(".rollback-", JsonSerializer.SerializeToUtf8Bytes(remaining, Indented), token).ConfigureAwait(false);
            var activeNow = await ReadRollbackManifestAsync(current, token).ConfigureAwait(false);
            if (File.Exists(backup) || !activeBytes.AsSpan().SequenceEqual(activeNow))
                throw new MediaOperationException("The component records changed during verification. Current components remain active; refresh and try again.");
            token.ThrowIfCancellationRequested();
            if (temporary is null) File.Move(current, backup); else File.Replace(temporary, current, backup, ignoreMetadataErrors: true);
            temporary = null;
        }
        finally
        {
            try { if (temporary is not null) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return "The versions shipped with KeaClip were restored and verified for " + string.Join(", ", restored) + ". The newer versions remain available for another rollback.";
    }

    // Neither or both of ffmpeg/ffprobe, as ffmpeg.exe and ffprobe.exe in one folder.
    private static bool WholePair(List<(ManagedComponent Entry, string Path)> candidates)
    {
        var pair = candidates.Where(c => MediaPair.Contains(c.Entry.Name)).ToList();
        return pair.Count == 0 || pair.Count == MediaPair.Length && pair.All(c => Path.GetFileName(c.Path).Equals(c.Entry.Name + ".exe", StringComparison.OrdinalIgnoreCase))
            && pair.Select(c => Path.GetDirectoryName(c.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
    }

    private async Task LaunchCheckAsync(List<(ManagedComponent Entry, string Path)> candidates, string failure, CancellationToken token)
    {
        // Check the entire set before activating any candidate.
        foreach (var (entry, path) in candidates)
        {
            var result = await runner.RunAsync(new(path, [entry.Name is "ffmpeg" or "ffprobe" ? "-version" : "--version"], Timeout: TimeSpan.FromSeconds(20), OutputLimit: 262144, Interactive: true), cancellationToken: token).ConfigureAwait(false);
            var version = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            if (result.ExitCode != 0 || version != entry.Version.Trim() || (result.StandardOutput + result.StandardError).Contains("--enable-nonfree", StringComparison.Ordinal))
                throw new MediaOperationException(failure);
        }
    }

    private async Task<string> WriteDurableAsync(string prefix, byte[] content, CancellationToken token)
    {
        var temporary = Path.Combine(_root, prefix + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(content, token).ConfigureAwait(false);
                await file.FlushAsync(token).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            return temporary;
        }
        catch
        {
            BestEffort(() => File.Delete(temporary));
            throw;
        }
    }

    private async Task<byte[]> ReadRollbackManifestAsync(string path, CancellationToken token)
    {
        RejectRollbackLinks(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length is < 2 or > 1_048_576) throw new MediaOperationException("The previous component record has an invalid size. Current components remain active.");
        var bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (await file.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0) throw new MediaOperationException("The component record changed while it was read. Current components remain active.");
        return bytes;
    }

    private static void ValidateRollbackEntry(string name, ManagedComponent? entry)
    {
        if (!Names.Contains(name) || entry is null || entry.Name != name || string.IsNullOrWhiteSpace(entry.Version) || entry.Version.Length > 512 || string.IsNullOrWhiteSpace(entry.Licence) || entry.Sha256 is not { Length: 64 } || !entry.Sha256.All(Uri.IsHexDigit))
            throw new MediaOperationException("The previous component record is incomplete or invalid. Current components remain active.");
        if (!Uri.TryCreate(entry.SourceUrl, UriKind.Absolute, out var source) || source.UserInfo.Length != 0 || source.Query.Length != 0 || source.Fragment.Length != 0)
            throw new MediaOperationException("The previous component source was not recognized. Current components remain active.");
        var trustedSource = name switch
        {
            "yt-dlp" => source.AbsoluteUri.StartsWith("https://github.com/yt-dlp/yt-dlp/releases/download/", StringComparison.Ordinal),
            "deno" => source.AbsoluteUri.StartsWith("https://github.com/denoland/deno/releases/download/", StringComparison.Ordinal),
            _ => source.AbsoluteUri == "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
        };
        if (!trustedSource)
            throw new MediaOperationException("The previous component source was not recognized. Current components remain active.");
    }

    private string ResolveRollbackPath(ManagedComponent entry)
    {
        if (string.IsNullOrWhiteSpace(entry.RelativePath) || entry.RelativePath.Contains(':')) throw new MediaOperationException("The previous component path is not valid. Current components remain active.");
        var parts = entry.RelativePath.Replace('\\', '/').Split('/');
        if (parts.Length != 3 || parts[0] != "versions" || !Guid.TryParseExact(parts[1], "N", out _) || !parts[2].Equals(entry.Name + ".exe", StringComparison.OrdinalIgnoreCase))
            throw new MediaOperationException("The previous component path is not an owned version. Current components remain active.");
        var path = ResolveOwned(entry.RelativePath);
        RejectRollbackLinks(path);
        return path;
    }

    private void RejectRollbackLinks(string path) => RejectLinks(_root, path, "The previous component path contains a file link. Rollback was stopped.");
    private static void RejectLinks(string root, string path, string message)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new MediaOperationException("The previous component path is outside its managed folder.");
        var current = root;
        if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new MediaOperationException("The component folder is a file link. Rollback was stopped.");
        foreach (var segment in Path.GetRelativePath(root, full).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new MediaOperationException(message);
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    private async Task InstallAsync(bool downloaderOnly, IProgress<JobProgress>? progress, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        string? staging = null;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new MediaOperationException("Component installation currently supports Windows x64.");
            var started = DateTime.UtcNow;
            Directory.CreateDirectory(_root);
            BestEffort(() => SweepLeftovers(started));
            var installId = Guid.NewGuid().ToString("N");
            var current = Path.Combine(_root, "current.json"); var backup = current + ".backup";
            Dictionary<string, ManagedComponent> manifest; var quarantined = false;
            try { manifest = ReadManifest(current); }
            catch (JsonException)
            {
                // An unreadable record (for example after power loss) must not block
                // repair. Keep it aside for diagnostics; Find already ignores it.
                File.Move(current, current + ".corrupt-" + installId);
                manifest = []; quarantined = true;
            }
            staging = Path.Combine(_root, ".staging-" + installId); Directory.CreateDirectory(staging);
            var acquired = new Dictionary<string, (string File, string Source, string Licence)>(StringComparer.Ordinal);
            // A managed copy that should be in use but fails verification is acquired
            // again, even when a shipped fallback works, so Set up repairs it.
            static bool Missing((Located? Found, bool Broken) state) => state.Found is null || state.Broken;
            var downloader = Resolution("yt-dlp"); var activeDownloader = downloader.Found;
            if (downloaderOnly || Missing(downloader))
            {
                progress?.Report(new(JobStage.Downloading, Message: "Checking for the latest official downloader…"));
                var asset = await GithubAssetAsync("yt-dlp/yt-dlp", "yt-dlp.exe", token).ConfigureAwait(false);
                // The installed copy is verified against this hash, so a match means no newer release.
                if (Missing(downloader) || !activeDownloader!.Value.Entry.Sha256.Equals(asset.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Report(new(JobStage.Downloading, Message: "Getting the official downloader…"));
                    var path = Path.Combine(staging, "yt-dlp.exe");
                    await DownloadVerifiedAsync(asset.Url, asset.Hash, path, progress, token).ConfigureAwait(false);
                    acquired["yt-dlp"] = (path, asset.Url, "GPL-3.0-or-later (official standalone executable)");
                }
            }
            if (!downloaderOnly && Missing(Resolution("deno")))
            {
                progress?.Report(new(JobStage.Downloading, Message: "Getting the YouTube JavaScript runtime…"));
                var asset = await GithubAssetAsync("denoland/deno", "deno-x86_64-pc-windows-msvc.zip", token).ConfigureAwait(false);
                var zip = Path.Combine(staging, "deno.zip");
                await DownloadVerifiedAsync(asset.Url, asset.Hash, zip, progress, token).ConfigureAwait(false);
                var extract = Path.Combine(staging, "deno-extracted"); ExtractVerifiedZip(zip, extract);
                var path = Path.Combine(staging, "deno.exe"); File.Move(SingleExecutable(extract, "deno"), path);
                acquired["deno"] = (path, asset.Url, "MIT plus third-party notices");
                BestEffort(() => File.Delete(zip)); BestEffort(() => Directory.Delete(extract, recursive: true));
            }
            if (!downloaderOnly && Missing(Resolution("ffmpeg")))
            {
                progress?.Report(new(JobStage.Downloading, Message: "Getting media tools…"));
                const string source = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
                var hash = (await _http.GetStringAsync(source + ".sha256", token).ConfigureAwait(false)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                    ?? throw new MediaOperationException("The publisher did not supply a component checksum. Installation was stopped." + KeptActive);
                var zip = Path.Combine(staging, "ffmpeg.zip");
                await DownloadVerifiedAsync(source, hash, zip, progress, token).ConfigureAwait(false);
                var extract = Path.Combine(staging, "ffmpeg-extracted"); ExtractVerifiedZip(zip, extract);
                foreach (var name in new[] { "ffmpeg", "ffprobe" })
                {
                    var path = Path.Combine(staging, name + ".exe"); File.Move(SingleExecutable(extract, name), path);
                    acquired[name] = (path, source, "GPL-3.0-or-later (Gyan release essentials)");
                }
                var notices = Path.Combine(staging, "ffmpeg-notices"); Directory.CreateDirectory(notices);
                foreach (var notice in Directory.GetFiles(extract, "*", SearchOption.AllDirectories).Where(p => Path.GetFileName(p).Contains("license", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).StartsWith("readme", StringComparison.OrdinalIgnoreCase)))
                    File.Copy(notice, Path.Combine(notices, Path.GetFileName(notice)), overwrite: true);
                BestEffort(() => File.Delete(zip)); BestEffort(() => Directory.Delete(extract, recursive: true));
            }
            if (acquired.Count == 0)
            {
                var version = activeDownloader?.Entry.Version?.Trim() is { Length: > 0 and <= 64 } known ? " (" + known + ")" : "";
                progress?.Report(new(JobStage.Finished, 100, downloaderOnly ? "yt-dlp is already up to date" + version + "." : "Components are already ready."));
                return;
            }
            progress?.Report(new(JobStage.Verifying, Message: "Checking component integrity and versions…"));
            var entries = new Dictionary<string, ManagedComponent>();
            foreach (var (name, value) in acquired)
            {
                token.ThrowIfCancellationRequested();
                var result = await runner.RunAsync(new(value.File, [name is "ffmpeg" or "ffprobe" ? "-version" : "--version"], Timeout: TimeSpan.FromSeconds(20), Interactive: true), cancellationToken: token).ConfigureAwait(false);
                var version = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(version) || result.StandardOutput.Contains("--enable-nonfree", StringComparison.Ordinal))
                    throw new MediaOperationException("A downloaded component did not pass verification. The previous version is still available.");
                entries[name] = new(name, "versions/" + installId + "/" + Path.GetFileName(value.File), version, HashFile(value.File), value.Source, value.Licence);
            }
            var versions = Path.Combine(_root, "versions"); Directory.CreateDirectory(versions);
            token.ThrowIfCancellationRequested();
            var activated = Path.Combine(versions, installId); Directory.Move(staging, activated); staging = null;
            // A repair that re-acquires identical binaries must not push the real
            // previous version out of the rollback record.
            var unchanged = File.Exists(current) && entries.All(e => manifest.TryGetValue(e.Key, out var old) && string.Equals(old.Sha256, e.Value.Sha256, StringComparison.OrdinalIgnoreCase));
            foreach (var entry in entries) manifest[entry.Key] = entry.Value;
            var temporary = await WriteDurableAsync(".manifest-", JsonSerializer.SerializeToUtf8Bytes(manifest, Indented), token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(current)) File.Replace(temporary, current, unchanged ? null : backup, ignoreMetadataErrors: true);
                else
                {
                    // No active record means the shipped versions were in use; they are
                    // the rollback target, so an older backup record no longer applies.
                    // A record moved aside as corrupt says nothing about that: keep the
                    // backup, the last good record, as the rollback target.
                    if (!quarantined && File.Exists(backup)) File.Delete(backup);
                    File.Move(temporary, current);
                }
            }
            finally { BestEffort(() => { if (File.Exists(temporary)) File.Delete(temporary); }); }
            _verified.Clear();
            // Activation is done; cleanup failures must not be reported as install failures.
            if (!quarantined) BestEffort(PruneVersions);
            progress?.Report(new(JobStage.Finished, 100, "All set. Paste a link to start."));
        }
        catch (Exception ex) when (FriendlyInstallFailure(ex, token.IsCancellationRequested) is { } friendly) { throw friendly; }
        finally
        {
            try { if (staging is not null && Directory.Exists(staging) && staging.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Directory.Delete(staging, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // Swept by the next install.
            finally { _gate.Release(); }
        }
    }

    private const int DiskFull = unchecked((int)0x80070070), HandleDiskFull = unchecked((int)0x80070027), SharingViolation = unchecked((int)0x80070020), LockViolation = unchecked((int)0x80070021);
    /// <summary>Plain-language message for common install failures; technical text stays in Details for diagnostics.</summary>
    internal static MediaOperationException? FriendlyInstallFailure(Exception error, bool userCancelled)
    {
        var message = error switch
        {
            MediaOperationException => null,
            OperationCanceledException => userCancelled ? null : "Checking for components timed out. Check your connection and try again.",
            HttpRequestException => "Components could not be downloaded. Check your connection and try again.",
            HttpIOException or IOException { InnerException: SocketException } => "The component download was interrupted. Check your connection and try again.",
            IOException { HResult: DiskFull or HandleDiskFull } => "Not enough disk space to set up components. Free some space and try again.",
            IOException { HResult: SharingViolation or LockViolation } => "A component file is in use, for example by security software or another KeaClip operation. Wait a moment and try again.",
            UnauthorizedAccessException => "KeaClip was not allowed to write to its component folder. Check the folder permissions or security software and try again.",
            IOException => "Component files could not be written. Check free disk space and the component folder, then try again.",
            JsonException => "The publisher's release information could not be read. Try again later.",
            _ => null
        };
        return message is null ? null : new MediaOperationException(message + KeptActive, MediaValidation.RedactDiagnostics(error.GetType().Name + ": " + error.Message), error);
    }

    private static string SingleExecutable(string folder, string name)
    {
        var found = Directory.GetFiles(folder, name + ".exe", SearchOption.AllDirectories);
        return found.Length == 1 ? found[0] : throw new MediaOperationException("The downloaded archive did not contain the expected " + name + " program." + KeptActive);
    }

    // Leftovers from an interrupted operation (crash, power loss, or cleanup that
    // a scanner blocked). Only names this service creates, only entries created
    // before this operation, never links. Runs under the operation gate and the
    // desktop app is single-instance, so no live operation owns them.
    private void SweepLeftovers(DateTime started)
    {
        if (!IsPlainDirectory(_root)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(_root))
        {
            var name = Path.GetFileName(entry);
            var folder = name.StartsWith(".staging-", StringComparison.Ordinal);
            if (!folder && !name.StartsWith(".manifest-", StringComparison.Ordinal) && !name.StartsWith(".rollback-", StringComparison.Ordinal)) continue;
            if (!Guid.TryParseExact(name[(name.IndexOf('-') + 1)..], "N", out _)) continue;
            try
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || folder != ((attributes & FileAttributes.Directory) != 0)) continue;
                if ((folder ? Directory.GetCreationTimeUtc(entry) : File.GetCreationTimeUtc(entry)) >= started) continue;
                if (folder) Directory.Delete(entry, recursive: true); else File.Delete(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // Keeps every version either record can still activate and deletes only owned
    // versions/<guid> folders that neither references. If a record cannot be read
    // nothing is pruned. A folder in use (a running download) is retried later.
    private void PruneVersions()
    {
        var versions = Path.Combine(_root, "versions");
        if (!IsPlainDirectory(_root) || !IsPlainDirectory(versions)) return;
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = Path.Combine(_root, "current.json");
        foreach (var record in new[] { current, current + ".backup" })
        {
            Dictionary<string, ManagedComponent> entries;
            try { entries = ReadManifest(record); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return; }
            // Canonical paths, so no spelling of a reference can hide a folder in use.
            foreach (var entry in entries.Values)
                if (Contained(_root, entry.RelativePath) is { } path && path.StartsWith(versions + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    referenced.Add(Path.GetRelativePath(versions, path).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])[0]);
        }
        foreach (var folder in Directory.EnumerateDirectories(versions))
        {
            var id = Path.GetFileName(folder);
            if (!Guid.TryParseExact(id, "N", out _) || referenced.Contains(id)) continue;
            try { if (IsPlainDirectory(folder)) Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    private static bool IsPlainDirectory(string path)
    {
        try { return Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    private static void BestEffort(Action cleanup)
    {
        try { cleanup(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Missing record: empty. Unreadable record: JsonException, so callers choose
    // between falling back (Find) and quarantining (install).
    private static Dictionary<string, ManagedComponent> ReadManifest(string path)
    {
        if (!File.Exists(path)) return [];
        var entries = JsonSerializer.Deserialize<Dictionary<string, ManagedComponent?>>(File.ReadAllText(path)) ?? throw new JsonException("The component record is empty.");
        return entries.Where(e => e.Value is not null).ToDictionary(e => e.Key, e => e.Value!, StringComparer.Ordinal);
    }
    private Dictionary<string, ManagedComponent> ReadShippedManifest() => Entries(_bundled) ?? [];
    private string ResolveOwned(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative));
        if (Path.IsPathRooted(relative) || !path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new MediaOperationException("Component path is not valid.");
        return path;
    }
    private async Task<(string Url, string Hash)> GithubAssetAsync(string repository, string name, CancellationToken token)
    {
        if (!_http.DefaultRequestHeaders.UserAgent.Any()) _http.DefaultRequestHeaders.UserAgent.ParseAdd("KeaClip/" + AppIdentity.Version);
        const string incomplete = "The publisher's release information was incomplete or not ready yet. Try again later." + KeptActive;
        using var release = await _http.GetFromJsonAsync<JsonDocument>("https://api.github.com/repos/" + repository + "/releases/latest", token).ConfigureAwait(false) ?? throw new MediaOperationException(incomplete);
        if (release.RootElement.ValueKind != JsonValueKind.Object || !release.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) throw new MediaOperationException(incomplete);
        var matches = assets.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object && Text(x, "name") == name).ToList();
        if (matches.Count != 1) throw new MediaOperationException(incomplete);
        var url = Text(matches[0], "browser_download_url");
        if (url is null || !url.StartsWith("https://github.com/" + repository + "/releases/download/", StringComparison.Ordinal)) throw new MediaOperationException("Component download source was not recognized.");
        var digest = Text(matches[0], "digest");
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal)) throw new MediaOperationException("The publisher did not supply a component checksum. Installation was stopped.");
        return (url, digest[7..]);
        static string? Text(JsonElement value, string property) => value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    }
    internal async Task DownloadVerifiedAsync(string url, string expected, string destination, IProgress<JobProgress>? progress, CancellationToken token)
    {
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)) throw new MediaOperationException("The component checksum is invalid.");
        // An inactivity limit rather than one total deadline: a slow but steady
        // connection can fetch the large media-tools archive, a stalled one stops
        // after one idle period, and the total cap still bounds a drip feed.
        var idleLimit = _http.Timeout == Timeout.InfiniteTimeSpan || _http.Timeout > DownloadIdleTimeout ? DownloadIdleTimeout : _http.Timeout;
        using var total = CancellationTokenSource.CreateLinkedTokenSource(token); total.CancelAfter(DownloadTotalLimit);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(total.Token); idle.CancelAfter(idleLimit);
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false); response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
            using var hasher=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920]; long received = 0; int count; var lastReport=DateTime.UtcNow;
                while (true)
                {
                    idle.CancelAfter(idleLimit);
                    if ((count = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false)) == 0) break;
                    received += count; if (received > 512L * 1024 * 1024) throw new MediaOperationException("The component download was larger than expected.");
                    await output.WriteAsync(buffer.AsMemory(0, count), idle.Token).ConfigureAwait(false);hasher.AppendData(buffer.AsSpan(0,count));
                    if(DateTime.UtcNow-lastReport>TimeSpan.FromMilliseconds(250))
                    {
                        progress?.Report(new(JobStage.Downloading, response.Content.Headers.ContentLength is > 0 ? received * 100d / response.Content.Headers.ContentLength : null, "Downloading the tools…"));lastReport=DateTime.UtcNow;
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            if (!Convert.ToHexString(hasher.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new MediaOperationException("The downloaded component did not match the publisher's checksum, so it was not installed. Try again later." + KeptActive);
        }
        catch(OperationCanceledException)when(!token.IsCancellationRequested){throw new MediaOperationException("The component download stalled and timed out. The previous components remain active; try again.");}
    }
    internal static void ExtractVerifiedZip(string source, string target)
    {
        var root = Path.GetFullPath(target); Directory.CreateDirectory(root);
        using var zip = ZipFile.OpenRead(source); long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            expanded += entry.Length;
            var path = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || expanded > 2L * 1024 * 1024 * 1024 || entry.FullName.Contains(':')) throw new MediaOperationException("The component archive contains an invalid path or size.");
            if (entry.Name.Length == 0) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); entry.ExtractToFile(path);
        }
    }
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
