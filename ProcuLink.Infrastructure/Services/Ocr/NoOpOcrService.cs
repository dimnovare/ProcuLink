using ProcuLink.Core.Services.Ocr;

namespace ProcuLink.Infrastructure.Services.Ocr;

/// <summary>
/// Default no-op implementation of <see cref="IDocumentOcrService"/>. PDF parsing's
/// primary path is text → LLM structured extraction; this seam is reserved for the
/// planned self-hosted, no-egress OCR engine (for scanned/image-only PDFs) and stays
/// a safe no-op until one is wired.
///
/// Registered by the DI container so that <c>PdfOrderParser</c> can always resolve an
/// <see cref="IDocumentOcrService"/> without nullability guards.
/// </summary>
public sealed class NoOpOcrService : IDocumentOcrService
{
    /// <inheritdoc/>
    public bool IsAvailable => false;

    /// <inheritdoc/>
    public Task<string> ExtractTextAsync(
        Stream document,
        string contentType,
        CancellationToken ct)
        => Task.FromResult(string.Empty);
}
