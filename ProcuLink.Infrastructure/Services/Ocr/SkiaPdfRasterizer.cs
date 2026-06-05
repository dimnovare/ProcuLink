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
    // Bound each rasterized page to fit a MaxRenderSide x MaxRenderSide box (aspect
    // preserved). PDF page dimensions are independent of file byte size — a tiny PDF
    // can declare a 200-inch MediaBox, which at a fixed DPI would rasterize to a
    // multi-GB bitmap and OOM the (single) worker. Sizing to a fixed pixel box caps
    // each bitmap to ~MaxRenderSide^2 x 4 bytes regardless of the declared page size,
    // while staying high-res enough for vision/OCR of a page.
    private const int MaxRenderSide = 2500;
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

            // Width + Height + WithAspectRatio → scale to fit the box, never exceeding it.
            var options = new RenderOptions(Width: MaxRenderSide, Height: MaxRenderSide, WithAspectRatio: true);

            for (var i = 0; i < take; i++)
            {
                using var bitmap = Conversion.ToImage(pdfBytes, page: i, password: null, options: options);
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
