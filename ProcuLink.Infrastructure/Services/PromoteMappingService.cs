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
/// Output side (canonical→output-field re-mapping): the <see cref="OrderMappingOverride.Output"/> is
/// now ALSO promoted — it is copied verbatim onto <see cref="PoMappingConfig.Output"/>, an additive
/// JSONB field that round-trips through the SAME <c>SupplierPoMapping.ConfigJson</c> column (no new
/// table, no EF migration). This makes the founder's "Save mappings for &lt;supplier&gt;" button
/// actually save the output side and report what it saved — fixing the silent no-op.
///
/// Consumption (launch batch 4A): the promoted supplier output mapping IS consumed at transform
/// time — when a future order from this supplier carries no usable per-order template/output
/// override, <c>OrderTransformService</c> (and the mapping-override preview / replay current side)
/// apply <see cref="PoMappingConfig.Output"/>. The per-order override always stays the
/// higher-priority seam, and a malformed/unusable promoted mapping falls back to the fixed
/// transformer.
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

        // Promotion writes the order's mapping back onto its SUPPLIER's reusable config, so it
        // requires a routed order. An unrouted order (no supplier yet) has nothing to promote to —
        // unreachable in Phase 0 (promotion is only offered post-resolution), guarded loudly so a
        // future mis-wire surfaces instead of silently promoting to an empty supplier.
        var supplierId = orderRow.SupplierId
            ?? throw new InvalidOperationException("Cannot promote a mapping for an unrouted order (no supplier assigned).");

        // Read the stored override from canonical_json (null = no override set yet).
        var @override = OrderMappingOverrideReader.Read(orderRow.CanonicalJson);
        var sourceMap = @override?.SourceMap;
        var output    = @override?.Output;

        // Load the existing supplier mapping (or start from an empty config) so we can merge.
        var existing = await _poMappingService.GetAsync(orgId, supplierId, ct)
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

        // Promote the OUTPUT side too. We copy the per-order output mapping verbatim onto the
        // supplier config's additive Output field so it persists across re-uploads. Only a NON-EMPTY
        // output mapping (at least one header or line rule) counts and is stored — an empty output
        // config never overwrites an existing supplier output mapping with nothing.
        int outputHeaderCount = output?.Header.Count ?? 0;
        int outputLineCount   = output?.Lines.Count  ?? 0;
        var hasUsableOutput   = outputHeaderCount > 0 || outputLineCount > 0;

        // Decide whether anything is actually promotable. If the order has no SourceMap entries that
        // map to a known canonical field AND no usable output mapping, leave the supplier mapping
        // untouched and report a clear "nothing to promote" — never a silent success-with-no-effect.
        var inboundPromoted = headerCount + lineCount;
        if (inboundPromoted == 0 && !hasUsableOutput)
        {
            return new PromoteMappingResult(
                SupplierId:                 supplierId,
                HeaderFieldsPromoted:       0,
                LineFieldsPromoted:         0,
                OutputHeaderFieldsPromoted: 0,
                OutputLineFieldsPromoted:   0,
                SchemaFingerprintHash:      orderRow.SchemaFingerprintHash,
                Message:                    BuildNothingToPromoteMessage(@override));
        }

        // Build the merged config. Inbound rules merge into Header/Lines (additive). The output mapping
        // is set only when this order carries a usable one — otherwise the existing supplier output
        // mapping (if any) is preserved unchanged.
        var merged = existing with
        {
            Header = header,
            Lines  = lines,
            Output = hasUsableOutput ? output : existing.Output,
        };

        // Upsert is idempotent (overwrites the existing mapping row — no duplication).
        await _poMappingService.UpsertAsync(orgId, supplierId, merged, ct);

        return new PromoteMappingResult(
            SupplierId:                 supplierId,
            HeaderFieldsPromoted:       headerCount,
            LineFieldsPromoted:         lineCount,
            OutputHeaderFieldsPromoted: hasUsableOutput ? outputHeaderCount : 0,
            OutputLineFieldsPromoted:   hasUsableOutput ? outputLineCount   : 0,
            SchemaFingerprintHash:      orderRow.SchemaFingerprintHash,
            Message:                    BuildPromotedMessage(headerCount, lineCount, hasUsableOutput ? outputHeaderCount : 0, hasUsableOutput ? outputLineCount : 0));
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

    // ── Message builders ──────────────────────────────────────────────────────

    /// <summary>
    /// Human-readable confirmation of a successful promotion, e.g.
    /// "Saved 3 source field(s) and 5 output field(s) to this supplier's reusable mapping."
    /// Only the non-zero halves are mentioned.
    /// </summary>
    private static string BuildPromotedMessage(
        int headerCount, int lineCount, int outputHeaderCount, int outputLineCount)
    {
        var inbound = headerCount + lineCount;
        var output  = outputHeaderCount + outputLineCount;

        var parts = new List<string>();
        if (inbound > 0) parts.Add($"{inbound} source field{(inbound == 1 ? "" : "s")}");
        if (output  > 0) parts.Add($"{output} output field{(output == 1 ? "" : "s")}");

        return $"Saved {string.Join(" and ", parts)} to this supplier's reusable mapping. " +
               "Future uploads from this supplier reuse it.";
    }

    /// <summary>
    /// Clear reason when there is nothing to promote, so the UI never shows an empty success.
    /// Distinguishes "no per-order mapping at all" from "a mapping exists but none of its fields
    /// map to a known canonical field / output rule".
    /// </summary>
    private static string BuildNothingToPromoteMessage(OrderMappingOverride? @override)
    {
        if (@override is null)
            return "Nothing to save — this order has no custom field mapping yet. " +
                   "Wire some fields (or edit the output mapping) first, then save.";

        return "Nothing to save — the current field mapping has no source or output rules that map " +
               "to a known field. Wire at least one field, then save.";
    }
}
