namespace ProcuLink.Core.Services.Ocr;

/// <summary>
/// Rasterizes a PDF's pages to PNG images, used by the vision fallback for
/// scanned / image-only PDFs (those with no extractable text layer). Returns an
/// empty list on any failure — it never throws, so the caller degrades to the
/// deterministic path.
/// </summary>
public interface IPdfRasterizer
{
    /// <summary>
    /// Renders up to <paramref name="maxPages"/> leading pages of the PDF to PNG
    /// byte arrays (one per page). Empty when the PDF can't be rasterized.
    /// </summary>
    IReadOnlyList<byte[]> RenderPagesPng(byte[] pdfBytes, int maxPages);
}
