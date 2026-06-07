using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Jobs;

/// <summary>
/// Hangfire job: delivers one integration event to one subscriber TargetUrl.
/// Signs with HMAC-SHA256 (X-ProcuLink-Signature: sha256=hex).
/// Deactivates subscription after 3 consecutive failures.
/// Idempotent: exits silently if subscription is inactive.
/// </summary>
public class FireIntegrationTriggerJob
{
    private readonly ProcuLinkDbContext                     _db;
    private readonly IHttpClientFactory                     _http;
    private readonly DeliveryEncryptionService              _enc;
    private readonly OutboundRequestGuard                   _guard;
    private readonly ILogger<FireIntegrationTriggerJob>     _logger;

    public FireIntegrationTriggerJob(
        ProcuLinkDbContext                 db,
        IHttpClientFactory                 http,
        DeliveryEncryptionService          enc,
        OutboundRequestGuard               guard,
        ILogger<FireIntegrationTriggerJob> logger)
    {
        _db     = db;
        _http   = http;
        _enc    = enc;
        _guard  = guard;
        _logger = logger;
    }

    // Built lazily once from the guard's connect-time-revalidating handler and reused.
    private HttpClient? _guardedClient;

    /// <summary>
    /// Resolves the <see cref="HttpClient"/> used to fire the webhook. The default swaps in the
    /// guard's connect-time-revalidating <see cref="System.Net.Http.SocketsHttpHandler"/> so a
    /// DNS-rebind to a private/metadata IP after the up-front
    /// <see cref="OutboundRequestGuard.ValidateAsync"/> is still rejected at TCP connect.
    /// Tests override this to inject a fake transport.
    /// </summary>
    internal virtual HttpClient CreateSendClient()
    {
        if (_guardedClient is not null) return _guardedClient;

        var timeout = _http.CreateClient("delivery").Timeout;
        _guardedClient = new HttpClient(_guard.CreateGuardedHttpHandler(), disposeHandler: true)
        {
            Timeout = timeout,
        };
        return _guardedClient;
    }

    [Queue("background")]
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 30, 120, 600 })]
    public async Task ExecuteAsync(Guid subscriptionId, string payloadJson, CancellationToken ct)
    {
        var sub = await _db.IntegrationSubscriptions
                           .Where(s => s.Id == subscriptionId)
                           .FirstOrDefaultAsync(ct);

        if (sub is null || !sub.IsActive)
        {
            _logger.LogInformation(
                "FireIntegrationTriggerJob: sub {SubId} not found or inactive — skipping.",
                subscriptionId);
            return;
        }

        string? sigHeader = null;
        if (!string.IsNullOrEmpty(sub.EncryptedSecret))
        {
            var secret      = _enc.Decrypt(sub.EncryptedSecret);
            if (secret is not null)
            {
                var secretBytes = Encoding.UTF8.GetBytes(secret);
                var dataBytes   = Encoding.UTF8.GetBytes(payloadJson);
                using var hmac  = new HMACSHA256(secretBytes);
                sigHeader = $"sha256={Convert.ToHexString(hmac.ComputeHash(dataBytes)).ToLowerInvariant()}";
            }
        }

        // ── SSRF guard — must pass before any outbound request ────────────────
        var guardResult = await _guard.ValidateAsync(sub.TargetUrl, ct);
        if (!guardResult.Allowed)
        {
            _logger.LogWarning(
                "FireIntegrationTriggerJob: SSRF guard blocked webhook to '{Url}' for sub {SubId}: {Reason}",
                sub.TargetUrl, subscriptionId, guardResult.Reason);
            // Treat as a delivery failure and apply the existing retry/deactivate flow.
            await IncrementFailureAsync(sub, ct);
            throw new InvalidOperationException(
                $"Webhook delivery blocked: {guardResult.Reason}");
        }

        try
        {
            var client = CreateSendClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, sub.TargetUrl)
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json"),
            };
            if (sigHeader is not null)
                request.Headers.TryAddWithoutValidation("X-ProcuLink-Signature", sigHeader);
            request.Headers.TryAddWithoutValidation("X-ProcuLink-Event", sub.EventType);

            var response = await client.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                sub.FailureCount = 0;
                sub.UpdatedAt    = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                _logger.LogInformation(
                    "FireIntegrationTriggerJob delivered to {Url}, status={Status}",
                    sub.TargetUrl, response.StatusCode);
            }
            else
            {
                await IncrementFailureAsync(sub, ct);
                throw new InvalidOperationException(
                    $"Delivery failed: HTTP {(int)response.StatusCode} from {sub.TargetUrl}");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            await IncrementFailureAsync(sub, ct);
            throw;
        }
    }

    private async Task IncrementFailureAsync(IntegrationSubscription sub, CancellationToken ct)
    {
        sub.FailureCount++;
        sub.UpdatedAt = DateTime.UtcNow;
        if (sub.FailureCount >= 3)
        {
            sub.IsActive = false;
            _logger.LogWarning(
                "FireIntegrationTriggerJob: deactivated sub {SubId} after {Count} failures.",
                sub.Id, sub.FailureCount);
        }
        await _db.SaveChangesAsync(ct);
    }

    public static void Enqueue(IBackgroundJobClient jobs, Guid subscriptionId, string payloadJson)
    {
        jobs.Enqueue<FireIntegrationTriggerJob>(
            j => j.ExecuteAsync(subscriptionId, payloadJson, CancellationToken.None));
    }
}
