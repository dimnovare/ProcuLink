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

    // ── The terminal-status guard (WP-19 follow-up) ───────────────────────────
    //
    // OrderStatusMachine.DeclaredTerminal states the product's answer to "is an order that reaches
    // this status finished?", and the dead-end invariant (NoNonTerminalStatus_IsADeadEnd) only bites
    // because that answer is declared INDEPENDENTLY of the transition map. It said {failed}, and it
    // justified itself with re-parse (ParseOrderJob refuses to re-drive a failed order) and transform
    // (OrdersController.Transform answers "Upload a corrected file before transforming").
    //
    // Neither argument covered THIS file, and all three status writers here carried no from-status
    // check at all. A failed source file leaves NO lines behind, so every `Lines.Any(NeedsReview)`
    // recompute over that empty collection lands on 'ready' — which is transformable, and then
    // deliverable. A header-only edit was enough to trigger it:
    //   POST /api/orders/{failedOrderId}/resolve {"poNumber":"X"}  →  ready
    //
    // Two readings were available and only one is consistent: either 'failed' is genuinely terminal
    // and these writers need the guard, or it is not and the declaration must change. The second
    // would mean an order whose source document never parsed can be pushed into the transform and
    // delivery pipeline — the exact thing the two existing refusals already prevent on their own
    // paths. So the declaration is right and the writers were wrong; deriving the guard FROM
    // DeclaredTerminal is what makes the declaration load-bearing instead of decorative.
    //
    // Honest about the cost: this DOES close an existing path. Any operator who has been recovering
    // a failed order by resolving it must now upload a corrected file as a new order — which is what
    // the product's own copy tells them everywhere else. Pinned by OrderServiceTerminalOrderGuardTests,
    // whose positive controls keep the guard narrow: pending_review and rejected_by_supplier must
    // still resolve, or this would restore the dead end WP-19 removed.

    /// <summary>The one sentence an operator reads, in the product's own words for this case.</summary>
    private const string SourceFileCannotBeFixedHere =
        "This order's source file could not be read, so there is nothing here to correct. " +
        "Upload a corrected file as a new order.";

    private static bool IsFinished(string status) =>
        OrderStatusMachine.DeclaredTerminal.Contains(status);

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

        if (IsFinished(entity.Status))
            return Result<PurchaseOrderEntity>.Fail(SourceFileCannotBeFixedHere);

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

            // Did the reviewer take the model's code verbatim, or type their own over it? Same
            // comparison BuildDecisionFromLine uses to call a decision "accepted". Read BEFORE the
            // Ai* fields are cleared below — the model's own number is the only real confidence
            // anywhere near this line, and it used to be thrown away here without being carried
            // anywhere, which is why no saved mapping in the product had ever held a real score.
            var acceptedModelCode =
                !string.IsNullOrWhiteSpace(line.AiSuggestedSupplierItemCode)
                && string.Equals(line.AiSuggestedSupplierItemCode!.Trim(), chosen,
                                 StringComparison.OrdinalIgnoreCase);
            var modelConfidence = acceptedModelCode ? line.AiSuggestionConfidence : null;

            line.SupplierItemCode = chosen;
            line.NeedsReview     = false;
            line.ReviewReason    = null; // human resolved the line — the "why flagged" no longer applies
            // Only a real model score, never a state flag. This used to be a flat `1.0f`, which the
            // order passport then printed as a green "100%" chip on the confidence ramp — for a
            // code a human had just typed by hand. "A human resolved this" is already carried by
            // NeedsReview and by the decision record; it is not a probability.
            line.Confidence      = modelConfidence;
            line.AiSuggestedSupplierItemCode = null;
            line.AiSuggestionConfidence = null;
            line.AiSuggestionReason = null;
            line.AiSuggestionProvenance = null;

            // Persist the mapping so future uploads auto-resolve it.
            if (saveMappings && !string.IsNullOrWhiteSpace(line.BuyerItemCode))
            {
                await _mappings.UpsertAsync(
                    organisationId, entity.SupplierId ?? Guid.Empty,
                    line.BuyerItemCode, line.SupplierItemCode,
                    // Accepting the model's code verbatim is not the same act as typing one, and
                    // the supplier screen prints this source. Recording BOTH as "manual" is what
                    // made the Source column unable to explain the score beside it.
                    acceptedModelCode ? MappingSource.Suggested : MappingSource.Manual,
                    modelConfidence, ct);
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

        // ── Phase 2: connection-level price-variance guard ──────────────────────────
        // Catalog price is a SUGGESTION; on a breach we HOLD the line (NeedsReview + reason) so the
        // order stays pending_review — we NEVER mutate the PO UnitPrice. Evaluated here on the
        // RESOLUTION path so a re-resolve re-checks against the current catalog (idempotent: the
        // same divergent price always re-produces the same hold; a corrected price clears it).
        // Org+supplier scoped; the catalog/connection reads are AsNoTracking so they never thrash
        // the tracked order graph.
        var guardConnection = await _db.SupplierConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.OrgId == organisationId && c.SupplierId == entity.SupplierId, ct);
        if (guardConnection is { PriceVarianceGuardEnabled: true })
        {
            var guard   = new PriceVarianceGuard(true, guardConnection.PriceVarianceThresholdPercent);
            var catalog = await OrderServiceShared.BuildCatalogLookupAsync(_db, organisationId, entity.SupplierId ?? Guid.Empty, ct);
            foreach (var line in entity.Lines)
            {
                var product =
                    (!string.IsNullOrWhiteSpace(line.SupplierItemCode) && catalog.TryGetValue(line.SupplierItemCode!, out var byCode)) ? byCode
                    : (!string.IsNullOrWhiteSpace(line.ManufacturerPartNumber) && catalog.TryGetValue(line.ManufacturerPartNumber!, out var byMpn)) ? byMpn
                    : null;
                if (product?.Price is null) continue;

                // The catalog price comes from a TYPED decimal? column → InvariantCulture is correct
                // here (it is NOT a raw locale string; the EU-aware parse only applies to raw input).
                var r = guard.Breaches(line.UnitPrice, product.Price.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (r.Breached)
                {
                    line.NeedsReview  = true;
                    line.ReviewReason =
                        $"Unit price {line.UnitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture)} differs from catalog " +
                        $"{product.Price.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)} by " +
                        $"{decimal.Round(r.VariancePercent, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}% — review before delivery.";
                }
            }
        }

        // Recompute order status (the guard above may have re-set NeedsReview → HOLD).
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

        // The laundering route, and the reason this guard cannot live on ResolveAsync alone: WP-19
        // deliberately gave rejected_by_supplier exits (resolve → pending_review|ready, and the
        // transform claim). Marking a FAILED order rejected would move it into a status with a
        // documented way back to 'ready' — around the guard above — while also asserting something
        // false, since no supplier ever saw a document that did not parse.
        if (IsFinished(entity.Status))
            return Result<PurchaseOrderEntity>.Fail(SourceFileCannotBeFixedHere);

        var now = DateTime.UtcNow;
        entity.Status    = OrderStatusConstants.RejectedBySupplier;
        entity.UpdatedAt = now;

        // A terminal supplier rejection settles the order, so close the SLA window — mirrors the
        // automatic 4xx path in DeliveryService.PersistAttemptAsync. Without this, an order marked
        // rejected while its DeliveryDueAt was still live (e.g. it was mid-delivery) keeps a live
        // deadline, and once it passes the sweep raises a false "delivery overdue" on a settled order.
        entity.DeliveryDueAt = null;
        entity.SlaBreached   = false;

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

        // The status recompute below runs even when nothing is accepted — zero suggestions over zero
        // lines still writes 'ready' — so this needs the same guard as ResolveAsync, not a weaker one.
        if (IsFinished(entity.Status))
            return Result<int>.Fail(SourceFileCannotBeFixedHere);

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
            // The model's real number, promoted so it survives the clearing below. Unchanged in
            // intent — this was already the one honest write to this column, and it is now the
            // ONLY kind of write to it.
            line.Confidence                 = line.AiSuggestionConfidence ?? line.Confidence;
            line.NeedsReview                = false;
            line.ReviewReason               = null; // suggestion accepted — the "why flagged" no longer applies
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
