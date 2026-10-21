using System.IO.Compression;
using Klippi.Core;

namespace Klippi.Infrastructure.Sharing;

/// <summary>What was dropped on the window to share: one file goes as it is, anything else is packed into one .zip.</summary>
public sealed record ShareSelection(IReadOnlyList<string> Items, string Name, int FileCount, long Bytes)
{
    /// <summary>A single file is shared as it is; folders and several items need packing first.</summary>
    public bool NeedsPacking => Items.Count != 1 || Directory.Exists(Items[0]);
}

public static class ShareBundle
{
    /// <summary>Upper bound on files in one bundle, so a drop of a whole drive cannot run for hours.</summary>
    public const int MaxFiles = 20_000;
    private const long FreeSpaceMargin = 512L * 1024 * 1024;

    /// <summary>Counts the dropped files and their size. Links and junctions inside folders are skipped, never followed.</summary>
    public static ShareSelection Describe(IReadOnlyList<string> items, CancellationToken cancellationToken = default)
    {
        var paths = items.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) throw new MediaOperationException("Nothing to share was dropped.");
        int files = 0; long bytes = 0;
        try
        {
            foreach (var path in paths)
                foreach (var file in Files(path, cancellationToken))
                {
                    if (++files > MaxFiles) throw new MediaOperationException($"That's more than {MaxFiles:N0} files. Share a smaller folder, or zip it yourself first.");
                    bytes += file.Length;
                }
        }
        catch (UnauthorizedAccessException) { throw Unreadable(); }
        if (files == 0) throw new MediaOperationException("There are no files in what was dropped.");
        var first = Path.GetFileName(paths[0].TrimEnd(Path.DirectorySeparatorChar)); if (first.Length == 0) first = "Shared files";
        var name = paths.Length == 1 ? first : $"{first} and {paths.Length - 1} more";
        return new ShareSelection(paths, name, files, bytes);
    }

    /// <summary>Packs the selection into <paramref name="folder"/> as one .zip and returns its path. Video and other
    /// already-compressed files are stored as they are; everything else is compressed quickly.</summary>
    public static async Task<string> PackAsync(ShareSelection selection, string folder, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        var root = Path.GetPathRoot(Path.GetFullPath(folder));
        if (root is not null && new DriveInfo(root) is { IsReady: true } drive && drive.AvailableFreeSpace < selection.Bytes + FreeSpaceMargin)
            throw new MediaOperationException("There isn't enough free space on " + root.TrimEnd(Path.DirectorySeparatorChar) + " to pack these files. Free up some space and try again.");
        var zip = Path.Combine(folder, SafeName(selection.Name) + ".zip");
        var partial = zip + ".partial";
        try
        {
            long done = 0;
            await using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in selection.Items)
                {
                    var parent = Path.GetDirectoryName(item.TrimEnd(Path.DirectorySeparatorChar)) ?? item;
                    foreach (var file in Files(item, cancellationToken))
                    {
                        var entryName = Unique(used, Path.GetRelativePath(parent, file.FullName).Replace(Path.DirectorySeparatorChar, '/'));
                        var entry = archive.CreateEntry(entryName, AlreadyCompressed(file.Extension) ? CompressionLevel.NoCompression : CompressionLevel.Fastest);
                        entry.LastWriteTime = file.LastWriteTime;
                        await using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await using var target = entry.Open();
                        var buffer = new byte[1 << 16]; int read;
                        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                            done += read; progress?.Report(selection.Bytes == 0 ? 1 : (double)done / selection.Bytes);
                        }
                    }
                }
            }
            File.Move(partial, zip, overwrite: true);
            return zip;
        }
        catch (Exception error)
        {
            try { File.Delete(partial); } catch (IOException) { }
            if (error is UnauthorizedAccessException) throw Unreadable();
            throw;
        }
    }

    /// <summary>Deletes a bundle KeaClip made; anything else is left alone.</summary>
    public static void Discard(string? zip, string folder)
    {
        if (zip is null) return;
        var full = Path.GetFullPath(zip);
        if (!full.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        try { File.Delete(full); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Removes bundles left behind by a crash or power cut; run when no share is in progress.</summary>
    public static void CleanLeftovers(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
            try { File.Delete(file); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static IEnumerable<FileInfo> Files(string path, CancellationToken cancellationToken)
    {
        if (File.Exists(path)) { yield return new FileInfo(path); yield break; }
        if (!Directory.Exists(path)) throw new MediaOperationException("This item is no longer available: " + Path.GetFileName(path));
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }
    }

    private static MediaOperationException Unreadable() => new("Windows won't let KeaClip read some of these files (they may be private or in use). Share the rest, or close the app using them.");

    private static bool AlreadyCompressed(string extension) => extension.ToLowerInvariant() is
        ".mp4" or ".m4v" or ".mkv" or ".webm" or ".mov" or ".avi" or ".mp3" or ".m4a" or ".aac" or ".ogg" or ".opus" or ".flac"
        or ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".heic" or ".zip" or ".rar" or ".7z" or ".gz" or ".pdf";

    private static string Unique(HashSet<string> used, string name)
    {
        if (used.Add(name)) return name;
        var extension = Path.GetExtension(name); var stem = name[..^extension.Length];
        for (var i = 2; ; i++) if (used.Add($"{stem} ({i}){extension}")) return $"{stem} ({i}){extension}";
    }

    private static string SafeName(string name)
    {
        var cleaned = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length is 0 ? "Shared files" : cleaned.Length > 80 ? cleaned[..80].TrimEnd() : cleaned;
    }
}
