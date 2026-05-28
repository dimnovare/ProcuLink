using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Services.Ocr;

namespace ProcuLink.Infrastructure.Services.Ocr;

/// <summary>
/// Azure Document Intelligence–backed implementation of <see cref="IDocumentOcrService"/>.
///
/// Uses the <c>prebuilt-read</c> model which is optimised for raw text extraction from
/// PDFs and images without form-field understanding overhead.
///
/// Opt-in pattern (mirrors <see cref="OpenAiMappingService"/>):
///   • No-op when <c>Ocr:Azure:Endpoint</c> or <c>Ocr:Azure:ApiKey</c> is absent/empty.
///   • Always returns empty string on failure — never throws to callers.
///   • Enforces a 30-second per-call hard timeout.
/// </summary>
public sealed class AzureDocumentIntelligenceOcrService : IDocumentOcrService
{
    private const string ModelId = "prebuilt-read";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    private readonly DocumentIntelligenceClient? _client;
    private readonly ILogger<AzureDocumentIntelligenceOcrService> _logger;

    public AzureDocumentIntelligenceOcrService(
        IConfiguration configuration,
        ILogger<AzureDocumentIntelligenceOcrService> logger)
    {
        _logger = logger;

        var endpoint = configuration["Ocr:Azure:Endpoint"];
        var apiKey   = configuration["Ocr:Azure:ApiKey"];

        if (!string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(apiKey))
        {
            _client = new DocumentIntelligenceClient(
                new Uri(endpoint),
                new AzureKeyCredential(apiKey));
        }
    }

    /// <inheritdoc/>
    public bool IsAvailable => _client is not null;

    /// <inheritdoc/>
    public async Task<string> ExtractTextAsync(
        Stream document,
        string contentType,
        CancellationToken ct)
    {
        if (_client is null)
        {
            _logger.LogDebug(
                "AzureDocumentIntelligenceOcrService: provider not configured — returning empty.");
            return string.Empty;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(CallTimeout);

            // Buffer the stream into a BinaryData so the SDK can send it as a request body.
            using var ms = new MemoryStream();
            await document.CopyToAsync(ms, timeoutCts.Token);
            ms.Position = 0;
            var binaryData = BinaryData.FromStream(ms);

            var operation = await _client.AnalyzeDocumentAsync(
                WaitUntil.Completed,
                ModelId,
                binaryData,
                cancellationToken: timeoutCts.Token);

            var result = operation.Value;

            if (result?.Content is not null)
                return result.Content;

            // Fallback: concatenate paragraph spans when Content is not populated.
            if (result?.Paragraphs is { Count: > 0 } paragraphs)
            {
                return string.Join("\n", paragraphs
                    .Where(p => !string.IsNullOrWhiteSpace(p.Content))
                    .Select(p => p.Content));
            }

            return string.Empty;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "AzureDocumentIntelligenceOcrService: call timed out after {Timeout}s.",
                CallTimeout.TotalSeconds);
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "AzureDocumentIntelligenceOcrService: OCR extraction failed — returning empty string.");
            return string.Empty;
        }
    }
}
