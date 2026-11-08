using SkiaSharp;

namespace ProcuLink.Infrastructure.Services.Ocr;

/// <summary>Normalises an uploaded photo: EXIF-upright, long side capped, PNG.</summary>
public static class ScanImages
{
    private const int MaxSide = 2048;

    public static byte[]? Upright(byte[] image)
    {
        try
        {
            using var codec = SKCodec.Create(new MemoryStream(image));
            if (codec is null) return null;
            using var raw = SKBitmap.Decode(codec);
            if (raw is null) return null;

            using var upright = Rotate(raw, codec.EncodedOrigin);
            var scale = Math.Min(1f, (float)MaxSide / Math.Max(upright.Width, upright.Height));
            using var sized = scale < 1f
                ? upright.Resize(new SKImageInfo(
                    Math.Max(1, (int)Math.Round(upright.Width * scale)),
                    Math.Max(1, (int)Math.Round(upright.Height * scale))), SKSamplingOptions.Default)
                : null;
            using var data = SKImage.FromBitmap(sized ?? upright).Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static SKBitmap Rotate(SKBitmap src, SKEncodedOrigin origin)
    {
        var swap = origin >= SKEncodedOrigin.LeftTop;
        var w = swap ? src.Height : src.Width;
        var h = swap ? src.Width : src.Height;
        var dst = new SKBitmap(w, h);
        using var canvas = new SKCanvas(dst);
        canvas.Concat(origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, w, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, w, -1, 0, h, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, h, 0, 0, 1),
            _ => SKMatrix.Identity,
        });
        canvas.DrawBitmap(src, 0, 0);
        return dst;
    }
}
