using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Services.Ocr;
using RapidOcrNet;
using SkiaSharp;

namespace ProcuLink.Infrastructure.Services.Ocr;

/// <summary>
/// Self-hosted, no-egress OCR (<see cref="IDocumentOcrService"/>) backed by RapidOcrNet
/// (PP-OCRv5 via ONNX Runtime, Apache-2.0; models bundled). For organisations that
/// forbid sending document data to OpenAI: a scanned/textless PDF is rasterized in
/// process (<see cref="IPdfRasterizer"/>) and OCR'd locally — nothing leaves the host.
///
/// Opt-in via <c>NoEgressOcr:Enabled</c> so the ~12 MB models + ONNX session are only
/// loaded when the feature is on (otherwise <see cref="NoOpOcrService"/> is registered
/// and this type is never constructed). Models load lazily on first use and the engine
/// is reused (singleton). Never throws — returns empty string on any failure.
/// </summary>
public sealed class RapidOcrDocumentOcrService : IDocumentOcrService, IDisposable
{
    private const int MaxOcrPages = 5;

    private readonly bool _enabled;
    private readonly IPdfRasterizer _rasterizer;
    private readonly ILogger<RapidOcrDocumentOcrService> _logger;
    private readonly object _gate = new();
    private RapidOcr? _ocr;

    public RapidOcrDocumentOcrService(
        IConfiguration configuration,
        IPdfRasterizer rasterizer,
        ILogger<RapidOcrDocumentOcrService> logger)
    {
        _enabled = configuration.GetValue<bool>("NoEgressOcr:Enabled");
        _rasterizer = rasterizer;
        _logger = logger;
    }

    public bool IsAvailable => _enabled;

    public async Task<string> ExtractTextAsync(Stream document, string contentType, CancellationToken ct)
    {
        if (!_enabled) return string.Empty;

        byte[] bytes;
        try
        {
            using var ms = new MemoryStream();
            await document.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Self-hosted OCR: could not read document stream.");
            return string.Empty;
        }

        var pages = _rasterizer.RenderPagesPng(bytes, MaxOcrPages);
        if (pages.Count == 0) return string.Empty;

        try
        {
            var sb = new StringBuilder();
            // RapidOcr/ONNX inference is CPU-bound and not documented as concurrency-safe
            // on a single instance — serialize. The worker runs single-concurrency anyway.
            lock (_gate)
            {
                _ocr ??= InitOcr();
                foreach (var png in pages)
                {
                    ct.ThrowIfCancellationRequested();
                    using var bitmap = SKBitmap.Decode(png);
                    if (bitmap is null) continue;
                    var result = _ocr.Detect(bitmap, RapidOcrOptions.Default);
                    if (!string.IsNullOrWhiteSpace(result?.StrRes))
                        sb.AppendLine(result.StrRes);
                }
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Self-hosted OCR extraction failed.");
            return string.Empty;
        }
    }

    private static RapidOcr InitOcr()
    {
        var ocr = new RapidOcr();
        ocr.InitModels();
        return ocr;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            (_ocr as IDisposable)?.Dispose();
            _ocr = null;
        }
    }
}
