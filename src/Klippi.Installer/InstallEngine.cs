using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Klippi.Installer;

public sealed record OwnedFile(string RelativePath, string Sha256);
public sealed record InstallReceipt(string ApplicationId, int Schema, string Root, string Version, DateTimeOffset InstalledAt, IReadOnlyList<OwnedFile> Files, IReadOnlyDictionary<string, string> Shortcuts, bool Protocol, bool ProgramsEntry);
public sealed record InstallChoices(bool DesktopShortcut, bool StartMenuShortcut, bool RegisterProtocol);
public sealed record InstallResult(string Root, string? Backup, string? Warning);

public interface IInstallIntegration
{
    InstallReceipt Apply(InstallReceipt receipt, InstallChoices choices);
    void Remove(InstallReceipt receipt);
    void Rollback() { }
    void Commit() { }
}
public sealed class NoIntegration : IInstallIntegration
{
    public InstallReceipt Apply(InstallReceipt receipt, InstallChoices choices) => receipt;
    public void Remove(InstallReceipt receipt) { }
}

public sealed class InstallEngine
{
    public const string ReceiptName = ".keaclip-install.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly IInstallIntegration _integration;
    public InstallEngine(IInstallIntegration? integration = null) => _integration = integration ?? new NoIntegration();
    public Action<string>? FailurePoint { get; init; }

    public InstallResult Install(Stream payload, string target, string installerExecutable, InstallChoices choices)
    {
        target = SafeTarget(target); CheckLinks(target); EnsureAppClosed(target);
        var parent = Path.GetDirectoryName(target)!; Directory.CreateDirectory(parent);
        InstallReceipt? oldReceipt = null;
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            oldReceipt = ReadReceipt(target);
            // A modified application file must never be silently overwritten during upgrade.
            foreach (var item in oldReceipt.Files)
            {
                var path = Under(target, item.RelativePath);
                if (File.Exists(path) && Hash(path) != item.Sha256) throw new IOException("An installed application file has changed. Choose a new folder or restore that file before upgrading.");
            }
        }
        var stage = Path.Combine(parent, ".keaclip-stage-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(parent, ".keaclip-backup-" + Guid.NewGuid().ToString("N"));
        var temporaryFiles = new List<OwnedFile>(); var movedOld = false; var promoted = false; InstallReceipt? receipt = null;
        Directory.CreateDirectory(stage);
        try
        {
            if (oldReceipt is not null)
            {
                var owned = oldReceipt.Files.Select(f => f.RelativePath).Append(ReceiptName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var source in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
                {
                    CheckLinks(source); var relative = Path.GetRelativePath(target, source).Replace('\\', '/');
                    if (owned.Contains(relative)) continue;
                    var destination = Under(stage, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    try { File.Copy(source, destination, false); }
                    finally { if (File.Exists(destination)) temporaryFiles.Add(new(relative, Hash(destination))); }
                    var digest = Hash(source); if (Hash(destination) != digest) throw new IOException("A preserved file could not be copied correctly.");
                }
            }
            var package = ExtractVerified(payload, stage, temporaryFiles);
            if (!File.Exists(Under(stage, "KeaClip.exe"))) throw new InvalidDataException("The package does not contain KeaClip.exe.");
            var uninstallPath = Under(stage, "KeaClip.Uninstall.exe");
            if (File.Exists(uninstallPath)) throw new InvalidDataException("The payload conflicts with the uninstaller.");
            try { File.Copy(installerExecutable, uninstallPath); }
            finally { if (File.Exists(uninstallPath)) temporaryFiles.Add(new("KeaClip.Uninstall.exe", Hash(uninstallPath))); }
            var uninstall = new OwnedFile("KeaClip.Uninstall.exe", Hash(uninstallPath)); package.Add(uninstall);
            receipt = new("KeaClip", 1, target, typeof(InstallEngine).Assembly.GetName().Version?.ToString(3) ?? "0.0.0", DateTimeOffset.UtcNow, package, oldReceipt?.Shortcuts ?? new Dictionary<string, string>(), oldReceipt?.Protocol ?? false, oldReceipt?.ProgramsEntry ?? false);
            WriteReceipt(stage, receipt); temporaryFiles.Add(new(ReceiptName, Hash(Under(stage, ReceiptName))));
            FailurePoint?.Invoke("before-promotion");
            if (Directory.Exists(target)) { Directory.Move(target, backup); movedOld = true; }
            Directory.Move(stage, target); promoted = true;
            FailurePoint?.Invoke("after-promotion");
            receipt = _integration.Apply(receipt, choices); WriteReceipt(target, receipt);
            temporaryFiles.RemoveAll(f => f.RelativePath == ReceiptName); temporaryFiles.Add(new(ReceiptName, Hash(Under(target, ReceiptName))));
            FailurePoint?.Invoke("after-integration");
            _integration.Commit();
            var retainedBackup = movedOld ? backup : null;
            // Keep the previous complete tree as an explicit rollback backup, including unrelated files.
            return new(target, retainedBackup, retainedBackup is null ? null : "The previous installation was kept in a sibling rollback backup. Settings and unrelated files were preserved.");
        }
        catch (Exception original)
        {
            Exception? integrationFailure = null;
            if (promoted)
            {
                try { _integration.Rollback(); } catch (Exception error) { integrationFailure = error; }
                Directory.Move(target, stage); promoted = false;
            }
            if (movedOld && Directory.Exists(backup) && !Directory.Exists(target)) Directory.Move(backup, target);
            CleanupCreated(stage, temporaryFiles);
            if (integrationFailure is not null) throw new AggregateException("The application files were rolled back, but Windows shortcut or registry rollback needs review.", original, integrationFailure);
            throw;
        }
    }

    public IReadOnlyList<string> Uninstall(string target)
    {
        target = SafeTarget(target); CheckLinks(target); var receipt = ReadReceipt(target); EnsureAppClosed(target);
        var preserved = new List<string>(); var removable = new List<OwnedFile>();
        foreach (var item in receipt.Files)
        {
            var path = Under(target, item.RelativePath); CheckLinks(path);
            if (!File.Exists(path)) continue;
            if (Hash(path) != item.Sha256) { preserved.Add(item.RelativePath); continue; }
            CheckUnlocked(path); removable.Add(item);
        }
        CheckUnlocked(Under(target, ReceiptName));
        var backup = Path.Combine(Path.GetDirectoryName(target)!, ".keaclip-removal-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(backup);
        var snapshots = new List<OwnedFile>(); var removed = new List<OwnedFile>();
        try
        {
            foreach (var item in removable.Append(new(ReceiptName, Hash(Under(target, ReceiptName)))))
            {
                var copy = Under(backup, item.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                try { File.Copy(Under(target, item.RelativePath), copy, false); }
                finally { if (File.Exists(copy)) snapshots.Add(new(item.RelativePath, Hash(copy))); }
                if (Hash(copy) != item.Sha256) throw new IOException("An application file changed while removal was prepared. Nothing was removed.");
            }
            foreach (var item in removable)
            {
                var path = Under(target, item.RelativePath); CheckLinks(path);
                if (Hash(path) != item.Sha256) throw new IOException("An application file changed before removal. The operation was rolled back.");
                File.Delete(path); removed.Add(item); FailurePoint?.Invoke("during-removal");
            }
            _integration.Remove(receipt);
            if (preserved.Count == 0) { var snapshot = snapshots.Single(f => f.RelativePath == ReceiptName); File.Delete(Under(target, ReceiptName)); removed.Add(snapshot); }
            RemoveEmptyDirectories(target); _integration.Commit();
            try { CleanupCreated(backup, snapshots); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Successful removal remains committed; a locked/changed recovery backup is kept. */ }
            // No recursive file deletion: unknown, edited or local Data files always remain.
            return preserved;
        }
        catch (Exception original)
        {
            var recoveryErrors = new List<Exception>(); try { _integration.Rollback(); } catch (Exception error) { recoveryErrors.Add(error); }
            foreach (var item in removed)
            {
                try
                {
                    var restore = Under(target, item.RelativePath); var copy = Under(backup, item.RelativePath); CheckLinks(restore); CheckLinks(copy);
                    if (File.Exists(restore)) { if (Hash(restore) != item.Sha256) throw new IOException("A changed file was preserved during uninstall recovery."); continue; }
                    if (Hash(copy) != item.Sha256) throw new IOException("The removal backup changed; it was retained for review.");
                    Directory.CreateDirectory(Path.GetDirectoryName(restore)!); File.Copy(copy, restore, false);
                }
                catch (Exception error) { recoveryErrors.Add(error); }
            }
            if (recoveryErrors.Count == 0) CleanupCreated(backup, snapshots);
            else throw new AggregateException("Removal failed; its recovery backup was kept for review.", new[] { original }.Concat(recoveryErrors));
            throw;
        }
    }
    private static void CheckUnlocked(string path)
    {
        try { using var check = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
        catch (IOException error) { throw new IOException("Close KeaClip and any programs using its application files, then try again. Nothing was removed.", error); }
    }
    private static void EnsureAppClosed(string target)
    {
        if (HasRunningProcess(target)) throw new IOException("Close KeaClip and its application processes before upgrading or uninstalling. Nothing was changed.");
    }
    /// <summary>True while any process runs an executable from inside the installation folder.</summary>
    public static bool HasRunningProcess(string target)
    {
        var prefix = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? executable; try { executable = process.MainModule?.FileName; } catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { continue; }
                if (executable is not null && Path.GetFullPath(executable).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    private static List<OwnedFile> ExtractVerified(Stream input, string stage, List<OwnedFile> temporary)
    {
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, true);
        if (archive.Entries.Count > 20_000) throw new InvalidDataException("The package contains too many entries.");
        var files = archive.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')).ToArray();
        if (files.Length == 0 || files.Sum(e => e.Length) > 3_000_000_000L) throw new InvalidDataException("The package is empty or too large.");
        var first = files.Select(e => e.FullName.Replace('\\', '/').Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var prefix = first.Length == 1 && files.All(e => e.FullName.Replace('\\', '/').Contains('/')) ? first[0] + "/" : "";
        var mapped = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var original = entry.FullName.Replace('\\', '/'); ValidateRelative(original.TrimEnd('/'));
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Links and special files are not allowed in the package.");
            if (original.EndsWith('/')) continue;
            var relative = prefix.Length > 0 ? original[prefix.Length..] : original; ValidateRelative(relative);
            if (!mapped.TryAdd(relative, entry)) throw new InvalidDataException("Duplicate package path.");
        }
        if (!mapped.TryGetValue("checksums.sha256", out var manifest) || manifest.Length > 5_000_000) throw new InvalidDataException("A bounded checksum manifest is required.");
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var manifestBytes = new MemoryStream())
        {
            CopyBounded(manifest, manifestBytes, 5_000_000);
            manifestBytes.Position = 0; using var reader = new StreamReader(manifestBytes, Encoding.UTF8, true);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var match = Regex.Match(line, "^([a-fA-F0-9]{64})  (.+)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                if (!match.Success) throw new InvalidDataException("Malformed checksum manifest.");
                var relative = match.Groups[2].Value; ValidateRelative(relative);
                if (!expected.TryAdd(relative, match.Groups[1].Value.ToLowerInvariant())) throw new InvalidDataException("Duplicate manifest path.");
            }
        }
        if (expected.Count != mapped.Count - 1 || expected.Keys.Any(k => !mapped.ContainsKey(k) || k.Equals("checksums.sha256", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("The manifest must cover exactly the payload files.");
        var result = new List<OwnedFile>();
        foreach (var (relative, digest) in expected)
        {
            var entry = mapped[relative];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); using var sink = new HashWriteStream(hash);
            CopyBounded(entry, sink, 3_000_000_000L); var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (actual != digest) throw new InvalidDataException("A package checksum did not match.");
            if (relative.Equals("portable.marker", StringComparison.OrdinalIgnoreCase)) continue;
            if (relative.Equals(ReceiptName, StringComparison.OrdinalIgnoreCase) || relative.Equals(ReceiptName + ".write", StringComparison.OrdinalIgnoreCase) || relative.Equals("Data", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("Data/", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Packages must not contain installation receipts or user data.");
            var output = Under(stage, relative); if (File.Exists(output)) throw new IOException("The upgrade would overwrite an unrelated file. Choose a new folder.");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            try { using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None); CopyBounded(entry, destination, 3_000_000_000L); }
            finally { if (File.Exists(output)) temporary.Add(new(relative, Hash(output))); }
            var record = new OwnedFile(relative, digest); if (Hash(output) != digest) throw new IOException("An extracted file failed verification."); result.Add(record);
        }
        return result;
    }
    private static void CopyBounded(ZipArchiveEntry entry, Stream destination, long maximum)
    {
        using var source = entry.Open(); var buffer = new byte[81920]; long total = 0; int read;
        while ((read = source.Read(buffer)) > 0) { total = checked(total + read); if (total > maximum || total > entry.Length) throw new InvalidDataException("A ZIP entry exceeds its declared safe size."); destination.Write(buffer, 0, read); }
        if (total != entry.Length) throw new InvalidDataException("A ZIP entry is truncated.");
    }
    private sealed class HashWriteStream(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);
    }
    public static InstallReceipt ReadReceipt(string target)
    {
        var path = Under(target, ReceiptName); if (!File.Exists(path) || new FileInfo(path).Length > 5_000_000) throw new IOException("This folder is not a receipt-backed KeaClip installation. Choose a new empty folder.");
        var receipt = JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("The installation receipt is invalid.");
        if (receipt.ApplicationId != "KeaClip" || receipt.Schema != 1 || !Path.GetFullPath(receipt.Root).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase) || receipt.Files.Count > 20_000 || receipt.Files.Count == 0) throw new InvalidDataException("The installation receipt does not belong to this folder.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in receipt.Files) { ValidateRelative(item.RelativePath); if (!Regex.IsMatch(item.Sha256, "^[a-f0-9]{64}$") || !names.Add(item.RelativePath) || item.RelativePath.Equals(ReceiptName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The receipt contains an invalid owned-file entry."); }
        return receipt;
    }
    public static void WriteReceipt(string target, InstallReceipt receipt)
    {
        var path = Under(target, ReceiptName); var temporary = path + ".write";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(receipt, Json), new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    /// <summary>Where an installed KeaClip keeps its own state (portable copies keep a Data folder beside the app instead).</summary>
    public static string DefaultDataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeaClip");
    // Parts KeaClip downloads or makes for itself and can always fetch again: media tools, update downloads, temporary work.
    private static readonly string[] DownloadedParts = ["Components", "Updates", "TemporaryWorkspaces"];
    // The user's settings and history (with KeaClip's own backup and set-aside copies of them).
    private static readonly string[] StateFiles = ["settings.json", "history.json", "queue.json", "update-state.json", "watch-links.json", "watch-usage.json", "beta-test.json"];

    /// <summary>
    /// Uninstall clean-up of the data folder: always the downloaded parts; settings and history only when the user
    /// ticks that. Only these known names are removed, linked paths are refused, and anything else (which KeaClip
    /// never puts there, such as media) is left alone, so the folder itself goes only once it is empty.
    /// </summary>
    public static IReadOnlyList<string> RemoveAppData(string dataRoot, bool settingsToo)
    {
        var root = SafeTarget(dataRoot);
        if (!Directory.Exists(root)) return [];
        CheckLinks(root);
        var removed = new List<string>();
        foreach (var name in DownloadedParts)
        {
            var path = Path.Combine(root, name);
            if (!Directory.Exists(path)) continue;
            CheckLinks(path);
            Directory.Delete(path, recursive: true); removed.Add(name);
        }
        if (settingsToo)
        {
            foreach (var file in Directory.EnumerateFiles(root))
            {
                var name = Path.GetFileName(file);
                if (!StateFiles.Any(state => name.Equals(state, StringComparison.OrdinalIgnoreCase) || name.Equals(state + ".backup", StringComparison.OrdinalIgnoreCase) || name.StartsWith(state + ".corrupt-", StringComparison.OrdinalIgnoreCase))) continue;
                File.Delete(file); removed.Add(name);
            }
            if (!Directory.EnumerateFileSystemEntries(root).Any()) { Directory.Delete(root); removed.Add("."); }
        }
        return removed;
    }

    public static string SafeTarget(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(path) || full.StartsWith("\\\\", StringComparison.Ordinal) || full.Length < 8 || full.Equals(Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose a dedicated local application folder.");
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows) && (full.Equals(windows, StringComparison.OrdinalIgnoreCase) || full.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) throw new IOException("The Windows system folder cannot be used.");
        if (full.Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose a dedicated application folder, not your home folder.");
        return full;
    }
    public static string Under(string root, string relative)
    {
        ValidateRelative(relative); var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar); var path = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("A path escaped its application folder."); return path;
    }
    public static void ValidateRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Any(char.IsControl) || Path.IsPathRooted(path) || path.Contains(':') || path.Contains('\\')) throw new InvalidDataException("Unsafe relative path.");
        foreach (var segment in path.Split('/')) if (segment is "" or "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.') || segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 || Regex.IsMatch(segment, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)", RegexOptions.IgnoreCase)) throw new InvalidDataException("Unsafe package filename.");
    }
    public static void CheckLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current) ?? "")
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked/reparse paths are not accepted for installation or removal.");
        if (Directory.Exists(path)) foreach (var child in Directory.EnumerateFileSystemEntries(path)) { if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked/reparse paths are not accepted."); if (Directory.Exists(child)) CheckLinks(child); }
    }
    private static void CleanupCreated(string root, IEnumerable<OwnedFile> created)
    {
        if (!Directory.Exists(root)) return; CheckLinks(root);
        foreach (var item in created.Reverse()) { var path = Under(root, item.RelativePath); if (File.Exists(path) && Hash(path) == item.Sha256) File.Delete(path); }
        RemoveEmptyDirectories(root);
    }
    private static void RemoveEmptyDirectories(string root)
    {
        if (!Directory.Exists(root)) return; CheckLinks(root);
        foreach (var directory in Directory.EnumerateDirectories(root)) RemoveEmptyDirectories(directory);
        if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root, false);
    }
}
