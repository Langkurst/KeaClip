using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Klippi.Core;

public static partial class MediaValidation
{
    public static bool TryMediaUrl(string? value, out Uri? url)
    {
        url = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(char.IsControl)) return false;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var candidate) || candidate.Scheme is not ("https" or "http") || string.IsNullOrWhiteSpace(candidate.Host) || !string.IsNullOrEmpty(candidate.UserInfo) || candidate.AbsoluteUri.Length > 8192) return false;
        url = candidate;
        return true;
    }

    public static Uri ParseProtocol(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 16384 || value.Any(char.IsControl) || !Uri.TryCreate(value, UriKind.Absolute, out var protocol)
            || protocol.Scheme != AppIdentity.Protocol || protocol.Host != "download" || !string.IsNullOrEmpty(protocol.UserInfo)
            || protocol.Port != -1 || protocol.AbsolutePath is not ("" or "/") || !string.IsNullOrEmpty(protocol.Fragment))
            throw new MediaOperationException("This app link is not valid.");
        var pairs = protocol.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        var urls = pairs.Where(p => p.StartsWith("url=", StringComparison.Ordinal)).ToArray();
        if (pairs.Length != 1 || urls.Length != 1 || !TryMediaUrl(Uri.UnescapeDataString(urls[0][4..]), out var url))
            throw new MediaOperationException("This app link does not contain a valid media URL.");
        return url!;
    }

    // The result is passed to yt-dlp and FFmpeg as literal file name text, so it must be valid
    // UTF-16: an unpaired surrogate makes the post-processors fail after the whole download.
    // Truncation keeps whole graphemes (emoji, flags, accented letters), and every exit path
    // returns a non-empty, non-reserved name.
    public static string FriendlyFileName(string title, int maxLength = 140)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToHashSet();
        var builder = new StringBuilder(title.Length);
        for (var index = 0; index < title.Length; index++)
        {
            var character = title[index];
            if (char.IsHighSurrogate(character) && index + 1 < title.Length && char.IsLowSurrogate(title[index + 1])) builder.Append(character).Append(title[++index]);
            else builder.Append(invalid.Contains(character) || char.IsControl(character) || char.IsSurrogate(character) ? '_' : character);
        }
        var limit = Math.Max(8, maxLength);
        var name = TextPrefix(builder.ToString().Trim().TrimEnd('.'), limit).TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(name)) return "Media";
        return ReservedName().IsMatch(name) ? TextPrefix("_" + name, limit).TrimEnd('.', ' ') : name;
    }

    // Cuts on a grapheme boundary. A single grapheme longer than the limit (a long run of
    // combining marks) is cut between code points instead, which still never splits a pair.
    private static string TextPrefix(string text, int limit)
    {
        if (text.Length <= limit) return text;
        int end = 0, next;
        while ((next = end + StringInfo.GetNextTextElementLength(text, end)) <= limit) end = next;
        if (end == 0) end = char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
        return text[..end];
    }

    // The seconds piece usually arrives as "48.250", but SRT subtitle timestamps
    // ("00:05:48,250") and comma-decimal keyboard layouts produce "48,250".
    // pieceCount is how many colon-separated parts the whole timecode had.
    private static string SecondsText(string piece, int pieceCount)
    {
        // After a colon, seconds stay below 60, so one comma can only be a decimal
        // point. A bare "1,000" (one second or a thousand?) and mixed separators
        // stay unparseable rather than guessed.
        if (pieceCount < 2 || piece.Contains('.') || piece.Count(c => c == ',') != 1) return piece;
        return piece.Replace(',', '.');
    }

    public static bool TryTimecode(string? value, out TimeSpan result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var pieces = value.Trim().Split(':');
        if (pieces.Length is < 1 or > 3) return false;
        if (!double.TryParse(SecondsText(pieces[^1], pieces.Length), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) || seconds < 0 || !double.IsFinite(seconds)) return false;
        if (pieces.Length > 1 && seconds >= 60) return false;
        double total = seconds;
        if (pieces.Length >= 2)
        {
            if (!int.TryParse(pieces[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes < 0 || (pieces.Length == 3 && minutes >= 60)) return false;
            total += minutes * 60L;
        }
        if (pieces.Length == 3)
        {
            if (!int.TryParse(pieces[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) || hours < 0) return false;
            total += hours * 3600L;
        }
        if (total > TimeSpan.MaxValue.TotalSeconds) return false;
        result = TimeSpan.FromSeconds(total);
        return true;
    }

    public static string Timecode(TimeSpan time) => $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";

    // Source-link policy. The stored link (history, the duplicate check, "Copy source link" and
    // "Download again") must still name the same single media item, but must never carry
    // secrets or tracking. It is an allowlist: a query parameter survives only when it selects
    // WHICH media a page shows (YouTube v, Bilibili p, Facebook story_fbid, Twitch clip, ...).
    // Everything else is removed, including sign-in, session and signature values (token, sig,
    // auth, key, X-Amz-*), tracking (utm_*, si, fbclid, feature, pp) and playback context
    // (t, start, index), so a new kind of secret is never stored by accident. A YouTube list is
    // identity only for a playlist page; next to v it is context, and downloads use
    // --no-playlist. p (Bilibili part, WordPress post) is kept only when numeric, because some
    // sites use it for a password. Vimeo's h is the unlisted-video hash that is part of the
    // video's address, like an unlisted YouTube id. Facebook's plugin href is itself a link, so
    // it is kept only as the safe form of a facebook.com/fb.watch link (one level deep).
    // Userinfo and fragments are always removed, and path-style signatures are scrubbed
    // exactly as in Redact. Applying the policy twice gives the same link.
    private static readonly HashSet<string> IdentityParameters = new(StringComparer.OrdinalIgnoreCase)
    { "v", "id", "vid", "video", "video_id", "videoid", "media_id", "mediaid", "clip", "clip_id", "list", "lang", "viewkey", "story_fbid", "fbid" };
    private static readonly (string[] Hosts, string[] Parameters)[] SiteIdentityParameters =
    [
        (["bilibili.com", "b23.tv"], ["bvid", "aid", "cid", "page"]),
        (["facebook.com", "fb.watch"], ["href"]),
        (["twitch.tv"], ["channel", "collection"]),
        (["vimeo.com"], ["h"]),
        (["vk.com", "vk.ru", "vkvideo.ru"], ["z", "oid"]),
        (["podcasts.apple.com"], ["i"]),
    ];
    // Dropped parameters known NOT to choose the media: tracking and share markers, playback
    // position and embed-player settings. Credentials are deliberately absent, because a
    // one-time download token can be the only thing that tells two files apart.
    private static readonly HashSet<string> ContextParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "t", "start", "end", "time_continue", "start_radio", "autoplay", "loop", "mute", "muted", "controls", "rel", "playsinline", "parent",
        "si", "feature", "pp", "ab_channel", "fbclid", "gclid", "gclsrc", "dclid", "msclkid", "yclid", "twclid", "ttclid", "igshid", "igsh", "mc_cid", "mc_eid", "mibextid",
        "ref", "ref_src", "ref_url", "rdid", "sfnsn", "share_source", "share_medium", "share_plat", "share_session_id", "share_from", "share_tag", "unique_k",
        "vd_source", "spm_id_from", "from_spmid", "is_from_webapp", "sender_device", "embeds_referring_euri", "embeds_referring_origin", "source_ve_path",
    };

    public static string SafeSourceUrl(Uri url) => SafeSourceUrl(url, nested: false);

    // History written by earlier versions stored links under an older policy (t, index, or a
    // YouTube list next to v were kept). Comparing SafeStoredSourceUrl(entry.SourceUrl) with
    // SafeSourceUrl(newLink) maps such entries onto today's form; null for a missing or
    // non-web link.
    public static string? SafeStoredSourceUrl(string? stored) => TryMediaUrl(stored, out var url) ? SafeSourceUrl(url!) : null;

    // False when SafeSourceUrl had to drop something that may choose the media: an unknown or
    // credential query parameter (download.php?file=a.mp4 versus file=b.mp4, ?token=one-time),
    // a path token, or a fragment other than a time (#/video/1). Two different items can then
    // share one safe link, so a duplicate found through it must not offer Replace.
    public static bool SafeSourceUrlIsExact(Uri url) => SafeSourceUrlIsExact(url, nested: false);

    private static bool SafeSourceUrlIsExact(Uri url, bool nested)
        => SourceParameters(url, nested).All(parameter => parameter.Exact)
            && ScrubSignedPath(url.Host, url.AbsolutePath) == url.AbsolutePath
            && (url.Fragment.Length <= 1 || TimeFragment().IsMatch(url.Fragment));

    private static string SafeSourceUrl(Uri url, bool nested)
    {
        var builder = new UriBuilder(url) { Fragment = "", Query = string.Join('&', SourceParameters(url, nested).Select(parameter => parameter.Kept).OfType<string>()), UserName = "", Password = "" };
        if (ScrubSignedPath(url.Host, url.AbsolutePath) is var path && path != url.AbsolutePath) builder.Path = path;
        return builder.Uri.AbsoluteUri;
    }

    // Kept is the query pair to store, or null when it is dropped. Exact is false when the pair
    // was dropped or reduced and may have chosen the media.
    private static IEnumerable<(string? Kept, bool Exact)> SourceParameters(Uri url, bool nested)
    {
        var pairs = url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair => (Name: Uri.UnescapeDataString(pair.Split('=')[0]), Value: pair.Contains('=') ? pair[(pair.IndexOf('=') + 1)..] : "", Pair: pair)).ToArray();
        var youTube = OnHost(url.Host, "youtube.com", "youtu.be", "youtube-nocookie.com");
        var youTubeVideo = youTube && pairs.Any(pair => pair.Name.Equals("v", StringComparison.OrdinalIgnoreCase));
        foreach (var (name, value, pair) in pairs)
        {
            if (youTubeVideo && name.Equals("list", StringComparison.OrdinalIgnoreCase) || youTube && name.Equals("index", StringComparison.OrdinalIgnoreCase)) yield return (null, true);
            else if (!(IdentityParameters.Contains(name) || SiteIdentityParameters.Any(site => site.Parameters.Contains(name, StringComparer.OrdinalIgnoreCase) && OnHost(url.Host, site.Hosts))
                || name.Equals("p", StringComparison.OrdinalIgnoreCase) && value.Length > 0 && value.All(char.IsAsciiDigit)))
                yield return (null, ContextParameters.Contains(name) || name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase));
            else if (name.Equals("href", StringComparison.OrdinalIgnoreCase))
            {
                var link = !nested && TryMediaUrl(Uri.UnescapeDataString(value), out var inner) && OnHost(inner!.Host, "facebook.com", "fb.watch") ? inner : null;
                yield return link is null ? (null, false) : (pair[..(pair.IndexOf('=') + 1)] + Uri.EscapeDataString(SafeSourceUrl(link, nested: true)), SafeSourceUrlIsExact(link, nested: true));
            }
            else yield return (pair, true);
        }
    }

    // The archive reference and the page links in archive metadata (EngineSupport.PublicSourceUrl)
    // keep a few more public query values than the stored link, such as a start time or an
    // episode, but never what this policy removes as a secret: path-style signatures and the
    // viewer's address in a CDN path (ScrubSignedPath), a p that is not numeric (a password on
    // some sites), userinfo and the fragment. The query is expected to be limited to public
    // parameter names already; only p is judged here. Applying it twice gives the same link.
    public static string ScrubPublicSourceUrl(Uri url)
    {
        var query = url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Where(pair =>
        {
            if (!Uri.UnescapeDataString(pair.Split('=')[0]).Equals("p", StringComparison.OrdinalIgnoreCase)) return true;
            var value = pair.Contains('=') ? pair[(pair.IndexOf('=') + 1)..] : "";
            return value.Length > 0 && value.All(char.IsAsciiDigit);
        });
        var builder = new UriBuilder(url) { Fragment = "", Query = string.Join('&', query), UserName = "", Password = "" };
        if (ScrubSignedPath(url.Host, url.AbsolutePath) is var path && path != url.AbsolutePath) builder.Path = path;
        return builder.Uri.AbsoluteUri;
    }

    // A File.Kiwi share link carries its decryption key in the fragment (#secret). It is the
    // user's own upload, never a new download, so it is not offered from the clipboard and its
    // key never reaches the Download box or a downloader's command line.
    // A KeaClip watch link carries "#<object id>.<key>" on whatever host serves the player, so the fragment shape decides.
    // Sites people usually copy video or audio links from. Clipboard detection fills the link box only for these or for a
    // direct media file, so copying an ordinary page (a shop, a docs page, a Ko-fi page) never lands in the Download box.
    // Pasting or typing still accepts any link.
    private static readonly string[] MediaSites =
    [
        "youtube.com", "youtu.be", "youtube-nocookie.com", "vimeo.com", "twitch.tv", "tiktok.com", "instagram.com", "x.com", "twitter.com",
        "reddit.com", "redd.it", "soundcloud.com", "bandcamp.com", "dailymotion.com", "dai.ly", "facebook.com", "fb.watch", "archive.org",
        "streamable.com", "bilibili.com", "b23.tv", "kick.com", "rumble.com", "odysee.com", "mixcloud.com", "wikimedia.org", "imgur.com",
        "vk.com", "nicovideo.jp", "medal.tv", "tumblr.com", "pinterest.com", "threads.net", "bsky.app", "ted.com", "loom.com", "bitchute.com",
        "peertube.tv", "audiomack.com", "spreaker.com", "podbean.com", "nebula.tv", "floatplane.com", "9gag.com", "ok.ru",
    ];
    private static readonly string[] MediaFileExtensions = [".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".opus", ".m3u8", ".mpd"];

    /// <summary>A link that very likely points at video or audio: a well-known media site, or a direct media file.</summary>
    public static bool LooksLikeMediaLink(Uri url) =>
        url.IsAbsoluteUri && url.Scheme is "https" or "http" && (OnHost(url.Host, MediaSites) || MediaFileExtensions.Any(extension => url.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)));

    public static bool IsPrivateShareLink(Uri url) => (OnHost(url.Host, "file.kiwi") && url.Fragment.Length > 1) || IsWatchLinkFragment(url.Fragment);

    /// <summary>The fragment of a KeaClip watch link: '#', a 22-character object id, '.', and a 22-character base64url key.</summary>
    public static bool IsWatchLinkFragment(string? fragment) => fragment is { Length: 46 } && fragment[0] == '#' && fragment[23] == '.'
        && fragment.AsSpan(1).ToString().Remove(22, 1).All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool OnHost(string host, params string[] domains) => domains.Any(domain => host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var redacted = SecretHeader().Replace(text, "$1[redacted]");
        redacted = SecretField().Replace(redacted, "$1[redacted]");
        return UrlPattern().Replace(redacted, match =>
        {
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)) return "[redacted URL]";
            var clean = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri;
            return clean.GetLeftPart(UriPartial.Authority) + ScrubSignedPath(clean.Host, clean.AbsolutePath);
        });
    }

    // Some CDNs sign in the path instead of the query. googlevideo manifest and segment URLs
    // read /expire/../ei/../ip/<viewer address>/../sig/../lsig/.., and Akamai-style tokens read
    // /hdnts=exp=..~hmac=../. A path counts as key/value signed only on googlevideo or when it
    // has at least two distinct signing markers, so an ordinary /sig/ or /ip/ page is untouched.
    // Only the values of secret keys are replaced; the rest of the path stays readable.
    private static readonly HashSet<string> SignedPathMarkers = new(StringComparer.OrdinalIgnoreCase) { "expire", "ei", "ip", "sparams", "lsparams", "sig", "lsig", "signature" };
    private static readonly HashSet<string> SignedPathSecrets = new(StringComparer.OrdinalIgnoreCase) { "ip", "ei", "sig", "lsig", "signature", "pot" };

    private static string ScrubSignedPath(string host, string path)
    {
        var segments = path.Split('/');
        if (OnHost(host, "googlevideo.com") || segments.SkipLast(1).Where(SignedPathMarkers.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2)
            for (var index = 0; index + 1 < segments.Length; index++)
                if (SignedPathSecrets.Contains(segments[index]) && segments[index + 1].Length > 0) segments[++index] = "[redacted]";
        return PathToken().Replace(string.Join('/', segments), "$1=[redacted]");
    }

    // URLs (already redacted) are matched first and kept, so "https://" is never mistaken for
    // a drive and a path inside a URL is never treated as local. A local path glued to a URL by
    // a quote, comma or semicolon (".../a',C:\Users\...", which Redact rewrites with slashes) ends
    // the URL and is redacted as a path. A double-quoted local path ends at its closing quote. A
    // single-quoted one (.NET quotes paths this way) ends at the first quote followed by a space,
    // punctuation or the line end, unless the text up to the next such quote (an apostrophe
    // inside a word, as in "Mom's", does not count) still reads as part of the path: a folder
    // separator comes first ("Kids' Mom's Videos\x.mp4") or that next quote closes it ("Guns N'
    // Roses'."). An unquoted one ends at a colon, a line break, just before the next
    // local root or URL (so "C:\a;D:\b" and "x to D:\y" never expose the second path), or at a
    // comma, semicolon or full stop followed by a space that comes right after a file extension
    // with no folder separator or extension later ("x.mp4, retrying"). After a bare folder name
    // ("C:\Users\Smith, John") the punctuation stays inside the path, so the rest of the line
    // is removed rather than exposed: when unsure, redact more. That keeps the usual reason text
    // ("The system cannot find the file specified.", ": Access denied") readable.
    public static string RedactDiagnostics(string? text) => DiagnosticSpan().Replace(Redact(text), match =>
    {
        var path = match.Groups["tail"] is { Success: true } tail ? tail : match.Groups["url"].Success ? null : (Group)match;
        if (path is null) return match.Value;
        var kept = path.Value.TrimEnd();
        return match.Value[..(path.Index - match.Index)] + "[local path]" + path.Value[kept.Length..];
    });

    // A drive root may follow any character ("1C:\", an escaped "\nC:\"); "/(?!/)" keeps
    // rtmp://, ftp:// and file:// from reading as a drive. \\?\ and \\.\ device prefixes,
    // including \\?\C:\, belong to the root so the path after them is never left behind.
    // /Users/ and /home/ are roots wherever they appear (/mnt/c/Users/..., /var/home/...),
    // except in the path of a link with a host (ftp://example.com/home/x; looking back at most
    // 1 KB keeps a long token cheap). file://server/ and file://// are UNC paths written as links.
    private const string DriveRoot = @"[a-z]:(?:\\|/(?!/))";
    private const string OtherRoot = @"\\\\(?:[?.]\\(?:[a-z]:\\)?)?|(?<![a-z0-9+.-])file:(?://(?!/)[^/\s]+/|/{4})|(?=/(?:Users|home)/)(?<!://[^\s/][^\s'"",;]{0,1024})/(?:Users|home)/";
    private const string LocalRoot = "(?:" + DriveRoot + "|" + OtherRoot + ")";
    private const string NextStart = @"[.,;]?\s?['""]?(?:https?://|" + DriveRoot + @"|(?<=[\s;,'""])(?:" + OtherRoot + "))";
    private const string UnquotedPath = @"(?:(?!" + NextStart + @")(?:[^\r\n""<>|*?:.,;]|[.,;](?!\s|\z)|(?<!\.[\p{L}\p{N}]{1,4})[.,;]|[.,;](?=[^\r\n""<>|*?:]*?(?:[\\/]|\.[\p{L}\p{N}]{1,5}(?![\p{L}\p{N}])))))*";
    // Inside a URL token Redact has already turned \ into /, so \\server\ reads as //server/.
    private const string UrlTailRoot = @"(?:[a-z]:[\\/]|[\\/]{2}|/(?:Users|home)/)";
    // An apostrophe inside a word ("O'Brien", "Mom's") is part of a single-quoted path.
    private const string InnerQuote = @"'(?![\s.,;:)\]]|$)";

    [GeneratedRegex(@"^#(?:t|start)=[0-9hms.:]{1,16}$", RegexOptions.IgnoreCase)] private static partial Regex TimeFragment();
    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase)] private static partial Regex ReservedName();
    [GeneratedRegex(@"(?i)((?:cookie|authorization|uploadAuth|token|password|secret|X-Amz-Signature)[\s\""':=]+)[^\s,\""']+")] private static partial Regex SecretField();
    [GeneratedRegex(@"(?im)((?:cookie|authorization)[\""']?\s*[:=]\s*)[^\r\n]+")] private static partial Regex SecretHeader();
    [GeneratedRegex(@"https?://[^\s\""<>]+", RegexOptions.IgnoreCase)] private static partial Regex UrlPattern();
    [GeneratedRegex(@"(?i)(?<![^/;~,&])(hdnts|hdntl|hdnea|hmac|token|__token__|access_token|sig|signature|policy|jsessionid|phpsessid|sessionid)=[^/;~,&]*")] private static partial Regex PathToken();
    [GeneratedRegex(@"(?i)(?<url>https?://(?:(?![',;]" + UrlTailRoot + @")[^\s""<>])+)(?:[',;](?<tail>" + UrlTailRoot + UnquotedPath + "))?"
        + @"|(?<=')" + LocalRoot + @"(?:[^'\r\n""<>|*?]|" + InnerQuote + @"|'(?=(?:[^'\r\n""<>|*?:]|" + InnerQuote + @")*?(?:[\\/]|'(?![^\s.,;:)\]]))))*"
        + @"|(?<="")" + LocalRoot + @"[^""\r\n<>|*?]*"
        + @"|" + LocalRoot + UnquotedPath)]
    private static partial Regex DiagnosticSpan();
}
