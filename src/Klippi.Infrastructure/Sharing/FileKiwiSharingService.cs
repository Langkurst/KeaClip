using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Klippi.Core;
using Microsoft.Win32.SafeHandles;

namespace Klippi.Infrastructure.Sharing;

public sealed class FileKiwiSharingOptions
{
    /// <summary>Deadline for API calls, and for the storage response once a chunk body has been sent.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Attempts allowed while File.Kiwi or its storage answers with a retryable status (408, 429 or 5xx).</summary>
    public int MaximumAttempts { get; init; } = 3;
    public int MaximumChunkBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>A chunk upload is abandoned only after no bytes could be sent for this long, so slow but steady connections still finish.</summary>
    public TimeSpan UploadStallTimeout { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>
    /// How long a chunk upload or status check keeps retrying a lost connection. It counts from the request's first failure, restarts whenever
    /// data flowed again or the service answered, and leaves out time the computer spent asleep or hibernated.
    /// </summary>
    public TimeSpan ConnectionOutageBudget { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>Deadline for the best-effort status call that keeps a long upload alive. It runs between chunks, so it is kept short.</summary>
    public TimeSpan UploadStatusTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    // Tests only: re-read the whole source after upload even where the open share lock already denies other writers.
    internal bool AlwaysRecheckSource { get; init; }
    // Tests only: the drive type of the volume that really holds the opened source (given its resolved final path) instead of asking Windows.
    internal Func<string, DriveType>? DriveTypeOf { get; init; }
}

/// <summary>Consent is supplied by the caller's explicit Share action. This adapter only uses free v2 uploads.</summary>
public sealed class FileKiwiSharingService : ISharingService
{
    private const long MaximumFileBytes = 999L * 1024 * 1024 * 1024;
    // 999 GiB with the documented example 10 MiB chunks needs about 100,000 signatures.
    internal const int MaximumResponseBytes = 16_777_216;
    private const int UploadSliceBytes = 65_536, RecheckBufferBytes = 1_048_576;
    private const string UploadExpiredMessage = "File.Kiwi upload expired.";
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200), MinimumOutageDelay = TimeSpan.FromMilliseconds(50);
    // A chunk attempt that sent data before its connection broke restarts the outage budget, but only this many times, so a link that always cuts the body still stops.
    internal const int MaximumInterruptedAttempts = 10;
    // Scheduling and cancellation latency allowed on top of the back-off and attempt deadline before a gap between failures counts as sleep.
    private static readonly TimeSpan FailureGapAllowance = TimeSpan.FromSeconds(10);
    // v2 docs: upload-status with the matching uploadAuth can extend a file that has under 2 hours left during a long upload; presigned URLs last 36 hours.
    // A failed check is retried after a short back-off (well inside the 2-hour window) rather than after every chunk.
    internal static readonly TimeSpan UploadStatusInterval = TimeSpan.FromMinutes(30), UploadStatusRetryDelay = TimeSpan.FromMinutes(5), SignatureValidity = TimeSpan.FromHours(36);
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly FileKiwiSharingOptions _options;

    /// <remarks>The service bounds every request itself, so it turns off the client's whole-request <see cref="HttpClient.Timeout"/> (100 s by default),
    /// which would otherwise cancel slow but steady chunk uploads. Pass a client dedicated to sharing: a client that has already sent requests with a
    /// finite timeout cannot be changed and is rejected.</remarks>
    public FileKiwiSharingService(HttpClient httpClient, Uri? apiEndpoint = null, FileKiwiSharingOptions? options = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        // Say who is calling, so File.Kiwi can tell KeaClip uploads apart (a caller's own agent is kept).
        if (!_http.DefaultRequestHeaders.UserAgent.Any()) _http.DefaultRequestHeaders.UserAgent.ParseAdd("KeaClip/" + AppIdentity.Version);
        _endpoint = apiEndpoint ?? new Uri("https://api.file.kiwi/");
        if (!_endpoint.IsAbsoluteUri || !string.IsNullOrEmpty(_endpoint.UserInfo) || !string.IsNullOrEmpty(_endpoint.Query) || !string.IsNullOrEmpty(_endpoint.Fragment)
            || (_endpoint.Scheme != Uri.UriSchemeHttps && !(_endpoint.IsLoopback && _endpoint.Scheme == Uri.UriSchemeHttp)))
            throw new ArgumentException("The API endpoint must use HTTPS (HTTP is allowed only for local tests).", nameof(apiEndpoint));
        _options = options ?? new FileKiwiSharingOptions();
        if (_options.RequestTimeout <= TimeSpan.Zero || _options.RequestTimeout > TimeSpan.FromHours(1)
            || _options.MaximumAttempts is < 1 or > 5 || _options.RetryDelay < TimeSpan.Zero
            || _options.MaximumRetryDelay < TimeSpan.Zero || _options.MaximumRetryDelay > TimeSpan.FromMinutes(1)
            || _options.MaximumChunkBytes is < 1 or > 128 * 1024 * 1024
            || _options.UploadStallTimeout <= TimeSpan.Zero || _options.UploadStallTimeout > TimeSpan.FromHours(1)
            || _options.ConnectionOutageBudget < TimeSpan.Zero || _options.ConnectionOutageBudget > TimeSpan.FromHours(1)
            || _options.UploadStatusTimeout <= TimeSpan.Zero || _options.UploadStatusTimeout > TimeSpan.FromHours(1) || _options.TimeProvider is null)
            throw new ArgumentException("Sharing limits are invalid.", nameof(options));
        if (_http.Timeout != Timeout.InfiniteTimeSpan)
        {
            // A used or disposed client cannot be changed. Keeping its whole-request limit would silently fail every chunk slower than that limit again.
            try { _http.Timeout = Timeout.InfiniteTimeSpan; }
            catch (InvalidOperationException) { throw new ArgumentException("Pass an HttpClient dedicated to sharing, with an infinite Timeout or not yet used.", nameof(httpClient)); }
        }
    }

    // File reads, hashing and encryption must never run on the caller's (UI) thread. Progress still reaches the UI through the caller's IProgress.
    public Task<ShareResult> ShareAsync(string path, string? title = null, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => ShareCoreAsync(path, title, progress, cancellationToken), cancellationToken);

    private async Task<ShareResult> ShareCoreAsync(string path, string? title, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path)) throw new MediaOperationException("Choose a file to share.");
        string folderTitle = string.IsNullOrWhiteSpace(title) ? "Shared with KeaClip" : title.Trim();
        if (folderTitle.Length > 200 || folderTitle.Any(char.IsControl)) throw new MediaOperationException("The sharing title must contain at most 200 characters.");
        TimeProvider clock = _options.TimeProvider;
        byte[] key = RandomNumberGenerator.GetBytes(16);
        byte[] plain = [], cipher = [];
        try
        {
            string fullPath = Path.GetFullPath(path);
            var source = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.RandomAccess);
            await using var sourceScope = source.ConfigureAwait(false);
            long sourceLength = source.Length;
            if (sourceLength is <= 0 or > MaximumFileBytes) throw new MediaOperationException("File.Kiwi sharing needs a nonempty file of at most 999 GiB.");
            string secret = Base64Url(key);
            byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
            string ske;
            try { ske = Convert.ToBase64String(Rfc8188Encryption.Encrypt(secretBytes, key)); }
            finally { CryptographicOperations.ZeroMemory(secretBytes); }
            string encryptedName = Convert.ToBase64String(Rfc8188Encryption.Encrypt(Encoding.UTF8.GetBytes(Path.GetFileName(path)), key));
            Uri createUri = new(_endpoint, "v2/folders");
            var payload = new { title = folderTitle, mode = "send", encryption = new { ske }, files = new[] { new { filename = encryptedName, filesize = sourceLength } } };
            progress?.Report(new JobProgress(JobStage.Uploading, 0, "Preparing an encrypted share…"));
            // Creation is deliberately not retried: the API does not document an idempotency key.
            using var created = await SendAsync(_ => new HttpRequestMessage(HttpMethod.Post, createUri) { Content = JsonContent.Create(payload) }, false, _options.RequestTimeout, progress, cancellationToken).ConfigureAwait(false);
            using var folderJson = await ReadJsonAsync(created, cancellationToken).ConfigureAwait(false);
            var registration = ParseRegistration(folderJson.RootElement, sourceLength);
            long registeredAt = clock.GetTimestamp();
            TimeSpan statusCheckDue = UploadStatusInterval;
            Uri statusUri = new(_endpoint, $"v2/folders/{registration.FolderId}/files/{registration.FileId}/upload-status?uploadAuth={Uri.EscapeDataString(registration.UploadAuth)}");
            // One plaintext and one ciphertext buffer per share; a retry resends the identical ciphertext.
            int bufferLength = (int)Math.Min(registration.ChunkSize, sourceLength);
            plain = new byte[bufferLength];
            cipher = new byte[Rfc8188Encryption.EncryptedLength(bufferLength)];
            // The checksum is built from exactly the plaintext that is uploaded, in file order, instead of separate whole-file passes.
            // The last chunk is uploaded second, so only its own hash is kept until it is re-read and appended at the end.
            using var uploadedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[]? lastChunkHash = null;
            int lastChunk = registration.Chunks - 1, sent = 0;
            long uploaded = 0;
            var throttle = new ProgressThrottle(clock);
            foreach (int chunkIndex in ChunkOrder(registration.Chunks))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clock.GetElapsedTime(registeredAt) >= SignatureValidity) throw UploadExpired("The upload links are older than File.Kiwi's 36-hour limit.");
                if (source.Length != sourceLength) throw ChangedFile();
                long offset = checked((long)chunkIndex * registration.ChunkSize);
                int length = (int)Math.Min(registration.ChunkSize, sourceLength - offset), cipherLength;
                try
                {
                    source.Position = offset;
                    await source.ReadExactlyAsync(plain.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                    if (chunkIndex == lastChunk && lastChunk > 0) lastChunkHash = SHA256.HashData(plain.AsSpan(0, length));
                    else uploadedHash.AppendData(plain, 0, length);
                    cipherLength = Rfc8188Encryption.EncryptInto(plain.AsSpan(0, length), cipher, key);
                }
                finally { CryptographicOperations.ZeroMemory(plain.AsSpan(0, length)); }
                long before = uploaded;
                Uri putUri = ChunkUrl(registration, chunkIndex);
                // Progress advances while the body is sent; a retry restarts from this chunk's starting value.
                void Sent(long written)
                {
                    if (written < cipherLength && throttle.Due())
                        progress?.Report(new JobProgress(JobStage.Uploading, 100d * (before + (double)written * length / cipherLength) / sourceLength, "Uploading encrypted data…"));
                }
                using var put = await SendAsync(attempt =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Put, putUri) { Content = new ChunkContent(cipher, cipherLength, attempt, _options.UploadStallTimeout, _options.RequestTimeout, Sent) };
                    // Normal chunks are sent like File.Kiwi's own browser uploader, without "Expect: 100-continue". Storage that refuses a link (for example
                    // an expired one) may answer early and close, which looks like a connection cut off mid-body. Only then does the retry ask to continue
                    // first, so a refusal arrives before the body instead of the chunk being re-sent until the retry limits run out.
                    if (attempt.FollowsInterruption) request.Headers.ExpectContinue = true;
                    foreach (var header in registration.Headers)
                    {
                        if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value)
                            && !request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value))
                        {
                            request.Dispose();
                            throw InvalidResponse();
                        }
                    }
                    return request;
                }, true, _options.UploadStallTimeout, progress, cancellationToken,
                    status => status == 403 ? UploadExpired("HTTP 403. Storage refused the upload link, which usually means it expired.") : null).ConfigureAwait(false);
                uploaded += length;
                progress?.Report(new JobProgress(JobStage.Uploading, 100d * uploaded / sourceLength, "Uploading encrypted data…"));
                if (++sent < registration.Chunks && clock.GetElapsedTime(registeredAt) >= statusCheckDue)
                {
                    bool extended = await ExtendUploadAsync(statusUri, cancellationToken).ConfigureAwait(false);
                    statusCheckDue = clock.GetElapsedTime(registeredAt) + (extended ? UploadStatusInterval : UploadStatusRetryDelay);
                }
            }

            bool recheck = _options.AlwaysRecheckSource || !ShareLockDeniesWriters(source);
            progress?.Report(recheck ? new JobProgress(JobStage.Verifying, 0, "Checking that the file did not change…") : new JobProgress(JobStage.Verifying, 100, "Verifying the completed upload…"));
            if (source.Length != sourceLength) throw ChangedFile();
            if (lastChunkHash is not null)
            {
                long offset = (long)lastChunk * registration.ChunkSize;
                int length = (int)(sourceLength - offset);
                try
                {
                    source.Position = offset;
                    await source.ReadExactlyAsync(plain.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(plain.AsSpan(0, length)), lastChunkHash)) throw ChangedFile();
                    uploadedHash.AppendData(plain, 0, length);
                }
                finally { CryptographicOperations.ZeroMemory(plain.AsSpan(0, length)); }
            }
            byte[] sha256 = uploadedHash.GetHashAndReset();
            if (recheck)
            {
                // Where share locks are not enforced (other systems, network paths), confirm that the source still matches the uploaded bytes.
                await RecheckSourceAsync(source, sourceLength, sha256, progress, cancellationToken).ConfigureAwait(false);
                progress?.Report(new JobProgress(JobStage.Verifying, 100, "Verifying the completed upload…"));
            }
            using var status = await SendAsync(_ => new HttpRequestMessage(HttpMethod.Get, statusUri), true, _options.RequestTimeout, progress, cancellationToken).ConfigureAwait(false);
            using var statusJson = await ReadJsonAsync(status, cancellationToken).ConfigureAwait(false);
            JsonElement root = statusJson.RootElement;
            if (!root.TryGetProperty("complete", out var complete) || complete.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("missing", out var missing) || missing.ValueKind != JsonValueKind.Array || missing.GetArrayLength() != 0)
                throw new MediaOperationException("File.Kiwi has not confirmed the complete upload.", "The local file is safe. Share again to start a new upload.");

            DateTimeOffset uploadedAt = clock.GetUtcNow();
            DateTimeOffset? freeUntil = registration.FreeHours is { } hours ? uploadedAt.AddHours(hours) : null;
            TimeSpan duration = clock.GetElapsedTime(registeredAt);
            string warning = "Free availability is estimated from the service's returned policy; no absolute expiry was returned. Basic files are temporary. Paid retention is not available through this adapter."
                + (duration >= TimeSpan.FromMinutes(10) ? $" The upload took about {Math.Round(duration.TotalMinutes):0} minutes; the free window may have started when it began, so it can end earlier than estimated." : "");
            progress?.Report(new JobProgress(JobStage.Finished, 100, "Encrypted share verified."));
            return new ShareResult(registration.ShareBase + "#" + secret, registration.FolderId, registration.FileId, registration.FreeHours, uploadedAt, freeUntil, Convert.ToHexString(sha256).ToLowerInvariant(), warning);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (MediaOperationException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException or CryptographicException or KeyNotFoundException or FormatException or HttpRequestException)
        {
            // Runtime/provider exceptions may contain media paths, signed URLs or server bodies. Do not retain them as inner exceptions.
            throw new MediaOperationException("This file could not be shared with File.Kiwi.", "The source file was preserved. Check file access, the network and the service, then try again.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(cipher);
        }
    }

    /// <summary>
    /// Sends one logical request. A retryable (idempotent) request is retried up to <see cref="FileKiwiSharingOptions.MaximumAttempts"/> times while the
    /// service answers with a retryable status, and with jittered exponential backoff for up to <see cref="FileKiwiSharingOptions.ConnectionOutageBudget"/>
    /// without forward progress while the connection fails or stalls. The factory receives the attempt so that a request body can extend its deadline
    /// and record progress while bytes are flowing.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<Attempt, HttpRequestMessage> createRequest, bool retryable, TimeSpan attemptTimeout, IProgress<JobProgress>? progress,
        CancellationToken cancellationToken, Func<int, MediaOperationException?>? refusal = null)
    {
        TimeProvider clock = _options.TimeProvider;
        int refusals = 0, failures = 0, interrupted = 0;
        long lastFailure = 0;
        TimeSpan outage = TimeSpan.Zero, backoff = TimeSpan.Zero;
        while (true)
        {
            bool timedOut = false;
            TimeSpan? statusDelay = null;
            long progressed;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(attemptTimeout);
                var attempt = new Attempt(deadline, clock, interrupted > 0);
                using var request = createRequest(attempt);
                try
                {
                    var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                    if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != request.RequestUri)
                    {
                        response.Dispose();
                        throw new MediaOperationException("File.Kiwi redirected the request unexpectedly.");
                    }
                    if (response.IsSuccessStatusCode) return response;
                    int status = (int)response.StatusCode;
                    if (refusal?.Invoke(status) is { } specific)
                    {
                        response.Dispose();
                        throw specific;
                    }
                    bool transient = status == 408 || status == 429 || status >= 500;
                    TimeSpan delay;
                    try { delay = RetryDelay(response, ++refusals); }
                    finally { response.Dispose(); }
                    if (!retryable || !transient || refusals >= _options.MaximumAttempts)
                        throw new MediaOperationException("File.Kiwi could not complete the request.", $"HTTP {status}. The source file was preserved; no paid capacity was requested.");
                    statusDelay = delay;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { timedOut = true; }
                catch (HttpRequestException) { }
                progressed = attempt.LastProgress;
            }
            if (statusDelay is { } wait)
            {
                // The service answered, so the connection works: a later outage gets a fresh budget.
                (lastFailure, outage, failures) = (0, TimeSpan.Zero, 0);
                await WaitRetry(wait, $"File.Kiwi is temporarily unavailable. Retrying in {Seconds(wait)} s…", progress, cancellationToken).ConfigureAwait(false);
                continue;
            }
            long now = clock.GetTimestamp();
            if (progressed != 0)
            {
                // Data was flowing until the connection broke or stalled, so a new outage starts at this failure. It is not dated from the last sent slice:
                // that would count a sleep in the middle of a chunk, or a stall deadline longer than the budget, as outage and give up without a retry.
                if (!retryable || ++interrupted >= MaximumInterruptedAttempts) throw ConnectionFailure(timedOut, retryable);
                (outage, failures) = (TimeSpan.Zero, 0);
            }
            // Only time spent awake and retrying counts. A gap since the previous failure that is longer than its back-off plus one attempt deadline can
            // explain means the computer slept or hibernated (timestamps include that time on Windows), so the excess is left out.
            else if (lastFailure != 0) outage += Min(clock.GetElapsedTime(lastFailure, now), backoff + attemptTimeout + FailureGapAllowance);
            lastFailure = now;
            if (!retryable || outage >= _options.ConnectionOutageBudget) throw ConnectionFailure(timedOut, false);
            backoff = OutageDelay(++failures);
            await WaitRetry(backoff, $"Connection lost. Retrying the encrypted upload in {Seconds(backoff)} s…", progress, cancellationToken).ConfigureAwait(false);
        }
    }

    // Best effort: an upload-status call with the matching uploadAuth lets File.Kiwi extend a file that is close to expiring during a long upload.
    // It runs between chunks under its own short deadline, so a slow API delays the upload only briefly. Returns false when the check could not be
    // made, so it is tried again after a back-off. Only a missing upload stops sharing.
    private async Task<bool> ExtendUploadAsync(Uri statusUri, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.UploadStatusTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, statusUri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) throw UploadExpired($"HTTP {(int)response.StatusCode}. File.Kiwi no longer lists this upload.");
            if (!response.IsSuccessStatusCode || (response.RequestMessage?.RequestUri is { } finalUri && finalUri != request.RequestUri)) return false;
            using var json = await ReadJsonAsync(response, deadline.Token).ConfigureAwait(false);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("complete", out var complete) && complete.ValueKind is JsonValueKind.True or JsonValueKind.False;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException
            || (exception is MediaOperationException && exception.Message != UploadExpiredMessage)) { return false; }
    }

    private static async Task RecheckSourceAsync(FileStream source, long sourceLength, byte[] expected, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[(int)Math.Min(RecheckBufferBytes, sourceLength)];
        var throttle = new ProgressThrottle(TimeProvider.System);
        long done = 0;
        try
        {
            source.Position = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                done += read;
                if (done > sourceLength) throw ChangedFile();
                hash.AppendData(buffer, 0, read);
                if (done < sourceLength && throttle.Due()) progress?.Report(new JobProgress(JobStage.Verifying, 100d * done / sourceLength, "Checking that the file did not change…"));
            }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
        if (done != sourceLength || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected)) throw ChangedFile();
    }

    // Windows enforces the FileShare.Read lock on local volumes, so nothing else can write the open source and a second full read adds nothing.
    // Network and redirected paths may be changed by systems that ignore Windows share modes, so those are re-read. The opened handle's final path
    // is classified, so a junction or symbolic link that leads to a network share is re-read too, and so is a path that cannot be resolved.
    private bool ShareLockDeniesWriters(FileStream source)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return LocalVolumePath(FinalPath(source.SafeFileHandle), _options.DriveTypeOf ?? (path => new DriveInfo(Path.GetPathRoot(path)!).DriveType)); }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }

    internal static bool LocalVolumePath(string? finalPath, Func<string, DriveType> driveType)
        => finalPath is not null && Path.GetPathRoot(finalPath) is { Length: 3 } root && char.IsAsciiLetter(root[0]) && root[1] == ':' && root[2] == '\\'
            && driveType(finalPath) is DriveType.Fixed or DriveType.Removable;

    // Windows only: where the open file really is once every junction, symbolic link and mapped drive is followed, as C:\... on a local volume
    // or \\?\UNC\server\share\... for a network file. Null when Windows cannot name it with a drive letter or a share.
    internal static string? FinalPath(SafeFileHandle handle)
    {
        char[] buffer = new char[512];
        for (int attempt = 0; attempt < 2; attempt++)
        {
            uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0) return null;
            if (length < buffer.Length)
            {
                string path = new(buffer, 0, (int)length);
                return path.Length >= 7 && path.StartsWith(@"\\?\", StringComparison.Ordinal) && char.IsAsciiLetter(path[4]) && path[5] == ':' && path[6] == '\\' ? path[4..] : path;
            }
            buffer = new char[length];
        }
        return null;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);

    private async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        try
        {
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw InvalidResponse();
            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var streamScope = stream.ConfigureAwait(false);
            using var data = new MemoryStream();
            byte[] buffer = new byte[16_384];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (data.Length + read > MaximumResponseBytes) throw InvalidResponse();
                await data.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            }
            return JsonDocument.Parse(data.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MediaOperationException("File.Kiwi's response timed out.");
        }
    }

    private Registration ParseRegistration(JsonElement root, long sourceLength)
    {
        string folderId = String(root, "folderId"), auth = String(root, "uploadAuth");
        if (!Identifier(folderId) || auth.Length is < 1 or > 1024 || auth.Any(char.IsControl)) throw InvalidResponse();
        string share = root.TryGetProperty("folderUrlBase", out var baseUrl) ? baseUrl.GetString() ?? "" : String(root, "folderUrl");
        if (!Uri.TryCreate(share, UriKind.Absolute, out var shareUri) || shareUri.Scheme != "https" || shareUri.Host != "file.kiwi" || !shareUri.IsDefaultPort
            || shareUri.UserInfo.Length != 0 || shareUri.Query.Length != 0 || shareUri.Fragment.Length != 0 || shareUri.AbsolutePath.TrimEnd('/') != "/" + folderId)
            throw InvalidResponse();
        var files = root.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() != 1) throw InvalidResponse();
        var file = files[0];
        string fileId = String(file, "fileId");
        int chunkSize = file.GetProperty("chunkSize").GetInt32(), chunks = file.GetProperty("chunks").GetInt32();
        if (!Identifier(fileId) || chunkSize < 1 || chunkSize > _options.MaximumChunkBytes || chunks < 1 || chunks > 1_000_000
            || (sourceLength + chunkSize - 1) / chunkSize != chunks) throw InvalidResponse();
        double? freeHours = file.TryGetProperty("freeDownloadHours", out var hours) && hours.ValueKind != JsonValueKind.Null ? hours.GetDouble() : null;
        if (freeHours.HasValue && (!double.IsFinite(freeHours.Value) || freeHours < 0 || freeHours > 8_760)) throw InvalidResponse();
        var urls = file.GetProperty("uploadUrls");
        string head = String(urls, "head"), path = String(urls, "path"), tail = String(urls, "tail");
        if (!Uri.TryCreate(head, UriKind.Absolute, out var uploadHead) || uploadHead.Scheme != "https" || !uploadHead.IsDefaultPort
            || !uploadHead.Host.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase) || uploadHead.UserInfo.Length != 0
            || uploadHead.Query.Length != 0 || uploadHead.Fragment.Length != 0 || !head.EndsWith('/')) throw InvalidResponse();
        if (path.Length is < 1 or > 2048 || path.StartsWith('/') || path.Split('/').Any(segment => segment is "." or ".." or "")
            || path.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('/' or '_' or '-' or '.'))
            || tail.Length is < 1 or > 16_384 || tail.Any(c => char.IsControl(c) || c == '#') || tail.StartsWith('?')) throw InvalidResponse();
        var signaturesJson = urls.GetProperty("signatures");
        if (signaturesJson.ValueKind != JsonValueKind.Array || signaturesJson.GetArrayLength() != chunks) throw InvalidResponse();
        var signatures = signaturesJson.EnumerateArray().Select(s => s.GetString() ?? "").ToArray();
        if (signatures.Any(s => s.Length is < 1 or > 256 || s.Any(c => !char.IsAsciiLetterOrDigit(c)))) throw InvalidResponse();
        var headersJson = urls.GetProperty("headers");
        if (headersJson.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in headersJson.EnumerateObject())
        {
            string value = property.Value.GetString() ?? "";
            if (!property.Name.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase) || property.Name.Length > 128
                || property.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) || value.Length > 2048 || value.Any(char.IsControl)
                || !headers.TryAdd(property.Name, value)) throw InvalidResponse();
        }
        if (headers.Count is < 1 or > 32) throw InvalidResponse();
        return new Registration(folderId, fileId, auth, shareUri.GetLeftPart(UriPartial.Path).TrimEnd('/'), chunkSize, chunks, freeHours, head, path, tail, signatures, headers);
    }

    private static Uri ChunkUrl(Registration registration, int index)
    {
        string value = registration.Head + registration.Path + "/" + (index + 1).ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + "?" + registration.Tail + "&X-Amz-Signature=" + registration.Signatures[index];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var result) || result.Host != new Uri(registration.Head).Host) throw InvalidResponse();
        return result;
    }

    private TimeSpan DefaultDelay(int attempt) => TimeSpan.FromMilliseconds(Math.Min(_options.MaximumRetryDelay.TotalMilliseconds, _options.RetryDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 30))));
    // Jittered exponential backoff; never a busy loop, even when the configured delays are zero.
    private TimeSpan OutageDelay(int failure)
    {
        double ceiling = DefaultDelay(failure).TotalMilliseconds;
        return TimeSpan.FromMilliseconds(Math.Max(MinimumOutageDelay.TotalMilliseconds, ceiling / 2 + Random.Shared.NextDouble() * ceiling / 2));
    }
    private TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        TimeSpan value = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : DefaultDelay(attempt));
        if (response.Headers.RetryAfter is not null && value > _options.MaximumRetryDelay)
            throw new MediaOperationException("File.Kiwi asked for a longer wait before trying again.", "Share again later. The source file was preserved, and the service's retry interval was respected.");
        return TimeSpan.FromMilliseconds(Math.Clamp(value.TotalMilliseconds, 0, _options.MaximumRetryDelay.TotalMilliseconds));
    }
    private static async Task WaitRetry(TimeSpan delay, string message, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new JobProgress(JobStage.Uploading, null, message));
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }
    private static int Seconds(TimeSpan delay) => Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds));
    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;
    private static IEnumerable<int> ChunkOrder(int count)
    {
        yield return 0;
        if (count > 1) yield return count - 1;
        for (int index = 1; index < count - 1; index++) yield return index;
    }
    private static string String(JsonElement value, string property) => value.GetProperty(property).GetString() ?? throw InvalidResponse();
    private static bool Identifier(string value) => value.Length is >= 1 and <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static MediaOperationException InvalidResponse() => new("File.Kiwi returned an unsupported response.", "No share was confirmed. Service limits or API behavior may have changed.");
    private static MediaOperationException ChangedFile() => new("The file changed during sharing.", "No share was confirmed. Share a stable copy of the file instead.");
    private static MediaOperationException UploadExpired(string reason) => new(UploadExpiredMessage, reason + " The source file was preserved. Share again to start a new upload.");
    private static MediaOperationException ConnectionFailure(bool timedOut, bool repeated) => new(timedOut ? "File.Kiwi did not respond in time." : "File.Kiwi could not be reached.",
        (repeated ? "The connection kept dropping during the upload. " : "") + "The source file was preserved. " + (timedOut ? "Share again when the connection is available." : "Check your connection and try again."));

    private sealed class ProgressThrottle(TimeProvider clock)
    {
        private long _last;
        private bool _started;
        // The first report is immediate so the status changes as soon as bytes flow; later ones are spaced so the UI dispatcher is not flooded.
        public bool Due()
        {
            long now = clock.GetTimestamp();
            if (_started && clock.GetElapsedTime(_last, now) < ProgressInterval) return false;
            (_started, _last) = (true, now);
            return true;
        }
    }

    // One send attempt: its deadline, which a request body extends while bytes flow, when the body last made progress (0 when it made none), and
    // whether an earlier attempt of the same request was cut off after sending data.
    private sealed class Attempt(CancellationTokenSource deadline, TimeProvider clock, bool followsInterruption)
    {
        private long _lastProgress;
        public CancellationTokenSource Deadline { get; } = deadline;
        public bool FollowsInterruption { get; } = followsInterruption;
        public long LastProgress => Interlocked.Read(ref _lastProgress);
        public void Progressed() => Interlocked.Exchange(ref _lastProgress, clock.GetTimestamp());
    }

    // Sends the ciphertext in slices. Each sent slice records progress, re-arms the attempt's stall deadline and reports progress, so slow but steady
    // uploads finish and the bar moves within a chunk. Once the whole body is sent, the normal response deadline applies.
    private sealed class ChunkContent(byte[] data, int length, Attempt attempt, TimeSpan stall, TimeSpan response, Action<long> sent) : HttpContent
    {
        protected override bool TryComputeLength(out long value)
        {
            value = length;
            return true;
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            for (int offset = 0; offset < length;)
            {
                int count = Math.Min(UploadSliceBytes, length - offset);
                await stream.WriteAsync(data.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
                offset += count;
                attempt.Progressed();
                try { attempt.Deadline.CancelAfter(offset == length ? response : stall); }
                catch (ObjectDisposedException) { throw new OperationCanceledException(cancellationToken); } // The attempt has already ended.
                sent(offset);
            }
        }
    }
    private sealed record Registration(string FolderId, string FileId, string UploadAuth, string ShareBase, int ChunkSize, int Chunks, double? FreeHours, string Head, string Path, string Tail, string[] Signatures, Dictionary<string, string> Headers);
}
