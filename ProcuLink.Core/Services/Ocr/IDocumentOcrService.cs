namespace ProcuLink.Core.Services.Ocr;

/// <summary>
/// Provider-neutral contract for extracting text from documents (e.g. scanned PDFs, images).
/// Used as a fallback by <c>PdfOrderParser</c> when PdfPig cannot extract any text from a
/// document (i.e. the PDF is image-only or the text layer is corrupt).
/// </summary>
public interface IDocumentOcrService
{
    /// <summary>
    /// Extracts text content from the supplied document stream.
    /// </summary>
    /// <param name="document">Readable stream containing the document bytes.</param>
    /// <param name="contentType">MIME type of the document (e.g. <c>application/pdf</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The extracted text, or <see cref="string.Empty"/> if extraction fails or the
    /// service is unavailable. This method never throws — failures are logged and
    /// swallowed so the caller can degrade gracefully.
    /// </returns>
    Task<string> ExtractTextAsync(Stream document, string contentType, CancellationToken ct);

    /// <summary>
    /// Returns <c>true</c> when the underlying provider is configured and ready to accept
    /// calls. When <c>false</c>, <see cref="ExtractTextAsync"/> will immediately return an
    /// empty string without making any network calls.
    /// </summary>
    bool IsAvailable { get; }
}
