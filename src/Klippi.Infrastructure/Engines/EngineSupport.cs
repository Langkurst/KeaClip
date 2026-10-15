using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Klippi.Core;

namespace Klippi.Infrastructure.Engines;

internal static partial class EngineSupport
{
    internal static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    internal static string Number(double value) => value.ToString("0.######", Invariant);
    internal static string Required(IComponentService components, string name) => components.Find(name)
        ?? throw new MediaOperationException($"{name} is not ready. Open Components and set it up first.");
    internal static void Success(ProcessResult result, string operation)
    {
        if (result.ExitCode != 0)
            // Engine diagnostics may contain cookies, signed URLs or local browser paths.
            // Keep raw output in memory only; do not pass it into UI/history/error records.
            throw new MediaOperationException(ClassifyFailure(result, operation));
    }
    // User-facing messages avoid words such as "cookie" or "token" followed by more
    // text: the queue redacts error text, which would otherwise garble them.
    internal static string ClassifyFailure(ProcessResult result, string operation)
    {
        var lines = DiagnosticSelection(result.StandardError);
        var message = string.Join('\n', lines);
        // A full disk is the cause whichever step noticed it, including a merge
        // or FFmpeg error printed after it.
        if (Contains(message, "no space left", "not enough space", "disk full", "errno 28", "disk quota exceeded")) return "Not enough disk space. Choose another output drive or free some space and retry.";
        // The last error is why the engine stopped. Earlier errors can belong to an
        // optional subtitle track or a step that was retried, so they decide the
        // message only when the last line is not recognised.
        return (lines.Count > 0 ? Classify(lines[^1]) : null) ?? Classify(message) ?? $"{operation} failed (engine exit code {result.ExitCode}). Check the file, selected format and component status.";
    }
    private static bool Contains(string text, params string[] parts) => parts.Any(text.Contains);
    private static string? Classify(string message)
    {
        bool Has(params string[] parts) => Contains(message, parts);
        if (message.Contains("could not find") && message.Contains("cookies database")) return "No sign-in data was found for the selected browser on this PC. Choose a browser you are signed into, or choose None.";
        if (Has("failed to decrypt with dpapi", "app-bound", "appbound") || message.Contains("cookie") && Has("could not copy", "decrypt", "locked", "permission denied")) return "Browser sign-in could not be read. Close the selected browser completely and retry, or choose Firefox. KeaClip did not save any browser sign-in data.";
        if (Has("drm protected", "drm-protected", "protected by drm")) return "This media is protected by DRM. KeaClip cannot save protected media.";
        if (Has("private video", "video is private", "this post is private", "account is private")) return "This video is private. Use an account allowed to access it, if you have permission to save it.";
        if (Has("not available in your country", "available in your country", "geo restrict", "geo-restrict", "georestrict", "from your location", "not available in your region")) return "This media is not available in your region.";
        if (Has("sign in", "sign-in", "login required", "log in", "logged-in", "logged in", "authentication required", "requires authentication", "members-only", "channel's members", "join this channel", "confirm your age", "age-restricted")) return "Sign-in is required. Choose a browser you are signed into, and only save content you are allowed to access.";
        if (Has("requested format is not available", "requested quality is unavailable")) return "The requested quality or format is unavailable. Analyse again and choose an available option.";
        if (Has("live event will begin", "premieres in", "premiere will begin", "is not currently live", "stream has not started")) return "This live stream or premiere has not started yet. Try again after it begins.";
        if (Has("video unavailable", "video is unavailable", "no longer available", "has been removed", "been terminated", "does not exist", "http error 404", "content isn't available", "content is not available", "post is unavailable")) return "This media is unavailable. It may have been removed, or the link may be wrong.";
        if (message.Contains("thumbnail embedding")) return "The cover image could not be embedded in this file format. Turn off Cover / thumbnail, or choose MP4, MKV, MP3, M4A, FLAC or Opus.";
        if (Has("ffmpeg is not installed", "ffmpeg not found", "ffprobe not found", "ffprobe and ffmpeg not found")) return "FFmpeg is not ready. Open Components and set it up first.";
        if (Has("postprocessing:", "conversion failed", "error merging", "ffmpeg", "invalid data found", "error opening input", "error while opening encoder", "error opening output", "could not write header", "encoder setup failed", "error initializing output stream", "could not find tag for codec")) return "FFmpeg could not process this file. Try another format or quality, or check Components.";
        if (Has("http error 403", "403: forbidden")) return "The site refused the download. Update the downloader in Components, or choose a browser you are signed into, then retry.";
        if (Has("http error 429", "too many requests")) return "The site is limiting requests right now. Wait a while, then retry.";
        // 5xx: the site itself failed, after yt-dlp's own retries.
        if (Has("http error 5", "internal server error", "service unavailable", "bad gateway", "gateway time")) return "The website is having trouble right now. Wait a few minutes, then try again.";
        if (Has("unsupported url", "no suitable extractor")) return "KeaClip can't save from this website yet. Check the link, or update the downloader.";
        if (Has("unable to extract", "extractor error", "please report this issue", "no video formats found", "signature extraction failed", "nsig extraction failed")) return "KeaClip couldn't find a video at this link. Check the link is right. If it is, the website may have changed: update the downloader and try again.";
        // Includes a transfer cut off mid-file ("... bytes read, ... more expected").
        if (Has("name resolution", "failed to resolve", "getaddrinfo", "network is unreachable", "connection refused", "actively refused", "connection reset", "connection aborted", "remote end closed", "timed out", "10054", "10060", "10061", "more expected", "incomplete read", "incompleteread", "content too short", "unable to download video data", "unable to download webpage")) return "Could not reach the media service, or the transfer was cut off. Check your internet connection and try again.";
        return null;
    }

    // Classify only the engine's own diagnostics. Standard output carries progress
    // JSON with titles and output paths, so a title such as "How to log in" or a
    // folder named "Private Videos" must never choose the message. yt-dlp marks
    // its failures with ERROR:, which keeps earlier warnings from steering the
    // result; FFmpeg has no prefix, so its last lines are used. Paths and links
    // are removed before matching for the same reason.
    private static List<string> DiagnosticSelection(string? standardError)
    {
        var lines = DiagnosticLines(standardError);
        var errors = lines.Where(line => line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)).ToArray();
        var selected = errors.Length > 0 ? errors.TakeLast(20) : lines.Where(line => !line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase)).TakeLast(20);
        return selected.Select(line => DiagnosticPath().Replace(DiagnosticLink().Replace(line.Length > 4000 ? line[..4000] : line, " "), " ").ToLowerInvariant()).ToList();
    }
    // The engine's own diagnostic lines, without KC_ template lines. yt-dlp clears
    // its progress line before a retried failure, so the real text arrives as
    // "ERROR: \r[download] Got error: ..."; the carriage return splits it into a bare
    // "ERROR:" and the message, which are joined again here.
    internal static IReadOnlyList<string> DiagnosticLines(string? standardError)
    {
        var lines = new List<string>();
        string? prefix = null;
        foreach (var line in (standardError ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("KC_", StringComparison.Ordinal)) continue;
            if (line is "ERROR:" or "WARNING:") { if (prefix is not null) lines.Add(prefix); prefix = line; continue; }
            lines.Add(prefix is null ? line : prefix + " " + line);
            prefix = null;
        }
        if (prefix is not null) lines.Add(prefix);
        return lines;
    }
    [GeneratedRegex(@"(?i)\b(?:https?|file)://\S+")] private static partial Regex DiagnosticLink();
    // A drive or UNC path runs to the next quote, colon or line end.
    [GeneratedRegex(@"(?:\b[A-Za-z]:[\\/]|\\\\)[^:*?""<>|\r\n']*")] private static partial Regex DiagnosticPath();
    internal static JsonDocument Json(string value, string operation)
    {
        if (value.Length > 16_777_216) throw new MediaOperationException($"{operation} returned too much metadata.");
        try { return JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException) { throw new MediaOperationException($"{operation} returned unreadable metadata. Try updating the component."); }
    }
    internal static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    internal static double? Numeric(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var field)) return null;
        double number;
        if (field.ValueKind == JsonValueKind.Number && field.TryGetDouble(out number) || field.ValueKind == JsonValueKind.String && double.TryParse(field.GetString(), NumberStyles.Float, Invariant, out number))
            return double.IsFinite(number) && number >= 0 ? number : null;
        return null;
    }
    internal static long? Integer64(JsonElement value, string name) => Numeric(value, name) is { } number && number <= long.MaxValue ? (long)number : null;
    internal static int? Integer(JsonElement value, string name) => Numeric(value, name) is { } number && number <= int.MaxValue ? (int)number : null;
    internal static TimeSpan? Duration(double? seconds) => seconds is { } value && value < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(value) : null;
    internal static Uri? Url(string? value) => MediaValidation.TryMediaUrl(value, out var url) ? url : null;
    // Query parameters that identify public media rather than a viewer, session or
    // signature. Everything else (share hashes, tracking, signed values) is dropped.
    private static readonly HashSet<string> PublicQueryParameters = new(StringComparer.OrdinalIgnoreCase) { "v", "list", "index", "t", "start", "time_continue", "id", "lang", "story_fbid", "fbid", "p", "vid", "video_id", "clip", "ep", "episode" };
    internal static string PublicSourceUrl(Uri uri)
    {
        if (uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!pair.StartsWith("v=", StringComparison.Ordinal)) continue;
                var id = Uri.UnescapeDataString(pair[2..]);
                if (id.Length == 11 && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')) return "https://www.youtube.com/watch?v=" + id;
            }
        }
        // Keep public identifiers such as facebook.com/watch/?v=123, which would
        // otherwise disappear from the archive reference with the whole query.
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Where(PublicQueryPair);
        return new UriBuilder(uri) { UserName = "", Password = "", Query = string.Join('&', query), Fragment = "" }.Uri.AbsoluteUri;
    }
    private static bool PublicQueryPair(string pair)
    {
        var parts = pair.Split('=', 2);
        var name = Uri.UnescapeDataString(parts[0]);
        if (!PublicQueryParameters.Contains(name)) return false;
        // "t" is a start time on video sites, but X uses it for a share token that
        // identifies who shared the link; only time values are kept.
        return name is not ("t" or "start" or "time_continue") || parts.Length == 2 && TimeValue().IsMatch(Uri.UnescapeDataString(parts[1]));
    }
    [GeneratedRegex(@"^(?=.)(?:\d{1,9}(?:\.\d{1,3})?s?|(?:\d{1,4}h)?(?:\d{1,4}m)?(?:\d{1,6}s)?)$", RegexOptions.IgnoreCase)] private static partial Regex TimeValue();
    internal static string Absolute(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl)) throw new ArgumentException();
            return Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new MediaOperationException("Choose a valid local file or folder."); }
    }
    internal static string Input(string path)
    {
        var full = Absolute(path);
        if (!File.Exists(full)) throw new MediaOperationException("The input file could not be found.");
        return full;
    }
    internal static string Canonical(string path)
    {
        var full = Absolute(path);
        var root = Path.GetPathRoot(full)!;
        var resolved = root;
        foreach (var segment in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, segment);
            if (!File.Exists(resolved) && !Directory.Exists(resolved)) continue;
            var attributes = File.GetAttributes(resolved);
            if ((attributes & FileAttributes.ReparsePoint) == 0) continue;
            FileSystemInfo entry = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(resolved) : new FileInfo(resolved);
            resolved = entry.ResolveLinkTarget(true)?.FullName ?? throw new MediaOperationException("This file link could not be resolved safely.");
        }
        return resolved;
    }
    internal static ITemporaryWorkspace CreateWorkspace(string parent, ITemporaryWorkspaceService? service) => (service ?? OwnedTemporaryWorkspaceService.Shared).Create(parent);
    internal static void Cleanup(ITemporaryWorkspace workspace, string parent)
    {
        var resolved = Absolute(workspace.Path);
        var expectedParent = Absolute(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(resolved), expectedParent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith(".keaclip-work-", StringComparison.Ordinal)) return;
        // The verified service retains its handles through bounded, nonrecursive
        // deletion. An injected service controls its own lease disposal.
        if (workspace is OwnedTemporaryWorkspaceService.Lease owned) owned.Cleanup();
    }
    internal static void CommitFile(string staged, string output, bool replace)
    {
        if (!File.Exists(staged) || new FileInfo(staged).Length == 0) throw new MediaOperationException("The engine did not create a complete output file.");
        try { File.Move(staged, output, replace); }
        catch (IOException) when (File.Exists(output) && !replace) { throw new MediaOperationException("That output already exists. Choose another name or explicitly allow replacement."); }
    }
    internal static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).ToLowerInvariant();
    }
    // Archive metadata is evidence: titles, descriptions, tags, chapters and IDs are
    // kept exactly. Only real secrets are removed, by field rather than by free-text
    // pattern: sign-in and header fields, local paths, and the signed or IP-bearing
    // parts of transport URLs. Free-text redaction would rewrite a title such as
    // "The Secret Life of Pets" and strip video IDs from links in descriptions.
    internal static string SafeMetadata(string json)
    {
        using var parsed = Json(json, "Media analysis");
        var node = JsonNode.Parse(parsed.RootElement.GetRawText())!;
        Scrub(node, null);
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
    private static readonly HashSet<string> PublicPageUrlKeys = new(StringComparer.Ordinal) { "webpage_url", "original_url", "channel_url", "uploader_url", "playlist_webpage_url" };
    private static readonly HashSet<string> SectionKeys = new(StringComparer.Ordinal) { "formats", "requested_formats", "requested_downloads", "thumbnails", "subtitles", "automatic_captions", "entries", "storyboards" };
    // Download instructions with no evidence value: AES keys and their key
    // addresses, signed values appended to every segment, request bodies.
    private static readonly HashSet<string> TransportKeys = new(StringComparer.Ordinal) { "hls_aes", "extra_param_to_segment_url", "extra_param_to_key_url", "request_data", "downloader_options" };
    // Free text and identifiers stay exact even when the whole value is a link.
    private static readonly HashSet<string> TextKeys = new(StringComparer.Ordinal) { "id", "display_id", "title", "fulltitle", "alt_title", "description", "text", "uploader", "uploader_id", "creator", "channel", "channel_id", "artist", "album", "album_artist", "track", "composer", "genre", "series", "season", "episode", "location", "license", "playlist", "playlist_title", "playlist_id", "name", "format", "format_id", "format_note" };
    private static bool SecretKey(string key) =>
        key.StartsWith("__", StringComparison.Ordinal) || key is "filepath" or "filename" or "_filename" or "infojson_filename" or "fragments" || TransportKeys.Contains(key)
        || new[] { "cookie", "token", "password", "authorization", "secret", "signature" }.Any(part => key.Contains(part, StringComparison.OrdinalIgnoreCase))
        || key.EndsWith("headers", StringComparison.OrdinalIgnoreCase);
    private static void Scrub(JsonNode node, string? section)
    {
        if (node is JsonObject obj)
        {
            var pageEntry = obj["_type"] is JsonValue type && type.TryGetValue<string>(out var kind) && kind is "url" or "url_transparent";
            foreach (var key in obj.Select(x => x.Key).ToArray())
            {
                if (SecretKey(key)) { obj.Remove(key); continue; }
                if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    if (TransportUrl(key, text, section, pageEntry) is { } safe) obj[key] = safe;
                }
                else if (obj[key] is { } child) Scrub(child, SectionKeys.Contains(key) ? key : section);
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array)
                if (child is JsonObject or JsonArray) Scrub(child, section);
    }
    // Returns a replacement only for links; every other string stays exact. A
    // link is a field named as one, or any other non-text field whose whole value
    // is an http(s) or file address (for example hls_aes.uri in older output).
    private static string? TransportUrl(string key, string text, string? section, bool pageEntry)
    {
        var isUrlField = key is "url" or "uri" or "thumbnail" || key.EndsWith("_url", StringComparison.Ordinal) || key.EndsWith("_uri", StringComparison.Ordinal);
        var looksLikeUrl = text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        if (TextKeys.Contains(key) || !isUrlField && !looksLikeUrl || !Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        // A local file address names the user's own folders.
        if (uri.IsFile) return "";
        if (uri.Scheme is not ("http" or "https")) return null;
        // Public pages keep their public identifiers (for example ?v=).
        if (PublicPageUrlKeys.Contains(key) || key == "url" && pageEntry) return MediaValidation.ScrubPublicSourceUrl(new Uri(PublicSourceUrl(uri)));
        // Thumbnails and subtitle tracks keep their path but lose signed queries.
        if (key == "thumbnail" || section is "thumbnails" or "subtitles" or "automatic_captions" or "storyboards") return new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.AbsoluteUri;
        // Media, manifest, fragment and key addresses expire and can carry
        // signatures and the viewer's IP address in the path; only the host is kept.
        if (key is "url" or "uri" or "manifest_url" or "fragment_base_url" || key.EndsWith("_uri", StringComparison.Ordinal) || section is "formats" or "requested_formats" or "requested_downloads") return uri.GetLeftPart(UriPartial.Authority) + "/";
        return MediaValidation.ScrubPublicSourceUrl(new Uri(PublicSourceUrl(uri)));
    }
}
