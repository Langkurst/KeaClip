using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Klippi.Core;
using Klippi.Infrastructure.Engines;
using Klippi.Infrastructure.Sharing;
using Xunit;

namespace Klippi.Tests;

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>An S3 endpoint stand-in that records requests and lets a test fail chosen ones.</summary>
internal sealed class FakeS3Handler : HttpMessageHandler
{
    public sealed record Seen(HttpMethod Method, string Path, string Query, string Host, Dictionary<string, string> Headers, byte[] Body);
    public ConcurrentQueue<Seen> Requests { get; } = new();
    public Func<Seen, HttpResponseMessage?>? Fail { get; set; }
    public Dictionary<int, byte[]> Parts { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        var uri = request.RequestUri!;
        var seen = new Seen(request.Method, uri.AbsolutePath, uri.Query.TrimStart('?'), uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port, headers, body);
        Requests.Enqueue(seen);
        if (Fail?.Invoke(seen) is { } failure) return failure;
        if (request.Method == HttpMethod.Post && seen.Query == "uploads=")
            return Xml("<InitiateMultipartUploadResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Bucket>clips</Bucket><Key>k</Key><UploadId>up-1</UploadId></InitiateMultipartUploadResult>");
        if (request.Method == HttpMethod.Put && seen.Query.StartsWith("partNumber=", StringComparison.Ordinal))
        {
            int part = int.Parse(seen.Query.Split('&')[0]["partNumber=".Length..], System.Globalization.CultureInfo.InvariantCulture);
            lock (Parts) Parts[part] = body;
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.TryAddWithoutValidation("ETag", $"\"etag-{part}\"");
            return response;
        }
        if (request.Method == HttpMethod.Post && seen.Query.StartsWith("uploadId=", StringComparison.Ordinal))
            return Xml("<CompleteMultipartUploadResult><ETag>\"x-2\"</ETag></CompleteMultipartUploadResult>");
        return new HttpResponseMessage(request.Method == HttpMethod.Delete ? HttpStatusCode.NoContent : HttpStatusCode.OK);
    }

    private static HttpResponseMessage Xml(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/xml") };
}

public sealed class S3CompatibleObjectStoreTests
{
    internal static readonly WatchLinkSetup Setup = new(new Uri("https://0123abcd.r2.cloudflarestorage.com"), "keaclip-watch", "AKIDEXAMPLE0123456789", new Uri("https://watch.example.com/v/"), 7);
    private const string Secret = "secretEXAMPLEsecretEXAMPLE0123456789abcd";
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 1, 2, 3, TimeSpan.Zero);
    private static S3CompatibleObjectStore Store(FakeS3Handler handler, WatchLinkSetup? setup = null) => new(new HttpClient(handler), setup ?? Setup, Secret,
        new S3StoreOptions { RetryDelay = TimeSpan.Zero, TimeProvider = new FixedClock(Now), PartBytes = 5 * 1024 * 1024 });

    // What the server can check: the signature computed from the request as it arrived (path, query, Host header, body hash).
    private static void AssertSignedAsReceived(FakeS3Handler.Seen seen)
    {
        string hash = AwsSigV4.Hex(SHA256.HashData(seen.Body));
        Assert.Equal(hash, seen.Headers["x-amz-content-sha256"]);
        Assert.Equal("20261013T010203Z", seen.Headers["x-amz-date"]);
        string expected = AwsSigV4.Authorization(Setup.AccessKeyId, Encoding.UTF8.GetBytes(Secret), Now, "auto", "s3", seen.Method.Method, seen.Path, seen.Query,
            [new("host", seen.Host), new("x-amz-content-sha256", hash), new("x-amz-date", "20261013T010203Z")], hash);
        Assert.Equal(expected, seen.Headers["authorization"]);
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE0123456789/20261013/auto/s3/aws4_request,SignedHeaders=host;x-amz-content-sha256;x-amz-date,Signature=", expected);
    }

    [Fact]
    public async Task Multipart_upload_is_path_style_signed_and_completed_with_every_part_etag()
    {
        var handler = new FakeS3Handler();
        using var store = Store(handler);
        string id = await store.StartMultipartAsync("w/abc/media", default);
        byte[] one = RandomNumberGenerator.GetBytes(store.PartBytes), two = RandomNumberGenerator.GetBytes(1000);
        var etags = new[] { await store.UploadPartAsync("w/abc/media", id, 1, one, default), await store.UploadPartAsync("w/abc/media", id, 2, two, default) };
        await store.CompleteMultipartAsync("w/abc/media", id, etags, default);
        var requests = handler.Requests.ToArray();
        Assert.Equal(["POST /keaclip-watch/w/abc/media uploads=", "PUT /keaclip-watch/w/abc/media partNumber=1&uploadId=up-1", "PUT /keaclip-watch/w/abc/media partNumber=2&uploadId=up-1", "POST /keaclip-watch/w/abc/media uploadId=up-1"],
            requests.Select(r => $"{r.Method} {r.Path} {r.Query}"));
        Assert.All(requests, AssertSignedAsReceived);
        Assert.Equal(one, handler.Parts[1]);
        Assert.Equal(two, handler.Parts[2]);
        var complete = XDocument.Parse(Encoding.UTF8.GetString(requests[3].Body));
        Assert.Equal(["1:\"etag-1\"", "2:\"etag-2\""], complete.Root!.Elements().Select(p => p.Elements().First().Value + ":" + p.Elements().Last().Value));
        Assert.Equal("0123abcd.r2.cloudflarestorage.com", requests[0].Host);
    }

    [Fact]
    public async Task Parts_are_retried_after_server_errors_but_creation_and_refusals_are_not()
    {
        int failures = 0;
        var handler = new FakeS3Handler { Fail = seen => seen.Query.StartsWith("partNumber=1", StringComparison.Ordinal) && failures++ < 2 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null };
        using var store = Store(handler);
        Assert.Equal("\"etag-1\"", await store.UploadPartAsync("w/a/media", "up-1", 1, new byte[10], default));
        Assert.Equal(3, handler.Requests.Count);

        var refusing = new FakeS3Handler { Fail = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<Error><Code>AccessDenied</Code><Message>secret detail</Message></Error>") } };
        using var refused = Store(refusing);
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => refused.PutObjectAsync("w/a/meta", new byte[5], default));
        Assert.Equal("Storage refused the access key.", error.Message);
        Assert.DoesNotContain("secret detail", error.Details);
        Assert.Single(refusing.Requests);

        var unavailable = new FakeS3Handler { Fail = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };
        using var flaky = Store(unavailable);
        await Assert.ThrowsAsync<MediaOperationException>(() => flaky.StartMultipartAsync("w/a/media", default));
        Assert.Single(unavailable.Requests);
    }

    [Fact]
    public async Task A_completion_that_reports_an_error_in_a_200_body_fails()
    {
        var handler = new FakeS3Handler { Fail = seen => seen.Method == HttpMethod.Post && seen.Query.StartsWith("uploadId", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<Error><Code>InternalError</Code></Error>") } : null };
        using var store = Store(handler);
        await Assert.ThrowsAsync<MediaOperationException>(() => store.CompleteMultipartAsync("w/a/media", "up-1", ["\"e\""], default));
    }

    [Fact]
    public void Unsafe_keys_and_incomplete_setups_are_refused()
    {
        Assert.False(S3CompatibleObjectStore.ValidKey("../x"));
        Assert.False(S3CompatibleObjectStore.ValidKey("/w/x"));
        Assert.False(S3CompatibleObjectStore.ValidKey("w//x"));
        Assert.False(S3CompatibleObjectStore.ValidKey("w/x?y"));
        Assert.True(S3CompatibleObjectStore.ValidKey("w/AbC-_9/media"));
        Assert.Throws<MediaOperationException>(() => new S3CompatibleObjectStore(new HttpClient(), Setup, "short"));
        Assert.Throws<MediaOperationException>(() => new S3CompatibleObjectStore(new HttpClient(), Setup with { StorageEndpoint = new Uri("http://example.com") }, Secret));
    }
}

public sealed class WatchLinkSetupTests
{
    [Fact]
    public void Setup_problems_are_reported_in_plain_language()
    {
        Assert.Empty(S3CompatibleObjectStoreTests.Setup.Problems());
        Assert.Empty((S3CompatibleObjectStoreTests.Setup with { StorageEndpoint = new Uri("http://127.0.0.1:9000"), PlayerPage = new Uri("http://localhost:8080/v/") }).Problems());
        Assert.Contains("storage endpoint", (S3CompatibleObjectStoreTests.Setup with { StorageEndpoint = new Uri("http://example.com") }).Problems().Single());
        Assert.Contains("storage endpoint", (S3CompatibleObjectStoreTests.Setup with { StorageEndpoint = new Uri("https://example.com/bucket") }).Problems().Single());
        Assert.Contains("bucket", (S3CompatibleObjectStoreTests.Setup with { Bucket = "Bad_Bucket" }).Problems().Single());
        Assert.Contains("player page", (S3CompatibleObjectStoreTests.Setup with { PlayerPage = new Uri("https://watch.example.com/v") }).Problems().Single());
        Assert.Contains("player page", (S3CompatibleObjectStoreTests.Setup with { PlayerPage = new Uri("https://watch.example.com/v/?x=1") }).Problems().Single());
        Assert.Contains("days", (S3CompatibleObjectStoreTests.Setup with { RetentionDays = 0 }).Problems().Single());
        Assert.Contains("access key", (S3CompatibleObjectStoreTests.Setup with { AccessKeyId = "short" }).Problems().Single());
        Assert.False(WatchLinkSetup.ValidSecret("has a space in it 0123456789"));
        Assert.True(WatchLinkSetup.ValidSecret("0123456789abcdef0123456789abcdef"));
    }
}

public sealed class WatchLinkSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "KeaClipWatchSettings", Guid.NewGuid().ToString("N"));
    private const string Secret = "R2secretValue0123456789abcdefABCDEF";
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [EngineWindowsFact]
    public async Task The_secret_is_stored_only_as_a_DPAPI_blob_and_kept_when_not_reentered()
    {
        var store = new WatchLinkSettingsStore(_root);
        Assert.False((await store.LoadAsync()).IsReady);
        await store.SaveAsync(S3CompatibleObjectStoreTests.Setup, Secret);
        string json = await File.ReadAllTextAsync(store.FilePath);
        Assert.DoesNotContain(Secret, json);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret)), json);
        var state = await store.LoadAsync();
        Assert.True(state.IsReady);
        Assert.Equal(S3CompatibleObjectStoreTests.Setup, state.Setup);
        Assert.Equal(Secret, (await store.LoadForUploadAsync(default)).Secret);
        // Saving without a new secret keeps the protected one; changing other fields works.
        await store.SaveAsync(S3CompatibleObjectStoreTests.Setup with { RetentionDays = 30 }, null);
        Assert.Equal(30, (await store.LoadAsync()).Setup!.RetentionDays);
        Assert.Equal(Secret, (await store.LoadForUploadAsync(default)).Secret);
        await store.ClearAsync();
        Assert.Null((await store.LoadAsync()).Setup);
        await Assert.ThrowsAsync<MediaOperationException>(() => store.LoadForUploadAsync(default));
    }

    [EngineWindowsFact]
    public async Task A_blob_that_cannot_be_unlocked_asks_for_the_secret_again()
    {
        var store = new WatchLinkSettingsStore(_root);
        await store.SaveAsync(S3CompatibleObjectStoreTests.Setup, Secret);
        var record = JsonSerializer.Deserialize<WatchLinkSettingsStore.Record>(await File.ReadAllTextAsync(store.FilePath))!;
        byte[] blob = Convert.FromBase64String(record.ProtectedSecret!);
        blob[^5] ^= 0xFF;
        await File.WriteAllTextAsync(store.FilePath, JsonSerializer.Serialize(record with { ProtectedSecret = Convert.ToBase64String(blob) }));
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => store.LoadForUploadAsync(default));
        Assert.Contains("could not be unlocked", error.Message);
    }

    [Fact]
    public async Task Invalid_setups_and_secrets_are_not_saved()
    {
        var store = new WatchLinkSettingsStore(_root, new PlainProtector());
        await Assert.ThrowsAsync<MediaOperationException>(() => store.SaveAsync(S3CompatibleObjectStoreTests.Setup with { Bucket = "X" }, Secret));
        await Assert.ThrowsAsync<MediaOperationException>(() => store.SaveAsync(S3CompatibleObjectStoreTests.Setup, "short"));
        Assert.False(File.Exists(store.FilePath));
    }

    internal sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string secret, byte[] entropy) => "p:" + secret;
        public string Unprotect(string protectedSecret, byte[] entropy) => protectedSecret.StartsWith("p:", StringComparison.Ordinal) ? protectedSecret[2..] : throw new CryptographicException();
    }
}

/// <summary>An in-memory object store with small parts, so multipart uploads are exercised with small files.</summary>
internal sealed class MemoryObjectStore(int partBytes = 1000) : IWatchObjectStore
{
    public ConcurrentDictionary<string, byte[]> Objects { get; } = new();
    public List<string> Log { get; } = [];
    public Func<string, bool>? FailOn { get; set; }
    private readonly Dictionary<string, SortedDictionary<int, byte[]>> _uploads = [];
    public int MaximumSinglePutBytes => partBytes;
    public int PartBytes => partBytes;
    private void Step(string entry) { lock (Log) Log.Add(entry); if (FailOn?.Invoke(entry) == true) throw new MediaOperationException("Storage could not complete the upload.", "HTTP 500."); }
    public Task PutObjectAsync(string key, ReadOnlyMemory<byte> body, CancellationToken cancellationToken) { Step("put " + key); Objects[key] = body.ToArray(); return Task.CompletedTask; }
    public Task<string> StartMultipartAsync(string key, CancellationToken cancellationToken) { Step("start " + key); _uploads["u1"] = []; return Task.FromResult("u1"); }
    public Task<string> UploadPartAsync(string key, string uploadId, int partNumber, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        Step($"part {partNumber}");
        Assert.True(partNumber == 1 || _uploads[uploadId][partNumber - 1].Length == partBytes, "Every part but the last must have the same size.");
        _uploads[uploadId][partNumber] = body.ToArray();
        return Task.FromResult("e" + partNumber);
    }
    public Task CompleteMultipartAsync(string key, string uploadId, IReadOnlyList<string> partETags, CancellationToken cancellationToken)
    {
        Step("complete " + key);
        Assert.Equal(_uploads[uploadId].Keys.Select(k => "e" + k), partETags);
        Objects[key] = _uploads[uploadId].Values.SelectMany(p => p).ToArray();
        return Task.CompletedTask;
    }
    public Task AbortMultipartAsync(string key, string uploadId, CancellationToken cancellationToken) { Step("abort " + key); _uploads.Remove(uploadId); return Task.CompletedTask; }
    public Task DeleteObjectAsync(string key, CancellationToken cancellationToken) { Step("delete " + key); Objects.TryRemove(key, out _); return Task.CompletedTask; }
    public void Dispose() { }
}

public sealed class WatchLinkServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "KeaClipWatchService", Guid.NewGuid().ToString("N"));
    private const string Secret = "R2secretValue0123456789abcdefABCDEF";
    public WatchLinkServiceTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class FakePreparer(string copyPath, Fmp4Layout layout) : IWatchMediaPreparer
    {
        public int Calls;
        public bool Disposed;
        public Task<WatchCopy> PrepareWatchCopyAsync(string path, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var probe = new ProbeInfo(copyPath, TimeSpan.FromSeconds(6.5), new FileInfo(copyPath).Length, true, true, 640, 360, 30, "h264", "aac", 48000, 2);
            return Task.FromResult(new WatchCopy(copyPath, probe, layout, false, () => Disposed = true));
        }
    }

    private (WatchLinkService Service, MemoryObjectStore Store, FakePreparer Media, byte[] Copy) Create(int copyBytes = 4500, bool configure = true)
    {
        byte[] copy = RandomNumberGenerator.GetBytes(copyBytes);
        string copyPath = Path.Combine(_root, "watch.mp4");
        File.WriteAllBytes(copyPath, copy);
        var layout = new Fmp4Layout(copyBytes, 100, "avc1.64001F,mp4a.40.2", true, true, 640, 360, [new(0, 100), new(2.002, 2000), new(4.004, 3500)]);
        var media = new FakePreparer(copyPath, layout);
        var settings = new WatchLinkSettingsStore(Path.Combine(_root, "data"), new WatchLinkSettingsStoreTests.PlainProtector());
        if (configure) settings.SaveAsync(S3CompatibleObjectStoreTests.Setup, Secret).GetAwaiter().GetResult();
        var store = new MemoryObjectStore();
        string? seenSecret = null;
        var service = new WatchLinkService(media, settings, (setup, secret) => { seenSecret = secret; Assert.Equal(S3CompatibleObjectStoreTests.Setup, setup); Assert.Equal(Secret, seenSecret); return store; },
            new FixedClock(new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero)));
        return (service, store, media, copy);
    }

    private static (string Id, byte[] Key) Parse(string url)
    {
        var fragment = new Uri(url).Fragment.TrimStart('#').Split('.');
        string key = fragment[1].Replace('-', '+').Replace('_', '/');
        return (fragment[0], Convert.FromBase64String(key + new string('=', (4 - key.Length % 4) % 4)));
    }

    [Fact]
    public async Task A_watch_link_is_the_player_page_with_id_and_key_only_in_the_fragment()
    {
        var (service, store, media, copy) = Create();
        string original = Path.Combine(_root, "Holiday clip.mkv");
        await File.WriteAllBytesAsync(original, [1, 2, 3]);
        var result = await service.ShareAsync(original);
        Assert.Matches("^https://watch\\.example\\.com/v/#[A-Za-z0-9_-]{22}\\.[A-Za-z0-9_-]{22}$", result.ShareUrl);
        var (id, key) = Parse(result.ShareUrl);
        Assert.Equal(16, key.Length);
        // Media first (as multipart parts of equal size), then the small metadata object that marks the link complete.
        Assert.Equal([$"start w/{id}/media", "part 1", "part 2", "part 3", "part 4", "part 5", $"complete w/{id}/media", $"put w/{id}/meta"], store.Log);
        Assert.Equal(copy, Rfc8188Encryption.Decrypt(store.Objects[$"w/{id}/media"], key));
        Assert.Equal(WatchLinkEncryption.EncryptedLength(copy.Length), store.Objects[$"w/{id}/media"].Length);
        using var meta = JsonDocument.Parse(Rfc8188Encryption.Decrypt(store.Objects[$"w/{id}/meta"], key));
        var root = meta.RootElement;
        Assert.Equal("Holiday clip.mp4", root.GetProperty("name").GetString());
        Assert.Equal("video/mp4; codecs=\"avc1.64001F,mp4a.40.2\"", root.GetProperty("mime").GetString());
        Assert.Equal(copy.Length, root.GetProperty("size").GetInt64());
        Assert.Equal(100, root.GetProperty("init").GetInt64());
        Assert.Equal(65_536, root.GetProperty("rs").GetInt32());
        Assert.Equal(6.5, root.GetProperty("duration").GetDouble());
        Assert.Equal("[[0,100],[2.002,2000],[4.004,3500]]", root.GetProperty("frags").GetRawText());
        Assert.Equal("2026-10-20T00:00:00Z", root.GetProperty("expires").GetString());
        // The key never appears in what storage receives: object names or bytes.
        string keyText = WatchLinkService.Base64Url(key);
        Assert.All(store.Objects, o => { Assert.DoesNotContain(keyText, o.Key); Assert.DoesNotContain(keyText, Encoding.Latin1.GetString(o.Value)); Assert.False(o.Value.AsSpan().IndexOf(key) >= 0); });
        Assert.Equal(Convert.ToHexString(SHA256.HashData(copy)).ToLowerInvariant(), result.Sha256);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), result.EstimatedFreeUntil);
        Assert.Contains("7 days", result.Warning);
        Assert.True(media.Disposed, "The temporary copy is deleted after the upload.");
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(original));
    }

    [Fact]
    public async Task Small_copies_use_one_put_and_each_link_has_its_own_id_and_key()
    {
        var (service, store, _, copy) = Create(copyBytes: 900);
        var first = await service.ShareAsync(Path.Combine(_root, "a.mp4"));
        var second = await service.ShareAsync(Path.Combine(_root, "a.mp4"));
        var (id, key) = Parse(first.ShareUrl);
        Assert.NotEqual(first.ShareUrl, second.ShareUrl);
        Assert.NotEqual(Parse(first.ShareUrl).Id, Parse(second.ShareUrl).Id);
        Assert.Equal([$"put w/{id}/media", $"put w/{id}/meta"], store.Log.Take(2));
        Assert.Equal(copy, Rfc8188Encryption.Decrypt(store.Objects[$"w/{id}/media"], key));
    }

    [Fact]
    public async Task A_failed_part_aborts_the_upload_and_a_failed_metadata_put_deletes_the_media()
    {
        var (service, store, media, _) = Create();
        store.FailOn = entry => entry == "part 3";
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => service.ShareAsync(Path.Combine(_root, "a.mp4")));
        Assert.Equal("Storage could not complete the upload.", error.Message);
        // Aborted, then both object names deleted (free on R2, harmless when missing) so the usage record can be released.
        Assert.StartsWith("abort w/", store.Log[^3]);
        Assert.EndsWith("/media", store.Log[^2]);
        Assert.EndsWith("/meta", store.Log[^1]);
        Assert.Empty(store.Objects);
        Assert.True(media.Disposed);

        store.Log.Clear();
        store.FailOn = entry => entry.StartsWith("put ", StringComparison.Ordinal) && entry.EndsWith("/meta", StringComparison.Ordinal);
        await Assert.ThrowsAsync<MediaOperationException>(() => service.ShareAsync(Path.Combine(_root, "a.mp4")));
        Assert.StartsWith("delete w/", store.Log[^1]);
        Assert.Empty(store.Objects);
    }

    [Fact]
    public async Task Sharing_before_setup_explains_what_to_do_and_converts_nothing()
    {
        var (service, store, media, _) = Create(configure: false);
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => service.ShareAsync(Path.Combine(_root, "a.mp4")));
        Assert.Equal("Watch links are not set up yet.", error.Message);
        Assert.Equal(0, media.Calls);
        Assert.Empty(store.Log);
    }

    [Fact]
    public async Task Cancellation_aborts_the_upload()
    {
        var (service, store, _, _) = Create();
        using var cancel = new CancellationTokenSource();
        store.FailOn = entry => { if (entry == "part 2") cancel.Cancel(); return false; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ShareAsync(Path.Combine(_root, "a.mp4"), null, cancel.Token));
        Assert.Contains(store.Log, entry => entry.StartsWith("abort w/", StringComparison.Ordinal));
        Assert.Empty(store.Objects);
    }

    // ----- Free-allowance limit (WatchLinkUsageLedger) -----
    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private (WatchLinkService Service, MemoryObjectStore Store, FakePreparer Media, WatchLinkUsageLedger Usage, MovableClock Clock) CreateLimited(long capBytes)
    {
        var (_, store, media, _) = Create();
        var clock = new MovableClock(new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero));
        var usage = new WatchLinkUsageLedger(Path.Combine(_root, "data"), capBytes, clock);
        var settings = new WatchLinkSettingsStore(Path.Combine(_root, "data"), new WatchLinkSettingsStoreTests.PlainProtector());
        return (new WatchLinkService(media, settings, (_, _) => store, clock, usage), store, media, usage, clock);
    }

    [Fact]
    public async Task Each_upload_is_counted_until_a_day_after_the_bucket_removes_it()
    {
        var (service, _, _, usage, clock) = CreateLimited(WatchLinkUsageLedger.DefaultCapBytes);
        await service.ShareAsync(Path.Combine(_root, "a.mp4"));
        long stored = WatchLinkService.StoredBytes(4500);
        Assert.Equal(stored, await usage.LiveBytesAsync());
        Assert.True(stored >= WatchLinkEncryption.EncryptedLength(4500));
        clock.Now = clock.Now.AddDays(7.9);
        Assert.Equal(stored, await usage.LiveBytesAsync());
        clock.Now = clock.Now.AddDays(0.2);
        Assert.Equal(0, await usage.LiveBytesAsync());
    }

    [Fact]
    public async Task An_upload_that_would_pass_the_limit_is_refused_before_anything_is_uploaded()
    {
        long one = WatchLinkService.StoredBytes(4500);
        var (service, store, media, usage, _) = CreateLimited(one * 2 + 10);
        await service.ShareAsync(Path.Combine(_root, "a.mp4"));
        await service.ShareAsync(Path.Combine(_root, "a.mp4"));
        int logged = store.Log.Count;
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => service.ShareAsync(Path.Combine(_root, "a.mp4")));
        Assert.Equal("Watch-link storage is full for now.", error.Message);
        Assert.Contains("free allowance", error.Details);
        Assert.Equal(logged, store.Log.Count);
        Assert.Equal(one * 2, await usage.LiveBytesAsync());
        Assert.Equal(3, media.Calls);
    }

    [Fact]
    public async Task When_the_limit_is_already_reached_nothing_is_converted()
    {
        long one = WatchLinkService.StoredBytes(4500);
        var (service, store, media, usage, clock) = CreateLimited(one);
        await usage.ReserveAsync("older", one, clock.Now.AddDays(7));
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => service.ShareAsync(Path.Combine(_root, "a.mp4")));
        Assert.Contains("frees up", error.Details);
        Assert.Equal(0, media.Calls);
        Assert.Empty(store.Log);
    }

    [Fact]
    public async Task A_single_video_larger_than_the_limit_is_explained()
    {
        var (service, store, _, _, _) = CreateLimited(1000);
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => service.ShareAsync(Path.Combine(_root, "a.mp4")));
        Assert.Equal("This video is too large for a watch link.", error.Message);
        Assert.Empty(store.Log);
    }

    [Fact]
    public async Task A_failed_upload_frees_its_reservation_only_after_a_confirmed_clean_up()
    {
        var (service, store, _, usage, _) = CreateLimited(WatchLinkUsageLedger.DefaultCapBytes);
        store.FailOn = entry => entry == "part 3";
        await Assert.ThrowsAsync<MediaOperationException>(() => service.ShareAsync(Path.Combine(_root, "a.mp4")));
        Assert.Equal(0, await usage.LiveBytesAsync());

        // The clean-up itself fails: the reservation stays, so usage can be overstated but never hidden.
        store.FailOn = entry => entry == "part 3" || entry.StartsWith("delete ", StringComparison.Ordinal);
        await Assert.ThrowsAsync<MediaOperationException>(() => service.ShareAsync(Path.Combine(_root, "a.mp4")));
        Assert.Equal(WatchLinkService.StoredBytes(4500), await usage.LiveBytesAsync());
    }

    [Fact]
    public async Task A_damaged_usage_record_stops_uploads_instead_of_forgetting_them()
    {
        var (service, store, media, _, _) = CreateLimited(WatchLinkUsageLedger.DefaultCapBytes);
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        await File.WriteAllTextAsync(Path.Combine(_root, "data", WatchLinkUsageLedger.FileName), "{ not json");
        var error = await Assert.ThrowsAsync<MediaOperationException>(() => service.ShareAsync(Path.Combine(_root, "a.mp4")));
        Assert.Contains("usage record", error.Message);
        Assert.Equal(0, media.Calls);
        Assert.Empty(store.Log);
    }
}

public sealed class WatchCopyArgumentTests
{
    private static ProbeInfo Probe(bool video, bool audio, string? videoCodec = "h264", string? audioCodec = "aac", string? json = null)
        => new("in.mp4", TimeSpan.FromSeconds(60), 1000, video, audio, video ? 1280 : null, video ? 720 : null, video ? 30 : null, video ? videoCodec : null, audio ? audioCodec : null, 48000, 2, json);

    private static string Streams(string pixelFormat, string profile) => $$"""{"streams":[{"codec_type":"video","codec_name":"h264","pix_fmt":"{{pixelFormat}}","profile":"{{profile}}"},{"codec_type":"audio","codec_name":"aac"}]}""";

    [Fact]
    public void Browser_safe_streams_are_repackaged_and_others_converted()
    {
        Assert.Equal((true, true), MediaService.WatchStreamsPlayable(Probe(true, true, json: Streams("yuv420p", "High"))));
        Assert.Equal((false, true), MediaService.WatchStreamsPlayable(Probe(true, true, json: Streams("yuv420p10le", "High 10"))));
        Assert.Equal((false, true), MediaService.WatchStreamsPlayable(Probe(true, true, json: Streams("yuv444p", "High 4:4:4 Predictive"))));
        Assert.Equal((false, false), MediaService.WatchStreamsPlayable(Probe(true, true, "vp9", "opus", Streams("yuv420p", "Profile 0"))));
        Assert.Equal((true, false), MediaService.WatchStreamsPlayable(Probe(false, true, audioCodec: "mp3")));
    }

    [Fact]
    public void The_copy_is_fragmented_without_metadata_and_never_overwrites()
    {
        var copy = MediaService.WatchCopyArguments("in.mkv", "out.mp4", Probe(true, true), null, true);
        Assert.Equal(["-map", "0:V:0", "-map", "0:a:0", "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1", "-c:v", "copy", "-c:a", "copy", "-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-min_frag_duration", "2000000", "-f", "mp4", "-n", "out.mp4"],
            copy.SkipWhile(a => a != "-map"));
        var encode = MediaService.WatchCopyArguments("in.webm", "out.mp4", Probe(true, true, "vp9", "opus"), "libx264", false);
        Assert.Contains("expr:gte(t,n_forced*2)", encode);
        Assert.Equal(["-c:a", "aac", "-b:a", "160k", "-ac", "2"], encode.SkipWhile(a => a != "-c:a").Take(6));
        Assert.Equal("yuv420p", encode[encode.IndexOf("-pix_fmt") + 1]);
        var audio = MediaService.WatchCopyArguments("in.mp3", "out.mp4", Probe(false, true, audioCodec: "mp3"), null, false);
        Assert.DoesNotContain("0:V:0", audio);
        Assert.Equal(["-movflags", "+empty_moov+default_base_moof", "-frag_duration", "2000000"], audio.SkipWhile(a => a != "-movflags").Take(4));
    }
}
