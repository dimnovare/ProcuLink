using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// EF Core implementation of <see cref="IAiSuggestionDecisionService"/>.
/// Every query is org-scoped — never cross-tenant. Writes are made idempotent in
/// application code (read-then-update/insert keyed on the unique index columns) so the
/// behaviour is identical on Npgsql and the EF InMemory provider used by the tests, and a
/// Hangfire retry / double-submit refreshes the existing row rather than duplicating it.
/// </summary>
public sealed class AiSuggestionDecisionService : IAiSuggestionDecisionService
{
    private readonly ProcuLinkDbContext _db;

    public AiSuggestionDecisionService(ProcuLinkDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc/>
    public Task RecordAsync(Guid orgId, Guid orderId, AiSuggestionDecisionRecord record, CancellationToken ct)
        => RecordManyAsync(orgId, orderId, new[] { record }, ct);

    /// <inheritdoc/>
    public async Task RecordManyAsync(
        Guid orgId, Guid orderId, IEnumerable<AiSuggestionDecisionRecord> records, CancellationToken ct)
    {
        var drafts = records as IReadOnlyList<AiSuggestionDecisionRecord> ?? records.ToList();
        if (drafts.Count == 0)
            return;

        var now = DateTime.UtcNow;

        // Pre-load the existing rows for this (org, order) so a replay updates in place.
        // Bounded: one order's decision history is small.
        var existing = await _db.AiSuggestionDecisions
            .Where(d => d.OrgId == orgId && d.OrderId == orderId)
            .ToListAsync(ct);

        foreach (var r in drafts)
        {
            var suggested = r.SuggestedSupplierItemCode ?? string.Empty;

            // Idempotency key mirrors the unique index: (org, order, line, suggested code, decision).
            var match = existing.FirstOrDefault(d =>
                d.LineNumber == r.LineNumber
                && d.SuggestedSupplierItemCode == suggested
                && d.Decision == r.Decision);

            if (match is not null)
            {
                // Refresh the mutable outcome fields; keep the row identity stable.
                match.ChosenSupplierItemCode = r.ChosenSupplierItemCode;
                match.CandidateSetJson       = r.CandidateSetJson;
                match.Confidence             = r.Confidence;
                match.ModelVersion           = r.ModelVersion;
                match.DecidedBy              = r.DecidedBy;
                match.DecidedAt             = now;
            }
            else
            {
                var row = new AiSuggestionDecision
                {
                    Id                        = Guid.NewGuid(),
                    OrgId                     = orgId,
                    OrderId                   = orderId,
                    LineNumber                = r.LineNumber,
                    SuggestedSupplierItemCode = suggested,
                    ChosenSupplierItemCode    = r.ChosenSupplierItemCode,
                    CandidateSetJson          = r.CandidateSetJson,
                    Confidence                = r.Confidence,
                    ModelVersion              = r.ModelVersion,
                    Decision                  = r.Decision,
                    DecidedBy                 = r.DecidedBy,
                    DecidedAt                 = now,
                };
                _db.AiSuggestionDecisions.Add(row);
                existing.Add(row); // guard against duplicates within the same batch
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AiSuggestionDecision>> ListForOrderAsync(
        Guid orgId, Guid orderId, CancellationToken ct)
    {
        return await _db.AiSuggestionDecisions
            .AsNoTracking()
            .Where(d => d.OrgId == orgId && d.OrderId == orderId)
            .OrderByDescending(d => d.DecidedAt)
            .ThenBy(d => d.LineNumber)
            .ToListAsync(ct);
    }
}
