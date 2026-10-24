using Klippi.Core;
using Klippi.Infrastructure.Engines;
using Xunit;

namespace Klippi.Tests;

// The crop itself was checked with the real FFmpeg (1280x720 -> 404x720 vertical, 720x720 square; a 720x1280 video
// stays 720x1280 for vertical). These tests pin the filter text and its bounds.
public sealed class ShapeFilterTests
{
    [Fact]
    public void Original_has_no_crop_and_shapes_crop_the_side_with_room_to_spare()
    {
        Assert.Null(MediaService.ShapeFilter(FrameShape.Original, 0.5));
        var vertical = MediaService.ShapeFilter(FrameShape.Vertical, 0.5)!;
        Assert.StartsWith("crop=w='if(gt(iw/ih,9/16),trunc(ih*9/16/2)*2,trunc(iw/2)*2)'", vertical);
        Assert.Contains("x='(iw-ow)*0.5'", vertical);
        Assert.Contains("y='(ih-oh)*0.5'", vertical);
        Assert.Contains("gt(iw/ih,1)", MediaService.ShapeFilter(FrameShape.Square, 0)!);
    }

    [Theory]
    [InlineData(-1, "0")]
    [InlineData(2, "1")]
    [InlineData(double.NaN, "0.5")]
    [InlineData(0.25, "0.25")]
    public void The_position_is_kept_inside_the_picture(double position, string expected) =>
        Assert.Contains($"x='(iw-ow)*{expected}'", MediaService.ShapeFilter(FrameShape.Vertical, position)!);
}
