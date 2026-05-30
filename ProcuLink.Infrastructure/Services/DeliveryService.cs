using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Infrastructure.Services;

public sealed class DeliveryService : IDeliveryService
{
    private readonly ProcuLinkDbContext _db;
    private readonly IFileStorageService _fileStorage;
    private readonly DeliveryEncryptionService _encryption;
    private readonly IReadOnlyDictionary<string, IDeliveryDispatcher> _dispatchers;
    private readonly IIntegrationTriggerService _integrationTrigger;
    private readonly IAnalyticsService _analytics;
    private readonly DeliveryReliabilityOptions _reliability;
    private readonly ILogger<DeliveryService> _logger;

    public DeliveryService(
        ProcuLinkDbContext db,
        IFileStorageService fileStorage,
        DeliveryEncryptionService encryption,
        IEnumerable<IDeliveryDispatcher> dispatchers,
        IIntegrationTriggerService integrationTrigger,
        IAnalyticsService analytics,
        ILogger<DeliveryService> logger,
        DeliveryReliabilityOptions? reliability = null)
    {
        _db = db;
        _fileStorage = fileStorage;
        _encryption = encryption;
        _dispatchers = dispatchers.ToDictionary(x => x.Protocol, StringComparer.OrdinalIgnoreCase);
        _integrationTrigger = integrationTrigger;
        _analytics = analytics;
        _reliability = reliability ?? new DeliveryReliabilityOptions();
        _logger = logger;
    }

    public async Task<DeliveryResult> DispatchArtifactAsync(
        Guid orgId,
        Guid orderId,
        Guid artifactId,
        bool requireAutoDeliver,
        CancellationToken ct)
    {
        var artifact = await _db.OutboundArtifacts
            .Where(x => x.Id == artifactId && x.OrderId == orderId && x.OrgId == orgId)
            .FirstOrDefaultAsync(ct);

        var order = await _db.PurchaseOrders
            .Where(x => x.Id == orderId && x.OrgId == orgId)
            .FirstOrDefaultAsync(ct);

        if (artifact is null || order is null)
            return new DeliveryResult(false, "Order artifact not found.");

        var config = await _db.SupplierDeliveryConfigs
            .Where(x => x.OrgId == orgId && x.SupplierId == order.SupplierId)
            .FirstOrDefaultAsync(ct);

        if (config is null)
            return new DeliveryResult(true, null);

        if (requireAutoDeliver && !config.AutoDeliver)
            return new DeliveryResult(true, null);

        if (!_dispatchers.TryGetValue(config.Protocol, out var dispatcher))
            return await FailBeforeDispatchAsync(order, artifact, config, "No dispatcher registered for delivery protocol.", ct);

        var credentials = string.IsNullOrWhiteSpace(config.EncryptedCredentials)
            ? string.Empty
            : _encryption.Decrypt(config.EncryptedCredentials);

        if (credentials is null)
            return await FailBeforeDispatchAsync(order, artifact, config, "Delivery credentials could not be decrypted.", ct);

        // SLA timer: opening a fresh delivery attempt (re)starts the confirmation window
        // and clears any prior breach flag. The SLA sweep flags the order if this deadline
        // passes without a confirmed delivery.
        var dispatchStart = DateTime.UtcNow;
        order.Status = OrderStatusConstants.Delivering;
        order.DeliveryDueAt = dispatchStart + _reliability.SlaWindow;
        order.SlaBreached = false;
        order.UpdatedAt = dispatchStart;
        await _db.SaveChangesAsync(ct);

        byte[] content;
        await using (var stream = await _fileStorage.DownloadAsync(artifact.FileKey, ct))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            content = buffer.ToArray();
        }

        var result = await dispatcher.DispatchAsync(
            content,
            BuildFileName(order, artifact),
            GetContentType(artifact.Format),
            config,
            credentials,
            ct);

        await PersistAttemptAsync(order, artifact, config, result, ct);
        return result;
    }

    public async Task<DeliveryTestResult> TestFireAsync(Guid orgId, Guid supplierId, CancellationToken ct)
    {
        var config = await _db.SupplierDeliveryConfigs
            .Where(x => x.OrgId == orgId && x.SupplierId == supplierId)
            .FirstOrDefaultAsync(ct);

        if (config is null)
            return new DeliveryTestResult(false, "Delivery config not found.", null);

        if (!_dispatchers.TryGetValue(config.Protocol, out var dispatcher))
            return new DeliveryTestResult(false, "No dispatcher registered for delivery protocol.", null);

        var credentials = string.IsNullOrWhiteSpace(config.EncryptedCredentials)
            ? string.Empty
            : _encryption.Decrypt(config.EncryptedCredentials);

        if (credentials is null)
            return new DeliveryTestResult(false, "Delivery credentials could not be decrypted.", null);

        var result = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes("test,from\r\nproculink,true\r\n"),
            "proculink-test.csv",
            "text/csv",
            config,
            credentials,
            ct);

        _db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(),
            OrderId = null,
            OrgId = orgId,
            Channel = config.Protocol,
            Destination = GetDestination(config),
            Status = result.Success ? "success" : "failed",
            AttemptNumber = 0, // test-fire is not part of an order's retry sequence
            AttemptedAt = DateTime.UtcNow,
            ResponseCode = result.ResponseCode,
            ErrorMessage = result.Success ? null : result.ErrorMessage,
        });

        await _db.SaveChangesAsync(ct);

        return new DeliveryTestResult(result.Success, result.ErrorMessage, result.ResponseCode);
    }

    private async Task<DeliveryResult> FailBeforeDispatchAsync(
        PurchaseOrderEntity order,
        OutboundArtifact artifact,
        SupplierDeliveryConfig config,
        string error,
        CancellationToken ct)
    {
        var result = new DeliveryResult(false, error);
        await PersistAttemptAsync(order, artifact, config, result, ct);
        return result;
    }

    private async Task PersistAttemptAsync(
        PurchaseOrderEntity order,
        OutboundArtifact artifact,
        SupplierDeliveryConfig config,
        DeliveryResult result,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Distinguish supplier rejection (4xx) from transient failure (5xx / network).
        // A 4xx means the supplier received and explicitly rejected the payload —
        // retrying the exact same payload will not help without a content fix.
        var isSupplierRejection = !result.Success
            && result.ResponseCode.HasValue
            && result.ResponseCode.Value is >= 400 and <= 499;

        order.Status = result.Success
            ? OrderStatusConstants.Delivered
            : isSupplierRejection
                ? OrderStatusConstants.RejectedBySupplier
                : OrderStatusConstants.DeliveryFailed;
        order.UpdatedAt = now;

        // SLA timer: a confirmed delivery closes the SLA window — clear the deadline and breach flag
        // so the sweep can never flag an already-delivered order.
        if (result.Success)
        {
            order.DeliveryDueAt = null;
            order.SlaBreached = false;
        }

        // 1-based attempt index within this order's delivery retry sequence.
        var attemptNumber = (await _db.DeliveryAttempts
            .CountAsync(a => a.OrderId == order.Id && a.OrgId == order.OrgId, ct)) + 1;

        _db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            OrgId = order.OrgId,
            Channel = config.Protocol,
            Destination = GetDestination(config),
            Status = result.Success ? "success" : "failed",
            AttemptNumber = attemptNumber,
            AttemptedAt = now,
            ResponseCode = result.ResponseCode,
            ErrorMessage = result.Success ? null : result.ErrorMessage,
            RejectionReason = isSupplierRejection ? result.ErrorMessage : null,
            // Rejection capture: persist the supplier's raw NACK body verbatim (bounded).
            ResponseBody = TruncateResponseBody(result.ResponseBody),
            // ACK round-trip: stamp the confirmation time on a successful dispatch.
            AcknowledgedAt = result.Success ? now : null,
        });

        await _db.SaveChangesAsync(ct);

        // ── Wave 4: fire order.delivered / order.failed triggers ──────────────────
        if (result.Success)
        {
            // Check whether this is the org's FIRST delivered order. The current order's status
            // has just been saved as 'delivered' above; exclude it by id.
            var hadOtherDeliveredOrders = await _db.PurchaseOrders
                .AnyAsync(o => o.OrgId == order.OrgId
                            && o.Id != order.Id
                            && o.Status == OrderStatusConstants.Delivered, ct);

            if (!hadOtherDeliveredOrders)
            {
                await _analytics.CaptureAsync(
                    organisationId: order.OrgId,
                    userId: null,
                    eventName: "first_delivery_succeeded",
                    properties: new Dictionary<string, object?>
                    {
                        ["order_id"] = order.Id,
                        ["protocol"] = config.Protocol,
                    },
                    ct: ct);
            }

            _ = _integrationTrigger.EnqueueAsync(
                order.OrgId,
                "order.delivered",
                new { order_id = order.Id, delivered_at = now, acknowledged_at = now },
                ct);
        }
        else if (isSupplierRejection)
        {
            _ = _integrationTrigger.EnqueueAsync(
                order.OrgId,
                "order.rejected",
                new { order_id = order.Id, rejected_at = now, reason = result.ErrorMessage },
                ct);
        }
        else
        {
            _ = _integrationTrigger.EnqueueAsync(
                order.OrgId,
                "order.failed",
                new { order_id = order.Id, failed_at = now, error = result.ErrorMessage },
                ct);
        }

        _logger.LogInformation(
            "Delivery attempt for order {OrderId}, artifact {ArtifactId}: {Status}",
            order.Id,
            artifact.Id,
            result.Success ? "success" : "failed");
    }

    private static string BuildFileName(PurchaseOrderEntity order, OutboundArtifact artifact)
    {
        var extension = artifact.Format switch
        {
            "xml" => "xml",
            "csv" => "csv",
            "json" => "json",
            _ => "dat",
        };

        return $"{SanitizeFileToken(order.PoNumber)}.{extension}";
    }

    private static string SanitizeFileToken(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "order" : sanitized;
    }

    private static string GetContentType(string format) => format switch
    {
        "xml" => "application/xml",
        "json" => "application/json",
        "csv" => "text/csv",
        _ => "application/octet-stream",
    };

    private static string? TruncateResponseBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        return body.Length <= DeliveryAttempt.MaxResponseBodyLength
            ? body
            : body[..DeliveryAttempt.MaxResponseBodyLength];
    }

    private static string GetDestination(SupplierDeliveryConfig config)
    {
        try
        {
            using var doc = JsonDocument.Parse(config.ConfigJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("url", out var url)) return url.GetString() ?? config.Protocol;
            if (root.TryGetProperty("host", out var host)) return host.GetString() ?? config.Protocol;
        }
        catch (JsonException)
        {
            // Config validation happens at save time. Keep attempts safe if old data is malformed.
        }

        return config.Protocol;
    }

    public async Task<DeliveryResult> RetryDeliveryAsync(
        Guid orgId,
        Guid orderId,
        int maxAttempts,
        CancellationToken ct)
    {
        if (maxAttempts < 1) maxAttempts = 1;

        var order = await _db.PurchaseOrders
            .Where(x => x.Id == orderId && x.OrgId == orgId)
            .FirstOrDefaultAsync(ct);

        if (order is null)
            return new DeliveryResult(false, "Order not found.");

        if (order.Status == OrderStatusConstants.DeliveryDeadLetter)
            return new DeliveryResult(false, "Order is in dead-letter state — retries are exhausted.");

        if (order.Status == OrderStatusConstants.Delivered)
            return new DeliveryResult(true, null);

        if (order.Status is not (OrderStatusConstants.DeliveryFailed
                              or OrderStatusConstants.ReadyToDeliver
                              or OrderStatusConstants.Delivering))
            return new DeliveryResult(false, $"Order status '{order.Status}' is not retryable.");

        var priorAttempts = await _db.DeliveryAttempts
            .CountAsync(a => a.OrderId == orderId && a.OrgId == orgId, ct);

        if (priorAttempts >= maxAttempts)
        {
            await DeadLetterAsync(order, priorAttempts, lastError: "Maximum delivery attempts reached.", ct);
            return new DeliveryResult(false, "Maximum delivery attempts reached — order moved to dead-letter.");
        }

        var artifact = await _db.OutboundArtifacts
            .Where(a => a.OrderId == orderId && a.OrgId == orgId)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (artifact is null)
            return new DeliveryResult(false, "No outbound artifact found. Transform the order before retrying delivery.");

        var result = await DispatchArtifactAsync(orgId, orderId, artifact.Id, requireAutoDeliver: false, ct);

        if (!result.Success)
        {
            var attemptsNow = priorAttempts + 1;
            if (attemptsNow >= maxAttempts)
                await DeadLetterAsync(order, attemptsNow, lastError: result.ErrorMessage, ct);
        }

        return result;
    }

    public Task<int> CountDeliveryAttemptsAsync(Guid orgId, Guid orderId, CancellationToken ct) =>
        _db.DeliveryAttempts.CountAsync(a => a.OrderId == orderId && a.OrgId == orgId, ct);

    private async Task DeadLetterAsync(
        PurchaseOrderEntity order,
        int attemptCount,
        string? lastError,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        order.Status = OrderStatusConstants.DeliveryDeadLetter;
        order.UpdatedAt = now;
        // Dead-letter is terminal: close the SLA window so the sweep never flags an
        // order whose retries are already exhausted.
        order.DeliveryDueAt = null;

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            attemptCount,
            lastError,
            deadLetteredAt = now,
        });

        _db.AuditEvents.Add(new Core.Entities.AuditEvent
        {
            Id = Guid.NewGuid(),
            OrgId = order.OrgId,
            UserId = null,
            EntityType = "Order",
            EntityId = order.Id,
            Action = "DeliveryDeadLettered",
            Payload = System.Text.Json.JsonDocument.Parse(payload),
            CreatedAt = now,
        });

        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Order {OrderId} (org {OrgId}) dead-lettered after {Attempts} attempt(s). Last error: {Error}",
            order.Id, order.OrgId, attemptCount, lastError ?? "(none)");
    }
}
