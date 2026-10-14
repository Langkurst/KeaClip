using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Klippi.Infrastructure.Sharing;

/// <summary>
/// AWS Signature Version 4 for S3-compatible storage (header-based, single-chunk payload). Dependency-free; checked against
/// the four published S3 examples in "Signature Calculations for the Authorization Header: Transferring Payload in a Single
/// Chunk". S3 paths are not normalized and are encoded once; '/' inside an object key is kept.
/// </summary>
internal static class AwsSigV4
{
    public const string Algorithm = "AWS4-HMAC-SHA256";
    public const string EmptyPayloadSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>RFC 3986 encoding of every byte except unreserved characters, with uppercase hex; '/' is kept only when asked.</summary>
    public static string UriEncode(string value, bool encodeSlash)
    {
        var builder = new StringBuilder(value.Length + 16);
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            char c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_' or '~' || (c == '/' && !encodeSlash)) builder.Append(c);
            else builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    /// <summary>Parameters encoded individually, then sorted by name (and value) in ordinal order; a valueless parameter is "name=".</summary>
    public static string CanonicalQuery(IEnumerable<KeyValuePair<string, string>> parameters)
        => string.Join('&', parameters.Select(p => (Name: UriEncode(p.Key, true), Value: UriEncode(p.Value, true)))
            .OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Value, StringComparer.Ordinal).Select(p => p.Name + "=" + p.Value));

    /// <summary>The canonical request and the signed-header list. Header names are lowercased and sorted; values trimmed with inner runs of spaces collapsed.</summary>
    public static (string CanonicalRequest, string SignedHeaders) CanonicalRequest(string method, string canonicalUri, string canonicalQuery, IEnumerable<KeyValuePair<string, string>> headers, string payloadHash)
    {
        var sorted = headers.Select(h => (Name: h.Key.Trim().ToLowerInvariant(), Value: CollapseSpaces(h.Value.Trim())))
            .OrderBy(h => h.Name, StringComparer.Ordinal).ToList();
        if (sorted.Select(h => h.Name).Distinct(StringComparer.Ordinal).Count() != sorted.Count) throw new ArgumentException("Each signed header may appear only once.", nameof(headers));
        string signed = string.Join(';', sorted.Select(h => h.Name));
        var canonical = new StringBuilder();
        canonical.Append(method).Append('\n').Append(canonicalUri).Append('\n').Append(canonicalQuery).Append('\n');
        foreach (var (name, value) in sorted) canonical.Append(name).Append(':').Append(value).Append('\n');
        canonical.Append('\n').Append(signed).Append('\n').Append(payloadHash);
        return (canonical.ToString(), signed);
    }

    public static string Scope(DateTimeOffset time, string region, string service) => $"{time.UtcDateTime:yyyyMMdd}/{region}/{service}/aws4_request";
    public static string Timestamp(DateTimeOffset time) => time.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    public static string StringToSign(DateTimeOffset time, string scope, string canonicalRequest)
        => Algorithm + "\n" + Timestamp(time) + "\n" + scope + "\n" + Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));

    public static byte[] SigningKey(ReadOnlySpan<byte> secretAccessKeyUtf8, DateTimeOffset time, string region, string service)
    {
        byte[] seed = new byte[4 + secretAccessKeyUtf8.Length];
        try
        {
            "AWS4"u8.CopyTo(seed);
            secretAccessKeyUtf8.CopyTo(seed.AsSpan(4));
            byte[] date = HMACSHA256.HashData(seed, Encoding.ASCII.GetBytes(time.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture)));
            byte[] dateRegion = HMACSHA256.HashData(date, Encoding.ASCII.GetBytes(region));
            byte[] dateRegionService = HMACSHA256.HashData(dateRegion, Encoding.ASCII.GetBytes(service));
            byte[] signing = HMACSHA256.HashData(dateRegionService, "aws4_request"u8);
            CryptographicOperations.ZeroMemory(date); CryptographicOperations.ZeroMemory(dateRegion); CryptographicOperations.ZeroMemory(dateRegionService);
            return signing;
        }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }

    /// <summary>The full Authorization header value for the given request parts.</summary>
    public static string Authorization(string accessKeyId, ReadOnlySpan<byte> secretAccessKeyUtf8, DateTimeOffset time, string region, string service,
        string method, string canonicalUri, string canonicalQuery, IEnumerable<KeyValuePair<string, string>> signedHeaders, string payloadHash)
    {
        var (canonical, signedList) = CanonicalRequest(method, canonicalUri, canonicalQuery, signedHeaders, payloadHash);
        string scope = Scope(time, region, service);
        byte[] key = SigningKey(secretAccessKeyUtf8, time, region, service);
        try
        {
            string signature = Hex(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(StringToSign(time, scope, canonical))));
            return $"{Algorithm} Credential={accessKeyId}/{scope},SignedHeaders={signedList},Signature={signature}";
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static string Hex(ReadOnlySpan<byte> value) => Convert.ToHexString(value).ToLowerInvariant();

    private static string CollapseSpaces(string value)
    {
        if (!value.Contains("  ", StringComparison.Ordinal)) return value;
        var builder = new StringBuilder(value.Length);
        bool space = false;
        foreach (char c in value)
        {
            if (c == ' ') { if (!space) builder.Append(c); space = true; }
            else { builder.Append(c); space = false; }
        }
        return builder.ToString();
    }
}
