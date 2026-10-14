using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Klippi.Core;

namespace Klippi.Infrastructure.Sharing;

/// <summary>The few object operations a watch link needs. Implementations must never log keys, signatures or response bodies.</summary>
public interface IWatchObjectStore : IDisposable
{
    /// <summary>Largest body the store accepts in one PUT; larger objects use a multipart upload.</summary>
    int MaximumSinglePutBytes { get; }
    /// <summary>The size of every multipart part except the last (R2 requires equal sizes).</summary>
    int PartBytes { get; }
    Task PutObjectAsync(string key, ReadOnlyMemory<byte> body, CancellationToken cancellationToken);
    Task<string> StartMultipartAsync(string key, CancellationToken cancellationToken);
    Task<string> UploadPartAsync(string key, string uploadId, int partNumber, ReadOnlyMemory<byte> body, CancellationToken cancellationToken);
    Task CompleteMultipartAsync(string key, string uploadId, IReadOnlyList<string> partETags, CancellationToken cancellationToken);
    Task AbortMultipartAsync(string key, string uploadId, CancellationToken cancellationToken);
    Task DeleteObjectAsync(string key, CancellationToken cancellationToken);
}

public sealed class S3StoreOptions
{
    public int MaximumAttempts { get; init; } = 4;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Deadline for one part or single-object upload attempt.</summary>
    public TimeSpan UploadTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public int PartBytes { get; init; } = 8 * 1024 * 1024;
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>
/// Path-style S3 API client signed with SigV4 (<see cref="AwsSigV4"/>), for Cloudflare R2 and other S3-compatible storage.
/// Every payload is hashed and signed. Idempotent operations are retried on 408, 429, 5xx and dropped connections with a
/// bounded back-off; creating a multipart upload is not retried. Error messages name the HTTP status only.
/// </summary>
public sealed class S3CompatibleObjectStore : IWatchObjectStore
{
    private const int MaximumResponseBytes = 1_048_576;
    private static readonly XNamespace S3 = "http://s3.amazonaws.com/doc/2006-03-01/";
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _bucket, _region, _accessKeyId;
    private readonly byte[] _secret;
    private readonly S3StoreOptions _options;

    public S3CompatibleObjectStore(HttpClient httpClient, WatchLinkSetup setup, string secretAccessKey, S3StoreOptions? options = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(setup);
        if (setup.Problems() is { Count: > 0 } problems) throw new MediaOperationException("The watch link setup is incomplete.", problems[0]);
        if (!WatchLinkSetup.ValidSecret(secretAccessKey)) throw new MediaOperationException("The watch link setup is incomplete.", "Enter the secret access key again in Settings.");
        _options = options ?? new S3StoreOptions();
        if (_options.MaximumAttempts is < 1 or > 8 || _options.PartBytes is < 5 * 1024 * 1024 or > 512 * 1024 * 1024 || _options.RetryDelay < TimeSpan.Zero
            || _options.RequestTimeout <= TimeSpan.Zero || _options.UploadTimeout <= TimeSpan.Zero || _options.TimeProvider is null)
            throw new ArgumentException("Storage limits are invalid.", nameof(options));
        _endpoint = setup.StorageEndpoint;
        _bucket = setup.Bucket;
        _region = setup.Region;
        _accessKeyId = setup.AccessKeyId;
        _secret = Encoding.UTF8.GetBytes(secretAccessKey);
    }

    public int MaximumSinglePutBytes => _options.PartBytes;
    public int PartBytes => _options.PartBytes;

    public async Task PutObjectAsync(string key, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Put, key, [], body, true, _options.UploadTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> StartMultipartAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, key, [new("uploads", "")], ReadOnlyMemory<byte>.Empty, false, _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
        var document = await ReadXmlAsync(response, cancellationToken).ConfigureAwait(false);
        string? uploadId = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "UploadId")?.Value;
        if (document.Root?.Name.LocalName != "InitiateMultipartUploadResult" || string.IsNullOrWhiteSpace(uploadId) || uploadId.Length > 1024 || uploadId.Any(char.IsControl)) throw Unsupported();
        return uploadId;
    }

    public async Task<string> UploadPartAsync(string key, string uploadId, int partNumber, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        if (partNumber is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(partNumber));
        using var response = await SendAsync(HttpMethod.Put, key, [new("partNumber", partNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)), new("uploadId", uploadId)], body, true, _options.UploadTimeout, cancellationToken).ConfigureAwait(false);
        string? etag = response.Headers.TryGetValues("ETag", out var values) ? values.FirstOrDefault() : null;
        if (string.IsNullOrWhiteSpace(etag) || etag.Length > 256 || etag.Any(char.IsControl)) throw Unsupported();
        return etag;
    }

    public async Task CompleteMultipartAsync(string key, string uploadId, IReadOnlyList<string> partETags, CancellationToken cancellationToken)
    {
        var xml = new XElement(S3 + "CompleteMultipartUpload", partETags.Select((etag, index) => new XElement(S3 + "Part", new XElement(S3 + "PartNumber", index + 1), new XElement(S3 + "ETag", etag))));
        byte[] body = Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
        using var response = await SendAsync(HttpMethod.Post, key, [new("uploadId", uploadId)], body, true, _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
        // S3 can answer 200 and still report a failure in the body.
        var document = await ReadXmlAsync(response, cancellationToken).ConfigureAwait(false);
        if (document.Root?.Name.LocalName != "CompleteMultipartUploadResult") throw new MediaOperationException("Storage did not confirm the completed upload.", "The source file was preserved. Try again.");
    }

    public async Task AbortMultipartAsync(string key, string uploadId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete, key, [new("uploadId", uploadId)], ReadOnlyMemory<byte>.Empty, true, _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteObjectAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete, key, [], ReadOnlyMemory<byte>.Empty, true, _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
    }

    internal static bool ValidKey(string key) => key.Length is >= 1 and <= 512 && !key.StartsWith('/') && key.Split('/').All(s => s.Length > 0 && s is not "." and not "..")
        && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.');

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string key, KeyValuePair<string, string>[] query, ReadOnlyMemory<byte> body, bool retryable, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!ValidKey(key)) throw new ArgumentException("The object key is not valid.", nameof(key));
        string path = "/" + AwsSigV4.UriEncode(_bucket, true) + "/" + AwsSigV4.UriEncode(key, false);
        string canonicalQuery = AwsSigV4.CanonicalQuery(query);
        var uri = new Uri(_endpoint.GetLeftPart(UriPartial.Authority) + path + (canonicalQuery.Length > 0 ? "?" + canonicalQuery : ""));
        string host = (uri.IsDefaultPort ? uri.IdnHost : uri.IdnHost + ":" + uri.Port).ToLowerInvariant();
        string payloadHash = AwsSigV4.Hex(SHA256.HashData(body.Span));
        TimeProvider clock = _options.TimeProvider;
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset now = clock.GetUtcNow();
            string date = AwsSigV4.Timestamp(now);
            string authorization = AwsSigV4.Authorization(_accessKeyId, _secret, now, _region, "s3", method.Method, path, canonicalQuery,
                [new("host", host), new("x-amz-content-sha256", payloadHash), new("x-amz-date", date)], payloadHash);
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.TryAddWithoutValidation("x-amz-date", date);
            request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            if ((method != HttpMethod.Get && method != HttpMethod.Delete) || !body.IsEmpty)
            {
                request.Content = new ReadOnlyMemoryContent(body);
                request.Content.Headers.ContentType = new("application/octet-stream");
            }
            int? status = null;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(timeout);
                try
                {
                    var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode && (response.RequestMessage?.RequestUri is not { } final || final == uri)) return response;
                    status = (int)response.StatusCode;
                    response.Dispose();
                    bool transient = status is 408 or 429 or >= 500;
                    if (!retryable || !transient || attempt >= _options.MaximumAttempts) throw Failure(status.Value);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (!retryable || attempt >= _options.MaximumAttempts) throw new MediaOperationException("Storage did not respond in time.", "The source file was preserved. Check the connection and try again.");
                }
                catch (HttpRequestException)
                {
                    if (!retryable || attempt >= _options.MaximumAttempts) throw new MediaOperationException("Storage could not be reached.", "The source file was preserved. Check the connection and the storage endpoint, then try again.");
                }
            }
            // Jittered exponential back-off between attempts.
            double ceiling = _options.RetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
            await Task.Delay(TimeSpan.FromMilliseconds(ceiling / 2 + Random.Shared.NextDouble() * ceiling / 2), clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<XDocument> ReadXmlAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw Unsupported();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using var scope = stream.ConfigureAwait(false);
        using var data = new MemoryStream();
        byte[] buffer = new byte[16_384];
        int read;
        while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (data.Length + read > MaximumResponseBytes) throw Unsupported();
            data.Write(buffer, 0, read);
        }
        data.Position = 0;
        // XDocument.Load prohibits DTDs by default, so entity expansion is not possible.
        try { return XDocument.Load(data); }
        catch (System.Xml.XmlException) { throw Unsupported(); }
    }

    private static MediaOperationException Failure(int status) => status switch
    {
        401 or 403 => new("Storage refused the access key.", $"HTTP {status}. Check the access key, its secret and that it may write to this bucket. The source file was preserved."),
        404 => new("The storage bucket was not found.", "HTTP 404. Check the storage endpoint and bucket name in Settings. The source file was preserved."),
        _ => new("Storage could not complete the upload.", $"HTTP {status}. The source file was preserved. Try again later."),
    };
    private static MediaOperationException Unsupported() => new("Storage returned an unsupported response.", "No watch link was created. The source file was preserved.");

    public void Dispose() => CryptographicOperations.ZeroMemory(_secret);
}
