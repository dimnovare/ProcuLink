using System.Net.Http.Headers;
using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Jobs;

/// <summary>
/// Hangfire background job: delivers an outbound artifact to the supplier's configured
/// webhook endpoint.  On 2xx → order status "delivered".  On 4xx → "delivery_failed"
/// (no retry — client error).  On 5xx/timeout → Hangfire retries (3 attempts).
/// </summary>
public class DeliverOrderJob
{
    private readonly ProcuLinkDbContext       _db;
    private readonly IFileStorageService      _fileStorage;
    private readonly IHttpClientFactory       _httpClientFactory;
    private readonly ILogger<DeliverOrderJob> _logger;

    public DeliverOrderJob(
        ProcuLinkDbContext       db,
        IFileStorageService      fileStorage,
        IHttpClientFactory       httpClientFactory,
        ILogger<DeliverOrderJob> logger)
    {
        _db                = db;
        _fileStorage       = fileStorage;
        _httpClientFactory = httpClientFactory;
        _logger            = logger;
    }

    /// <summary>Entry point called by Hangfire after a successful transform.</summary>
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 30, 120, 600 })]
    public async Task ExecuteAsync(
        Guid orderId,
        Guid organisationId,
        Guid artifactId,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "DeliverOrderJob starting for order {OrderId}, artifact {ArtifactId}",
            orderId, artifactId);

        // ── Load artifact and supplier profile ────────────────────────────────
        var artifact = await _db.OutboundArtifacts
            .Where(a => a.Id == artifactId && a.OrderId == orderId && a.OrgId == organisationId)
            .FirstOrDefaultAsync(ct);

        if (artifact is null)
        {
            _logger.LogError(
                "DeliverOrderJob: artifact {ArtifactId} not found for order {OrderId}",
                artifactId, orderId);
            return; // nothing to retry
        }

        var order = await _db.PurchaseOrders
            .FirstOrDefaultAsync(o => o.Id == orderId && o.OrgId == organisationId, ct);

        if (order is null) return;

        var profile = await _db.SupplierProfiles
            .FirstOrDefaultAsync(p => p.SupplierId == order.SupplierId && p.OrgId == organisationId, ct);

        // Only deliver via webhook — download delivery is handled by the signed-URL endpoint
        if (profile is null || profile.DestinationType != "webhook" || profile.DestinationConfig is null)
        {
            // No webhook configured — mark as delivered (user downloads manually)
            order.Status    = "delivered";
            order.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "DeliverOrderJob: no webhook configured for order {OrderId}, marked delivered",
                orderId);
            return;
        }

        // ── Parse webhook config ───────────────────────────────────────────────
        string? webhookUrl = null;
        try
        {
            var config = profile.DestinationConfig.RootElement;
            webhookUrl = config.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Invalid DestinationConfig for supplier profile");
        }

        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            _logger.LogWarning(
                "DeliverOrderJob: supplier profile has webhook type but no url, order {OrderId}",
                orderId);
            order.Status    = "delivered"; // treat as manual download
            order.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return;
        }

        // ── Download artifact and POST to webhook ─────────────────────────────
        var now = DateTime.UtcNow;
        int? responseCode = null;
        string? errorMessage = null;

        try
        {
            await using var stream = await _fileStorage.DownloadAsync(artifact.FileKey, ct);
            using var content = new StreamContent(stream);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(
                artifact.Format == "xml" ? "application/xml" : "text/csv");

            var client   = _httpClientFactory.CreateClient("delivery");
            var response = await client.PostAsync(webhookUrl, content, ct);

            responseCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                order.Status    = "delivered";
                order.UpdatedAt = now;

                _db.AuditEvents.Add(BuildAuditEvent(organisationId, orderId, "Delivered", new
                {
                    webhookUrl, artifactId, responseCode
                }));

                _logger.LogInformation(
                    "DeliverOrderJob: delivered order {OrderId} to {Url}, HTTP {Code}",
                    orderId, webhookUrl, responseCode);
            }
            else if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
            {
                // Client error — no retry
                order.Status    = "delivery_failed";
                order.UpdatedAt = now;
                errorMessage    = $"HTTP {responseCode} — client error, no retry";

                _db.AuditEvents.Add(BuildAuditEvent(organisationId, orderId, "DeliveryFailed", new
                {
                    webhookUrl, responseCode, reason = "client_error"
                }));

                _logger.LogError(
                    "DeliverOrderJob: delivery failed (client error) for order {OrderId}, HTTP {Code}",
                    orderId, responseCode);
            }
            else
            {
                // Server error — Hangfire will retry
                order.Status    = "delivery_failed";
                order.UpdatedAt = now;
                errorMessage    = $"HTTP {responseCode} — server error, retrying";

                throw new HttpRequestException(
                    $"Webhook returned HTTP {responseCode} for order {orderId}.");
            }
        }
        catch (HttpRequestException) { throw; } // let Hangfire retry
        catch (Exception ex)
        {
            order.Status    = "delivery_failed";
            order.UpdatedAt = now;
            errorMessage    = ex.Message;

            _db.AuditEvents.Add(BuildAuditEvent(organisationId, orderId, "DeliveryFailed", new
            {
                webhookUrl, responseCode, error = ex.Message
            }));

            _logger.LogError(ex, "DeliverOrderJob exception for order {OrderId}", orderId);
            throw; // let Hangfire retry
        }
        finally
        {
            // Persist delivery attempt regardless of outcome
            _db.DeliveryAttempts.Add(new Core.Entities.DeliveryAttempt
            {
                Id           = Guid.NewGuid(),
                OrderId      = orderId,
                OrgId        = organisationId,
                Channel      = "webhook",
                Destination  = webhookUrl ?? "unknown",
                Status       = order.Status,
                AttemptedAt  = now,
                ResponseCode = responseCode,
                ErrorMessage = errorMessage
            });

            await _db.SaveChangesAsync(ct);
        }
    }

    private static Core.Entities.AuditEvent BuildAuditEvent(
        Guid orgId, Guid entityId, string action, object payload) =>
        new()
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            EntityType = "Order",
            EntityId   = entityId,
            Action     = action,
            Payload    = JsonDocument.Parse(JsonSerializer.Serialize(payload)),
            CreatedAt  = DateTime.UtcNow
        };

    // ── Static factory method ─────────────────────────────────────────────────

    public static void Enqueue(
        IBackgroundJobClient jobs,
        Guid orderId,
        Guid organisationId,
        Guid artifactId)
    {
        jobs.Enqueue<DeliverOrderJob>(j =>
            j.ExecuteAsync(orderId, organisationId, artifactId, CancellationToken.None));
    }
}
