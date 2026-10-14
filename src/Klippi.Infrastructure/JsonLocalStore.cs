using System.Text.Json;
using Klippi.Core;
namespace Klippi.Infrastructure;

public sealed class JsonLocalStore(string dataRoot) : ILocalStore
{
    private const string SettingsFile = "settings.json", HistoryFile = "history.json", QueueFile = "queue.json";
    private readonly string _root = Path.GetFullPath(dataRoot);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _recoveredNames = [];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private const int MaxNotices = 8;
    private readonly object _noticeSync = new();
    private readonly List<string> _notices = [];
    // Recoveries already reported since each file was last saved (used only under _gate). A damaged or missing file is
    // recovered from its backup on every read until the next save, and must not bring the same notice back each time.
    private readonly HashSet<(string Name, string Notice)> _reported = [];
    // Pending recovery notices (backup restored, unreadable history set aside; the newest MaxNotices distinct ones), kept until
    // TakeWarning hands them to the UI so one does not hide another, e.g. damaged settings and history in the same startup.
    public string? LastWarning { get { lock (_noticeSync) return JoinNotices(); } }
    public string? TakeWarning() { lock (_noticeSync) { var notice = JoinNotices(); _notices.Clear(); return notice; } }
    public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => LockedAsync(async () => Bounded(await ReadAsync<AppSettings>(SettingsFile, cancellationToken) ?? new AppSettings()), cancellationToken);
    public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings.HistoryRetentionDays is <= 0 or > ILocalStore.MaxHistoryRetentionDays)
            throw new MediaOperationException($"History retention must be blank or between 1 and {ILocalStore.MaxHistoryRetentionDays} days.");
        if (settings.ConcurrentDownloads is < 1 or > 8 || settings.SoundboardMaxSeconds <= 0 || !double.IsFinite(settings.SoundboardMaxSeconds) || settings.SoundboardTargetBytes <= 0 || !new[] { "Dark", "Light", "System" }.Contains(settings.Theme))
            throw new MediaOperationException("One of these settings is not valid.");
        return LockedAsync(async () => { await WriteAsync(SettingsFile, settings, cancellationToken); return true; }, cancellationToken);
    }
    public Task<IReadOnlyList<HistoryEntry>> LoadHistoryAsync(CancellationToken cancellationToken = default) => LockedAsync<IReadOnlyList<HistoryEntry>>(async () => (await ReadHistoryAsync(cancellationToken)).OrderByDescending(x => x.Date).ToArray(), cancellationToken);
    public Task AddHistoryAsync(HistoryEntry entry, CancellationToken cancellationToken = default) => UpdateHistoryAsync(list => { list.RemoveAll(x => x.Id == entry.Id); list.Add(entry); }, cancellationToken);
    public Task RemoveHistoryAsync(string id, CancellationToken cancellationToken = default) => UpdateHistoryAsync(list => list.RemoveAll(x => x.Id == id), cancellationToken);
    public Task ClearHistoryAsync(CancellationToken cancellationToken = default) => UpdateHistoryAsync(list => list.Clear(), cancellationToken);
    public Task PruneHistoryAsync(int? retentionDays, CancellationToken cancellationToken = default)
    {
        if (retentionDays is null) return Task.CompletedTask;
        if (retentionDays <= 0) throw new MediaOperationException("History retention must be at least one day.");
        // Older settings may hold any positive value; a span reaching past the calendar's start keeps everything instead of throwing.
        var now = DateTimeOffset.UtcNow;
        if (retentionDays.Value >= (now - DateTimeOffset.MinValue).TotalDays) return Task.CompletedTask;
        var cutoff = now.AddDays(-retentionDays.Value);
        return UpdateHistoryAsync(list => list.RemoveAll(x => x.Date < cutoff), cancellationToken);
    }
    // Unfinished downloads, saved like history: written atomically with a backup, recovered from the backup when the
    // newest copy is damaged, and set aside (never deleted) when neither copy can be read, so the app starts with an
    // empty queue and says so once.
    public Task<SavedQueue?> LoadQueueAsync(CancellationToken cancellationToken = default) => LockedAsync(() => ReadAsync<SavedQueue>(QueueFile, cancellationToken, setAsideUnreadable: true), cancellationToken);
    public Task SaveQueueAsync(SavedQueue queue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        return LockedAsync(async () => { await WriteAsync(QueueFile, queue, cancellationToken); return true; }, cancellationToken);
    }
    private Task UpdateHistoryAsync(Action<List<HistoryEntry>> change, CancellationToken token) => LockedAsync(async () =>
    {
        var entries = await ReadHistoryAsync(token); change(entries);
        await WriteAsync(HistoryFile, entries, token); return true;
    }, token);
    private async Task<List<HistoryEntry>> ReadHistoryAsync(CancellationToken token)
    {
        var entries = await ReadAsync<List<HistoryEntry>>(HistoryFile, token, setAsideUnreadable: true).ConfigureAwait(false) ?? [];
        entries.RemoveAll(entry => entry is null); // A hand edit or sync merge can leave null records; they hold nothing to keep.
        return entries;
    }
    // Older versions saved any positive retention. Anything above the maximum keeps more than any real history, so it loads as
    // the maximum: pruning is unchanged and saving unrelated settings is not blocked by a value the user cannot see is wrong.
    private static AppSettings Bounded(AppSettings settings) => settings.HistoryRetentionDays > ILocalStore.MaxHistoryRetentionDays ? settings with { HistoryRetentionDays = ILocalStore.MaxHistoryRetentionDays } : settings;
    private async Task<T> LockedAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { return await action().ConfigureAwait(false); } finally { _gate.Release(); }
    }
    private async Task<T?> ReadAsync<T>(string name, CancellationToken token, bool setAsideUnreadable = false)
    {
        Directory.CreateDirectory(_root); var path = Path.Combine(_root, name); var backup = path + ".backup";
        var primaryExists = File.Exists(path);
        if (primaryExists)
            try { return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false), Json); }
            catch (JsonException) { }
        else if (!File.Exists(backup)) return default;
        if (File.Exists(backup))
            try
            {
                var recovered = JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(backup, token).ConfigureAwait(false), Json);
                if (recovered is not null)
                {
                    if (primaryExists) _recoveredNames.Add(name);
                    var notice = Describe(name) + (primaryExists ? " could not be read" : " file was missing") + ", so the previous saved copy was recovered from its backup. " + (name == HistoryFile ? "The newest records may be missing." : name == QueueFile ? "The newest queue changes may be missing." : "Your most recent change may be missing.");
                    if (_reported.Add((name, notice))) Warn(notice);
                    return recovered;
                }
            }
            catch (JsonException) { }
        // Only damaged content reaches here; I/O errors (a locked or inaccessible file) propagate and never move anything.
        if (setAsideUnreadable) { SetAside(name, path, backup); return default; }
        throw new MediaOperationException(primaryExists ? "Saved data could not be read. The original was preserved for recovery." : "The saved-data backup could not be read. It was preserved for recovery.");
    }
    // Moves (never deletes) each unreadable copy to an application-owned .corrupt-* name for manual recovery, so one damaged
    // file cannot block every later write, clear or prune. History and the queue only: unreadable settings keep failing loudly instead.
    // All or nothing: if one copy cannot be moved (held by a scanner or sync client) the copies already moved are put back and
    // the error propagates, so the next attempt moves and names the whole pair instead of leaving the newer copy unnamed.
    private void SetAside(string name, string path, string backup)
    {
        var suffix = ".corrupt-" + Guid.NewGuid().ToString("N"); var moved = new List<string>(2);
        try
        {
            foreach (var file in new[] { path, backup })
                if (File.Exists(file)) { File.Move(file, file + suffix); moved.Add(file); }
        }
        catch
        {
            foreach (var file in moved)
                try { File.Move(file + suffix, file); } catch (Exception restore) when (restore is IOException or UnauthorizedAccessException) { /* Keep the first error; the copy stays under its .corrupt-* name. */ }
            throw;
        }
        _recoveredNames.Remove(name); Forget(name);
        if (moved.Count > 0)
            Warn(Describe(name) + " could not be read, so a new one was started. Your media files were not touched; the unreadable " + (moved.Count == 1 ? "copy was kept as " : "copies were kept as ") + string.Join(" and ", moved.Select(file => Path.GetFileName(file + suffix))) + " in the KeaClip data folder.");
    }
    private static string Describe(string name) => name switch { SettingsFile => "Your saved settings", HistoryFile => "Your download history", QueueFile => "Your saved queue", _ => "Saved data" };
    private void Warn(string message)
    {
        lock (_noticeSync)
        {
            if (_notices.Contains(message)) return; // Not yet taken: one copy is enough.
            if (_notices.Count == MaxNotices) _notices.RemoveAt(0);
            _notices.Add(message);
        }
    }
    private string? JoinNotices() => _notices.Count == 0 ? null : string.Join(" ", _notices);
    // The file was saved or set aside, so its earlier recovery has ended; damage found after this is reported again.
    private void Forget(string name) => _reported.RemoveWhere(entry => entry.Name == name);
    private async Task WriteAsync<T>(string name, T value, CancellationToken token)
    {
        Directory.CreateDirectory(_root); var path = Path.Combine(_root, name);
        var temporary = Path.Combine(_root, ".write-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, Json, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false); stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            if (File.Exists(path) && _recoveredNames.Contains(name))
            {
                // Preserve the known-good backup; replacing it with the damaged primary
                // would destroy recovery if the next primary is interrupted/corrupted.
                File.Copy(path,path+".corrupt-"+Guid.NewGuid().ToString("N"));
                File.Replace(temporary,path,destinationBackupFileName:null,ignoreMetadataErrors:true);
            }
            else if (File.Exists(path)) File.Replace(temporary, path, path + ".backup", ignoreMetadataErrors: true);
            else File.Move(temporary, path);
            _recoveredNames.Remove(name); Forget(name);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
