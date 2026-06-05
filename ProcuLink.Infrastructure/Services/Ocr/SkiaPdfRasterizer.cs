using Microsoft.Extensions.Logging;
using PDFtoImage;
using ProcuLink.Core.Services.Ocr;
using SkiaSharp;

namespace ProcuLink.Infrastructure.Services.Ocr;

/// <summary>
/// <see cref="IPdfRasterizer"/> backed by PDFtoImage (PDFium) + SkiaSharp — both
/// MIT/BSD permissive, with self-contained native assets that load on the Debian
/// aspnet:8.0 base image with no extra system packages.
///
/// Used only for the vision fallback (scanned / image-only PDFs). Never throws —
/// returns whatever pages it managed to render (possibly none).
/// </summary>
public sealed class SkiaPdfRasterizer : IPdfRasterizer
{
    private const int RenderDpi = 200;
    private readonly ILogger<SkiaPdfRasterizer> _logger;

    public SkiaPdfRasterizer(ILogger<SkiaPdfRasterizer> logger) => _logger = logger;

    public IReadOnlyList<byte[]> RenderPagesPng(byte[] pdfBytes, int maxPages)
    {
        var pages = new List<byte[]>();
        if (pdfBytes is null || pdfBytes.Length == 0 || maxPages <= 0)
            return pages;

        try
        {
            // PDFium isn't thread-safe; PDFtoImage serializes internally.
            var pageCount = Conversion.GetPageCount(pdfBytes);
            var take = Math.Min(pageCount, maxPages);

            for (var i = 0; i < take; i++)
            {
                using var bitmap = Conversion.ToImage(
                    pdfBytes, page: i, password: null, options: new RenderOptions(Dpi: RenderDpi));
                using var data = bitmap.Encode(SKEncodedImageFormat.Png, 90);
                if (data is not null && data.Size > 0)
                    pages.Add(data.ToArray());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PDF rasterization failed after {Rendered} page(s).", pages.Count);
        }

        return pages;
    }
}
