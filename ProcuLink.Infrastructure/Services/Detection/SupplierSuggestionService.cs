using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Catalog;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Detection;

namespace ProcuLink.Infrastructure.Services.Detection;

/// <inheritdoc />
/// <remarks>
/// Reads only: the org's suppliers, its schema fingerprints, its supplier catalog and its own
/// earlier orders' sender domains. Every query filters on <c>OrgId</c> — a supplier suggestion
/// that crossed tenants would leak the existence of another organisation's counterparties.
/// </remarks>
public sealed class SupplierSuggestionService : ISupplierSuggestionService
{
    /// <summary>Recorded as <c>decided_by</c> when a re-scoring supersedes a suggestion nobody acted on.</summary>
    private const string SupersededBy = "system";

    private readonly ProcuLinkDbContext _db;
    private readonly ISchemaFingerprintService _fingerprints;
    private readonly ILogger<SupplierSuggestionService> _logger;

    public SupplierSuggestionService(
        ProcuLinkDbContext db,
        ISchemaFingerprintService fingerprints,
        ILogger<SupplierSuggestionService> logger)
    {
        _db = db;
        _fingerprints = fingerprints;
        _logger = logger;
    }

    /// <summary>A candidate supplier reduced to the fields the scorer compares against.</summary>
    private sealed record Candidate(
        Guid Id, string Name, string? VatNumber, string? RegistrationNumber, string? EdiCode, string? PrimaryDomain);

    public async Task<IReadOnlyList<SupplierSuggestion>> SuggestAsync(
        SupplierSuggestionInput input, CancellationToken ct)
    {
        var orgId = input.OrganisationId;

        // Sample suppliers are onboarding scaffolding, soft-deleted ones are gone; neither is
        // somewhere a real order should be routed, so neither belongs in an operator's shortlist.
        var candidates = await _db.Suppliers
            .AsNoTracking()
            .Where(s => s.OrgId == orgId && s.DeletedAt == null && !s.IsSample)
            .Select(s => new Candidate(s.Id, s.Name, s.VatNumber, s.RegistrationNumber, s.EdiCode, s.PrimaryDomain))
            .ToListAsync(ct);

        if (candidates.Count == 0) return Array.Empty<SupplierSuggestion>();

        var layout        = await LayoutContributionsAsync(orgId, input.ColumnHeaders, candidates, ct);
        var catalog       = await CatalogOverlapContributionsAsync(orgId, input.LineCodes, ct);
        var senderDomain  = SupplierSuggestionScoring.NormalizeDomain(input.InboundSenderDomain);
        var domainHistory = await SenderDomainHistoryContributionsAsync(orgId, input.OrderId, senderDomain, ct);

        var scored = new List<(Guid, string, IReadOnlyList<SupplierSignalContribution>)>();

        foreach (var candidate in candidates)
        {
            // Canonical order — strongest evidence first, so the provenance list and the generated
            // sentence read the same way every time.
            var signals = new List<SupplierSignalContribution>();

            if (IdentityContribution(candidate, input.Parties) is { } identity) signals.Add(identity);
            if (SenderDomainContribution(candidate, senderDomain) is { } domain) signals.Add(domain);
            if (domainHistory.TryGetValue(candidate.Id, out var history)) signals.Add(history);
            if (layout.TryGetValue(candidate.Id, out var fingerprint)) signals.Add(fingerprint);
            if (catalog.TryGetValue(candidate.Id, out var overlap)) signals.Add(overlap);
            if (NameContribution(candidate, input) is { } name) signals.Add(name);

            if (signals.Count > 0) scored.Add((candidate.Id, candidate.Name, signals));
        }

        var ranked = SupplierSuggestionScoring.Rank(scored);

        if (ranked.Count > 0)
            _logger.LogInformation(
                "Supplier auto-detect: {Count} candidate(s) for order {OrderId} (org {OrgId}); best={Supplier} at {Score:0.00}.",
                ranked.Count, input.OrderId, orgId, ranked[0].SupplierName, ranked[0].Score);

        return ranked;
    }

    // ── Signals ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A registration identifier printed anywhere on the document that matches this supplier's.
    ///
    /// <para>Role-agnostic on purpose: the data model is direction-agnostic, so the counterparty is
    /// the <c>supplier</c> party on an outbound PO and the <c>buyer</c> party on an inbound customer
    /// one. A VAT number identifies the same company either way.</para>
    ///
    /// <para>Fires AT MOST ONCE. A supplier whose VAT, registry code and EDI code all match is not
    /// three times as likely to be the right one — it is the same fact recorded three ways.</para>
    /// </summary>
    private static SupplierSignalContribution? IdentityContribution(
        Candidate candidate, IReadOnlyList<SupplierSuggestionParty>? parties)
    {
        if (parties is null || parties.Count == 0) return null;

        foreach (var party in parties)
        {
            // VAT and registration numbers are cross-compared because in much of the EU one is
            // derived from the other and the two get entered into whichever field is at hand.
            // The EDI code is not: a GLN is a different kind of identifier, not a spelling of these.
            if (SupplierSuggestionScoring.IdentifiersMatch(party.Vat, candidate.VatNumber)
                || SupplierSuggestionScoring.IdentifiersMatch(party.Vat, candidate.RegistrationNumber))
                return new SupplierSignalContribution(
                    SupplierSignalKind.Identity, SupplierSuggestionScoring.IdentityMatchWeight,
                    "the VAT number on the document is theirs");

            if (SupplierSuggestionScoring.IdentifiersMatch(party.RegNr, candidate.RegistrationNumber)
                || SupplierSuggestionScoring.IdentifiersMatch(party.RegNr, candidate.VatNumber))
                return new SupplierSignalContribution(
                    SupplierSignalKind.Identity, SupplierSuggestionScoring.IdentityMatchWeight,
                    "the company registration number on the document is theirs");

            if (SupplierSuggestionScoring.IdentifiersMatch(party.EdiCode, candidate.EdiCode))
                return new SupplierSignalContribution(
                    SupplierSignalKind.Identity, SupplierSuggestionScoring.IdentityMatchWeight,
                    "the EDI code on the document is theirs");
        }

        return null;
    }

    private static SupplierSignalContribution? SenderDomainContribution(Candidate candidate, string? senderDomain)
    {
        if (senderDomain is null) return null;

        var supplierDomain = SupplierSuggestionScoring.NormalizeDomain(candidate.PrimaryDomain);
        if (supplierDomain is null || !string.Equals(supplierDomain, senderDomain, StringComparison.Ordinal))
            return null;

        return new SupplierSignalContribution(
            SupplierSignalKind.SenderDomain, SupplierSuggestionScoring.SenderDomainWeight,
            $"the order arrived from their email domain {senderDomain}");
    }

    /// <summary>
    /// Name equality after normalisation. Read from the document's supplier header and its
    /// <c>supplier</c>-role party only — deliberately NOT the buyer party, which on the default
    /// outbound direction is the organisation itself and would match nothing useful. An inbound-
    /// direction org whose counterparty appears as the buyer still reaches them through the
    /// identity and domain signals; widening this needs measurement, not a guess.
    /// </summary>
    private static SupplierSignalContribution? NameContribution(Candidate candidate, SupplierSuggestionInput input)
    {
        var supplierName = SupplierSuggestionScoring.NormalizeCompanyName(candidate.Name);
        if (supplierName is null) return null;

        var documentNames = new List<string?> { input.DocumentSupplierName };
        if (input.Parties is not null)
            documentNames.AddRange(input.Parties
                .Where(p => string.Equals(p.Role, "supplier", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Name));

        var matched = documentNames
            .Select(SupplierSuggestionScoring.NormalizeCompanyName)
            .Any(n => n is not null && string.Equals(n, supplierName, StringComparison.Ordinal));

        return matched
            ? new SupplierSignalContribution(
                SupplierSignalKind.Name, SupplierSuggestionScoring.NameMatchWeight,
                "the supplier name on the document matches theirs")
            : null;
    }

    /// <summary>
    /// Suppliers whose orders have used this exact column layout before.
    ///
    /// <para>When the layout is bound to several LIVE candidates the weight is split evenly between
    /// them (founder ruling D4). Even means two things at once: every bound supplier is offered, and
    /// none of them can be separated from the others by this signal — which is what the fingerprint
    /// entity demands of a layout collision. Splitting rather than repeating the full weight also
    /// says the honest thing: a layout three suppliers share is weaker evidence for each of them.</para>
    /// </summary>
    private async Task<Dictionary<Guid, SupplierSignalContribution>> LayoutContributionsAsync(
        Guid orgId, IReadOnlyList<string>? columnHeaders, IReadOnlyList<Candidate> candidates, CancellationToken ct)
    {
        var result = new Dictionary<Guid, SupplierSignalContribution>();
        if (columnHeaders is null || columnHeaders.Count == 0) return result;

        var match = await _fingerprints.LookupAsync(orgId, columnHeaders, ct);
        if (match is null || match.SupplierIds.Count == 0) return result;

        // Only bindings to suppliers that are still live candidates count — a layout bound to a
        // since-deleted supplier must not dilute the weight of the ones that remain.
        var live = candidates.Select(c => c.Id).ToHashSet();
        var bound = match.SupplierIds.Where(live.Contains).ToList();
        if (bound.Count == 0) return result;

        var contribution = SupplierSuggestionScoring.LayoutWeight / bound.Count;
        var detail = bound.Count == 1
            ? "this file's column layout has only ever been used by them"
            : $"this file's column layout is shared by {bound.Count} of your suppliers, including them";

        foreach (var supplierId in bound)
            result[supplierId] = new SupplierSignalContribution(SupplierSignalKind.Layout, contribution, detail);

        return result;
    }

    /// <summary>
    /// How much of the document's item codes each supplier actually sells.
    ///
    /// <para>This is the one query in the feature with a new ACCESS PATTERN: every other catalog read
    /// is scoped to a known (org, supplier), and this asks "which suppliers sell these codes?" with
    /// the supplier unconstrained. It runs against the <c>(org_id, code)</c> index added alongside
    /// this feature; without that index Postgres scans the org's whole catalog slice.</para>
    /// </summary>
    private async Task<Dictionary<Guid, SupplierSignalContribution>> CatalogOverlapContributionsAsync(
        Guid orgId, IReadOnlyList<string>? lineCodes, CancellationToken ct)
    {
        var result = new Dictionary<Guid, SupplierSignalContribution>();
        if (lineCodes is null || lineCodes.Count == 0) return result;

        // The same code on twenty lines is one piece of evidence. Distinct first, then capped, so a
        // long order cannot turn one suggestion into a several-hundred-term IN clause.
        //
        // The case rule is ItemCodeComparison — the SAME member OrderServiceShared.BuildCatalogLookupAsync
        // uses to resolve a line against THIS VERY COLUMN. An ordinal probe here disagreed with the
        // resolver: a supplier whose ERP exports lower-case scored ZERO on auto-detect while
        // resolving perfectly once routed, so the order parked `unrouted` looking exactly like "we
        // do not recognise this supplier". Nothing errored, nothing logged — the worst shape of
        // failure this feature can have. Pinned by
        // SupplierSuggestionServiceTests.SuggestAsync_catalogOverlap_appliesTheSameCaseRuleAsTheCatalogLookup.
        var codes = lineCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(ItemCodeComparison.Comparer)
            .Take(SupplierSuggestionScoring.MaxProbedLineCodes)
            .ToList();

        if (codes.Count == 0) return result;

        // Folded to the SQL-comparable form so `lower(code) IN (…)` matches whatever casing the
        // catalog stored. Key() lower-cases exactly the way Npgsql translates `.ToLower()`.
        var foldedCodes = codes.Select(ItemCodeComparison.Key).ToList();

        var hits = await _db.SupplierProducts
            .AsNoTracking()
            .Where(p => p.OrgId == orgId
                     && (codes.Contains(p.Code) || foldedCodes.Contains(p.Code.ToLower())))
            .Select(p => new { p.SupplierId, p.Code })
            .Distinct()
            .ToListAsync(ct);

        foreach (var group in hits.GroupBy(h => h.SupplierId))
        {
            // Case-variant catalog rows for one code are ONE match, not two — otherwise a supplier
            // holding both "AAA" and "aaa" could score a fraction above 1.
            var matched = group.Select(h => h.Code).Distinct(ItemCodeComparison.Comparer).Count();
            var fraction = (double)matched / codes.Count;

            result[group.Key] = new SupplierSignalContribution(
                SupplierSignalKind.CatalogOverlap,
                SupplierSuggestionScoring.CatalogOverlapWeight * fraction,
                $"{matched} of {codes.Count} item codes on this order are in their catalog");
        }

        return result;
    }

    /// <summary>
    /// Where this organisation's EARLIER orders from the same sender domain were routed. The
    /// order being scored is excluded — an order must never be evidence for itself.
    /// </summary>
    private async Task<Dictionary<Guid, SupplierSignalContribution>> SenderDomainHistoryContributionsAsync(
        Guid orgId, Guid orderId, string? senderDomain, CancellationToken ct)
    {
        var result = new Dictionary<Guid, SupplierSignalContribution>();
        if (senderDomain is null) return result;

        var history = await _db.PurchaseOrders
            .AsNoTracking()
            .Where(o => o.OrgId == orgId
                     && o.Id != orderId
                     && o.SupplierId != null
                     && o.InboundSenderDomain == senderDomain)
            .GroupBy(o => o.SupplierId!.Value)
            .Select(g => new { SupplierId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var total = history.Sum(h => h.Count);
        if (total == 0) return result;

        foreach (var row in history)
        {
            var share = (double)row.Count / total;
            result[row.SupplierId] = new SupplierSignalContribution(
                SupplierSignalKind.SenderDomainHistory,
                SupplierSuggestionScoring.SenderDomainHistoryWeight * share,
                row.Count == 1
                    ? $"1 earlier order from {senderDomain} was routed to them"
                    : $"{row.Count} earlier orders from {senderDomain} were routed to them");
        }

        return result;
    }

    // ── Recording ─────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task RecordAsync(
        Guid organisationId, Guid orderId, IReadOnlyList<SupplierSuggestion> suggestions, CancellationToken ct)
    {
        var live = await _db.OrderSupplierSuggestions
            .Where(s => s.OrgId == organisationId && s.OrderId == orderId && s.Decision == null)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var incoming = suggestions.ToDictionary(s => s.SupplierId);

        foreach (var existing in live)
        {
            if (incoming.TryGetValue(existing.SupplierId, out var refreshed))
            {
                // Still the same live suggestion, re-scored — refresh it in place rather than
                // superseding and re-inserting. Besides being the truer description, it keeps this
                // method from ever asking the database to hold two undecided rows for one
                // (org, order, supplier), which the partial unique index forbids and which a
                // supersede-then-insert would attempt inside a single SaveChanges.
                // Pinned by SupplierSuggestionRescorePostgresTests against the real index.
                existing.Rank        = refreshed.Rank;
                existing.Score       = refreshed.Score;
                existing.SignalsJson = SerializeSignals(refreshed.Signals);
                existing.ModelVersion = SupplierSuggestionScoring.ModelVersion;
                incoming.Remove(existing.SupplierId);
            }
            else
            {
                // No longer a candidate. Superseded, never deleted — the fact that we once offered
                // this supplier is part of the record even though nobody acted on it.
                existing.Decision  = OrderSupplierSuggestionDecision.Superseded;
                existing.DecidedBy = SupersededBy;
                existing.DecidedAt = now;
            }
        }

        foreach (var suggestion in incoming.Values)
            _db.OrderSupplierSuggestions.Add(new OrderSupplierSuggestion
            {
                Id           = Guid.NewGuid(),
                OrgId        = organisationId,
                OrderId      = orderId,
                SupplierId   = suggestion.SupplierId,
                Rank         = suggestion.Rank,
                Score        = suggestion.Score,
                SignalsJson  = SerializeSignals(suggestion.Signals),
                ModelVersion = SupplierSuggestionScoring.ModelVersion,
                Decision     = null,
                CreatedAt    = now,
            });

        // No SaveChangesAsync — see the interface contract. The parse path commits these with the
        // order's status flip and lines, inside its own transaction, or not at all.
    }

    private static string SerializeSignals(IReadOnlyList<SupplierSignalContribution> signals) =>
        JsonSerializer.Serialize(signals);
}
