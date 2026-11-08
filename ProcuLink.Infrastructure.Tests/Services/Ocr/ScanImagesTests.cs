using FluentAssertions;
using ProcuLink.Infrastructure.Services.Ocr;
using SkiaSharp;

namespace ProcuLink.Infrastructure.Tests.Services.Ocr;

public class ScanImagesTests
{
    private static byte[] Encode(int w, int h, SKEncodedImageFormat fmt)
    {
        using var bmp = new SKBitmap(w, h);
        bmp.Erase(SKColors.Gray);
        using var data = SKImage.FromBitmap(bmp).Encode(fmt, 90);
        return data.ToArray();
    }

    private static (int W, int H) Size(byte[] png)
    {
        using var b = SKBitmap.Decode(png);
        return (b.Width, b.Height);
    }

    [Fact]
    public void Upright_applies_exif_rotation_and_caps_the_long_side()
    {
        var jpeg = Encode(40, 20, SKEncodedImageFormat.Jpeg);
        // APP1 Exif segment, big-endian TIFF, one IFD entry: Orientation (0x0112) = 6.
        byte[] app1 =
        [
            0xFF, 0xE1, 0x00, 0x22, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'M', (byte)'M', 0, 0x2A, 0, 0, 0, 8,
            0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, 6, 0, 0,
            0, 0, 0, 0,
        ];
        var tagged = jpeg.Take(2).Concat(app1).Concat(jpeg.Skip(2)).ToArray();

        Size(ScanImages.Upright(tagged)!).Should().Be((20, 40));
        Size(ScanImages.Upright(Encode(4000, 1000, SKEncodedImageFormat.Png))!).Should().Be((2048, 512));
        ScanImages.Upright([1, 2, 3, 4, 5, 6, 7, 8]).Should().BeNull();
    }
}
