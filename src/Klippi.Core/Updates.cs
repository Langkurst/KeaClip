using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Klippi.Core;

/// <summary>A plain release version (major.minor.patch). Pre-release and build suffixes are not accepted.</summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    private const int MaxPart = 99_999;
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        var value = text?.Trim() ?? "";
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        var parts = value.Split('.');
        if (parts.Length != 3) return false;
        var numbers = new int[3];
        for (var index = 0; index < 3; index++)
        {
            var part = parts[index];
            // Digits only: no sign, spaces or leading zeros ("01" is ambiguous in a tag).
            if (part.Length is 0 or > 5 || part.Any(c => c is < '0' or > '9') || part.Length > 1 && part[0] == '0') return false;
            numbers[index] = int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture);
            if (numbers[index] > MaxPart) return false;
        }
        version = new(numbers[0], numbers[1], numbers[2]);
        return true;
    }
    public static ReleaseVersion Parse(string text) => TryParse(text, out var version) ? version : throw new FormatException("A version must look like 1.2.3.");
    public int CompareTo(ReleaseVersion other) => Major != other.Major ? Major.CompareTo(other.Major) : Minor != other.Minor ? Minor.CompareTo(other.Minor) : Patch.CompareTo(other.Patch);
    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}

/// <summary>One downloadable file named by the release feed: where it is, its exact SHA-256 and its exact size.</summary>
public sealed record UpdateAsset(Uri Url, string Sha256, long Size);

/// <summary>A newer release described by the feed. Notes are plain text (shown as text, never as markup).</summary>
public sealed record UpdateOffer(ReleaseVersion Version, string Notes, DateTimeOffset? PublishedAt, UpdateAsset? Installer, UpdateAsset? Portable, Uri? ReleasePage);

public enum UpdateInstallKind
{
    /// <summary>Installed with KeaClip.Setup.exe (a receipt sits beside the app): updates run the verified installer.</summary>
    Installed,
    /// <summary>The portable ZIP (portable.marker beside the app): updates download the verified ZIP for a manual swap.</summary>
    Portable,
    /// <summary>A source or test build: updates can be checked but not installed.</summary>
    Development,
}

public enum UpdateCheckOutcome { UpToDate, Available, Offline, Failed, Skipped }

/// <param name="Quiet">True when the outcome needs no attention (skipped, offline or metered in the background).</param>
public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, UpdateOffer? Offer, string Message, DateTimeOffset? CheckedAt, bool Quiet = false);

/// <summary>A downloaded update whose size and SHA-256 matched the feed exactly.</summary>
public sealed record PreparedUpdate(UpdateOffer Offer, string FilePath, UpdateInstallKind Kind, string Sha256, string SignatureNote);

/// <summary>The feed or a downloaded file failed validation. The message is safe to show.</summary>
public sealed class UpdateValidationException(string message) : Exception(message);

public interface IUpdateService
{
    UpdateInstallKind InstallKind { get; }
    ReleaseVersion CurrentVersion { get; }
    Uri FeedUrl { get; }
    /// <summary>When a check last reached the feed (or tried to), from the saved update state; null when never.</summary>
    DateTimeOffset? LastChecked { get; }
    /// <summary>True when the daily background check may run now.</summary>
    bool IsBackgroundCheckDue { get; }
    /// <summary>Uses another https feed (from settings); returns false and keeps the current one when it is not acceptable.</summary>
    bool TrySetFeed(string? url);
    /// <summary>Asks the feed for a newer release. Background checks run at most once a day and stay quiet when offline or metered.</summary>
    Task<UpdateCheckResult> CheckAsync(bool background, CancellationToken cancellationToken = default);
    /// <summary>Downloads the right asset for this install kind into the app-owned update folder and verifies size and SHA-256.</summary>
    Task<PreparedUpdate> DownloadAsync(UpdateOffer offer, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default);
    /// <summary>Installed mode only: verifies the file again and starts the installer in upgrade mode, which waits for this process to exit.</summary>
    void StartInstall(PreparedUpdate update);
    /// <summary>Removes downloads for this version or older that this app created. Never throws.</summary>
    void CleanupDownloads();
    /// <summary>"Updated to 0.3.0" once after a successful upgrade relaunched the app; null otherwise.</summary>
    string? TakeCompletedUpdateNotice() => null;
}

/// <summary>
/// The release feed format (keaclip-update.json, schema 1). Every field is checked strictly: a feed
/// that is malformed, names another product, has a bad version, an http link, a missing or malformed
/// SHA-256 or an implausible size is refused as a whole.
/// </summary>
public static partial class UpdateManifest
{
    public const string FileName = "keaclip-update.json";
    public const int Schema = 1;
    public const string Product = "KeaClip";
    /// <summary>The feed file itself is small; anything larger is not a manifest.</summary>
    public const int MaxManifestBytes = 256 * 1024;
    /// <summary>Upper bound for one installer or ZIP (the app with bundled components is a few hundred MB).</summary>
    public const long MaxAssetBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxNotesLength = 8_000;

    /// <param name="localFolder">For a local feed file: asset links are then bare file names in this folder.</param>
    public static UpdateOffer Parse(string json, bool allowLoopbackHttp = false, string? localFolder = null)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxManifestBytes) throw Invalid("is empty or too large");
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false }); }
        catch (JsonException) { throw Invalid("is not valid JSON"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid("is not a JSON object");
            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaValue) || schemaValue != Schema) throw Invalid("uses an unknown format version");
            if (String(root, "product") != Product) throw Invalid("is for another product");
            if (!ReleaseVersion.TryParse(String(root, "version"), out var version)) throw Invalid("has no valid version");
            var notes = CleanNotes(OptionalString(root, "notes"));
            DateTimeOffset? published = null;
            if (OptionalString(root, "publishedAt") is { } date)
            {
                if (!DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) throw Invalid("has an invalid publish date");
                published = parsed;
            }
            Uri? page = null;
            if (OptionalString(root, "releasePage") is { } pageText)
            {
                if (!Uri.TryCreate(pageText, UriKind.Absolute, out page) || !IsAllowedUrl(page, allowLoopbackHttp)) throw Invalid("has an invalid release page link");
            }
            var installer = Asset(root, "installer", ".exe", allowLoopbackHttp, localFolder);
            var portable = Asset(root, "portable", ".zip", allowLoopbackHttp, localFolder);
            if (installer is null && portable is null) throw Invalid("lists no installer or portable download");
            return new(version, notes, published, installer, portable, page);
        }
    }

    /// <summary>HTTPS only, without user info. Plain http is accepted only for loopback test servers when explicitly allowed.</summary>
    public static bool IsAllowedUrl(Uri url, bool allowLoopbackHttp = false)
    {
        if (!url.IsAbsoluteUri || !string.IsNullOrEmpty(url.UserInfo) || string.IsNullOrEmpty(url.Host)) return false;
        if (url.Scheme == Uri.UriSchemeHttps) return true;
        return allowLoopbackHttp && url.Scheme == Uri.UriSchemeHttp && url.IsLoopback;
    }

    public static bool IsSha256(string? value) => value is { Length: 64 } && Sha256Pattern().IsMatch(value);

    /// <summary>
    /// A local feed: a keaclip-update.json file in a folder on a local drive (not a network share).
    /// Its downloads are files beside it, so nothing leaves the PC.
    /// </summary>
    public static bool IsLocalFeed(Uri url) =>
        url.IsAbsoluteUri && url.IsFile && !url.IsUnc && Path.IsPathFullyQualified(url.LocalPath) && !url.LocalPath.StartsWith(@"\\", StringComparison.Ordinal)
        && string.Equals(Path.GetFileName(url.LocalPath), FileName, StringComparison.OrdinalIgnoreCase);

    private static UpdateAsset? Asset(JsonElement root, string name, string extension, bool allowLoopbackHttp, string? localFolder)
    {
        if (!root.TryGetProperty(name, out var asset) || asset.ValueKind == JsonValueKind.Null) return null;
        if (asset.ValueKind != JsonValueKind.Object) throw Invalid($"has an invalid {name} entry");
        Uri? url;
        if (localFolder is not null)
        {
            // Only a plain file name beside the feed: no folders, drives, links or escapes elsewhere.
            var file = String(asset, "url");
            if (file.Length is 0 or > 200 || file != Path.GetFileName(file) || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || file.Trim('.').Length == 0) throw Invalid($"has a {name} entry that is not a file name beside the feed");
            url = new Uri(Path.Combine(Path.GetFullPath(localFolder), file));
        }
        else if (!Uri.TryCreate(String(asset, "url"), UriKind.Absolute, out url) || !IsAllowedUrl(url, allowLoopbackHttp)) throw Invalid($"has a {name} link that is not a secure https link");
        if (!url.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) throw Invalid($"has a {name} link that is not a {extension} file");
        // The hash is required even though the feed is fetched over HTTPS: it is what the download is checked against.
        var hash = String(asset, "sha256").Trim().ToLowerInvariant();
        if (!IsSha256(hash)) throw Invalid($"has no valid SHA-256 for the {name}");
        if (!asset.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var bytes) || bytes <= 0 || bytes > MaxAssetBytes) throw Invalid($"has no valid size for the {name}");
        return new(url, hash, bytes);
    }

    private static string String(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw Invalid($"has an invalid {name}");
        return value.GetString();
    }

    // Plain text only: control characters (other than line breaks and tabs) are dropped and the length is bounded.
    private static string CleanNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return "";
        var builder = new StringBuilder(Math.Min(notes.Length, MaxNotesLength));
        foreach (var c in notes.Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            if (builder.Length >= MaxNotesLength) break;
            if (c is '\n' or '\t' || !char.IsControl(c)) builder.Append(c);
        }
        var text = builder.ToString().Trim();
        return notes.Length > MaxNotesLength ? text + "…" : text;
    }

    private static UpdateValidationException Invalid(string reason) => new("The update information " + reason + ", so it was ignored.");

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
