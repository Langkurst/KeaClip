using Klippi.Infrastructure.Engines;
using Xunit;

namespace Klippi.Tests;

// Archive metadata (source.json, .info.json) must drop path signatures and the viewer's IP,
// as stored history links already do (final review #12).
public sealed class MetadataScrubTests
{
    [Fact]
    public void Page_links_in_archive_metadata_lose_signatures_and_the_viewers_ip()
    {
        const string json = """{"id":"x","title":"T","webpage_url":"https://r1.googlevideo.com/videoplayback/ip/203.0.113.5/expire/1/sig/SIGVALUE/file.mp4","original_url":"https://r1.googlevideo.com/videoplayback/ip/203.0.113.5/sig/SIGVALUE/"}""";
        var safe = EngineSupport.SafeMetadata(json);
        Assert.DoesNotContain("203.0.113.5", safe); Assert.DoesNotContain("SIGVALUE", safe);
        Assert.Contains("googlevideo.com", safe);
    }
}
