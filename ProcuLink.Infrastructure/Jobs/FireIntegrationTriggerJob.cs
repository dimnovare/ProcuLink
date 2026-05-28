using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure.Services;

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
    private readonly ILogger<FireIntegrationTriggerJob>     _logger;

    public FireIntegrationTriggerJob(
        ProcuLinkDbContext                 db,
        IHttpClientFactory                 http,
        DeliveryEncryptionService          enc,
        ILogger<FireIntegrationTriggerJob> logger)
    {
        _db     = db;
        _http   = http;
        _enc    = enc;
        _logger = logger;
    }

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

        try
        {
            var client = _http.CreateClient("delivery");
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
