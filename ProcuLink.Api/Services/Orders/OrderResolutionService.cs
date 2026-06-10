using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services;

/// <summary>
/// Internal sub-service of <see cref="OrderService"/> owning line resolution,
/// AI-suggestion acceptance, and manual supplier rejection. Methods moved verbatim
/// from the original God-class; only the host type and shared-helper call sites
/// changed (audit W1/B1 decomposition).
/// </summary>
internal sealed class OrderResolutionService
{
    private readonly ProcuLinkDbContext            _db;
    private readonly IItemMappingService           _mappings;
    private readonly ILogger<OrderService>         _logger;
    private readonly OrderServiceShared            _shared;
    private readonly IAiSuggestionDecisionService  _aiDecisions;

    public OrderResolutionService(
        ProcuLinkDbContext            db,
        IItemMappingService           mappings,
        ILogger<OrderService>         logger,
        OrderServiceShared            shared,
        IAiSuggestionDecisionService  aiDecisions)
    {
        _db          = db;
        _mappings    = mappings;
        _logger      = logger;
        _shared      = shared;
        _aiDecisions = aiDecisions;
    }

    // ── ResolveAsync ──────────────────────────────────────────────────────────

    public async Task<Result<PurchaseOrderEntity>> ResolveAsync(
        Guid organisationId,
        Guid orderId,
        IReadOnlyList<LineResolution> resolutions,
        bool saveMappings,
        CancellationToken ct,
        ResolveHeaderFields? header = null)
    {
        // Load with tracking so EF picks up property changes on the line entities
        var entity = await _db.PurchaseOrders
            .Include(x => x.Lines)
            .Include(x => x.Supplier)
            .Where(x => x.Id == orderId && x.OrgId == organisationId)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            return Result<PurchaseOrderEntity>.Fail("Order not found.");

        // Validate all resolutions before mutating anything
        foreach (var res in resolutions)
        {
            if (string.IsNullOrWhiteSpace(res.SupplierItemCode))
                return Result<PurchaseOrderEntity>.Fail(
                    $"SupplierItemCode is required for line {res.LineNumber}.");

            if (!entity.Lines.Any(l => l.LineNumber == res.LineNumber))
                return Result<PurchaseOrderEntity>.Fail(
                    $"Line {res.LineNumber} does not exist in this order.");
        }

        // Wrap all writes in a transaction so mapping corrections, order status,
        // audit event, and passport event commit atomically (relational providers only;
        // the InMemory test provider does not support transactions).
        await using var tx = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;

        // Capture AI-suggestion decisions BEFORE the transient Ai* fields are cleared below,
        // so the durable history survives resolution (the fields are about to be nulled out).
        var decisionRecords = new List<AiSuggestionDecisionRecord>();

        // Apply resolutions
        foreach (var res in resolutions)
        {
            var line             = entity.Lines.First(l => l.LineNumber == res.LineNumber);
            var chosen           = res.SupplierItemCode.Trim();

            // Record what happened to any AI suggestion that was attached to this line.
            decisionRecords.Add(BuildDecisionFromLine(line, chosen, decidedBy: "user"));

            line.SupplierItemCode = chosen;
            line.NeedsReview     = false;
            line.Confidence      = 1.0f;
            line.AiSuggestedSupplierItemCode = null;
            line.AiSuggestionConfidence = null;
            line.AiSuggestionReason = null;
            line.AiSuggestionProvenance = null;

            // Persist the mapping so future uploads auto-resolve it
            if (saveMappings && !string.IsNullOrWhiteSpace(line.BuyerItemCode))
            {
                await _mappings.UpsertAsync(
                    organisationId, entity.SupplierId,
                    line.BuyerItemCode, line.SupplierItemCode,
                    MappingSource.Manual, ct);
            }
        }

        // Apply optional header-field corrections. Null/blank = no change per field.
        // The read path (GET /api/orders/{id} → MapToDto) sources OrderDate, Currency and
        // PoNumber from the columns, BuyerName column-first with a canonical_json fallback,
        // and the document/display supplier name (DocumentSupplierName) from the SupplierName
        // column. We therefore write the columns AND mirror into canonical_json so the two
        // stay consistent (the buyer-name denormalisation split is the reason header edits
        // were dropped before — see CLAUDE.md).
        //
        // PO number and the document/display supplier name ARE editable now. What is NOT
        // editable here is order ROUTING: SupplierId is never touched, so the order keeps
        // delivering to the supplier chosen via the picker — only the printed/display values
        // change.
        var changedHeaderFields = new List<string>();
        if (header is not null && header.HasAnyChange)
        {
            var canonicalUpdates = new Dictionary<string, object?>();

            if (header.OrderDate.HasValue)
            {
                entity.OrderDate = header.OrderDate.Value;
                canonicalUpdates["orderDate"] = header.OrderDate.Value.ToString("yyyy-MM-dd");
                changedHeaderFields.Add("orderDate");
            }

            if (!string.IsNullOrWhiteSpace(header.Currency))
            {
                entity.Currency = header.Currency.Trim().ToUpperInvariant();
                canonicalUpdates["currency"] = entity.Currency;
                changedHeaderFields.Add("currency");
            }

            if (!string.IsNullOrWhiteSpace(header.BuyerName))
            {
                var trimmed = header.BuyerName.Trim();
                entity.BuyerName = trimmed;                 // denormalised column
                canonicalUpdates["buyerName"] = trimmed;    // canonical_json mirror
                changedHeaderFields.Add("buyerName");
            }

            if (!string.IsNullOrWhiteSpace(header.PoNumber))
            {
                var trimmed = header.PoNumber.Trim();
                entity.PoNumber = trimmed;                  // po_number column (read path source)
                canonicalUpdates["poNumber"] = trimmed;     // canonical_json mirror (transform/Scriban)
                changedHeaderFields.Add("poNumber");
            }

            if (!string.IsNullOrWhiteSpace(header.SupplierName))
            {
                var trimmed = header.SupplierName.Trim();
                entity.SupplierName = trimmed;              // supplier_name column (display only; NOT routing)
                canonicalUpdates["supplierName"] = trimmed; // canonical_json mirror
                changedHeaderFields.Add("supplierName");
            }

            if (canonicalUpdates.Count > 0)
                entity.CanonicalJson = CanonicalJsonMerge.MergeStrings(entity.CanonicalJson, canonicalUpdates);
        }

        // Recompute order status
        entity.Status    = entity.Lines.Any(l => l.NeedsReview) ? "pending_review" : "ready";
        entity.UpdatedAt = DateTime.UtcNow;

        _db.AuditEvents.Add(OrderServiceShared.BuildAuditEvent(organisationId, orderId, "Resolved", new
        {
            lineCount    = resolutions.Count,
            savedMappings = saveMappings,
            newStatus    = entity.Status,
            headerFieldsChanged = changedHeaderFields
        }));

        await _db.SaveChangesAsync(ct);

        // Persist the durable AI-suggestion decision history inside the same transaction so it
        // commits atomically with the resolution. Idempotent across retries via the unique index.
        if (decisionRecords.Count > 0)
            await _aiDecisions.RecordManyAsync(organisationId, orderId, decisionRecords, ct);

        await _shared.EmitPassportEventAsync(organisationId, orderId, "Map", "Corrected",
            actorType: "user",
            payload: new { linesResolved = resolutions.Count, savedMappings = saveMappings }, ct: ct);

        if (tx is not null)
            await tx.CommitAsync(ct);

        await _shared.SafeReconcileExceptionsAsync(organisationId, orderId, ct);

        _logger.LogInformation(
            "Order {OrderId} resolved: {Count} lines, saveMappings={Save}, status={Status}",
            orderId, resolutions.Count, saveMappings, entity.Status);

        return Result<PurchaseOrderEntity>.Ok(entity);
    }

    // ── MarkRejectedAsync ─────────────────────────────────────────────────────

    public async Task<Result<PurchaseOrderEntity>> MarkRejectedAsync(
        Guid organisationId,
        Guid orderId,
        string reason,
        CancellationToken ct)
    {
        var entity = await _db.PurchaseOrders
            .Include(x => x.Lines)
            .Include(x => x.OutboundArtifacts)
            .Where(x => x.Id == orderId && x.OrgId == organisationId)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            return Result<PurchaseOrderEntity>.Fail("Order not found.");

        var now = DateTime.UtcNow;
        entity.Status    = OrderStatusConstants.RejectedBySupplier;
        entity.UpdatedAt = now;

        // Write the rejection reason onto the most-recent delivery attempt for this
        // order (if one exists), so the audit trail shows it in context.
        var latestAttempt = await _db.DeliveryAttempts
            .Where(a => a.OrderId == orderId && a.OrgId == organisationId)
            .OrderByDescending(a => a.AttemptedAt)
            .FirstOrDefaultAsync(ct);

        if (latestAttempt is not null)
        {
            latestAttempt.RejectionReason = reason;
        }

        _db.AuditEvents.Add(OrderServiceShared.BuildAuditEvent(organisationId, orderId, "MarkedRejected", new
        {
            reason,
            markedAt = now,
        }));

        await _db.SaveChangesAsync(ct);

        await _shared.SafeReconcileExceptionsAsync(organisationId, orderId, ct);

        _logger.LogInformation(
            "Order {OrderId} (org {OrgId}) manually marked as rejected. Reason: {Reason}",
            orderId, organisationId, reason);

        return Result<PurchaseOrderEntity>.Ok(entity);
    }

    // ── AcceptAiSuggestionsAsync ──────────────────────────────────────────────

    public async Task<Result<int>> AcceptAiSuggestionsAsync(
        Guid organisationId,
        Guid orderId,
        double minConfidence,
        CancellationToken ct)
    {
        // Load with tracking so EF picks up property changes on the line entities
        var entity = await _db.PurchaseOrders
            .Include(x => x.Lines)
            .Where(x => x.Id == orderId && x.OrgId == organisationId)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            return Result<int>.Fail("Order not found.");

        var acceptedCount = 0;

        // Capture decisions BEFORE clearing the transient Ai* fields below.
        var decisionRecords = new List<AiSuggestionDecisionRecord>();

        foreach (var line in entity.Lines)
        {
            if (!line.NeedsReview) continue;
            if (string.IsNullOrWhiteSpace(line.AiSuggestedSupplierItemCode)) continue;
            if ((line.AiSuggestionConfidence ?? 0.0) < minConfidence) continue;

            var chosen = line.AiSuggestedSupplierItemCode;

            // The bulk-accept path always keeps the AI's suggested code verbatim → "accepted",
            // decided by the AI/system (no human review). Recorded before the fields are cleared.
            decisionRecords.Add(BuildDecisionFromLine(line, chosen, decidedBy: "ai"));

            line.SupplierItemCode           = chosen;
            line.Confidence                 = (float)(line.AiSuggestionConfidence ?? line.Confidence);
            line.NeedsReview                = false;
            line.AiSuggestedSupplierItemCode = null;
            line.AiSuggestionConfidence     = null;
            line.AiSuggestionReason         = null;
            line.AiSuggestionProvenance     = null;

            acceptedCount++;
        }

        // Recompute order status
        entity.Status    = entity.Lines.Any(l => l.NeedsReview)
                               ? OrderStatusConstants.PendingReview
                               : OrderStatusConstants.Ready;
        entity.UpdatedAt = DateTime.UtcNow;

        _db.AuditEvents.Add(OrderServiceShared.BuildAuditEvent(organisationId, orderId, "AiSuggestionsBulkAccepted", new
        {
            acceptedCount,
            minConfidence,
            newStatus = entity.Status
        }));

        await _db.SaveChangesAsync(ct);

        // Persist the durable accept history. Idempotent across retries via the unique index.
        if (decisionRecords.Count > 0)
            await _aiDecisions.RecordManyAsync(organisationId, orderId, decisionRecords, ct);

        await _shared.EmitPassportEventAsync(organisationId, orderId, "Map", "AiAccepted",
            actorType: "ai",
            payload: new { accepted = acceptedCount }, ct: ct);

        await _shared.SafeReconcileExceptionsAsync(organisationId, orderId, ct);

        _logger.LogInformation(
            "Order {OrderId}: {Count} AI suggestions bulk-accepted (minConfidence={Min}), status={Status}",
            orderId, acceptedCount, minConfidence, entity.Status);

        return Result<int>.Ok(acceptedCount);
    }

    // ── Decision-history helper ────────────────────────────────────────────────

    /// <summary>
    /// Builds an <see cref="AiSuggestionDecisionRecord"/> from a line's transient AI metadata
    /// and the code that was actually chosen. Classifies the outcome:
    /// <list type="bullet">
    ///   <item>no AI suggestion present → <c>manual</c></item>
    ///   <item>chosen code equals the suggested code → <c>accepted</c></item>
    ///   <item>a different code was chosen → <c>rejected</c></item>
    /// </list>
    /// The candidate-set evidence (reason + provenance) is captured as a small JSON blob so the
    /// suggestion context survives the clearing of the line's Ai* fields.
    /// </summary>
    private static AiSuggestionDecisionRecord BuildDecisionFromLine(
        PurchaseOrderLineEntity line, string chosenCode, string decidedBy)
    {
        var suggested = line.AiSuggestedSupplierItemCode;
        var hasSuggestion = !string.IsNullOrWhiteSpace(suggested);

        string decision;
        if (!hasSuggestion)
            decision = AiSuggestionDecisionKind.Manual;
        else if (string.Equals(suggested!.Trim(), chosenCode.Trim(), StringComparison.OrdinalIgnoreCase))
            decision = AiSuggestionDecisionKind.Accepted;
        else
            decision = AiSuggestionDecisionKind.Rejected;

        // Capture reason + provenance as the candidate-set evidence (minimal, queryable JSON).
        string? candidateSetJson = null;
        if (hasSuggestion && (!string.IsNullOrWhiteSpace(line.AiSuggestionReason)
                              || !string.IsNullOrWhiteSpace(line.AiSuggestionProvenance)))
        {
            candidateSetJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                reason     = line.AiSuggestionReason,
                provenance = line.AiSuggestionProvenance,
            });
        }

        return new AiSuggestionDecisionRecord(
            LineNumber:                line.LineNumber,
            SuggestedSupplierItemCode: hasSuggestion ? suggested!.Trim() : string.Empty,
            ChosenSupplierItemCode:    string.IsNullOrWhiteSpace(chosenCode) ? null : chosenCode.Trim(),
            CandidateSetJson:          candidateSetJson,
            Confidence:                line.AiSuggestionConfidence.HasValue
                                           ? (double?)line.AiSuggestionConfidence.Value
                                           : null,
            ModelVersion:              null, // per-line model version is not persisted on the line today
            Decision:                  decision,
            DecidedBy:                 decidedBy);
    }
}
