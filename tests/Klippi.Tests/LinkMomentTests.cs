using Klippi.Core;
using Xunit;

namespace Klippi.Tests;

public sealed class LinkMomentTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=kea&t=90", 90)]
    [InlineData("https://youtu.be/kea?t=90s", 90)]
    [InlineData("https://www.youtube.com/watch?v=kea&t=1m30s", 90)]
    [InlineData("https://www.twitch.tv/videos/123?t=1h2m3s", 3723)]
    [InlineData("https://www.youtube.com/embed/kea?start=45", 45)]
    [InlineData("https://vimeo.com/123#t=1m5s", 65)]
    [InlineData("https://example.com/clip.mp4#t=1:30", 90)]
    [InlineData("https://example.com/v?t=1:02:03", 3723)]
    [InlineData("https://www.youtube.com/watch?v=kea&time_continue=12", 12)]
    public void Reads_the_moment_a_link_points_to(string link, int seconds)
    {
        Assert.True(LinkMoment.TryRead(link, out var at));
        Assert.Equal(TimeSpan.FromSeconds(seconds), at);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=kea")]          // no moment
    [InlineData("https://www.youtube.com/watch?v=kea&t=0")]      // the start is not a moment
    [InlineData("https://www.youtube.com/watch?v=kea&t=abc")]
    [InlineData("https://www.youtube.com/watch?v=kea&t=1:75")]   // not a clock time
    [InlineData("https://www.youtube.com/watch?v=kea&tt=90")]    // another parameter
    [InlineData("https://www.youtube.com/watch?v=kea&t=200h")]   // a day or more is not a moment in a clip
    [InlineData("ftp://example.com/v?t=90")]
    [InlineData("not a link")]
    [InlineData(null)]
    public void Ignores_links_without_a_usable_moment(string? link) => Assert.False(LinkMoment.TryRead(link, out _));
}
