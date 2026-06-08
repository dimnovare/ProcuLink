using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Services.Mapping;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// EF Core implementation of <see cref="IPromoteMappingService"/>.
///
/// Translation logic (SourceMap → PoMappingConfig):
/// <list type="bullet">
///   <item><see cref="SourceFieldRule.SourceToken"/> → <see cref="FieldMappingEntry.ExternalField"/>
///        (a stable source-column token id, e.g. the CSV column header name or XPath). When the
///        token contains a "cell:" / XPath prefix from the tokeniser, the full value is stored as
///        ExternalField because <see cref="PoMappingEngine"/> performs a dictionary-key lookup
///        against the tokenised header row — the same stable identifier must be used both times.
///   </item>
///   <item><see cref="SourceFieldRule.FixedValue"/> → <see cref="FieldMappingEntry.FixedValue"/>
///        (constant used when no external column exists).
///   </item>
///   <item><see cref="SourceFieldRule.Manipulators"/> → <see cref="FieldMappingEntry.FieldManipulators"/>
///        (verbatim copy — both types use the same <see cref="ManipulatorEntry"/> record).
///   </item>
/// </list>
///
/// Canonical field routing:
/// <list type="bullet">
///   <item>Header fields (PoNumber, OrderDate, BuyerName, Currency, SupplierName) → <see cref="PoMappingConfig.Header"/>.</item>
///   <item>Line fields (LineNumber, BuyerItemCode, SupplierItemCode, Description, Quantity, Unit, UnitPrice, LineTotal) → <see cref="PoMappingConfig.Lines"/>.</item>
///   <item>Unknown field names are silently skipped — future canonical-field additions won't break promotion.</item>
/// </list>
///
/// TODO(output-promote): The <see cref="OrderMappingOverride.Output"/> side (canonical→output-field
/// re-mapping) is not promoted here. <see cref="PoMappingConfig"/> models only the inbound
/// (source→canonical) direction. Persisting the output mapping requires a separate per-supplier
/// output config concept (a <c>SupplierOutputMapping</c> entity analogous to <c>SupplierPoMapping</c>)
/// that does not yet exist. Until that entity ships, re-promote the order override each time or
/// keep the per-order override. The SourceMap promotion alone eliminates the bulk of manual
/// re-entry on repeat layouts.
/// </summary>
public sealed class PromoteMappingService : IPromoteMappingService
{
    /// <summary>
    /// Canonical header-scoped field names recognised by <see cref="PoMappingEngine"/> and the
    /// fixed transformers. Keys that appear in a <see cref="OrderMappingOverride.SourceMap"/> and
    /// match one of these names are written into <see cref="PoMappingConfig.Header"/>.
    /// </summary>
    private static readonly HashSet<string> HeaderFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "PoNumber", "OrderDate", "BuyerName", "Currency", "SupplierName",
    };

    /// <summary>
    /// Canonical line-scoped field names. Keys in the SourceMap that match are written into
    /// <see cref="PoMappingConfig.Lines"/>.
    /// </summary>
    private static readonly HashSet<string> LineFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "LineNumber", "BuyerItemCode", "SupplierItemCode", "Description",
        "Quantity", "Unit", "UnitPrice", "LineTotal",
    };

    private readonly ProcuLinkDbContext _db;
    private readonly IPoMappingService _poMappingService;

    public PromoteMappingService(ProcuLinkDbContext db, IPoMappingService poMappingService)
    {
        _db = db;
        _poMappingService = poMappingService;
    }

    /// <inheritdoc/>
    public async Task<PromoteMappingResult?> PromoteAsync(
        Guid orgId, Guid orderId, CancellationToken ct)
    {
        // Org-scoped load — only the columns we need to avoid pulling the full canonical_json graph.
        var orderRow = await _db.PurchaseOrders
            .AsNoTracking()
            .Where(o => o.Id == orderId && o.OrgId == orgId)
            .Select(o => new
            {
                o.SupplierId,
                o.CanonicalJson,
                o.SchemaFingerprintHash,
            })
            .FirstOrDefaultAsync(ct);

        // Unknown or cross-tenant order → caller maps to 404.
        if (orderRow is null)
            return null;

        // Read the stored SourceMap from canonical_json (null = no override set yet).
        var @override = OrderMappingOverrideReader.Read(orderRow.CanonicalJson);
        var sourceMap = @override?.SourceMap;

        // Load the existing supplier mapping (or start from an empty config) so we can merge.
        var existing = await _poMappingService.GetAsync(orgId, orderRow.SupplierId, ct)
                       ?? new PoMappingConfig();

        // Copy the mutable dictionaries so we can merge without mutating the existing config in-place.
        var header = new Dictionary<string, FieldMappingEntry>(existing.Header, StringComparer.OrdinalIgnoreCase);
        var lines  = new Dictionary<string, FieldMappingEntry>(existing.Lines,  StringComparer.OrdinalIgnoreCase);

        int headerCount = 0;
        int lineCount   = 0;

        if (sourceMap is not null)
        {
            foreach (var (canonicalField, sourceRule) in sourceMap)
            {
                var entry = Translate(sourceRule);

                if (HeaderFields.Contains(canonicalField))
                {
                    // Use the exact case from HeaderFields so PoMappingEngine's dictionary lookup hits.
                    var normalised = NormaliseKey(canonicalField, HeaderFields);
                    header[normalised] = entry;
                    headerCount++;
                }
                else if (LineFields.Contains(canonicalField))
                {
                    var normalised = NormaliseKey(canonicalField, LineFields);
                    lines[normalised] = entry;
                    lineCount++;
                }
                // else: unknown canonical field — skip silently (forward-compatible).
            }
        }

        // Upsert is idempotent (overwrites the existing mapping row — no duplication).
        var merged = existing with { Header = header, Lines = lines };
        await _poMappingService.UpsertAsync(orgId, orderRow.SupplierId, merged, ct);

        return new PromoteMappingResult(
            SupplierId:           orderRow.SupplierId,
            HeaderFieldsPromoted: headerCount,
            LineFieldsPromoted:   lineCount,
            SchemaFingerprintHash: orderRow.SchemaFingerprintHash);
    }

    // ── Translation helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Translates one <see cref="SourceFieldRule"/> into the equivalent <see cref="FieldMappingEntry"/>.
    /// The two types are structurally identical in purpose:
    /// <list type="bullet">
    ///   <item>SourceToken → ExternalField (the source-column identifier at lookup time)</item>
    ///   <item>FixedValue  → FixedValue    (constant, used when no external column matches)</item>
    ///   <item>Manipulators → FieldManipulators (verbatim — same <see cref="ManipulatorEntry"/> type)</item>
    /// </list>
    /// </summary>
    private static FieldMappingEntry Translate(SourceFieldRule rule) =>
        new()
        {
            ExternalField      = rule.SourceToken,
            FixedValue         = rule.FixedValue,
            FieldManipulators  = rule.Manipulators is { Count: > 0 }
                ? new List<ManipulatorEntry>(rule.Manipulators)
                : new List<ManipulatorEntry>(),
        };

    /// <summary>
    /// Returns the canonical casing of a field name from the authoritative set so the
    /// caller-supplied key (which might differ in case) is stored with consistent casing.
    /// Falls back to the input as-is when not found (shouldn't happen after the Contains check).
    /// </summary>
    private static string NormaliseKey(string input, HashSet<string> authoritative) =>
        authoritative.TryGetValue(input, out var canonical) ? canonical : input;
}
