using Avalonia.Headless.XUnit;
using Klippi.Desktop;
using SkiaSharp;
using Xunit;

namespace Klippi.UiTests;

public sealed class ThumbnailDecodeTests
{
    private static byte[] Jpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height); using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.OliveDrab);
        using var image = SKImage.FromBitmap(bitmap); return image.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
    }

    [AvaloniaFact]
    public void Downloaded_thumbnail_bytes_decode_to_a_bounded_bitmap()
    {
        // A YouTube-sized 1280x720 thumbnail decodes to at most 640 px wide, keeping its aspect:
        // sharp in the 296 DIP preview frame at 200% scaling (592 px).
        using var bitmap = MainViewModel.DecodeThumbnail(Jpeg(1280, 720));
        Assert.NotNull(bitmap); Assert.Equal(640, bitmap!.PixelSize.Width); Assert.InRange(bitmap.PixelSize.Height, 359, 361);
        Assert.Equal(640, MainViewModel.ThumbnailDecodeMaxWidth);
    }

    [AvaloniaFact]
    public void Small_thumbnails_are_never_upscaled_while_decoding()
    {
        using var bitmap = MainViewModel.DecodeThumbnail(Jpeg(320, 180));
        Assert.NotNull(bitmap); Assert.Equal(320, bitmap!.PixelSize.Width); Assert.Equal(180, bitmap.PixelSize.Height);
    }

    [AvaloniaFact]
    public void Undecodable_or_oversized_images_are_rejected_without_throwing()
    {
        Assert.Null(MainViewModel.DecodeThumbnail([1, 2, 3, 4]));
        Assert.Null(MainViewModel.DecodeThumbnail(Jpeg(8200, 100)));
    }
}
