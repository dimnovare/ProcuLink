using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services;

/// <summary>
/// Read-model assembler for the PO Passport. Every query is org-scoped and read-only
/// (<c>AsNoTracking</c>); the service never mutates state. Large blobs (source file,
/// generated artifact) are referenced by storage key / id only — never downloaded or
/// embedded. Data the model does not persist is returned null/empty and recorded in
/// <see cref="PassportDto.Notes"/>.
/// </summary>
public sealed class PassportService : IPassportService
{
    private readonly ProcuLinkDbContext _db;

    public PassportService(ProcuLinkDbContext db) => _db = db;

    /// <summary>Audit actions that represent an explicit human correction.</summary>
    private static readonly HashSet<string> ManualCorrectionActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Resolved",
        "AiSuggestionsBulkAccepted",
        "MarkedRejected",
    };

    public async Task<Result<PassportDto>> GetAsync(
        Guid organisationId,
        Guid orderId,
        CancellationToken ct)
    {
        // ── Order + supplier (org-scoped existence gate) ──────────────────────────
        var order = await _db.PurchaseOrders
            .AsNoTracking()
            .Include(o => o.Supplier)
            .Where(o => o.Id == orderId && o.OrgId == organisationId)
            .FirstOrDefaultAsync(ct);

        if (order is null)
            return Result<PassportDto>.Fail("Order not found.");

        // ── Lines (canonical summary + mapping decisions + AI suggestions) ────────
        var lines = await _db.PurchaseOrderLines
            .AsNoTracking()
            .Where(l => l.OrderId == orderId)
            .OrderBy(l => l.LineNumber)
            .ToListAsync(ct);

        // ── Outbound artifacts (output reference) ─────────────────────────────────
        var artifacts = await _db.OutboundArtifacts
            .AsNoTracking()
            .Where(a => a.OrderId == orderId && a.OrgId == organisationId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

        // ── Delivery attempts (delivery + supplier response) ──────────────────────
        var deliveryAttempts = await _db.DeliveryAttempts
            .AsNoTracking()
            .Where(a => a.OrderId == orderId && a.OrgId == organisationId)
            .OrderBy(a => a.AttemptedAt)
            .ToListAsync(ct);

        // ── Audit events (timeline + manual corrections) ──────────────────────────
        var auditEvents = await _db.AuditEvents
            .AsNoTracking()
            .Where(e => e.EntityId == orderId
                     && e.OrgId == organisationId
                     && e.EntityType == "Order")
            .OrderBy(e => e.CreatedAt)
            .ToListAsync(ct);

        // ── Supplier delivery config + profile (acceptance-profile section) ───────
        var deliveryConfig = await _db.SupplierDeliveryConfigs
            .AsNoTracking()
            .Where(c => c.OrgId == organisationId && c.SupplierId == order.SupplierId)
            .OrderByDescending(c => c.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        var supplierProfile = await _db.SupplierProfiles
            .AsNoTracking()
            .Where(p => p.OrgId == organisationId && p.SupplierId == order.SupplierId)
            .OrderByDescending(p => p.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        var notes = new List<string>();

        // ── Compose sections ──────────────────────────────────────────────────────
        var orderMeta = new PassportOrderMeta(
            OrderId:      order.Id,
            PoNumber:     order.PoNumber,
            Status:       order.Status,
            SupplierId:   order.SupplierId ?? Guid.Empty,
            SupplierName: order.Supplier?.Name ?? "Unknown Supplier",
            BuyerName:    ExtractBuyerName(order),
            Currency:     order.Currency,
            OrderDate:    order.OrderDate.ToString("yyyy-MM-dd"),
            CreatedAt:    order.CreatedAt,
            UpdatedAt:    order.UpdatedAt,
            IsSample:     order.IsSample);

        var sourceArtifact = new PassportSourceArtifact(
            StorageKey:     order.SourceFileKey,
            DetectedFormat: FormatFromFileKey(order.SourceFileKey));

        if (order.SourceFileKey is null)
            notes.Add("No stored source file — order was created from an already-parsed payload (e.g. webhook/ingress).");

        var canonical = new PassportCanonicalSummary(
            LineCount:     lines.Count,
            Currency:      order.Currency,
            TotalValue:    lines.Sum(l => l.Quantity * l.UnitPrice),
            TotalQuantity: lines.Sum(l => l.Quantity));

        var supplierProfileDto = BuildSupplierProfile(deliveryConfig, supplierProfile, notes);

        // Structured per-order validation outcomes, persisted by the acceptance-profile
        // validate endpoint. Empty until the order has been validated against a profile.
        var validationResults = await _db.OrderValidationResults
            .AsNoTracking()
            .Where(r => r.OrgId == organisationId && r.OrderId == orderId)
            .OrderBy(r => r.LineNumber)
            .Select(r => new PassportValidationResult(r.LineNumber, r.Severity, r.Code, r.Message))
            .ToListAsync(ct);

        if (validationResults.Count == 0)
            notes.Add("No structured validation results recorded for this order yet. "
                    + "Parse failures appear in the timeline as 'ParseFailed' events, and lines still flagged "
                    + "for review are visible via the mapping decisions (source='unresolved').");

        var mappingDecisions = lines
            .Select(l => new PassportMappingDecision(
                LineNumber:       l.LineNumber,
                BuyerItemCode:    l.BuyerItemCode,
                SupplierItemCode: l.SupplierItemCode,
                Source:           MappingSourceFor(l),
                Confidence:       l.Confidence))
            .ToList();

        var manualCorrections = auditEvents
            .Where(e => ManualCorrectionActions.Contains(e.Action))
            .Select(e => new PassportManualCorrection(
                Action: e.Action,
                At:     e.CreatedAt,
                Detail: PayloadText(e.Payload)))
            .ToList();

        // Only suggestions still attached to a line can be reported. On resolve the
        // service clears Ai* fields, so accepted/rejected history is not recoverable.
        var aiSuggestions = lines
            .Where(l => !string.IsNullOrWhiteSpace(l.AiSuggestedSupplierItemCode))
            .Select(l => new PassportAiSuggestionOutcome(
                LineNumber:                l.LineNumber,
                SuggestedSupplierItemCode: l.AiSuggestedSupplierItemCode!,
                Confidence:                l.AiSuggestionConfidence,
                Reason:                    l.AiSuggestionReason,
                Provenance:                l.AiSuggestionProvenance,
                Status:                    "pending"))
            .ToList();

        notes.Add("Only AI suggestions still PENDING a decision are listed here — resolving a line clears its "
                + "AI metadata on the line. The durable accept/reject decision history (which survives "
                + "resolution) is available at GET /api/orders/{id}/ai-decisions; a bulk-accept also appears in "
                + "manual corrections as 'AiSuggestionsBulkAccepted'.");

        var latestArtifact = artifacts.FirstOrDefault();
        var outputArtifact = latestArtifact is null
            ? null
            : new PassportOutputArtifact(
                ArtifactId: latestArtifact.Id,
                Format:     latestArtifact.Format,
                FileKey:    latestArtifact.FileKey,
                CreatedAt:  latestArtifact.CreatedAt);

        var deliveryAttemptDtos = deliveryAttempts
            .Select(a => new PassportDeliveryAttempt(
                AttemptNumber:   a.AttemptNumber,
                Status:          a.Status,
                Channel:         a.Channel,
                Destination:     a.Destination,
                AttemptedAt:     a.AttemptedAt,
                ResponseCode:    a.ResponseCode,
                AcknowledgedAt:  a.AcknowledgedAt,
                RejectionReason: a.RejectionReason,
                ErrorMessage:    a.ErrorMessage))
            .ToList();

        var supplierResponse = BuildSupplierResponse(order, deliveryAttempts);

        var timeline = auditEvents
            .Select(e => new PassportTimelineEvent(
                Action: e.Action,
                At:     e.CreatedAt,
                Detail: PayloadText(e.Payload)))
            .ToList();

        // Merge durable passport events into the timeline
        var passportEventRows = await _db.PoPassportEvents
            .AsNoTracking()
            .Where(e => e.OrgId == organisationId && e.OrderId == orderId)
            .OrderBy(e => e.OccurredAt)
            .ToListAsync(ct);

        foreach (var row in passportEventRows)
        {
            timeline.Add(new PassportTimelineEvent(
                Action: $"{row.Stage}.{row.EventType}",
                At:     row.OccurredAt,
                Detail: row.Payload));
        }

        // Re-sort after merge
        timeline.Sort((a, b) => DateTime.Compare(a.At, b.At));

        var dto = new PassportDto(
            Order:             orderMeta,
            SourceArtifact:    sourceArtifact,
            Canonical:         canonical,
            SupplierProfile:   supplierProfileDto,
            ValidationResults: validationResults,
            MappingDecisions:  mappingDecisions,
            ManualCorrections: manualCorrections,
            AiSuggestions:     aiSuggestions,
            OutputArtifact:    outputArtifact,
            DeliveryAttempts:  deliveryAttemptDtos,
            SupplierResponse:  supplierResponse,
            FinalStatus:       order.Status,
            Timeline:          timeline,
            Notes:             notes);

        return Result<PassportDto>.Ok(dto);
    }

    // ── Section builders ────────────────────────────────────────────────────────

    private static PassportSupplierProfile? BuildSupplierProfile(
        SupplierDeliveryConfig? deliveryConfig,
        SupplierProfileEntity?  profile,
        List<string>            notes)
    {
        if (deliveryConfig is null && profile is null)
        {
            notes.Add("No supplier delivery config or acceptance profile exists for this supplier yet.");
            return null;
        }

        notes.Add("Supplier acceptance profile has no explicit version field in the model; "
                + "'lastUpdatedAt' is surfaced as the version marker and 'version' is null.");

        // Prefer the delivery config's timestamp (it is the artifact most directly
        // governing delivery); fall back to the profile's.
        var lastUpdated = deliveryConfig?.UpdatedAt ?? profile?.UpdatedAt;

        return new PassportSupplierProfile(
            Protocol:        deliveryConfig?.Protocol,
            OutputFormat:    profile?.OutputFormat,
            AcceptedFormats: profile?.AcceptedFormats ?? new List<string>(),
            Version:         null,
            LastUpdatedAt:   lastUpdated);
    }

    private static PassportSupplierResponse? BuildSupplierResponse(
        PurchaseOrderEntity     order,
        IReadOnlyList<DeliveryAttempt> attempts)
    {
        var latest = attempts
            .OrderByDescending(a => a.AttemptedAt)
            .FirstOrDefault();

        var rejected     = order.Status == OrderStatusConstants.RejectedBySupplier;
        var acknowledged = order.Status == OrderStatusConstants.Delivered
                           || latest?.AcknowledgedAt is not null;

        // Nothing to report when there is neither a delivery attempt nor a terminal
        // delivery/rejection status.
        if (latest is null && !rejected && !acknowledged)
            return null;

        var outcome = rejected
            ? "rejected"
            : acknowledged
                ? "acknowledged"
                : "unknown";

        return new PassportSupplierResponse(
            Outcome:         outcome,
            AcknowledgedAt:  latest?.AcknowledgedAt,
            RejectionReason: latest?.RejectionReason,
            ResponseCode:    latest?.ResponseCode,
            ResponseBody:    latest?.ResponseBody);
    }

    // ── Field helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// How a line's supplier code was decided:
    ///  - "ai"            : an AI suggestion is still attached (line not yet resolved deterministically)
    ///  - "deterministic" : a supplier code is set and no AI suggestion remains
    ///  - "unresolved"    : still needs review and has no supplier code
    /// </summary>
    private static string MappingSourceFor(PurchaseOrderLineEntity line)
    {
        if (!string.IsNullOrWhiteSpace(line.AiSuggestedSupplierItemCode))
            return "ai";
        if (!string.IsNullOrWhiteSpace(line.SupplierItemCode))
            return "deterministic";
        return "unresolved";
    }

    /// <summary>
    /// Extracts BuyerName from the denormalized column (set by the async parse job)
    /// or falls back to CanonicalJson for orders created via the sync path.
    /// </summary>
    private static string? ExtractBuyerName(PurchaseOrderEntity order)
    {
        // Prefer the denormalized column — always populated by ParseStoredFileAsync.
        if (!string.IsNullOrWhiteSpace(order.BuyerName)) return order.BuyerName;

        // Fall back to CanonicalJson for orders created via the sync (non-file) path.
        if (order.CanonicalJson is null) return null;
        try
        {
            var root = order.CanonicalJson.RootElement;
            if (root.TryGetProperty("buyerName", out var el))
                return el.GetString();
            if (root.TryGetProperty("BuyerName", out var el2))
                return el2.GetString();
        }
        catch { /* malformed JSON — ignore */ }
        return null;
    }

    private static string? FormatFromFileKey(string? fileKey)
    {
        if (string.IsNullOrWhiteSpace(fileKey)) return null;
        var ext = Path.GetExtension(fileKey).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "pdf"           => "pdf",
            "csv"           => "csv",
            "xlsx" or "xls" => "xlsx",
            "xml" or "cxml" => "cxml",
            "edi" or "x12"  => "edi",
            "txt"           => "edi",
            ""              => null,
            _               => ext,
        };
    }

    private static string? PayloadText(JsonDocument? payload)
    {
        if (payload is null) return null;
        try { return payload.RootElement.GetRawText(); }
        catch { return null; }
    }
}
