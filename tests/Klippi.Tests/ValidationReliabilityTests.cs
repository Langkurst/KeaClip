using System.Globalization;
using System.Text;
using Klippi.Core;
using Xunit;

namespace Klippi.Tests;

public sealed class ValidationReliabilityTests
{
    private const string Grin = "\U0001F600";
    private const string Family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";

    private static bool ValidUtf16(string value) => Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(value)) == value;
    private static bool OnTextElementBoundary(string source, int length) => length == source.Length || StringInfo.ParseCombiningCharacters(source).Contains(length);

    [Theory]
    [InlineData(139, 140)]
    [InlineData(89, 90)]
    public void FriendlyFileNameNeverSplitsASurrogatePair(int prefix, int maxLength)
    {
        var name = MediaValidation.FriendlyFileName(new string('a', prefix) + Grin + "zzz", maxLength);
        Assert.True(ValidUtf16(name));
        Assert.Equal(new string('a', prefix), name);
    }

    [Fact]
    public void FriendlyFileNameKeepsWholeEmojiAndCombiningSequences()
    {
        var family = new string('a', 137) + Family + "z";
        Assert.Equal(new string('a', 137), MediaValidation.FriendlyFileName(family));
        var accent = new string('a', 139) + "e\u0301x";
        Assert.Equal(new string('a', 139), MediaValidation.FriendlyFileName(accent));
        var fits = "Clip " + Family + " e\u0301";
        Assert.Equal(fits, MediaValidation.FriendlyFileName(fits));
    }

    [Fact]
    public void FriendlyFileNameReplacesUnpairedSurrogates()
    {
        Assert.Equal("x_y", MediaValidation.FriendlyFileName("x\uD83Dy"));
        Assert.Equal("_abc", MediaValidation.FriendlyFileName("\uDE00abc"));
        Assert.Equal("a" + Grin + "_", MediaValidation.FriendlyFileName("a" + Grin + "\uD83D"));
    }

    [Theory]
    [InlineData(". . .")]
    [InlineData(".. ..")]
    [InlineData(" . ")]
    [InlineData("\u00A0.\u00A0")]
    [InlineData("")]
    public void FriendlyFileNameIsNeverEmpty(string title) => Assert.Equal("Media", MediaValidation.FriendlyFileName(title));

    [Fact]
    public void FriendlyFileNameFallsBackWhenTruncationLeavesOnlyDots()
        => Assert.Equal("Media", MediaValidation.FriendlyFileName(new string('.', 10) + "abc", 8));

    [Fact]
    public void FriendlyFileNameChecksReservedNamesAfterTruncation()
        => Assert.Equal("_CON", MediaValidation.FriendlyFileName("CON" + new string(' ', 10) + "x", 8));

    [Fact]
    public void FriendlyFileNameHandlesOneOverlongGrapheme()
    {
        var name = MediaValidation.FriendlyFileName("a" + new string('\u0301', 300), 20);
        Assert.InRange(name.Length, 1, 20);
        Assert.Equal('a', name[0]);
        Assert.True(ValidUtf16(name));
    }

    [Fact]
    public void FriendlyFileNameAlwaysProducesASafeName()
    {
        string[] pieces = ["a", "Z", " ", ".", "..", Grin, Family, "e\u0301", "\uD83D", "\uDE00", "\u200D", "CON", "nul.", "LPT1", "<", ":", "/", "\\", "\t", "%", "\u00A0", "\U0001F1F3\U0001F1FF"];
        var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToHashSet();
        var random = new Random(20261012);
        for (var run = 0; run < 3000; run++)
        {
            var builder = new StringBuilder();
            for (var count = random.Next(0, 60); count > 0; count--) builder.Append(pieces[random.Next(pieces.Length)]);
            var title = builder.ToString();
            var maxLength = random.Next(1, 40);
            var name = MediaValidation.FriendlyFileName(title, maxLength);
            Assert.True(name.Length > 0, $"Empty name for run {run}");
            Assert.True(name.Length <= Math.Max(8, maxLength), $"Too long for run {run}");
            Assert.True(ValidUtf16(name), $"Unpaired surrogate for run {run}");
            Assert.DoesNotContain(name, character => invalid.Contains(character) || char.IsControl(character));
            Assert.False(name.EndsWith('.') || name.EndsWith(' ') || char.IsWhiteSpace(name[0]), $"Untrimmed name for run {run}");
            Assert.DoesNotMatch(@"(?i)^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", name);
        }
    }

    [Theory]
    [InlineData("https://www.bilibili.com/video/BV1bK411W797?p=1", "https://www.bilibili.com/video/BV1bK411W797?p=2")]
    [InlineData("https://www.facebook.com/permalink.php?story_fbid=111&id=222", "https://www.facebook.com/permalink.php?story_fbid=333&id=222")]
    [InlineData("https://www.facebook.com/photo.php?fbid=111", "https://www.facebook.com/photo.php?fbid=222")]
    [InlineData("https://clips.twitch.tv/embed?clip=FunnySlug1&parent=example.com", "https://clips.twitch.tv/embed?clip=OtherSlug2&parent=example.com")]
    [InlineData("https://player.twitch.tv/?video=v111", "https://player.twitch.tv/?video=v222")]
    [InlineData("https://vk.com/video?z=video-1_111", "https://vk.com/video?z=video-1_222")]
    [InlineData("https://podcasts.apple.com/us/podcast/show/id123?i=1000111", "https://podcasts.apple.com/us/podcast/show/id123?i=1000222")]
    [InlineData("https://player.bilibili.com/player.html?bvid=BV1aaa&page=1", "https://player.bilibili.com/player.html?bvid=BV1aaa&page=2")]
    [InlineData("https://www.youtube.com/playlist?list=PL111", "https://www.youtube.com/playlist?list=PL222")]
    public void SafeSourceUrlKeepsDifferentMediaApart(string first, string second)
        => Assert.NotEqual(MediaValidation.SafeSourceUrl(new(first)), MediaValidation.SafeSourceUrl(new(second)));

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42s&si=abc&feature=share&pp=x", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PL123&index=7&start_radio=1", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=tracking&t=3", "https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/playlist?list=PL123&si=abc", "https://www.youtube.com/playlist?list=PL123")]
    [InlineData("https://www.bilibili.com/video/BV1bK411W797?p=2&vd_source=abc&spm_id_from=333", "https://www.bilibili.com/video/BV1bK411W797?p=2")]
    [InlineData("https://player.vimeo.com/video/123456?h=abc123&autoplay=1", "https://player.vimeo.com/video/123456?h=abc123")]
    [InlineData("https://example.com/watch?h=abc123&v=9", "https://example.com/watch?v=9")]
    [InlineData("https://www.facebook.com/watch/?v=123&mibextid=abc&fbclid=xyz", "https://www.facebook.com/watch/?v=123")]
    [InlineData("https://example.com/media?id=5&utm_source=x&utm_medium=y&gclid=z", "https://example.com/media?id=5")]
    [InlineData("https://example.com/v?id=5&token=a&access_token=b&sig=c&signature=d&X-Amz-Signature=e&auth=f&key=g&session=h&expires=i&policy=j&unknown=k", "https://example.com/v?id=5")]
    [InlineData("https://user:password@example.com/clip?v=1#secret", "https://example.com/clip?v=1")]
    [InlineData("https://blog.example.com/?p=123&replytocom=9", "https://blog.example.com/?p=123")]
    [InlineData("https://media.example.com/watch?u=me&p=hunter2&id=7", "https://media.example.com/watch?id=7")]
    public void SafeSourceUrlKeepsIdentityAndDropsTrackingAndSecrets(string input, string expected)
        => Assert.Equal(expected, MediaValidation.SafeSourceUrl(new(input)));

    [Fact]
    public void SafeSourceUrlTreatsTheSameMediaWithDifferentTrackingAsEqual()
        => Assert.Equal(MediaValidation.SafeSourceUrl(new("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=10")), MediaValidation.SafeSourceUrl(new("https://www.youtube.com/watch?si=x&v=dQw4w9WgXcQ")));

    private const string Manifest = "https://manifest.googlevideo.com/api/manifest/hls_playlist/expire/1693526400/ei/IAbcdEFgh/ip/203.0.113.5/id/o-AbCdEf.1/itag/96/source/youtube/requiressl/yes/sparams/expire,ei,ip,id,itag,source,requiressl/sig/AOq0QJ8wRQIgSIGVALUEAAA/lsparams/mh,mm,mn/lsig/AG3C_xAwRQIhLSIGVALUEBBB/playlist/index.m3u8";

    [Fact]
    public void RedactScrubsPathEncodedSignedUrlParameters()
    {
        var redacted = MediaValidation.Redact("WARNING: fragment failed for " + Manifest + " retrying");
        Assert.DoesNotContain("203.0.113.5", redacted);
        Assert.DoesNotContain("SIGVALUEAAA", redacted);
        Assert.DoesNotContain("LSIGVALUEBBB", redacted);
        Assert.DoesNotContain("IAbcdEFgh", redacted);
        Assert.Contains("https://manifest.googlevideo.com/api/manifest/hls_playlist/", redacted);
        Assert.Contains("/itag/96/", redacted);
        Assert.Contains("/playlist/index.m3u8 retrying", redacted);
        Assert.StartsWith("WARNING: fragment failed for ", redacted);
    }

    [Fact]
    public void RedactScrubsIpv6AndSegmentBaseUrls()
    {
        var redacted = MediaValidation.Redact("https://rr3---sn-abc.googlevideo.com/videoplayback/id/o-AbC/itag/137/ip/2001:db8:85a3::8a2e:370:7334/pot/MlPoTokenValue/sig/SIGVALUE/");
        Assert.DoesNotContain("2001:db8", redacted);
        Assert.DoesNotContain("MlPoTokenValue", redacted);
        Assert.DoesNotContain("SIGVALUE", redacted);
        Assert.Contains("/itag/137/", redacted);
    }

    [Theory]
    [InlineData("https://cdn.example.akamaized.net/hdnts=exp=1700000000~acl=%2f*~hmac=0123abcdef/master.m3u8", "0123abcdef", "/master.m3u8")]
    [InlineData("https://cdn.example.com/hls/token=SESSIONTOKEN/playlist.m3u8", "SESSIONTOKEN", "https://cdn.example.com/hls/")]
    [InlineData("https://example.com/watch;jsessionid=ABCDEF123456/video", "ABCDEF123456", "/video")]
    public void RedactScrubsPathTokens(string url, string secret, string kept)
    {
        var redacted = MediaValidation.Redact(url);
        Assert.DoesNotContain(secret, redacted);
        Assert.Contains(kept, redacted);
    }

    [Theory]
    [InlineData("https://example.org/sig/networking/page")]
    [InlineData("https://example.com/docs/ip/addressing")]
    [InlineData("https://example.com/releases/1.2.3.4/notes")]
    [InlineData("https://www.youtube.com/watch")]
    [InlineData("https://example.com/expire/soon")]
    [InlineData("https://example.com/home/Users/page")]
    public void RedactLeavesOrdinaryPathsAlone(string url) => Assert.Equal(url, MediaValidation.Redact(url));

    [Fact]
    public void SafeSourceUrlDoesNotKeepPathSignatures()
    {
        var safe = MediaValidation.SafeSourceUrl(new(Manifest));
        Assert.DoesNotContain("203.0.113.5", safe);
        Assert.DoesNotContain("SIGVALUEAAA", safe);
        Assert.True(MediaValidation.TryMediaUrl(safe, out _));
    }

    [Fact]
    public void RedactDiagnosticsKeepsUrlsReadable()
    {
        Assert.Equal("See https://example.com/x for help", MediaValidation.RedactDiagnostics("See https://example.com/x for help"));
        Assert.Equal("https://file.kiwi/abcd", MediaValidation.RedactDiagnostics("https://file.kiwi/abcd#SECRETKEY"));
        Assert.Equal("Open https://example.com/home/page and http://example.com/Users/x", MediaValidation.RedactDiagnostics("Open https://example.com/home/page and http://example.com/Users/x"));
        Assert.Equal("rtmp://live.example.com/app and ftp://example.com/home/x", MediaValidation.RedactDiagnostics("rtmp://live.example.com/app and ftp://example.com/home/x"));
        Assert.Equal("ftp://example.com/pub/home/x and wss://example.com/a/Users/b", MediaValidation.RedactDiagnostics("ftp://example.com/pub/home/x and wss://example.com/a/Users/b"));
    }

    [Fact]
    public void RedactDiagnosticsKeepsTheReasonAfterAQuotedPath()
    {
        var redacted = MediaValidation.RedactDiagnostics(@"An error occurred trying to start process 'C:\Users\Sam\AppData\Local\KeaClip\yt-dlp.exe' with working directory 'C:\Users\Sam'. The system cannot find the file specified.");
        Assert.Equal("An error occurred trying to start process '[local path]' with working directory '[local path]'. The system cannot find the file specified.", redacted);
    }

    [Theory]
    [InlineData(@"Opening C:\Users\Sam\Downloads\KeaClip\video.mp4 failed: Access denied", "Opening [local path]: Access denied")]
    [InlineData(@"Access to the path 'C:\Users\Sam\Videos\clip.mp4' is denied.", "Access to the path '[local path]' is denied.")]
    [InlineData(@"Could not find file 'C:\Users\O'Brien\Videos\x.mp4'.", "Could not find file '[local path]'.")]
    [InlineData(@"ERROR: unable to open for writing: [Errno 13] Permission denied: 'C:\\Users\\Sam\\x.part'", "ERROR: unable to open for writing: [Errno 13] Permission denied: '[local path]'")]
    [InlineData("[Merger] Merging formats into \"C:\\Users\\Sam\\My 'Clip'.mp4\" done", "[Merger] Merging formats into \"[local path]\" done")]
    [InlineData(@"C:\Users\Sam\x.mp4: No such file or directory", "[local path]: No such file or directory")]
    [InlineData(@"[download] Destination: D:/Media/Sam/x.mp4", "[download] Destination: [local path]")]
    [InlineData(@"Could not open C:\Users\Sam\x.mp4 see https://example.com/help", "Could not open [local path] https://example.com/help")]
    [InlineData(@"\\server\share\Sam\x.mp4 failed: offline", "[local path]: offline")]
    [InlineData("/home/sam/videos/x.mp4: denied", "[local path]: denied")]
    [InlineData("Read /Users/sam/Movies/x.mov, retrying in 5 s", "Read [local path], retrying in 5 s")]
    [InlineData(@"Could not read C:\Users\Sam\x.mp4. Choose another file.", "Could not read [local path]. Choose another file.")]
    [InlineData(@"Saved C:\Users\Smith, John\Videos\x.mp4 failed: offline", "Saved [local path]: offline")]
    [InlineData(@"Saved C:\Users\Sam\Hello, World.mp4 failed", "Saved [local path]")]
    [InlineData(@"Read C:\Users\Sam\Mr. Smith.mp4, retrying", "Read [local path], retrying")]
    [InlineData("file:///C:/Users/Sam/x.mp4 missing\nNext line: kept", "file:///[local path]\nNext line: kept")]
    [InlineData(@"PATH=C:\Windows;C:\Users\Sam\bin;D:\Tools", "PATH=[local path];[local path];[local path]")]
    [InlineData(@"Read C:\Users\Sam\x.mp4, then wrote D:\y.mp4", "Read [local path], then wrote [local path]")]
    [InlineData(@"Moving C:\Users\Sam\x.mp4 to 'D:\Users\bob\y.mp4' failed", "Moving [local path] '[local path]' failed")]
    [InlineData(@"\\?\C:\Users\Sam\x.mp4: denied", "[local path]: denied")]
    [InlineData(@"Could not read C:\Users\Sam\x.mp4.", "Could not read [local path].")]
    [InlineData(@"C:\Users\Sam\x.mp4:Zone.Identifier", "[local path]:Zone.Identifier")]
    [InlineData("Stream #0:1: Video: h264, yuv420p, 1920x1080. Time 12:30:45, ratio 16:9", "Stream #0:1: Video: h264, yuv420p, 1920x1080. Time 12:30:45, ratio 16:9")]
    [InlineData("wss://example.com/home/x and srt://host:9000/x", "wss://example.com/home/x and srt://host:9000/x")]
    [InlineData(@"Could not find file 'C:\Users\Sam\Kids' Videos\Emma birthday.mp4'.", "Could not find file '[local path]'.")]
    [InlineData(@"The process cannot access the file 'C:\Users\Sam\Parents' house\x.mp4' because it is being used by another process.", "The process cannot access the file '[local path]' because it is being used by another process.")]
    [InlineData(@"Could not find a part of the path 'C:\Music\Guns N' Roses'.", "Could not find a part of the path '[local path]'.")]
    [InlineData(@"Access to the path 'C:\x.mp4' is denied. Check the folder's permissions.", "Access to the path '[local path]' is denied. Check the folder's permissions.")]
    [InlineData(@"Could not open 'C:\x.mp4': it's locked", "Could not open '[local path]': it's locked")]
    [InlineData(@"Could not open 'C:\Users\Sam\Kids' Mom's Videos\x.mp4' because it's locked", "Could not open '[local path]' because it's locked")]
    [InlineData("/mnt/c/Users/sam/x.mp4: denied", "/mnt/c[local path]: denied")]
    [InlineData("file://nas/share/sam/x.mp4: offline", "[local path]: offline")]
    [InlineData(@"Fetch https://example.com/a',C:\Users\Sam\x.mp4 failed: offline", "Fetch https://example.com/a',[local path]: offline")]
    public void RedactDiagnosticsRedactsOnlyThePath(string input, string expected) => Assert.Equal(expected, MediaValidation.RedactDiagnostics(input));

    // Every input names a person in its path; none of those names may survive, whatever the
    // path is next to. When a boundary is unclear the rest of the line is redacted instead.
    [Theory]
    [InlineData(@"PATH=C:\Windows;C:\Users\jane\bin;D:\Tools")]
    [InlineData(@"Copying C:\Users\jane\x.mp4 to D:\Backup\jane\x.mp4 failed")]
    [InlineData(@"Cannot create directory C:\Users\Smith, John")]
    [InlineData(@"Cannot create directory C:\Users\Mr. Smith")]
    [InlineData(@"Folder C:\Users\J. R. R. Tolkien\Books; retry")]
    [InlineData(@"Wrote C:\Users\jane\Videos, then stopped")]
    [InlineData(@"1C:\Users\jane\x.mp4")]
    [InlineData(@"Escaped text \nC:\Users\jane\x.mp4")]
    [InlineData(@"JSON text \nC:\\Users\\jane\\x.mp4")]
    [InlineData(@"\\?\UNC\server\share\jane\x.mp4 failed: offline")]
    [InlineData(@"\\?\C:\Users\jane\x.mp4")]
    [InlineData(@"\\.\C:\Users\jane\x.mp4")]
    [InlineData(@"Tried C:\a (see C:\Users\jane)")]
    [InlineData(@"Search C:\Users\jane,D:\Users\bob")]
    [InlineData(@"Search C:\Users\jane, \\nas\bob\x")]
    [InlineData(@"Search C:\Users\jane; /home/bob/x")]
    [InlineData("C:\\Users\\jane\\x.mp4\tD:\\Users\\bob")]
    [InlineData(@"Moving C:\Users\jane\x.mp4 to ""D:\Users\bob\y.mp4"" failed")]
    [InlineData("/home/jane/a:/home/bob/b")]
    [InlineData(@"Saved C:\Users\Jürgen Müller\x.mp4, retrying")]
    // .NET quotes paths in single quotes, and folder and file names often hold an apostrophe
    // followed by a space ("Kids' Videos", "Guns N' Roses", "D' Angelo").
    [InlineData(@"Could not find file 'C:\Users\jane\Kids' Videos\Emma birthday.mp4'.")]
    [InlineData(@"The process cannot access the file 'C:\Users\jane\Parents' house\x.mp4' because it is being used by another process.")]
    [InlineData(@"Could not find file 'C:\Users\D' Angelo\x.exe'.")]
    [InlineData(@"Could not find file 'C:\Users\jane\Sam' wedding, final.mp4'.")]
    [InlineData(@"Could not find a part of the path 'C:\Users\jane\Guns N' Roses'.")]
    [InlineData("'/home/jane/Kids' Videos/Emma.mp4' denied")]
    [InlineData(@"Could not find file 'C:\Users\jane\Kids' Mom's Videos\Emma.mp4'.")]
    // Unix-style and link-style roots that are not at the start of a word.
    [InlineData("/mnt/c/Users/jane/x.mp4: denied")]
    [InlineData("/var/home/jane/x.mp4")]
    [InlineData("file://server/share/jane/x.mp4 missing")]
    [InlineData("file:////server/share/jane/x.mp4 missing")]
    [InlineData("file:///home/jane/x.mp4 missing")]
    [InlineData("ftp://example.com/a;/home/jane/x")]
    // A path glued to a URL, which Redact rewrites with forward slashes.
    [InlineData(@"Fetch https://example.com/a',C:\Users\jane\x.mp4 failed")]
    [InlineData(@"Fetch https://example.com/a',C:\Users\jane\my bob.mp4 failed")]
    [InlineData(@"Fetch https://example.com/a,\\nas\jane\x.mp4 failed")]
    [InlineData("Fetch https://example.com/a;/home/jane/x failed")]
    [InlineData(@"Fetch https://example.com/a'C:\Users\jane\x.mp4' failed")]
    public void RedactDiagnosticsNeverExposesPartOfALocalPath(string input)
    {
        var redacted = MediaValidation.RedactDiagnostics(input);
        foreach (var name in new[] { "jane", "bob", "John", "Smith", "Tolkien", "Jürgen", "Müller", "Kids", "Mom", "Emma", "birthday", "house", "Angelo", "wedding", "Roses", "server" }) Assert.DoesNotContain(name, redacted);
        Assert.Contains("[local path]", redacted);
    }

    [Fact]
    public void RedactDiagnosticsStaysFastOnAdversarialInput()
    {
        string[] pieces = ["C:\\a.b, ", "x.mp4, zz ", "C:\\x ", "a:/", "ab. ", "'C:\\a' ", "\\\\", "; ", "https://e.com/x "];
        var random = new Random(7);
        var builder = new StringBuilder();
        while (builder.Length < 8192) builder.Append(pieces[random.Next(pieces.Length)]);
        var text = builder.ToString(0, 8192);
        MediaValidation.RedactDiagnostics(text);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var run = 0; run < 5; run++) MediaValidation.RedactDiagnostics(text);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"Took {watch.ElapsedMilliseconds} ms");
    }

    // One long token without spaces makes every look-back scan the whole token: a link with
    // many /home/ segments, many inner apostrophes, and paths glued to a URL.
    [Theory]
    [InlineData("ftp://h/", "a/home/")]
    [InlineData("'C:\\a", "' b")]
    [InlineData("'C:\\a", "' b/")]
    [InlineData("'C:\\a", "' b's")]
    [InlineData("'C:\\a", "'s b")]
    [InlineData("https://e.com/", "',x")]
    [InlineData("https://e.com/a,C:/", "x, y")]
    public void RedactDiagnosticsStaysFastOnOneLongToken(string start, string piece)
    {
        var text = start + string.Concat(Enumerable.Repeat(piece, 8192 / piece.Length));
        MediaValidation.RedactDiagnostics(text);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var run = 0; run < 5; run++) MediaValidation.RedactDiagnostics(text);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"Took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void SafeSourceUrlSanitisesTheFacebookPluginLink()
    {
        var safe = MediaValidation.SafeSourceUrl(new("https://www.facebook.com/plugins/video.php?href=https%3A%2F%2Fwww.facebook.com%2Fx%2Fvideos%2F1%2F%3Ftoken%3DSECRET%26fbclid%3Dxyz%26__cft__%5B0%5D%3Dabc&width=500&show_text=false"));
        Assert.DoesNotContain("SECRET", safe);
        Assert.DoesNotContain("fbclid", safe);
        Assert.DoesNotContain("cft", safe);
        Assert.DoesNotContain("width", safe);
        Assert.Equal("https://www.facebook.com/plugins/video.php?href=https%3A%2F%2Fwww.facebook.com%2Fx%2Fvideos%2F1%2F", safe);
        Assert.NotEqual(safe, MediaValidation.SafeSourceUrl(new("https://www.facebook.com/plugins/video.php?href=https%3A%2F%2Fwww.facebook.com%2Fx%2Fvideos%2F2%2F")));
    }

    [Theory]
    [InlineData("https://www.facebook.com/plugins/video.php?href=https%3A%2F%2Fevil.example%2F%3Ftoken%3DSECRET")]
    [InlineData("https://www.facebook.com/plugins/video.php?href=not-a-link-SECRET")]
    [InlineData("https://www.facebook.com/plugins/video.php?href=https%3A%2F%2Fwww.facebook.com%2Fplugins%2Fvideo.php%3Fhref%3Dhttps%253A%252F%252Fx.example%252F%253FSECRET")]
    public void SafeSourceUrlDropsAnUnsafeFacebookPluginLink(string url)
    {
        var safe = MediaValidation.SafeSourceUrl(new(url));
        Assert.DoesNotContain("SECRET", safe);
        Assert.DoesNotContain("evil", safe);
        Assert.False(MediaValidation.SafeSourceUrlIsExact(new(url)));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42s&list=PL1&index=3&si=x")]
    [InlineData("https://www.bilibili.com/video/BV1bK411W797?p=2&vd_source=abc")]
    [InlineData("https://www.facebook.com/plugins/video.php?href=https%3A%2F%2Fwww.facebook.com%2Fx%2Fvideos%2F1%2F%3Ftoken%3DS&width=5")]
    [InlineData("https://player.vimeo.com/video/123456?h=abc123&autoplay=1#t=30")]
    [InlineData("https://manifest.googlevideo.com/api/manifest/hls_playlist/expire/1/ei/E/ip/203.0.113.5/sig/S/lsig/L/index.m3u8")]
    [InlineData("https://cdn.example.akamaized.net/hdnts=exp=1~hmac=0123abcdef/master.m3u8?token=x")]
    [InlineData("https://example.com/watch?v=caf%C3%A9&id=a%2Fb&p=12")]
    [InlineData("https://user:pw@example.com/clip?v=1&utm_source=x#secret")]
    public void SafeSourceUrlIsStableWhenAppliedAgain(string url)
    {
        var once = MediaValidation.SafeSourceUrl(new(url));
        Assert.Equal(once, MediaValidation.SafeSourceUrl(new(once)));
        Assert.Equal(once, MediaValidation.SafeStoredSourceUrl(once));
    }

    // Links stored by 0.1.0 kept t, start, index and a YouTube list next to v. Normalising the
    // stored side keeps the duplicate check working for that history.
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PL1&index=3", "https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PL1&index=3&pp=x")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42s", "https://www.youtube.com/watch?t=1&v=dQw4w9WgXcQ")]
    [InlineData("https://example.com/media?id=5&start=10", "https://example.com/media?id=5&utm_source=x")]
    public void SafeStoredSourceUrlMatchesLegacyHistoryLinks(string legacyStored, string pasted)
    {
        var key = MediaValidation.SafeSourceUrl(new(pasted));
        Assert.NotEqual(legacyStored, key);
        Assert.Equal(key, MediaValidation.SafeStoredSourceUrl(legacyStored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("C:\\Videos\\x.mp4")]
    [InlineData("ftp://example.com/x")]
    public void SafeStoredSourceUrlIgnoresLocalOrMissingLinks(string? stored) => Assert.Null(MediaValidation.SafeStoredSourceUrl(stored));

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42s&list=PL1&index=3&si=x&pp=y&feature=share", true)]
    [InlineData("https://www.bilibili.com/video/BV1bK411W797?p=2&vd_source=abc&spm_id_from=333", true)]
    [InlineData("https://example.com/media?id=5&utm_source=x&utm_campaign=y&fbclid=z", true)]
    [InlineData("https://player.vimeo.com/video/123456?h=abc123&autoplay=1#t=1m30s", true)]
    [InlineData("https://clips.twitch.tv/embed?clip=FunnySlug1&parent=example.com", true)]
    [InlineData("https://example.com/files/clip.mp4", true)]
    [InlineData("https://www.facebook.com/plugins/video.php?href=https%3A%2F%2Fwww.facebook.com%2Fx%2Fvideos%2F1%2F%3Ffbclid%3Dz", true)]
    [InlineData("https://www.facebook.com/plugins/video.php?href=https%3A%2F%2Fwww.facebook.com%2Fx%2Fvideos%2F1%2F%3Ftoken%3DS", false)]
    [InlineData("https://example.com/download.php?file=a.mp4", false)]
    [InlineData("https://example.com/files/clip.mp4?token=one-time", false)]
    [InlineData("https://media.example.com/watch?p=hunter2&id=7", false)]
    [InlineData("https://example.com/app#/video/123", false)]
    [InlineData("https://cdn.example.com/hls/token=SESSION/playlist.m3u8", false)]
    [InlineData("https://example.com/watch?v=1&h=abc", false)]
    public void SafeSourceUrlIsExactOnlyWhenNothingThatMayChooseTheMediaWasDropped(string url, bool exact)
        => Assert.Equal(exact, MediaValidation.SafeSourceUrlIsExact(new(url)));

    [Fact]
    public void DifferentDirectFilesSharingASafeLinkAreNotExact()
    {
        var first = new Uri("https://example.com/download.php?file=a.mp4");
        var second = new Uri("https://example.com/download.php?file=b.mp4");
        Assert.Equal(MediaValidation.SafeSourceUrl(first), MediaValidation.SafeSourceUrl(second));
        Assert.False(MediaValidation.SafeSourceUrlIsExact(first));
        Assert.False(MediaValidation.SafeSourceUrlIsExact(second));
    }
}

public sealed class MediaLinkTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=kea", true)]
    [InlineData("https://youtu.be/kea", true)]
    [InlineData("https://m.youtube.com/watch?v=kea", true)]
    [InlineData("https://clips.twitch.tv/SomeClip", true)]
    [InlineData("https://www.tiktok.com/@kea/video/1", true)]
    [InlineData("https://commons.wikimedia.org/wiki/File:Kea.webm", true)]
    [InlineData("https://example.com/files/clip.MP4", true)]
    [InlineData("https://cdn.example.com/audio/song.flac?token=1", true)]
    [InlineData("https://ko-fi.com/keaclip#setCreatorStatusModal", false)]
    [InlineData("https://github.com/Langkurst/Klippi", false)]
    [InlineData("https://docs.google.com/document/d/1", false)]
    [InlineData("https://notyoutube.com/watch?v=kea", false)]
    [InlineData("https://youtube.com.evil.example/watch?v=kea", false)]
    [InlineData("https://example.com/watch.mp4.html", false)]
    public void Only_likely_media_links_count(string link, bool expected) => Assert.Equal(expected, MediaValidation.LooksLikeMediaLink(new Uri(link)));
}
