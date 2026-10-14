using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using Klippi.Core;

namespace Klippi.Infrastructure;

public sealed class OwnedTemporaryWorkspaceService(string dataRoot) : ITemporaryWorkspaceService
{
    internal const string ReceiptName = ".keaclip-workspace.json";
    internal const string LockName = ".keaclip-workspace.lock";
    private const int ReceiptLimit = 65_536, CandidateLimit = 2000;
    private readonly string _registry = System.IO.Path.Combine(System.IO.Path.GetFullPath(dataRoot), "TemporaryWorkspaces");
    private readonly SemaphoreSlim _cleanupGate = new(1, 1);
    private static readonly AsyncLocal<Lease?> Ambient = new();
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, Converters = { new JsonStringEnumConverter() } };
    internal static OwnedTemporaryWorkspaceService Shared { get; } = new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KeaClipWorkspaceData"));
    internal static void RegisterWriterJob(string name)
    {
        if (!ValidJob(name)) throw new InvalidDataException("An engine job identity could not be verified.");
        for (var lease = Ambient.Value; lease is not null; lease = lease.Previous) lease.RecordWriter(name);
    }
    private static bool ValidJob(string name) => name.StartsWith("KeaClip.WorkspaceJob.", StringComparison.Ordinal) && Guid.TryParseExact(name["KeaClip.WorkspaceJob.".Length..], "N", out _);

    public ITemporaryWorkspace Create(string parent)
    {
        FileStream? registryLock = null, folderLock = null;
        Anchors? folderAnchors = null, registryAnchors = null;
        try
        {
            var root = System.IO.Path.GetFullPath(parent);
            VerifyPath(root); Directory.CreateDirectory(root); VerifyPath(root);
            registryAnchors = AnchorParents(_registry, create: true);
            var owner = RegistryOwner();
            var id = Guid.NewGuid().ToString("N");
            var folder = System.IO.Path.Combine(root, ".keaclip-work-" + id);
            Directory.CreateDirectory(folder);
            folderAnchors = AnchorParents(folder);
            var receipt = new Receipt { Id = id, Folder = folder, Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), RegistryNonce = owner.Nonce, DirectoryIdentity = folderAnchors.Leaf.Identity, CreatedAtUtc = DateTimeOffset.UtcNow, Disposition = Disposition.Active };
            registryLock = Lock(System.IO.Path.Combine(_registry, id + ".lock"), FileMode.CreateNew);
            folderLock = Lock(System.IO.Path.Combine(folder, LockName), FileMode.CreateNew);
            Write(System.IO.Path.Combine(folder, ReceiptName), receipt);
            Write(System.IO.Path.Combine(_registry, id + ".json"), receipt);
            return new Lease(this, receipt, registryLock, folderLock, folderAnchors, registryAnchors);
        }
        catch
        {
            folderLock?.Dispose(); registryLock?.Dispose(); folderAnchors?.Dispose(); registryAnchors?.Dispose();
            // A failed receipt write leaves an unregistered folder. Preserve it
            // rather than treating a filename prefix as evidence of ownership.
            throw new MediaOperationException("A safe temporary workspace could not be prepared. Existing files were kept.");
        }
    }

    public async Task<TemporaryCleanupResult> CleanAsync(CancellationToken cancellationToken = default)
    {
        var result = new Counts();
        try
        {
            await _cleanupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var registryAnchors = AnchorParents(_registry, create: true);
                var owner = RegistryOwner();
                var candidates = new List<Receipt>();
                foreach (var record in Directory.EnumerateFiles(_registry, "*.json").Take(CandidateLimit + 1))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (System.IO.Path.GetFileName(record) == "owner.json") continue;
                    if (candidates.Count == CandidateLimit) { result.Skip("Further workspace records were kept because the cleanup limit was reached."); break; }
                    try
                    {
                        var receipt = Read<Receipt>(record);
                        Validate(receipt, record, owner);
                        candidates.Add(receipt);
                    }
                    catch (Exception error) when (SafeFailure(error)) { result.Skip("An unreadable or unverified workspace record was kept."); }
                }
                // Nested engine staging may have its own receipt. Remove a
                // verified child before considering its parent's flat contents.
                foreach (var receipt in candidates.OrderByDescending(item => item.Folder.Length))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileStream? control = null, folderLock = null;
                    Anchors? anchors = null;
                    try
                    {
                        VerifyPath(_registry); VerifyPath(receipt.Folder);
                        control = Lock(System.IO.Path.Combine(_registry, receipt.Id + ".lock"), FileMode.Open);
                        var current = Read<Receipt>(RecordPath(receipt)); Validate(current, RecordPath(receipt), owner);
                        if (current != receipt) { result.Skip("A workspace changed during cleanup and was kept."); continue; }
                        if (receipt.Disposition is Disposition.Recovery or Disposition.PromotionPending) { result.Recovery++; continue; }
                        if (receipt.WriterJobs.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(WindowsChildLifetime.IsJobActive)) { result.Active++; continue; }
                        if (!Directory.Exists(receipt.Folder)) { File.Delete(RecordPath(receipt)); continue; }
                        anchors = AnchorParents(receipt.Folder);
                        if (anchors.Leaf.Identity != receipt.DirectoryIdentity) { result.Skip("A workspace directory identity changed and was kept."); continue; }
                        folderLock = Lock(System.IO.Path.Combine(receipt.Folder, LockName), FileMode.Open);
                        var marker = Read<Receipt>(System.IO.Path.Combine(receipt.Folder, ReceiptName));
                        if (marker != receipt) { result.Skip("A workspace ownership receipt did not match and was kept."); continue; }
                        CleanFolder(receipt, anchors, ref folderLock, result, allowCompletedBackups: false, cancellationToken);
                    }
                    catch (IOException error) when (SharingViolation(error)) { result.Active++; }
                    catch (Exception error) when (SafeFailure(error)) { result.Skip("A workspace was busy, changed, or could not be verified; it was kept."); }
                    finally { folderLock?.Dispose(); anchors?.Dispose(); control?.Dispose(); if (!File.Exists(RecordPath(receipt))) TryDeleteControl(receipt.Id); }
                }
            }
            finally { _cleanupGate.Release(); }
        }
        catch (OperationCanceledException) { result.Cancelled = true; }
        catch (Exception error) when (SafeFailure(error)) { result.Skip("Temporary workspace records could not be verified. They were kept."); }
        return result.Result();
    }

    private void CleanFolder(Receipt receipt, Anchors anchors, ref FileStream? folderLock, Counts counts, bool allowCompletedBackups, CancellationToken token)
    {
        var entries = Directory.EnumerateFileSystemEntries(receipt.Folder).Take(CandidateLimit + 1).ToArray();
        if (entries.Length > CandidateLimit) { counts.Skip("A workspace exceeded the bounded cleanup limit and was kept."); return; }
        var expected = ExpectedFiles(receipt).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) { counts.Skip("A workspace with unfamiliar folders or file links was kept."); return; }
            var name = System.IO.Path.GetFileName(entry);
            if (name.StartsWith(".restore-", StringComparison.Ordinal))
            {
                if (!allowCompletedBackups || !expected.Contains(name)) { counts.Recovery++; return; }
                if (!Guid.TryParseExact(name[".restore-".Length..], "N", out _)) { counts.Skip("Unverified recovery files were kept."); return; }
            }
            else if (!ControlFile(name) && !expected.Contains(name) && !DownloadPayload(name, receipt.DownloadBaseName)) { counts.Skip("A workspace with unfamiliar files was kept."); return; }
        }
        // Open all payloads exclusively before deleting any: a surviving writer
        // or unreadable file must preserve the entire candidate, not half of it.
        var files = new List<CheckedFile>();
        try
        {
            foreach (var entry in entries.Where(path => !ControlFile(System.IO.Path.GetFileName(path)))) { token.ThrowIfCancellationRequested(); files.Add(new CheckedFile(entry)); }
            foreach (var file in files) { token.ThrowIfCancellationRequested(); var bytes = file.Length; file.Delete(); counts.Bytes += bytes; }
        }
        finally { foreach (var file in files) file.Dispose(); }
        token.ThrowIfCancellationRequested();
        folderLock?.Dispose(); folderLock = null;
        // Directory identity anchors stay held through every payload deletion.
        // Final removal opens that same identity for deletion without recursion.
        using (var marker = new CheckedFile(System.IO.Path.Combine(receipt.Folder, ReceiptName))) marker.Delete();
        using (var control = new CheckedFile(System.IO.Path.Combine(receipt.Folder, LockName))) control.Delete();
        try { anchors.DeleteLeaf(receipt.DirectoryIdentity); }
        catch
        {
            // A late writer may create another file before the empty-directory
            // deletion. Restore controls for a verified retry, never recurse.
            if (Directory.Exists(receipt.Folder))
            {
                using var restored = AnchorParents(receipt.Folder);
                if (restored.Leaf.Identity == receipt.DirectoryIdentity) { Write(System.IO.Path.Combine(receipt.Folder, ReceiptName), receipt); using var guard = Lock(System.IO.Path.Combine(receipt.Folder, LockName), FileMode.OpenOrCreate); }
            }
            throw;
        }
        File.Delete(RecordPath(receipt)); counts.Deleted++;
    }

    internal static bool ControlFile(string name) => name is ReceiptName or LockName;
    // The names yt-dlp derives from the registered base name inside an app-owned job folder:
    // "<base>.<ext>", "<base>.temp.<ext>" and "<base>.f<format id>.<ext>" streams (format IDs
    // such as 137, hls-1080p or 251-drc), each optionally as ".part", ".ytdl" or a fragment
    // ".part-Frag<n>" / ".part-Frag<n>.part", plus thumbnails, subtitles, ".info.json",
    // ".description" and the metadata step's chapter file ".meta". Anything else keeps the folder.
    internal static bool DownloadPayload(string name, string baseName)
    {
        if (baseName.Length == 0 || !name.StartsWith(baseName + ".", StringComparison.Ordinal)) return false;
        var suffix = name[(baseName.Length + 1)..];
        if (suffix is "info.json" or "description" or "meta") return true;
        var fragment = suffix.IndexOf(".part-Frag", StringComparison.Ordinal);
        if (fragment >= 0)
        {
            var number = suffix[(fragment + ".part-Frag".Length)..];
            if (number.EndsWith(".part", StringComparison.Ordinal)) number = number[..^".part".Length];
            if (number.Length == 0 || !number.All(char.IsAsciiDigit) || !int.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)) return false;
            suffix = suffix[..fragment];
        }
        else if (suffix.EndsWith(".part", StringComparison.Ordinal)) suffix = suffix[..^5];
        else if (suffix.EndsWith(".ytdl", StringComparison.Ordinal)) suffix = suffix[..^5];
        var dot = suffix.LastIndexOf('.'); var middle = dot < 0 ? "" : suffix[..dot]; var extension = dot < 0 ? suffix : suffix[(dot + 1)..];
        if (new[] { "mp4", "mkv", "webm", "mov", "avi", "flv", "ts", "m4v", "mp3", "m4a", "aac", "opus", "ogg", "flac", "wav" }.Contains(extension)) return middle.Length == 0 || middle == "temp" || FormatIdMiddle(middle);
        if (new[] { "jpg", "jpeg", "png", "webp", "gif" }.Contains(extension)) return middle.Length == 0 || middle.All(char.IsAsciiDigit);
        return new[] { "vtt", "srt", "ass", "ttml", "srv1", "srv2", "srv3" }.Contains(extension) && middle.Length <= 100 && middle.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }
    // "f" followed by a yt-dlp format ID of 1 to 64 letters, digits, dots, dashes or underscores.
    private static bool FormatIdMiddle(string middle) => middle.Length is >= 2 and <= 65 && middle[0] == 'f' && middle.AsSpan(1).IndexOfAnyExcept(FormatIdCharacters) < 0;
    private static readonly System.Buffers.SearchValues<char> FormatIdCharacters = System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-");
    private static bool ValidPayloadName(string name) => !string.IsNullOrEmpty(name) && name.Length <= 255 && name is not "." and not ".." && name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0 && !name.Any(char.IsControl);
    private static string[] ExpectedFiles(Receipt receipt)
    {
        if (receipt.ExpectedFiles.Length > 16_384) throw new InvalidDataException();
        var names = JsonSerializer.Deserialize<string[]>(receipt.ExpectedFiles) ?? throw new InvalidDataException();
        if (names.Length > 128 || names.Any(name => name is null || !ValidPayloadName(name)) || names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw new InvalidDataException();
        return names;
    }
    private string RecordPath(Receipt receipt) => System.IO.Path.Combine(_registry, receipt.Id + ".json");
    private Owner RegistryOwner()
    {
        var path = System.IO.Path.Combine(_registry, "owner.json");
        if (!File.Exists(path))
        {
            try { Write(path, new Owner { Registry = _registry, Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant() }, replace: false); }
            catch (IOException) when (File.Exists(path)) { /* Another instance created the owner. Verify its receipt below. */ }
        }
        var owner = Read<Owner>(path);
        if (owner.Schema != 1 || owner.Registry != _registry || !Hex(owner.Nonce, 64)) throw new InvalidDataException();
        return owner;
    }
    private static void Validate(Receipt receipt, string recordPath, Owner owner)
    {
        if (receipt.Schema != 1 || !Guid.TryParseExact(receipt.Id, "N", out _) || receipt.Id != System.IO.Path.GetFileNameWithoutExtension(recordPath) || !Hex(receipt.Nonce, 64) || receipt.RegistryNonce != owner.Nonce || !Enum.IsDefined(receipt.Disposition) || receipt.CreatedAtUtc.Offset != TimeSpan.Zero || string.IsNullOrEmpty(receipt.Folder)) throw new InvalidDataException();
        var folder = System.IO.Path.GetFullPath(receipt.Folder);
        if (folder != receipt.Folder || System.IO.Path.GetFileName(folder) != ".keaclip-work-" + receipt.Id || System.IO.Path.GetPathRoot(folder) == folder) throw new InvalidDataException();
        if (OperatingSystem.IsWindows() && !Hex(receipt.DirectoryIdentity!, 24) || receipt.WriterJobs is null || receipt.WriterJobs.Length > 4096 || receipt.WriterJobs.Split(';', StringSplitOptions.RemoveEmptyEntries) is { } jobs && (jobs.Length > 64 || jobs.Distinct(StringComparer.Ordinal).Count() != jobs.Length || jobs.Any(job => !ValidJob(job)))) throw new InvalidDataException();
        if (receipt.DownloadBaseName is null || receipt.DownloadBaseName.Length > 0 && !ValidPayloadName(receipt.DownloadBaseName) || receipt.ExpectedFiles is null) throw new InvalidDataException();
        ExpectedFiles(receipt);
    }
    private static bool Hex(string value, int length) => value is not null && value.Length == length && value.All(Uri.IsHexDigit);
    private static FileStream Lock(string path, FileMode mode) { VerifyPath(path); return new FileStream(path, mode, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough); }
    private static T Read<T>(string path)
    {
        VerifyPath(path); using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > ReceiptLimit) throw new InvalidDataException();
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
        using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        if (parsed.RootElement.ValueKind != JsonValueKind.Object || parsed.RootElement.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() > 1)) throw new InvalidDataException();
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException();
    }
    private static void Write<T>(string path, T value, bool replace = true)
    {
        VerifyPath(path); var temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, ".receipt-write-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { JsonSerializer.Serialize(file, value, Json); file.Flush(true); }
            File.Move(temporary, path, replace);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private void TryDeleteControl(string id) { try { VerifyPath(_registry); File.Delete(System.IO.Path.Combine(_registry, id + ".lock")); } catch (Exception error) when (SafeFailure(error)) { } }
    private static bool SafeFailure(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;
    private static bool SharingViolation(IOException error) => (error.HResult & 0xffff) is 32 or 33;
    private static void VerifyPath(string path)
    {
        var full = System.IO.Path.GetFullPath(path); var root = System.IO.Path.GetPathRoot(full)!; var current = root;
        foreach (var segment in full[root.Length..].Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("A workspace path is a file link.");
        }
    }
    private static Anchors AnchorParents(string path, bool create = false)
    {
        VerifyPath(path); if (create) Directory.CreateDirectory(path); VerifyPath(path);
        return new Anchors(path);
    }
    internal enum Disposition { Active, Completed, Recovery, PromotionPending }
    private sealed record Owner { public int Schema { get; init; } = 1; public string Registry { get; init; } = ""; public string Nonce { get; init; } = ""; }
    internal sealed record Receipt
    {
        public int Schema { get; init; } = 1; public string Id { get; init; } = ""; public string Folder { get; init; } = ""; public string Nonce { get; init; } = ""; public string RegistryNonce { get; init; } = ""; public string? DirectoryIdentity { get; init; }
        public DateTimeOffset CreatedAtUtc { get; init; } public Disposition Disposition { get; init; }
        public string WriterJobs { get; init; } = "";
        public string ExpectedFiles { get; init; } = "[]";
        public string DownloadBaseName { get; init; } = "";
    }
    private sealed class Counts
    {
        internal int Deleted, Active, Recovery, Skipped; internal long Bytes; internal bool Cancelled; private readonly List<string> _warnings = [];
        internal void Skip(string message) { Skipped++; if (_warnings.Count < 20) _warnings.Add(message); }
        internal TemporaryCleanupResult Result() => new() { DeletedFolders = Deleted, DeletedBytes = Bytes, ActiveFolders = Active, RecoveryFolders = Recovery, SkippedFolders = Skipped, Warnings = _warnings.ToArray(), Cancelled = Cancelled };
    }
    internal sealed class Lease : ITemporaryWorkspace
    {
        private readonly OwnedTemporaryWorkspaceService _owner; private Receipt _receipt; private FileStream? _control, _folderLock; private Anchors? _anchors; private readonly Anchors _registryAnchors; private bool _disposed;
        internal Lease? Previous { get; }
        public string Path => _receipt.Folder;
        internal Lease(OwnedTemporaryWorkspaceService owner, Receipt receipt, FileStream control, FileStream folderLock, Anchors anchors, Anchors registryAnchors) { _owner = owner; _receipt = receipt; _control = control; _folderLock = folderLock; _anchors = anchors; _registryAnchors = registryAnchors; Previous = Ambient.Value; Ambient.Value = this; }
        internal void RecordWriter(string name)
        {
            lock (this)
            {
                if (_disposed) return;
                var jobs = _receipt.WriterJobs.Split(';', StringSplitOptions.RemoveEmptyEntries);
                if (jobs.Contains(name, StringComparer.Ordinal)) return;
                if (jobs.Length >= 64) throw new MediaOperationException("The temporary workspace writer limit was reached. Retry as a separate job.");
                _receipt = _receipt with { WriterJobs = string.Join(';', jobs.Append(name)) };
                Write(_owner.RecordPath(_receipt), _receipt); Write(System.IO.Path.Combine(Path, ReceiptName), _receipt);
            }
        }
        public void RegisterPayload(string fileName)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!ValidPayloadName(fileName) || ControlFile(fileName)) throw new MediaOperationException("A generated temporary filename could not be verified.");
            var names = ExpectedFiles(_receipt); if (names.Contains(fileName, StringComparer.Ordinal)) return;
            if (names.Length >= 128) throw new MediaOperationException("The temporary workspace file limit was reached.");
            _receipt = _receipt with { ExpectedFiles = JsonSerializer.Serialize(names.Append(fileName).Order(StringComparer.Ordinal).ToArray()) }; Save();
        }
        public void RegisterDownloadBaseName(string baseName)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!ValidPayloadName(baseName) || _receipt.DownloadBaseName.Length > 0 && _receipt.DownloadBaseName != baseName) throw new MediaOperationException("The download temporary filename could not be verified.");
            _receipt = _receipt with { DownloadBaseName = baseName }; Save();
        }
        private void Save() { Write(_owner.RecordPath(_receipt), _receipt); Write(System.IO.Path.Combine(Path, ReceiptName), _receipt); }
        private void SetDisposition(Disposition disposition)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); _receipt = _receipt with { Disposition = disposition };
            // Registry disposition first: interrupted mirrored writes fail closed.
            Write(_owner.RecordPath(_receipt), _receipt); Write(System.IO.Path.Combine(Path, ReceiptName), _receipt);
        }
        public void PreserveForRecovery() => SetDisposition(Disposition.Recovery);
        public void PrepareForPromotion()
        {
            SetDisposition(Disposition.PromotionPending);
            _folderLock?.Dispose(); _folderLock = null;
            using (var receipt = new CheckedFile(System.IO.Path.Combine(Path, ReceiptName))) receipt.Delete();
            using (var control = new CheckedFile(System.IO.Path.Combine(Path, LockName))) control.Delete();
            // Keep the registry control lock held across the directory move.
            _anchors?.Dispose(); _anchors = null;
        }
        internal void Cleanup()
        {
            if (_disposed || _receipt.Disposition == Disposition.Recovery) return;
            try
            {
                using var registry = AnchorParents(_owner._registry);
                if (!Directory.Exists(Path)) { File.Delete(_owner.RecordPath(_receipt)); return; }
                if (_receipt.Disposition == Disposition.PromotionPending) return; // An interrupted promotion needs review.
                SetDisposition(Disposition.Completed);
                _owner.CleanFolder(_receipt, _anchors!, ref _folderLock, new Counts(), allowCompletedBackups: true, CancellationToken.None);
            }
            catch (Exception error) when (SafeFailure(error)) { /* Receipt remains available for explicit cleanup. */ }
        }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            if (ReferenceEquals(Ambient.Value, this)) Ambient.Value = Previous;
            _folderLock?.Dispose(); _anchors?.Dispose(); _control?.Dispose();
            if (!File.Exists(_owner.RecordPath(_receipt))) _owner.TryDeleteControl(_receipt.Id);
            _registryAnchors.Dispose();
        }
    }

    internal sealed class Anchors : IDisposable
    {
        private readonly List<DirectoryAnchor> _items = [];
        internal DirectoryAnchor Leaf => _items[^1];
        internal Anchors(string path)
        {
            var parts = new Stack<string>(); string? current = System.IO.Path.GetFullPath(path);
            while (current is not null) { parts.Push(current); current = System.IO.Path.GetDirectoryName(current); }
            try { foreach (var part in parts) _items.Add(new DirectoryAnchor(part)); }
            catch { Dispose(); throw; }
        }
        internal void DeleteLeaf(string? identity)
        {
            var path = Leaf.Path; Leaf.Dispose(); _items.RemoveAt(_items.Count - 1);
            using var deletion = new DirectoryAnchor(path, deleteAccess: true);
            if (deletion.Identity != identity) throw new InvalidDataException();
            deletion.Delete();
        }
        public void Dispose() { foreach (var item in _items.AsEnumerable().Reverse()) item.Dispose(); _items.Clear(); }
    }
    internal sealed class DirectoryAnchor : IDisposable
    {
        private readonly SafeFileHandle? _handle; internal string Path { get; } internal string? Identity { get; }
        internal DirectoryAnchor(string path, bool deleteAccess = false)
        {
            Path = path; VerifyPath(path);
            if (!OperatingSystem.IsWindows()) return;
            _handle = Native.Open(path, directory: true, deleteAccess);
            var information = Native.Information(_handle);
            if ((information.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || (information.Attributes & (uint)FileAttributes.Directory) == 0) { _handle.Dispose(); throw new InvalidDataException(); }
            Identity = information.Volume.ToString("x8") + information.IndexHigh.ToString("x8") + information.IndexLow.ToString("x8");
        }
        internal void Delete() { if (_handle is not null) Native.Delete(_handle); else Directory.Delete(Path, recursive: false); }
        public void Dispose() => _handle?.Dispose();
    }
    private sealed class CheckedFile : IDisposable
    {
        private readonly SafeFileHandle? _handle; private FileStream? _portable; private readonly string _path; internal long Length { get; }
        internal CheckedFile(string path)
        {
            _path = path; VerifyPath(path);
            if (OperatingSystem.IsWindows())
            {
                _handle = Native.Open(path, directory: false, deleteAccess: true);
                var information = Native.Information(_handle);
                if ((information.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) { _handle.Dispose(); throw new InvalidDataException(); }
                Length = (long)information.SizeHigh << 32 | information.SizeLow;
            }
            else { _portable = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); Length = _portable.Length; }
        }
        internal void Delete() { if (_handle is not null) Native.Delete(_handle); else { _portable?.Dispose(); _portable = null; VerifyPath(_path); File.Delete(_path); } }
        public void Dispose() { _handle?.Dispose(); _portable?.Dispose(); }
    }
    private static class Native
    {
        internal static SafeFileHandle Open(string path, bool directory, bool deleteAccess)
        {
            var handle = CreateFileW(path, 0x80u | (deleteAccess ? 0x10000u : 0), directory ? 3u : 0u, IntPtr.Zero, 3, 0x00200000u | (directory ? 0x02000000u : 0), IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            var error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new IOException("A workspace handle could not be opened.", unchecked((int)(0x80070000u | (uint)error)));
        }
        internal static FileInformation Information(SafeFileHandle handle) { if (!GetFileInformationByHandle(handle, out var information)) throw new IOException("A workspace handle could not be verified."); return information; }
        internal static void Delete(SafeFileHandle handle) { var disposition = new FileDisposition { Delete = 1 }; if (!SetFileInformationByHandle(handle, 4, ref disposition, 4)) throw new IOException("A workspace entry could not be removed."); }
        [StructLayout(LayoutKind.Sequential)] internal struct FileInformation { internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
        [StructLayout(LayoutKind.Sequential)] private struct FileDisposition { internal int Delete; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref FileDisposition information, int size);
    }
}
