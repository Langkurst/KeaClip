using System.Text;
using System.Text.Json;
using Klippi.Core;

namespace Klippi.Infrastructure.Sharing;

/// <summary>
/// Keeps watch-link storage inside the free allowance. Every upload is recorded here (<c>watch-usage.json</c>) before it
/// starts, with its size and the time the bucket's lifecycle rule removes it; an upload that would take the objects still in
/// the bucket past <see cref="CapBytes"/> is refused. A record counts until a day after its expiry, because lifecycle rules
/// can run up to a day late. A failed upload whose objects were removed is taken off again; an unconfirmed clean-up keeps
/// its reservation, so an error can only overstate usage, never hide it.
/// </summary>
public sealed class WatchLinkUsageLedger
{
    public const string FileName = "watch-usage.json";
    /// <summary>8 GB: Cloudflare R2's free tier is 10 GB-month of storage; this leaves room for metadata and timing.</summary>
    public const long DefaultCapBytes = 8_000_000_000;
    private static readonly TimeSpan LifecycleGrace = TimeSpan.FromDays(1);
    /// <summary>
    /// The bucket's own lifecycle rule (docs/WATCH_LINKS.md) deletes after 7 days. A shorter setting in KeaClip does not
    /// make the bucket delete sooner, so usage is always counted for at least this long.
    /// </summary>
    public const int MinimumCountedDays = 7;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _path;
    private readonly TimeProvider _clock;

    public WatchLinkUsageLedger(string dataRoot, long capBytes = DefaultCapBytes, TimeProvider? clock = null)
    {
        if (capBytes <= 0) throw new ArgumentOutOfRangeException(nameof(capBytes));
        _path = Path.Combine(Path.GetFullPath(dataRoot), FileName);
        CapBytes = capBytes;
        _clock = clock ?? TimeProvider.System;
    }

    public long CapBytes { get; }

    public sealed record Entry(string Id, long Bytes, DateTimeOffset UploadedAt, DateTimeOffset ExpiresAt);

    /// <summary>Bytes of recorded uploads that may still be in the bucket.</summary>
    public async Task<long> LiveBytesAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return Live(Read()).Sum(entry => entry.Bytes); }
        finally { Gate.Release(); }
    }

    /// <summary>Refuses straight away when the allowance is already used up, before any conversion work.</summary>
    public async Task EnsureRoomAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var live = Live(Read()).ToList();
            long used = live.Sum(entry => entry.Bytes);
            if (used >= CapBytes) throw OverCap(used, 1, live);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Records an upload before it starts, or refuses it when it would pass the cap.</summary>
    public async Task ReserveAsync(string id, long bytes, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        if (bytes <= 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var live = Live(Read()).ToList();
            long used = live.Sum(entry => entry.Bytes);
            if (used + bytes > CapBytes) throw OverCap(used, bytes, live);
            live.Add(new Entry(id, bytes, _clock.GetUtcNow(), expiresAt));
            Write(live);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Takes a reservation off after its objects were confirmed removed (or never created).</summary>
    public async Task ReleaseAsync(string id)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try { Write(Live(Read()).Where(entry => entry.Id != id).ToList()); }
        finally { Gate.Release(); }
    }

    private IEnumerable<Entry> Live(List<Entry> entries)
    {
        var now = _clock.GetUtcNow();
        return entries.Where(entry => entry.ExpiresAt + LifecycleGrace > now);
    }

    private MediaOperationException OverCap(long used, long bytes, List<Entry> live)
    {
        string Size(long value) => value >= 1_000_000_000 ? $"{value / 1e9:0.0} GB" : $"{value / 1e6:0} MB";
        if (bytes > CapBytes) return new("This video is too large for a watch link.", $"Watch links are limited to {Size(CapBytes)} in total so storage stays inside Cloudflare's free allowance. Trim it or lower its quality, or use a normal share link.");
        var freed = live.OrderBy(entry => entry.ExpiresAt).FirstOrDefault()?.ExpiresAt + LifecycleGrace;
        string when = freed is { } date ? $" Space frees up as older links expire (next around {date.ToLocalTime():d MMM})." : "";
        return new("Watch-link storage is full for now.", $"{Size(used)} of {Size(CapBytes)} is in use, and this video needs {Size(bytes)}. The limit keeps storage inside Cloudflare's free allowance.{when} A normal share link still works.");
    }

    // A missing or unreadable ledger reads as empty only when there is no file; a damaged file refuses uploads instead of
    // silently forgetting what is stored.
    private List<Entry> Read()
    {
        if (!File.Exists(_path)) return [];
        try { return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path), Json) ?? []; }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new MediaOperationException("KeaClip couldn't read its watch-link usage record.", $"So storage can't go over the free allowance, no upload was started. The record is {FileName} in KeaClip's data folder; links expire on their own, so removing it once the last link has expired is safe.");
        }
    }

    private void Write(List<Entry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".write";
        File.WriteAllText(temporary, JsonSerializer.Serialize(entries, Json), new UTF8Encoding(false));
        File.Move(temporary, _path, overwrite: true);
    }
}
