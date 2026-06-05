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
    // Serialize OCR — RapidOcr.Detect on one instance isn't documented as concurrency-safe.
    // A SemaphoreSlim (not lock) so a waiting Hangfire worker RELEASES its thread while it
    // waits (the worker pool is WorkerCount=10): only the one running OCR pins a thread, so
    // a burst of OCR jobs can't starve unrelated parse/delivery work.
    private readonly SemaphoreSlim _gate = new(1, 1);
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

        var acquired = false;
        try
        {
            await _gate.WaitAsync(ct);
            acquired = true;

            var sb = new StringBuilder();
            _ocr ??= InitOcr(); // one-time model load (lazy)
            foreach (var png in pages)
            {
                ct.ThrowIfCancellationRequested();
                using var bitmap = SKBitmap.Decode(png);
                if (bitmap is null) continue;
                var result = _ocr.Detect(bitmap, RapidOcrOptions.Default);
                if (!string.IsNullOrWhiteSpace(result?.StrRes))
                    sb.AppendLine(result.StrRes);
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Self-hosted OCR extraction failed.");
            return string.Empty;
        }
        finally
        {
            if (acquired) _gate.Release();
        }
    }

    /// <summary>
    /// Eagerly loads the OCR models so the FIRST scanned-PDF OCR after a deploy doesn't
    /// pay the multi-second <c>InitModels()</c> cost inside a Hangfire job. No-op when the
    /// engine is disabled. Idempotent and concurrency-safe: it shares the same
    /// <see cref="_gate"/> + <c>_ocr ??=</c> lazy-init guard as <see cref="ExtractTextAsync"/>,
    /// so a job that arrives mid-warm reuses the warmed instance and never double-loads.
    /// Never throws — a warm-up failure degrades to the existing lazy-on-first-use path.
    /// </summary>
    public async Task WarmAsync(CancellationToken ct = default)
    {
        if (!_enabled) return;

        var acquired = false;
        try
        {
            await _gate.WaitAsync(ct);
            acquired = true;
            _ocr ??= InitOcr(); // one-time model load (shares the lazy-init guard)
        }
        catch (OperationCanceledException)
        {
            // Host shutting down before warm-up finished — fine, lazy path still works.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Self-hosted OCR warm-up failed (non-fatal); will lazy-load on first use.");
        }
        finally
        {
            if (acquired) _gate.Release();
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
        (_ocr as IDisposable)?.Dispose();
        _ocr = null;
        _gate.Dispose();
    }
}
