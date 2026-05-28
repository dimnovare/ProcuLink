using ProcuLink.Core.Services.Ocr;

namespace ProcuLink.Infrastructure.Services.Ocr;

/// <summary>
/// No-op implementation of <see cref="IDocumentOcrService"/> used when
/// <c>Ocr:Azure:Endpoint</c> or <c>Ocr:Azure:ApiKey</c> are not configured.
///
/// Registered by the DI container as the fallback so that <c>PdfOrderParser</c>
/// can always resolve an <see cref="IDocumentOcrService"/> without nullability guards.
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
