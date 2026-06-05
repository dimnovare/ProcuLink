using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Services.Ocr;
using ProcuLink.Infrastructure.Services.Ocr;

namespace ProcuLink.Worker;

/// <summary>
/// Warms the self-hosted OCR models at Worker startup so the FIRST scanned-PDF OCR
/// after a deploy doesn't pay the multi-second model load inside a Hangfire job.
///
/// No-op unless <c>NoEgressOcr:Enabled</c> (the same flag that registers the real
/// <see cref="RapidOcrDocumentOcrService"/>; otherwise <c>IDocumentOcrService</c> is the
/// no-op and the cast below short-circuits). Runs entirely on a background thread so it
/// NEVER blocks host start, and never throws. Worker-only by design — the API hosts no
/// Hangfire server and never executes OCR.
/// </summary>
public sealed class OcrWarmupHostedService : IHostedService, IDisposable
{
    private readonly IDocumentOcrService _ocr;
    private readonly IConfiguration _config;
    private readonly ILogger<OcrWarmupHostedService> _logger;
    private readonly CancellationTokenSource _cts = new();

    public OcrWarmupHostedService(
        IDocumentOcrService ocr,
        IConfiguration config,
        ILogger<OcrWarmupHostedService> logger)
    {
        _ocr = ocr;
        _config = config;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.GetValue<bool>("NoEgressOcr:Enabled"))
            return Task.CompletedTask;                 // engine disabled — nothing to warm
        if (_ocr is not RapidOcrDocumentOcrService rapid)
            return Task.CompletedTask;                 // NoOpOcrService — nothing to warm

        // Fire-and-forget on a background thread so host start is NEVER blocked by the
        // multi-second InitModels(). WarmAsync never throws; the catch is belt-and-suspenders.
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("Warming self-hosted OCR models at startup…");
                var sw = Stopwatch.StartNew();
                await rapid.WarmAsync(_cts.Token);
                _logger.LogInformation("Self-hosted OCR warm complete in {Ms} ms.", sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Self-hosted OCR warm-up failed (non-fatal).");
            }
        }, _cts.Token);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose() => _cts.Dispose();
}
