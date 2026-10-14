using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Klippi.Core;
using Klippi.Infrastructure.Engines;

namespace Klippi.Infrastructure.Sharing;

/// <summary>
/// Creates a watch link: a browser-playable copy is made (the original is only read), encrypted on this PC with a fresh
/// 16-byte key in the seekable RFC 8188 layout, and uploaded to the user's S3-compatible bucket as <c>w/&lt;id&gt;/media</c>
/// plus a small encrypted <c>w/&lt;id&gt;/meta</c> object (name, codecs, duration, fragment index). The link is the player
/// page plus <c>#&lt;id&gt;.&lt;key&gt;</c>: browsers never send the fragment to any server, so storage and the page host
/// never see the key. Objects are removed by the bucket's lifecycle rule after <see cref="WatchLinkSetup.RetentionDays"/>.
/// </summary>
public sealed class WatchLinkService : IWatchLinkService
{
    internal const string ObjectPrefix = "w/";
    private readonly IWatchMediaPreparer _media;
    private readonly WatchLinkSettingsStore _settings;
    private readonly Func<WatchLinkSetup, string, IWatchObjectStore> _storeFactory;
    private readonly TimeProvider _clock;
    private readonly WatchLinkUsageLedger? _usage;

    public WatchLinkService(IWatchMediaPreparer media, WatchLinkSettingsStore settings, Func<WatchLinkSetup, string, IWatchObjectStore> storeFactory, TimeProvider? clock = null, WatchLinkUsageLedger? usage = null)
    {
        _usage = usage;
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The production service: S3 requests through a client dedicated to watch links, which does not follow redirects.</summary>
    public static WatchLinkService Create(IWatchMediaPreparer media, string dataRoot)
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        return new WatchLinkService(media, new WatchLinkSettingsStore(dataRoot), (setup, secret) => new S3CompatibleObjectStore(http, setup, secret), usage: new WatchLinkUsageLedger(dataRoot));
    }

    public Task<WatchLinkSetupState> LoadSetupAsync(CancellationToken cancellationToken = default) => _settings.LoadAsync(cancellationToken);
    public Task SaveSetupAsync(WatchLinkSetup setup, string? secretAccessKey, CancellationToken cancellationToken = default) => _settings.SaveAsync(setup, secretAccessKey, cancellationToken);
    public Task ClearSetupAsync(CancellationToken cancellationToken = default) => _settings.ClearAsync(cancellationToken);

    // Conversion, encryption and uploads never run on the caller's (UI) thread.
    public Task<ShareResult> ShareAsync(string path, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => ShareCoreAsync(path, progress, cancellationToken), cancellationToken);

    private async Task<ShareResult> ShareCoreAsync(string path, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new MediaOperationException("Choose a file to share.");
        var (setup, secret) = await _settings.LoadForUploadAsync(cancellationToken).ConfigureAwait(false);
        byte[] key = RandomNumberGenerator.GetBytes(16);
        string id = Base64Url(RandomNumberGenerator.GetBytes(16));
        string mediaKey = ObjectPrefix + id + "/media", metaKey = ObjectPrefix + id + "/meta";
        try
        {
            // The store checks the setup before any conversion work starts.
            using var store = _storeFactory(setup, secret);
            secret = "";
            // A full allowance is reported before any conversion work.
            if (_usage is not null) await _usage.EnsureRoomAsync(cancellationToken).ConfigureAwait(false);
            using var copy = await _media.PrepareWatchCopyAsync(path, progress, cancellationToken).ConfigureAwait(false);
            if (_usage is not null)
            {
                int countedDays = Math.Max(setup.RetentionDays, WatchLinkUsageLedger.MinimumCountedDays);
                await _usage.ReserveAsync(id, StoredBytes(new FileInfo(copy.FilePath).Length), _clock.GetUtcNow().AddDays(countedDays), cancellationToken).ConfigureAwait(false);
            }
            byte[] sha256;
            DateTimeOffset uploadedAt, expires;
            try
            {
                sha256 = await UploadMediaAsync(store, mediaKey, copy, key, progress, cancellationToken).ConfigureAwait(false);
                uploadedAt = _clock.GetUtcNow(); expires = uploadedAt.AddDays(setup.RetentionDays);
                byte[] meta = Metadata(path, copy, expires);
                try { await store.PutObjectAsync(metaKey, Rfc8188Encryption.Encrypt(meta, key), cancellationToken).ConfigureAwait(false); }
                finally { CryptographicOperations.ZeroMemory(meta); }
            }
            catch
            {
                // Deleting is free on R2 and a missing object deletes cleanly; only a confirmed clean-up frees the reservation.
                bool removed = await BestEffort(token => store.DeleteObjectAsync(mediaKey, token)).ConfigureAwait(false)
                    & await BestEffort(token => store.DeleteObjectAsync(metaKey, token)).ConfigureAwait(false);
                if (removed && _usage is not null)
                {
                    try { await _usage.ReleaseAsync(id).ConfigureAwait(false); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or MediaOperationException) { /* Overstates usage until it expires; never hides it. */ }
                }
                throw;
            }
            progress?.Report(new JobProgress(JobStage.Finished, 100, "Watch link ready."));
            string url = setup.PlayerPage.AbsoluteUri + "#" + id + "." + Base64Url(key);
            string warning = $"Anyone with the full link can watch until the bucket deletes it, about {setup.RetentionDays} day{(setup.RetentionDays == 1 ? "" : "s")} from now (the bucket's lifecycle rule decides the exact time)."
                + (copy.Reencoded ? " The video was converted to H.264/AAC so every browser can play it; the original file was not changed." : "");
            return new ShareResult(url, id, "media", setup.RetentionDays * 24d, uploadedAt, expires, Convert.ToHexString(sha256).ToLowerInvariant(), warning);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (MediaOperationException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException or HttpRequestException or ArgumentException or InvalidOperationException)
        {
            // Runtime exceptions can contain paths, URLs or signatures; none is kept.
            throw new MediaOperationException("The watch link could not be created.", "The original file was preserved. Check file access, the network and the storage settings, then try again.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Encrypts the copy record by record and uploads it in one PUT or as equal-sized multipart parts. Returns the plaintext SHA-256.</summary>
    private static async Task<byte[]> UploadMediaAsync(IWatchObjectStore store, string objectKey, WatchCopy copy, byte[] key, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var file = new FileStream(copy.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1_048_576, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var fileScope = file.ConfigureAwait(false);
        long plainLength = file.Length;
        if (plainLength < copy.Layout.Length) throw new InvalidDataException("The copy changed after it was indexed.");
        using var cipher = new WatchLinkEncryptingStream(file, plainLength, key);
        long total = cipher.Length;
        progress?.Report(new JobProgress(JobStage.Uploading, 0, "Encrypting and uploading…"));
        if (total <= store.MaximumSinglePutBytes)
        {
            byte[] body = new byte[total];
            await cipher.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
            await store.PutObjectAsync(objectKey, body, cancellationToken).ConfigureAwait(false);
            progress?.Report(new JobProgress(JobStage.Uploading, 100, "Encrypting and uploading…"));
            return cipher.PlaintextSha256;
        }
        int partBytes = store.PartBytes;
        long parts = (total + partBytes - 1) / partBytes;
        if (parts > 10_000) throw new MediaOperationException("This file is too large for one watch link.", "The storage allows at most 10,000 upload parts.");
        string uploadId = await store.StartMultipartAsync(objectKey, cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] buffer = new byte[partBytes];
            var etags = new List<string>((int)parts);
            long sent = 0;
            for (int part = 1; part <= parts; part++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int length = (int)Math.Min(partBytes, total - sent);
                await cipher.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                etags.Add(await store.UploadPartAsync(objectKey, uploadId, part, buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false));
                sent += length;
                progress?.Report(new JobProgress(JobStage.Uploading, 100d * sent / total, "Encrypting and uploading…"));
            }
            await store.CompleteMultipartAsync(objectKey, uploadId, etags, cancellationToken).ConfigureAwait(false);
            return cipher.PlaintextSha256;
        }
        catch
        {
            // An unfinished upload is aborted so no partial ciphertext stays in the bucket.
            await BestEffort(token => store.AbortMultipartAsync(objectKey, uploadId, token)).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The metadata the player decrypts first. The fragment list is compact: [seconds, byte offset] pairs.</summary>
    internal static byte[] Metadata(string originalPath, WatchCopy copy, DateTimeOffset expires)
    {
        var layout = copy.Layout;
        string extension = layout.HasVideo ? ".mp4" : ".m4a";
        string name = Path.GetFileNameWithoutExtension(originalPath);
        if (string.IsNullOrWhiteSpace(name)) name = "KeaClip video";
        if (name.Length > 120) name = name[..120];
        var document = new
        {
            v = 1,
            name = name + extension,
            kind = layout.HasVideo ? "video" : "audio",
            mime = (layout.HasVideo ? "video/mp4" : "audio/mp4") + "; codecs=\"" + layout.Codecs + "\"",
            size = new FileInfo(copy.FilePath).Length,
            end = layout.Length,
            rs = WatchLinkEncryption.RecordSize,
            duration = Math.Round(copy.Probe.Duration.TotalSeconds, 3),
            width = layout.Width,
            height = layout.Height,
            init = layout.InitLength,
            frags = layout.Fragments.Select(f => new object[] { Math.Round(f.Time, 3), f.Offset }).ToArray(),
            expires = expires.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
        };
        return JsonSerializer.SerializeToUtf8Bytes(document);
    }

    // Clean-up after a failure must not hang the share: it gets its own short deadline and ignores errors.
    private static async Task<bool> BestEffort(Func<CancellationToken, Task> action)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await action(deadline.Token).ConfigureAwait(false); return true; }
        catch (Exception error) when (error is MediaOperationException or OperationCanceledException or HttpRequestException or IOException) { return false; }
    }

    /// <summary>What the bucket stores for a copy of this size: the encrypted media (17 bytes per record plus its header) and a metadata allowance.</summary>
    internal static long StoredBytes(long plainBytes) => WatchLinkEncryption.EncryptedLength(plainBytes) + 1_048_576;

    internal static string Base64Url(ReadOnlySpan<byte> value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
