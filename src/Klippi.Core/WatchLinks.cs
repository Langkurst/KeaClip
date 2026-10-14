namespace Klippi.Core;

/// <summary>
/// Where watch links are stored and played. The storage is an S3-compatible bucket the user owns (Cloudflare R2 is the
/// documented target); the player page is KeaClip's static web player. The secret access key is never part of this record:
/// it is kept separately, protected for the current Windows user.
/// </summary>
/// <param name="StorageEndpoint">The S3 API endpoint, for R2 <c>https://&lt;account id&gt;.r2.cloudflarestorage.com</c>.</param>
/// <param name="Bucket">The bucket that holds the encrypted objects.</param>
/// <param name="AccessKeyId">The access key id of a token that can write objects to that bucket.</param>
/// <param name="PlayerPage">The player page, for example <c>https://watch.example.com/v/</c>. Links are this page plus <c>#id.key</c>.</param>
/// <param name="RetentionDays">The bucket's delete-after rule for the <c>w/</c> prefix. KeaClip shows it as the link's expiry; the bucket enforces it.</param>
/// <param name="Region">The SigV4 region. R2 uses <c>auto</c>.</param>
public sealed record WatchLinkSetup(Uri StorageEndpoint, string Bucket, string AccessKeyId, Uri PlayerPage, int RetentionDays, string Region = "auto")
{
    public const int MaximumRetentionDays = 365;

    /// <summary>Plain-language problems with this setup; empty when it can be used.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (!SafeOrigin(StorageEndpoint, allowPath: false)) problems.Add("The storage endpoint must be an https:// address without a path, query or user name.");
        if (Bucket is not { Length: >= 3 and <= 63 } || !Bucket.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') || Bucket[0] == '-' || Bucket[^1] == '-')
            problems.Add("The bucket name must be 3 to 63 lowercase letters, digits or hyphens.");
        if (AccessKeyId is not { Length: >= 16 and <= 128 } || !AccessKeyId.All(char.IsAsciiLetterOrDigit)) problems.Add("The access key ID must be 16 to 128 letters and digits.");
        if (!SafeOrigin(PlayerPage, allowPath: true) || !PlayerPage.AbsolutePath.EndsWith('/')) problems.Add("The player page must be an https:// address ending in a slash, for example https://watch.example.com/v/.");
        if (RetentionDays is < 1 or > MaximumRetentionDays) problems.Add($"Delete links after 1 to {MaximumRetentionDays} days.");
        if (Region is not { Length: >= 1 and <= 32 } || !Region.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) problems.Add("The region must be letters, digits or hyphens (R2 uses \"auto\").");
        return problems;
    }

    /// <summary>A secret access key is 16 to 128 printable ASCII characters without spaces.</summary>
    public static bool ValidSecret(string? secret) => secret is { Length: >= 16 and <= 128 } && secret.All(c => c is > ' ' and < (char)127);

    // HTTPS only; plain HTTP is accepted for loopback addresses so the local test server can stand in for storage.
    private static bool SafeOrigin(Uri? uri, bool allowPath) => uri is { IsAbsoluteUri: true } && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 && (allowPath || uri.AbsolutePath == "/");
}

/// <summary>The saved setup (null when none) and whether a protected secret is stored with it.</summary>
public sealed record WatchLinkSetupState(WatchLinkSetup? Setup, bool HasSecret)
{
    public bool IsReady => Setup is not null && HasSecret;
}

/// <summary>
/// Creates "watch in browser" links: a browser-playable copy of the file is encrypted on this PC (key only in the link's
/// fragment) and uploaded to the user's own storage, where KeaClip's web player streams and decrypts it.
/// </summary>
public interface IWatchLinkService
{
    Task<WatchLinkSetupState> LoadSetupAsync(CancellationToken cancellationToken = default);
    /// <summary>Saves the setup. A null <paramref name="secretAccessKey"/> keeps the stored secret.</summary>
    Task SaveSetupAsync(WatchLinkSetup setup, string? secretAccessKey, CancellationToken cancellationToken = default);
    Task ClearSetupAsync(CancellationToken cancellationToken = default);
    Task<ShareResult> ShareAsync(string path, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default);
}
