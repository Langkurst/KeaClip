using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Klippi.Core;
using Klippi.Infrastructure.Sharing;
using Xunit;

namespace Klippi.Tests;

public sealed class SharingServiceTests
{
    private static readonly FileKiwiSharingOptions ImmediateRetries = new() { RetryDelay = TimeSpan.Zero, MaximumRetryDelay = TimeSpan.Zero };

    [Fact]
    public async Task V2EncryptsMetadataAndChunksUsesReturnedPolicyAndVerifiesCompletion()
    {
        using var file = new TestFile(Encoding.UTF8.GetBytes("0123456789"));
        using var server = new FakeServer(file.Bytes.Length);
        using var client = new HttpClient(server);
        var progress = new CaptureProgress();
        var result = await new FileKiwiSharingService(client, options: ImmediateRetries).ShareAsync(file.Path, progress: progress);
        byte[] key = DecodeKey(result.ShareUrl);
        Assert.Equal([1, 3, 2], server.Chunks);
        Assert.Equal("send", server.CreateRequest!.RootElement.GetProperty("mode").GetString());
        Assert.Equal("Shared with KeaClip", server.CreateRequest.RootElement.GetProperty("title").GetString());
        byte[] ske = Convert.FromBase64String(server.CreateRequest.RootElement.GetProperty("encryption").GetProperty("ske").GetString()!);
        Assert.Equal(result.ShareUrl.Split('#')[1], Encoding.UTF8.GetString(Rfc8188Encryption.Decrypt(ske, key)));
        var requestedFile = server.CreateRequest.RootElement.GetProperty("files")[0];
        Assert.Equal(file.Bytes.Length, requestedFile.GetProperty("filesize").GetInt64());
        Assert.Equal(System.IO.Path.GetFileName(file.Path), Encoding.UTF8.GetString(Rfc8188Encryption.Decrypt(Convert.FromBase64String(requestedFile.GetProperty("filename").GetString()!), key)));
        byte[] restored = server.Uploaded.OrderBy(kv => kv.Key).SelectMany(kv => Rfc8188Encryption.Decrypt(kv.Value, key)).ToArray();
        Assert.Equal(file.Bytes, restored);
        Assert.Equal(file.Bytes, await File.ReadAllBytesAsync(file.Path));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(file.Bytes)).ToLowerInvariant(), result.Sha256);
        Assert.Equal(12d, result.FreeDownloadHours);
        Assert.Equal(result.UploadedAt.AddHours(12), result.EstimatedFreeUntil);
        Assert.Contains("estimated", result.Warning);
        Assert.Equal(1, server.StatusRequests);
        Assert.True(server.AllHeadersPresent);
        Assert.Equal(JobStage.Finished, progress.Values[^1].Stage);
    }

    [Fact]
    public void UploadsIdentifyKeaClipWithoutReplacingACallersAgent()
    {
        using var plain = new HttpClient();
        _ = new FileKiwiSharingService(plain);
        Assert.Equal("KeaClip/" + AppIdentity.Version, plain.DefaultRequestHeaders.UserAgent.ToString());
        using var own = new HttpClient(); own.DefaultRequestHeaders.UserAgent.ParseAdd("Tester/1.0");
        _ = new FileKiwiSharingService(own);
        Assert.Equal("Tester/1.0", own.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Fact]
    public async Task MissingPolicyStaysUnknownInsteadOfInventingExpiry()
    {
        using var file = new TestFile([1, 2, 3]);
        using var server = new FakeServer(3) { FreeHours = null };
        using var client = new HttpClient(server);
        var result = await new FileKiwiSharingService(client).ShareAsync(file.Path);
        Assert.Null(result.FreeDownloadHours);
        Assert.Null(result.EstimatedFreeUntil);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task UploadCompletionMustBeExplicitAndConsistent(bool complete, bool missing)
    {
        using var file = new TestFile([1, 2, 3]);
        using var server = new FakeServer(3) { Complete = complete, Missing = missing };
        using var client = new HttpClient(server);
        var exception = await Assert.ThrowsAsync<MediaOperationException>(() => new FileKiwiSharingService(client).ShareAsync(file.Path));
        Assert.Contains("not confirmed", exception.Message);
        Assert.Equal(file.Bytes, await File.ReadAllBytesAsync(file.Path));
    }

    [Theory]
    [InlineData("https://attacker.example/")]
    [InlineData("http://filekiwi.example.r2.cloudflarestorage.com/")]
    [InlineData("https://filekiwi.example.r2.cloudflarestorage.com.attacker.example/")]
    [InlineData("https://user:password@filekiwi.example.r2.cloudflarestorage.com/")]
    public async Task UntrustedStorageUrlsAreRejectedBeforeUpload(string head)
    {
        using var file = new TestFile([1, 2, 3]);
        using var server = new FakeServer(3) { Head = head };
        using var client = new HttpClient(server);
        await Assert.ThrowsAsync<MediaOperationException>(() => new FileKiwiSharingService(client).ShareAsync(file.Path));
        Assert.Empty(server.Chunks);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("prefix/../outside")]
    [InlineData("prefix?secret=bad")]
    [InlineData("%2e%2e/outside")]
    public async Task UnsafeStoragePathsAreRejectedBeforeUpload(string path)
    {
        using var file = new TestFile([1, 2, 3]);
        using var server = new FakeServer(3) { UploadPath = path };
        using var client = new HttpClient(server);
        await Assert.ThrowsAsync<MediaOperationException>(() => new FileKiwiSharingService(client).ShareAsync(file.Path));
        Assert.Empty(server.Chunks);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(4, 99)]
    [InlineData(134217729, 1)]
    public async Task InconsistentOrUnboundedChunkRegistrationsAreRejected(int size, int count)
    {
        using var file = new TestFile([1, 2, 3]);
        using var server = new FakeServer(3) { ChunkSize = size, ChunkCountOverride = count };
        using var client = new HttpClient(server);
        await Assert.ThrowsAsync<MediaOperationException>(() => new FileKiwiSharingService(client).ShareAsync(file.Path));
        Assert.Empty(server.Chunks);
    }

    [Fact]
    public async Task TransientPutFailureRetriesIdenticalCiphertextOnlyWithinBound()
    {
        using var file = new TestFile([1, 2, 3]);
        using var server = new FakeServer(3) { FailPutAttempts = 2 };
        using var client = new HttpClient(server);
        await new FileKiwiSharingService(client, options: ImmediateRetries).ShareAsync(file.Path);
        Assert.Equal([1, 1, 1], server.Chunks);
        Assert.All(server.AttemptBodies, body => Assert.Equal(server.AttemptBodies[0], body));
        Assert.Equal(1, server.CreateRequests);
        Assert.Equal(1, server.StatusRequests);
    }

    [Fact]
    public async Task FailedPostIsNotRetriedBecauseCreationIsNotIdempotent()
    {
        using var file = new TestFile([1]);
        using var server = new FakeServer(1) { CreateStatus = HttpStatusCode.ServiceUnavailable };
        using var client = new HttpClient(server);
        await Assert.ThrowsAsync<MediaOperationException>(() => new FileKiwiSharingService(client, options: ImmediateRetries).ShareAsync(file.Path));
        Assert.Equal(1, server.CreateRequests);
        Assert.Empty(server.Chunks);
    }

    [Fact]
    public async Task PersistentPutFailureStopsAfterThreeAttemptsAndLeaksNoProviderSecrets()
    {
        using var file = new TestFile([1]);
        using var server = new FakeServer(1) { FailPutAttempts = 100 };
        using var client = new HttpClient(server);
        var exception = await Assert.ThrowsAsync<MediaOperationException>(() => new FileKiwiSharingService(client, options: ImmediateRetries).ShareAsync(file.Path));
        Assert.Equal(3, server.Chunks.Count);
        Assert.Equal(0, server.StatusRequests);
        Assert.DoesNotContain(FakeServer.SyntheticAuth, exception.ToString() + exception.Details);
        Assert.DoesNotContain("synthetic-provider-secret", exception.ToString() + exception.Details);
        Assert.Null(exception.InnerException);
        Assert.Equal(file.Bytes, await File.ReadAllBytesAsync(file.Path));
    }

    [Fact]
    public async Task RetryAfterIsRespectedWithoutPrematureRetry()
    {
        using var file = new TestFile([1]);
        using var server = new FakeServer(1) { FailPutAttempts = 1, FailureStatus = HttpStatusCode.TooManyRequests, RetryAfter = TimeSpan.FromMilliseconds(100) };
        using var client = new HttpClient(server);
        var stopwatch = Stopwatch.StartNew();
        await new FileKiwiSharingService(client, options: new() { RetryDelay = TimeSpan.Zero, MaximumRetryDelay = TimeSpan.FromSeconds(1) }).ShareAsync(file.Path);
        Assert.True(stopwatch.ElapsedMilliseconds >= 85);
        Assert.Equal(2, server.Chunks.Count);
    }

    [Fact]
    public async Task LongRetryAfterFailsSafelyWithoutShorteningProviderInterval()
    {
        using var file = new TestFile([1]);
        using var server = new FakeServer(1) { FailPutAttempts = 100, FailureStatus = HttpStatusCode.TooManyRequests, RetryAfter = TimeSpan.FromMinutes(2) };
        using var client = new HttpClient(server);
        var exception = await Assert.ThrowsAsync<MediaOperationException>(() => new FileKiwiSharingService(client).ShareAsync(file.Path));
        Assert.Contains("longer wait", exception.Message);
        Assert.Single(server.Chunks);
    }

    [Fact]
    public async Task CancelledUploadStopsWithoutCompletionAndPreservesSource()
    {
        using var file = new TestFile(Encoding.UTF8.GetBytes("0123456789"));
        using var cancellation = new CancellationTokenSource();
        using var server = new FakeServer(10) { AfterPut = () => cancellation.Cancel() };
        using var client = new HttpClient(server);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileKiwiSharingService(client).ShareAsync(file.Path, cancellationToken: cancellation.Token));
        Assert.Single(server.Chunks);
        Assert.Equal(0, server.StatusRequests);
        Assert.Equal(file.Bytes, await File.ReadAllBytesAsync(file.Path));
    }

    [Fact]
    public async Task RequestTimeoutIsBoundedAndDoesNotExposeNetworkException()
    {
        using var file = new TestFile([1]);
        using var server = new FakeServer(1) { DelayCreate = true };
        using var client = new HttpClient(server);
        var exception = await Assert.ThrowsAsync<MediaOperationException>(() => new FileKiwiSharingService(client, options: new() { RequestTimeout = TimeSpan.FromMilliseconds(100) }).ShareAsync(file.Path));
        Assert.Contains("in time", exception.Message);
        Assert.Equal(1, server.CreateRequests);
    }

    [Fact]
    public async Task CancellingDuringPutAbortsRequestAndNeverChecksCompletion()
    {
        using var file = new TestFile([1, 2, 3]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        using var server = new FakeServer(3) { DelayPut = true };
        using var client = new HttpClient(server);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileKiwiSharingService(client).ShareAsync(file.Path, cancellationToken: cancellation.Token));
        Assert.Single(server.Chunks);
        Assert.Equal(0, server.StatusRequests);
        Assert.Equal(file.Bytes, await File.ReadAllBytesAsync(file.Path));
    }

    [Fact]
    public async Task NetworkExceptionSecretsAreRemovedAlongWithInnerException()
    {
        using var file = new TestFile([1]);
        using var server = new FakeServer(1) { ThrowCreate = true };
        using var client = new HttpClient(server);
        var exception = await Assert.ThrowsAsync<MediaOperationException>(() => new FileKiwiSharingService(client).ShareAsync(file.Path));
        Assert.DoesNotContain("synthetic-provider-secret", exception.ToString() + exception.Details);
        Assert.Null(exception.InnerException);
        Assert.Equal(1, server.CreateRequests);
    }

    [Fact]
    public async Task OversizedApiResponseIsRejectedBeforeDeserialization()
    {
        using var file = new TestFile([1]);
        using var server = new FakeServer(1) { CreateBodyOverride = new string(' ', FileKiwiSharingService.MaximumResponseBytes + 1) };
        using var client = new HttpClient(server);
        var exception = await Assert.ThrowsAsync<MediaOperationException>(() => new FileKiwiSharingService(client).ShareAsync(file.Path));
        Assert.Contains("unsupported response", exception.Message);
        Assert.Empty(server.Chunks);
    }

    [Fact]
    public async Task SourceMutationIsDeniedOrDetectedBeforeReturningSuccess()
    {
        using var file = new TestFile(Encoding.UTF8.GetBytes("0123456789"));
        bool denied = false;
        using var server = new FakeServer(10) { AfterPut = () =>
        {
            try { File.WriteAllBytes(file.Path, Encoding.UTF8.GetBytes("9876543210")); }
            catch (IOException) { denied = true; }
        } };
        using var client = new HttpClient(server);
        if (OperatingSystem.IsWindows())
        {
            await new FileKiwiSharingService(client).ShareAsync(file.Path);
            Assert.True(denied);
            Assert.Equal(file.Bytes, await File.ReadAllBytesAsync(file.Path));
        }
        else
        {
            var exception = await Assert.ThrowsAsync<MediaOperationException>(() => new FileKiwiSharingService(client).ShareAsync(file.Path));
            Assert.Contains("changed", exception.Message);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json synthetic-provider-secret")]
    public async Task MalformedProviderResponsesFailWithSanitizedErrors(string response)
    {
        using var file = new TestFile([1]);
        using var server = new FakeServer(1) { CreateBodyOverride = response };
        using var client = new HttpClient(server);
        var exception = await Assert.ThrowsAsync<MediaOperationException>(() => new FileKiwiSharingService(client).ShareAsync(file.Path));
        Assert.DoesNotContain("synthetic-provider-secret", exception.ToString() + exception.Details);
        Assert.Empty(server.Chunks);
    }

    [Fact]
    public async Task EmptyInputIsRejectedWithoutServiceMutation()
    {
        using var file = new TestFile([]);
        using var server = new FakeServer(0);
        using var client = new HttpClient(server);
        await Assert.ThrowsAsync<MediaOperationException>(() => new FileKiwiSharingService(client).ShareAsync(file.Path));
        Assert.Equal(0, server.CreateRequests);
    }

    private static byte[] DecodeKey(string shareUrl)
    {
        string key = shareUrl.Split('#')[1].Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(key + new string('=', (4 - key.Length % 4) % 4));
    }

    private sealed class TestFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "keaclip-sharing-" + Guid.NewGuid().ToString("N") + ".txt");
        public byte[] Bytes { get; }
        public TestFile(byte[] bytes) { Bytes = bytes; File.WriteAllBytes(Path, bytes); }
        public void Dispose() { File.Delete(Path); }
    }

    private sealed class CaptureProgress : IProgress<JobProgress>
    {
        public List<JobProgress> Values { get; } = [];
        public void Report(JobProgress value) => Values.Add(value);
    }

    private sealed class FakeServer(int sourceLength) : HttpMessageHandler
    {
        public const string SyntheticAuth = "synthetic-test-upload-auth";
        public string Head { get; init; } = "https://filekiwi.test.r2.cloudflarestorage.com/";
        public string UploadPath { get; init; } = "opaque/folder/1/file";
        public int ChunkSize { get; init; } = 4;
        public int? ChunkCountOverride { get; init; }
        public double? FreeHours { get; init; } = 12;
        public bool Complete { get; init; } = true;
        public bool Missing { get; init; }
        public int FailPutAttempts { get; init; }
        public HttpStatusCode FailureStatus { get; init; } = HttpStatusCode.ServiceUnavailable;
        public HttpStatusCode CreateStatus { get; init; } = HttpStatusCode.OK;
        public TimeSpan? RetryAfter { get; init; }
        public Action? AfterPut { get; init; }
        public bool DelayCreate { get; init; }
        public bool DelayPut { get; init; }
        public bool ThrowCreate { get; init; }
        public string? CreateBodyOverride { get; init; }
        public int CreateRequests { get; private set; }
        public int StatusRequests { get; private set; }
        public List<int> Chunks { get; } = [];
        public List<byte[]> AttemptBodies { get; } = [];
        public Dictionary<int, byte[]> Uploaded { get; } = [];
        public JsonDocument? CreateRequest { get; private set; }
        public bool AllHeadersPresent { get; private set; } = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                CreateRequests++;
                Assert.Equal("/v2/folders", request.RequestUri!.AbsolutePath);
                CreateRequest = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (ThrowCreate) throw new HttpRequestException("https://api.file.kiwi/folder#synthetic-provider-secret");
                if (DelayCreate) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                int count = ChunkCountOverride ?? Math.Max(1, (sourceLength + ChunkSize - 1) / ChunkSize);
                return Json(CreateBodyOverride ?? JsonSerializer.Serialize(new
                {
                    folderId = "folder123", folderUrl = "https://file.kiwi/folder123", uploadAuth = SyntheticAuth,
                    files = new[] { new { fileId = "file123", chunkSize = ChunkSize, chunks = count, freeDownloadHours = FreeHours,
                        uploadUrls = new { head = Head, path = UploadPath, tail = "X-Amz-Algorithm=AWS4-HMAC-SHA256", signatures = Enumerable.Repeat("a1b2c3", count).ToArray(), headers = new Dictionary<string, string> { ["x-amz-meta-folder_id"] = "folder123", ["x-amz-meta-chunks"] = count.ToString() } } } }
                }), CreateStatus);
            }
            if (request.Method == HttpMethod.Put)
            {
                int chunk = int.Parse(request.RequestUri!.Segments[^1]);
                Chunks.Add(chunk);
                byte[] bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                AttemptBodies.Add(bytes);
                AllHeadersPresent &= request.Headers.Contains("x-amz-meta-folder_id") && request.Headers.Contains("x-amz-meta-chunks") && request.Content.Headers.ContentLength == bytes.Length;
                if (DelayPut) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                if (Chunks.Count <= FailPutAttempts)
                {
                    var failed = Json("{\"error\":\"synthetic-provider-secret\"}", FailureStatus);
                    if (RetryAfter.HasValue) failed.Headers.RetryAfter = new RetryConditionHeaderValue(RetryAfter.Value);
                    return failed;
                }
                Uploaded[chunk] = bytes;
                AfterPut?.Invoke();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            StatusRequests++;
            Assert.Equal("/v2/folders/folder123/files/file123/upload-status", request.RequestUri!.AbsolutePath);
            Assert.Equal("?uploadAuth=" + SyntheticAuth, request.RequestUri.Query);
            return Json(JsonSerializer.Serialize(new { complete = Complete, missing = Missing ? new[] { 1 } : Array.Empty<int>() }));
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        protected override void Dispose(bool disposing) { if (disposing) CreateRequest?.Dispose(); base.Dispose(disposing); }
    }
}
