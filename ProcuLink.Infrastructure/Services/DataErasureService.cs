using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure.Services.Ingress;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// Implements <see cref="IDataErasureService"/>. Org-scoped per-order hard erase:
/// the sensitive R2 blobs (PO source + transformed artifacts + any order-confirmation
/// source) are deleted first, then every order-tied DB row in a single SaveChanges.
/// R2 deletes are best-effort (idempotent on a missing key; a transient failure is
/// logged, not fatal). Tenant isolation: the parent order is loaded with an OrgId
/// filter, so every child delete is scoped to that verified order.
///
/// FK note: order_confirmation_lines.purchase_order_line_id is ON DELETE RESTRICT, so
/// confirmation lines + confirmations are removed in the same SaveChanges as (and EF
/// orders them before) the purchase_order_lines they reference — otherwise Postgres
/// would abort the erase.
/// </summary>
public sealed class DataErasureService : IDataErasureService
{
    private readonly ProcuLinkDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly ILogger<DataErasureService> _logger;

    public DataErasureService(
        ProcuLinkDbContext db,
        IFileStorageService storage,
        ILogger<DataErasureService> logger)
    {
        _db = db;
        _storage = storage;
        _logger = logger;
    }

    public async Task<OrderErasureResult> EraseOrderAsync(Guid organisationId, Guid orderId, CancellationToken ct)
    {
        // Org-scoped load — never erase across tenants. Unknown/already-erased = no-op.
        var order = await _db.PurchaseOrders
            .FirstOrDefaultAsync(o => o.Id == orderId && o.OrgId == organisationId, ct);
        if (order is null)
        {
            _logger.LogInformation(
                "Erase order {OrderId} (org {OrgId}): not found — no-op.", orderId, organisationId);
            return new OrderErasureResult(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        var artifacts = await _db.OutboundArtifacts
            .Where(a => a.OrderId == orderId && a.OrgId == organisationId).ToListAsync(ct);
        var lines = await _db.PurchaseOrderLines
            .Where(l => l.OrderId == orderId).ToListAsync(ct);
        var attempts = await _db.DeliveryAttempts
            .Where(a => a.OrderId == orderId && a.OrgId == organisationId).ToListAsync(ct);
        var exceptions = await _db.OrderExceptions
            .Where(e => e.OrderId == orderId && e.OrgId == organisationId).ToListAsync(ct);
        var validations = await _db.OrderValidationResults
            .Where(v => v.OrderId == orderId && v.OrgId == organisationId).ToListAsync(ct);
        var passport = await _db.PoPassportEvents
            .Where(p => p.OrderId == orderId && p.OrgId == organisationId).ToListAsync(ct);
        // The order's audit events are keyed by EntityId == orderId (Guids are unique,
        // so this never matches another entity), org-scoped for defence in depth.
        var audits = await _db.AuditEvents
            .Where(e => e.EntityId == orderId && e.OrgId == organisationId).ToListAsync(ct);
        // AI suggestion decisions carry real buyer/supplier item codes (Suggested/Chosen
        // codes + the full CandidateSetJson) — sensitive PO content that must not survive
        // the erase. Idempotency keys map back to the erased order id.
        var aiDecisions = await _db.AiSuggestionDecisions
            .Where(d => d.OrderId == orderId && d.OrgId == organisationId).ToListAsync(ct);
        var idempotencyKeys = await _db.IdempotencyKeys
            .Where(k => k.OrderId == orderId && k.OrgId == organisationId).ToListAsync(ct);
        // Ranked supplier candidates offered for this order. Unlike every other child above,
        // order_supplier_suggestions has NO foreign key to purchase_orders — its only FK is to
        // organisations (ProcuLinkDbContext OrderSupplierSuggestion mapping; migration
        // 20260726094835_AddSupplierAutoDetect) — so deleting the order neither cascades to it
        // nor errors. Left behind, each row keeps a dangling OrderId plus SignalsJson, which
        // embeds document-derived identity text ("the order arrived from their email domain
        // {domain}", SupplierSuggestionService.SenderDomainContribution), and DecidedBy, the
        // Clerk user id of the operator who ruled on it. That is erasable order content, so it
        // is deleted here explicitly. Nothing else in the tree removes these rows: neither
        // retention sweep touches this table.
        var supplierSuggestions = await _db.OrderSupplierSuggestions
            .Where(s => s.OrderId == orderId && s.OrgId == organisationId).ToListAsync(ct);
        // Order confirmations (inbound supplier confirmations) + their lines are tied to
        // this order and hold sensitive PO content (item codes/qty/price/notes + their own
        // R2 source). Lines have a RESTRICT FK onto purchase_order_lines, so they MUST go.
        var confirmations = await _db.OrderConfirmations
            .Where(c => c.PurchaseOrderId == orderId && c.OrgId == organisationId).ToListAsync(ct);
        var confirmationIds = confirmations.Select(c => c.Id).ToList();
        var confirmationLines = await _db.OrderConfirmationLines
            .Where(cl => cl.OrgId == organisationId && confirmationIds.Contains(cl.OrderConfirmationId))
            .ToListAsync(ct);
        // IMAP-ingest ledger rows for this order carry the attachment file name +
        // IMAP Message-Id — orphan PII if left behind after the order is erased. These are safe to
        // DELETE outright: the IMAP message is flagged SEEN on the mail server, so it is never
        // re-fetched and cannot re-import.
        var emailImportRecords = await _db.EmailImportRecords
            .Where(r => r.OrderId == orderId && r.OrgId == organisationId).ToListAsync(ct);
        // SFTP/S3 pull-ingress ledger rows are DIFFERENT: the source file still lives on the
        // customer's SFTP/S3 (ProcuLink never deletes it) and the poller lists it every cycle. The
        // row must NOT be deleted (the file would re-import as a NEW order) and must NOT keep its
        // real, now-dangling OrderId (the poller's resume-on-conflict would see the order missing and
        // RESURRECT the erased order — a duplicate-PO defect AND a right-to-erasure breach). Instead
        // TOMBSTONE it: OrderId = Guid.Empty makes IngressDedupe.ClaimSatisfiedAsync return satisfied,
        // so the file is permanently skipped and the erased order can never resurrect. Loaded +
        // mutated (not ExecuteUpdate) so this is included in the same SaveChanges as the order delete
        // and stays translatable on the EF InMemory test provider.
        var sftpLedger = await _db.Set<Core.Entities.ImportedSftpFile>()
            .Where(f => f.OrderId == orderId && f.OrgId == organisationId).ToListAsync(ct);
        var s3Ledger = await _db.Set<Core.Entities.ImportedS3Object>()
            .Where(f => f.OrderId == orderId && f.OrgId == organisationId).ToListAsync(ct);

        // ── 1. Delete the sensitive R2 blobs first (idempotent; best-effort) ──────
        var r2Keys = new List<string>();
        if (!string.IsNullOrWhiteSpace(order.SourceFileKey)) r2Keys.Add(order.SourceFileKey);
        r2Keys.AddRange(artifacts.Select(a => a.FileKey).Where(k => !string.IsNullOrWhiteSpace(k)));
        r2Keys.AddRange(confirmations.Where(c => !string.IsNullOrWhiteSpace(c.SourceFileKey)).Select(c => c.SourceFileKey!));

        var r2Deleted = 0;
        foreach (var key in r2Keys.Distinct())
        {
            try
            {
                await _storage.DeleteAsync(key, ct);
                r2Deleted++;
            }
            catch (Exception ex)
            {
                // Don't fail the whole erase on a single storage hiccup — the DB rows
                // still go; surface the orphaned key for manual/sweep cleanup.
                _logger.LogError(ex,
                    "Erase order {OrderId} (org {OrgId}): failed to delete R2 key {Key}.",
                    orderId, organisationId, key);
            }
        }

        // ── 2. Delete DB rows. Confirmation lines/headers commit FIRST: their
        // purchase_order_line_id FK is ON DELETE RESTRICT, so they must be gone
        // before the purchase_order_lines they reference. A separate SaveChanges
        // guarantees that order regardless of how EF models the FK. ───────────────
        _db.OrderConfirmationLines.RemoveRange(confirmationLines);
        _db.OrderConfirmations.RemoveRange(confirmations);
        if (confirmationLines.Count > 0 || confirmations.Count > 0)
            await _db.SaveChangesAsync(ct);

        _db.PurchaseOrderLines.RemoveRange(lines);
        _db.OutboundArtifacts.RemoveRange(artifacts);
        _db.DeliveryAttempts.RemoveRange(attempts);
        _db.OrderExceptions.RemoveRange(exceptions);
        _db.OrderValidationResults.RemoveRange(validations);
        _db.PoPassportEvents.RemoveRange(passport);
        _db.AuditEvents.RemoveRange(audits);
        _db.AiSuggestionDecisions.RemoveRange(aiDecisions);
        _db.IdempotencyKeys.RemoveRange(idempotencyKeys);
        _db.OrderSupplierSuggestions.RemoveRange(supplierSuggestions);
        _db.EmailImportRecords.RemoveRange(emailImportRecords);
        // Tombstone the SFTP/S3 pull-ingress ledger rows (see rationale above): keep the row so the
        // file is not re-imported, but sever its link to the erased order so it is never resurrected.
        foreach (var row in sftpLedger) row.OrderId = IngressDedupe.TerminalOrderId;
        foreach (var row in s3Ledger)   row.OrderId = IngressDedupe.TerminalOrderId;
        _db.PurchaseOrders.Remove(order);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Erased order {OrderId} (org {OrgId}): R2={R2} lines={Lines} artifacts={Artifacts} " +
            "attempts={Attempts} exceptions={Exceptions} validations={Validations} passport={Passport} " +
            "audit={Audit} confirmations={Confirmations} confirmationLines={ConfirmationLines} " +
            "aiDecisions={AiDecisions} idempotencyKeys={IdempotencyKeys} emailImportRecords={EmailImportRecords} " +
            "supplierSuggestions={SupplierSuggestions} " +
            "sftpLedgerTombstoned={SftpLedger} s3LedgerTombstoned={S3Ledger}.",
            orderId, organisationId, r2Deleted, lines.Count, artifacts.Count, attempts.Count,
            exceptions.Count, validations.Count, passport.Count, audits.Count,
            confirmations.Count, confirmationLines.Count, aiDecisions.Count, idempotencyKeys.Count,
            emailImportRecords.Count, supplierSuggestions.Count, sftpLedger.Count, s3Ledger.Count);

        return new OrderErasureResult(
            Found: true,
            R2ObjectsDeleted: r2Deleted,
            LinesDeleted: lines.Count,
            ArtifactsDeleted: artifacts.Count,
            DeliveryAttemptsDeleted: attempts.Count,
            ExceptionsDeleted: exceptions.Count,
            ValidationResultsDeleted: validations.Count,
            PassportEventsDeleted: passport.Count,
            AuditEventsDeleted: audits.Count,
            ConfirmationsDeleted: confirmations.Count,
            ConfirmationLinesDeleted: confirmationLines.Count,
            AiSuggestionDecisionsDeleted: aiDecisions.Count,
            IdempotencyKeysDeleted: idempotencyKeys.Count,
            EmailImportRecordsDeleted: emailImportRecords.Count,
            SupplierSuggestionsDeleted: supplierSuggestions.Count);
    }

    public async Task<BulkOrderErasureResult> BulkEraseOrdersAsync(
        Guid organisationId, BulkEraseFilter filter, CancellationToken ct)
    {
        // Org guard FIRST and ALWAYS: the org id is the base of the query, so every
        // criterion below only ever narrows WITHIN this tenant. A filter (even one
        // carrying another org's Ids) can never reach across tenants.
        var query = _db.PurchaseOrders.Where(o => o.OrgId == organisationId);

        // A blank/whitespace prefix or status, or an empty Ids list, is "not supplied".
        // Critically this stops PoNumberPrefix="" from StartsWith-matching the whole org.
        var prefix = string.IsNullOrWhiteSpace(filter.PoNumberPrefix) ? null : filter.PoNumberPrefix;
        var status = string.IsNullOrWhiteSpace(filter.Status) ? null : filter.Status;
        var ids = filter.Ids is { Count: > 0 } ? filter.Ids : null;
        // Normalise to UTC Kind: a DateTime deserialised from JSON is Kind=Unspecified,
        // which Npgsql refuses to write to a `timestamptz` column (would 500 in prod;
        // InMemory tests wouldn't catch it). Same guard as AdminController.SetOrganisationLimits.
        var olderThan = filter.OlderThan is { } ot
            ? DateTime.SpecifyKind(ot, DateTimeKind.Utc)
            : (DateTime?)null;

        var hasCriterion = prefix is not null || status is not null || ids is not null || olderThan is not null;
        if (!hasCriterion)
        {
            // No active criterion ⇒ match nothing. NEVER interpret an empty filter as
            // "erase the whole org" — that is the one mistake this method must not make.
            _logger.LogWarning(
                "Bulk erase (org {OrgId}): empty filter — matched nothing, erased nothing.", organisationId);
            return new BulkOrderErasureResult(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        if (prefix is not null)    query = query.Where(o => o.PoNumber.StartsWith(prefix));
        if (status is not null)    query = query.Where(o => o.Status == status);
        if (ids is not null)       query = query.Where(o => ids.Contains(o.Id));
        if (olderThan is { } cutoff) query = query.Where(o => o.CreatedAt < cutoff);

        var matchedIds = await query.Select(o => o.Id).ToListAsync(ct);

        _logger.LogWarning(
            "Bulk erase (org {OrgId}): {Count} orders matched (prefix={Prefix} status={Status} ids={IdCount} olderThan={OlderThan}) — erasing.",
            organisationId, matchedIds.Count, prefix, status, ids?.Count, olderThan);

        // Reuse the per-order erase so R2 blobs + every child table + the confirmation
        // FK-ordering are handled identically to the single-order path. Each order is
        // its own atomic erase; we sum the counts for the batch result. (Idempotent: a
        // concurrently-erased order just returns Found=false and is skipped.)
        var result = new BulkOrderErasureResult(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        foreach (var id in matchedIds)
        {
            ct.ThrowIfCancellationRequested();
            var r = await EraseOrderAsync(organisationId, id, ct);
            if (!r.Found) continue;
            result = result with
            {
                OrdersErased             = result.OrdersErased + 1,
                R2ObjectsDeleted         = result.R2ObjectsDeleted + r.R2ObjectsDeleted,
                LinesDeleted             = result.LinesDeleted + r.LinesDeleted,
                ArtifactsDeleted         = result.ArtifactsDeleted + r.ArtifactsDeleted,
                DeliveryAttemptsDeleted  = result.DeliveryAttemptsDeleted + r.DeliveryAttemptsDeleted,
                ExceptionsDeleted        = result.ExceptionsDeleted + r.ExceptionsDeleted,
                ValidationResultsDeleted = result.ValidationResultsDeleted + r.ValidationResultsDeleted,
                PassportEventsDeleted    = result.PassportEventsDeleted + r.PassportEventsDeleted,
                AuditEventsDeleted       = result.AuditEventsDeleted + r.AuditEventsDeleted,
                ConfirmationsDeleted     = result.ConfirmationsDeleted + r.ConfirmationsDeleted,
                ConfirmationLinesDeleted = result.ConfirmationLinesDeleted + r.ConfirmationLinesDeleted,
                AiSuggestionDecisionsDeleted = result.AiSuggestionDecisionsDeleted + r.AiSuggestionDecisionsDeleted,
                IdempotencyKeysDeleted       = result.IdempotencyKeysDeleted + r.IdempotencyKeysDeleted,
                EmailImportRecordsDeleted    = result.EmailImportRecordsDeleted + r.EmailImportRecordsDeleted,
                SupplierSuggestionsDeleted   = result.SupplierSuggestionsDeleted + r.SupplierSuggestionsDeleted,
            };
        }

        _logger.LogWarning(
            "Bulk erase (org {OrgId}): erased {Erased}/{Matched} orders.",
            organisationId, result.OrdersErased, matchedIds.Count);

        return result;
    }
}
