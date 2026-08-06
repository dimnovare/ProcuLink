using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// Decorates <see cref="IPoMappingService"/> so that saving a supplier's PO mapping cannot leave that
/// supplier's already-transformed orders holding an artifact the new mapping would not have produced.
///
/// <para><b>Why a decorator and not a call at each endpoint.</b> Five production paths write
/// <c>SupplierPoMappings</c> — <c>PUT /api/suppliers/{id}/po-mapping</c>,
/// <c>POST {id}/po-mapping/apply-template</c>, <c>DELETE {id}/po-mapping/output-tree</c>,
/// <c>DELETE {id}/po-mapping</c>, and <c>PromoteMappingService</c> (both
/// <c>PromoteAsync</c> and <c>ClearPromotedOutputTreeAsync</c>) — and every one of them funnels
/// through <see cref="IPoMappingService.UpsertAsync"/> or <see cref="IPoMappingService.DeleteAsync"/>.
/// Wrapping the interface makes the invalidation total by construction: a sixth writer added tomorrow
/// gets it without knowing this type exists. The per-endpoint alternative would need a scanner to
/// prove nobody forgot, and a scanner is a weaker guarantee than having nowhere to forget.</para>
///
/// <para><b>Membership in the status set is necessary, not sufficient.</b> Three further conditions
/// each drop orders the live supplier mapping provably cannot reach — this is a supplier-wide write,
/// so a candidate set that is merely plausible would churn every in-flight order of the supplier on
/// every ordinary save:</para>
///
/// <list type="number">
///   <item><description><b>The <c>Output</c>/<c>OutputTree</c> half must actually have changed.</b>
///     <c>Header</c>/<c>Lines</c> drive <c>PoMappingEngine</c> at PARSE time
///     (<c>OrderIngestionService</c>), and <c>OrderTransformService.TransformAsync</c> loads the
///     persisted order and its lines — it never re-parses. So an inbound-mapping save cannot change
///     what a re-transform emits for an order that is already parsed, and resetting for one would be
///     pure status churn. Editing the mapping editor's four fields is the COMMON save; the output
///     half is the rare one.</description></item>
///   <item><description><b>The order must not be pinned to a published revision.</b>
///     <c>OrderTransformService</c> is explicit that the two sources are mutually exclusive: a pinned
///     order consults ONLY its revision snapshot and an unpinned/flag-off order consults ONLY the live
///     supplier mapping. A pinned order would therefore re-transform to byte-identical output, so a
///     reset buys nothing and costs a status change. The pin is asked of the SAME
///     <see cref="IEffectiveConnectionConfigResolver"/> the transform asks, so the two can never
///     disagree about which orders those are — including when the
///     <c>Connections:RevisionAuthority</c> flag is OFF, where a pinned order DOES read the live table
///     and must be reset.</description></item>
///   <item><description><b>The order must carry no per-order output seam that outranks the supplier
///     layer.</b> A usable per-order template always wins (transform mode 1), and a usable per-order
///     flat <c>Output</c> wins whenever the format supports overrides at all — and when it does not,
///     the supplier layer is refused by the same <c>SupportsOverrideFormat</c> gate, so the supplier
///     mapping is unread either way. Both skips hold for every format, which is why neither needs the
///     effective output format resolved here; duplicating that resolution would be a second copy of a
///     rule that has already drifted elsewhere in this codebase. A per-order <c>OutputTree</c> is
///     deliberately NOT a skip: when its format cannot drive the document the tree is dropped and the
///     supplier layer runs.</description></item>
/// </list>
///
/// <para><b>Ordering is fail-safe, not atomic.</b> The reset commits BEFORE the mapping write, in its
/// own <c>SaveChangesAsync</c>. The two cannot be one transaction without restructuring
/// <see cref="PoMappingService"/>, so the order is chosen for which half-failure is survivable: a
/// crash between them leaves orders reset to <c>ready</c> under the OLD mapping, which re-transforms
/// to the bytes they already had and returns them to <c>ready_to_deliver</c> — churn, no wrong
/// document. The opposite order would leave the NEW mapping saved with the stale artifacts still
/// shippable, which is the defect this type exists to remove.</para>
/// </summary>
public sealed class ArtifactInvalidatingPoMappingService : IPoMappingService
{
    /// <summary>Mirrors <see cref="PoMappingService"/>'s serializer so the change comparison sees the
    /// same text the table round-trips, not an incidentally-different rendering of equal values.</summary>
    private static readonly JsonSerializerOptions ComparisonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly IPoMappingService _inner;
    private readonly ProcuLinkDbContext _db;
    private readonly IEffectiveConnectionConfigResolver _effectiveConfig;
    private readonly ILogger<ArtifactInvalidatingPoMappingService> _logger;

    public ArtifactInvalidatingPoMappingService(
        IPoMappingService inner,
        ProcuLinkDbContext db,
        IEffectiveConnectionConfigResolver effectiveConfig,
        ILogger<ArtifactInvalidatingPoMappingService> logger)
    {
        _inner = inner;
        _db = db;
        _effectiveConfig = effectiveConfig;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<PoMappingConfig?> GetAsync(Guid organisationId, Guid supplierId, CancellationToken ct = default) =>
        _inner.GetAsync(organisationId, supplierId, ct);

    /// <inheritdoc/>
    public async Task<PoMappingConfig> UpsertAsync(
        Guid organisationId, Guid supplierId, PoMappingConfig config, CancellationToken ct = default)
    {
        var existing = await _inner.GetAsync(organisationId, supplierId, ct);

        if (OutputHalfChanged(existing, config))
            await InvalidateStaleArtifactsAsync(organisationId, supplierId, ct);

        return await _inner.UpsertAsync(organisationId, supplierId, config, ct);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(Guid organisationId, Guid supplierId, CancellationToken ct = default)
    {
        var existing = await _inner.GetAsync(organisationId, supplierId, ct);

        // Deleting a mapping that HAD an output half changes what a re-transform emits (it falls back
        // to the fixed transformer), so it invalidates artifacts exactly as an edit does. Deleting one
        // that never had an output half changes nothing the transform reads.
        if (OutputHalfChanged(existing, null))
            await InvalidateStaleArtifactsAsync(organisationId, supplierId, ct);

        await _inner.DeleteAsync(organisationId, supplierId, ct);
    }

    /// <summary>
    /// Did this save change the half of the config the TRANSFORM reads? Compared as serialized text
    /// through the persistence serializer, so an unchanged re-save — which the mapping editor issues
    /// routinely — never resets anything.
    /// </summary>
    private static bool OutputHalfChanged(PoMappingConfig? before, PoMappingConfig? after) =>
        !string.Equals(OutputHalf(before), OutputHalf(after), StringComparison.Ordinal);

    private static string OutputHalf(PoMappingConfig? config) =>
        JsonSerializer.Serialize(
            new { output = config?.Output, outputTree = config?.OutputTree }, ComparisonOptions);

    /// <summary>
    /// Resets this supplier's post-transform orders whose stored artifact the live mapping would no
    /// longer reproduce, so the next Send RE-TRANSFORMS instead of redelivering the pre-edit document.
    /// The old artifact row is deliberately left in place — audit history is preserved exactly as in
    /// the per-order path.
    /// </summary>
    private async Task InvalidateStaleArtifactsAsync(Guid organisationId, Guid supplierId, CancellationToken ct)
    {
        // Materialised to an array so the membership test translates to a SQL IN. EF Core does not
        // translate Contains through the IReadOnlySet<string> static type, and a set that silently
        // fell back to client evaluation would load every order this supplier has.
        var statuses = OrderStatusMachine.SupplierMappingEditInvalidatesArtifactFrom.ToArray();

        // Two passes ON PURPOSE. The pin decides most of the outcome and needs only one nullable Guid
        // per order, while the per-order-seam check needs canonical_json — which is the whole parsed
        // document and the largest column on the row. Deciding the pin first means a supplier whose
        // orders are all pinned (the production shape: revision authority is ON and every supplier
        // with any config surface is given a connection at each API boot) loads NO canonical_json at
        // all on an ordinary mapping save. One tracked load of everything would have made a routine
        // admin click proportional to the supplier's entire in-flight book.
        var candidates = await _db.PurchaseOrders
            .AsNoTracking()
            .Where(o => o.OrgId == organisationId
                        && o.SupplierId == supplierId
                        && statuses.Contains(o.Status))
            .Select(o => new { o.Id, o.ConnectionRevisionId })
            .ToListAsync(ct);

        if (candidates.Count == 0) return;

        // Resolved once per DISTINCT revision, not once per order: the resolver hits the database on
        // every call, and a supplier's in-flight orders share very few revisions.
        var authoritative = new HashSet<Guid>();
        foreach (var revisionId in candidates
                     .Select(c => c.ConnectionRevisionId)
                     .Where(id => id is not null)
                     .Distinct())
        {
            // Pinned → the transform reads the revision snapshot, never this table. Asked of the same
            // resolver the transform asks, so "pinned" means the same thing in both places — including
            // when the flag is OFF, where this correctly returns the live bundle and the order IS reset.
            var effective = await _effectiveConfig.ResolveAsync(organisationId, revisionId, ct);
            if (effective.IsRevision) authoritative.Add(revisionId!.Value);
        }

        var unpinnedIds = candidates
            .Where(c => c.ConnectionRevisionId is null || !authoritative.Contains(c.ConnectionRevisionId.Value))
            .Select(c => c.Id)
            .ToList();

        if (unpinnedIds.Count == 0) return;

        var orders = await _db.PurchaseOrders
            .Where(o => o.OrgId == organisationId && unpinnedIds.Contains(o.Id))
            .ToListAsync(ct);

        var reset = 0;
        foreach (var order in orders)
        {
            // A per-order template or flat output outranks the supplier layer for every format.
            var @override = OrderMappingOverrideReader.Read(order.CanonicalJson);
            if (OrderMappingOverrideReader.HasUsableTemplate(@override)) continue;
            if (OrderMappingOverrideReader.HasUsableOutput(@override)) continue;

            order.Status = OrderStatusConstants.Ready;
            order.UpdatedAt = DateTime.UtcNow;
            reset++;
        }

        if (reset == 0) return;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Supplier {SupplierId} output mapping changed: {Count} order(s) reset to ready so the next send " +
            "re-transforms instead of shipping the pre-edit document.",
            supplierId, reset);
    }
}
