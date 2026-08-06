using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Services;
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

        // WP-12 — promote the STRUCTURED output tree too. Without this the visual output designer's
        // layout dies with the order it was drawn on: the next identical file from the same supplier
        // renders through the fixed transformer and every design decision is silently lost. Same
        // "only a usable value is stored" rule as the flat output above, so promoting an ordinary
        // order never wipes a layout a previous promote saved.
        //
        // Usability is the SHARED predicate the transform consumes with, so promote can never report
        // saving something delivery will not honour (offer ⇔ works). It is also the null-safe one: a
        // hand-posted `"root": null` used to throw a NullReferenceException straight out of this
        // endpoint as a 500.
        var tree = @override?.OutputTree;

        // …and the SAME format question the transform's adoption gate asks. The bytes come from
        // tree.Format while the artifact row, the delivered content type and the file name all come
        // from the CONNECTION's format, so a tree in any other format can never drive this supplier's
        // document. Refusing it HERE means the operator learns at the click instead of discovering it
        // in a delivered file.
        var supplierFormat = await ResolveSupplierOutputFormatAsync(orgId, supplierId, ct);
        var hasUsableTree  = IsHonestForThisSupplier(tree, supplierFormat);

        // A cXML/X12 template is never rendered — only its envelope identity is honoured — so the
        // confirmation must name the identity, not a file layout that will never apply.
        var treeIsIdentityOnly = hasUsableTree && OrderMappingOverrideReader.IsEnvelopeOnlyTree(tree);

        // Decide whether anything is actually promotable. If the order has no SourceMap entries that
        // map to a known canonical field AND no usable output mapping AND no usable output tree,
        // leave the supplier mapping untouched and report a clear "nothing to promote" — never a
        // silent success-with-no-effect.
        var inboundPromoted = headerCount + lineCount;
        if (inboundPromoted == 0 && !hasUsableOutput && !hasUsableTree)
        {
            return new PromoteMappingResult(
                SupplierId:                 supplierId,
                HeaderFieldsPromoted:       0,
                LineFieldsPromoted:         0,
                OutputHeaderFieldsPromoted: 0,
                OutputLineFieldsPromoted:   0,
                SchemaFingerprintHash:      orderRow.SchemaFingerprintHash,
                Message:                    BuildNothingToPromoteMessage(@override, supplierFormat));
        }

        // Build the merged config. Inbound rules merge into Header/Lines (additive). The output mapping
        // and the output tree are each set only when this order carries a usable one — otherwise the
        // existing supplier value (if any) is preserved unchanged.
        //
        // EXCEPT that an already-stored tree is re-checked against the SAME predicate before it is
        // carried forward. `existing.OutputTree` used to be preserved unconditionally, so a tree
        // promoted while the predicate was wrong (a Ubl/EDIFACT layout, or one in a format this
        // supplier does not deliver) survived every subsequent promote — there was no way to
        // un-poison the supplier at all. Now any promote also cleans it, and
        // DELETE /api/suppliers/{id}/po-mapping/output-tree removes it outright.
        var merged = existing with
        {
            Header     = header,
            Lines      = lines,
            Output     = hasUsableOutput ? output : existing.Output,
            OutputTree = hasUsableTree
                             ? tree
                             : IsHonestForThisSupplier(existing.OutputTree, supplierFormat)
                                 ? existing.OutputTree
                                 : null,
        };

        // Upsert is idempotent (overwrites the existing mapping row — no duplication).
        //
        // CONCURRENCY (D9b, accepted risk — stated, not hidden): read (above) → merge → write is not
        // atomic, and SupplierPoMapping carries no concurrency token, so two promotes racing on the
        // SAME supplier can lose one side's fields. Accepted because: promote is a deliberate,
        // per-supplier human click (not a job, not a fan-out), it is idempotent — re-clicking the
        // losing order re-promotes exactly the same rules — and the outcome is now fully recoverable
        // through the clear endpoint. Adding an xmin concurrency token to this table is a model +
        // snapshot + migration change whose blast radius (every EF InMemory test in the suite loses
        // token support) is far larger than the risk it removes; if promotion ever becomes automated,
        // that is the moment to add it.
        await _poMappingService.UpsertAsync(orgId, supplierId, merged, ct);

        return new PromoteMappingResult(
            SupplierId:                 supplierId,
            HeaderFieldsPromoted:       headerCount,
            LineFieldsPromoted:         lineCount,
            OutputHeaderFieldsPromoted: hasUsableOutput ? outputHeaderCount : 0,
            OutputLineFieldsPromoted:   hasUsableOutput ? outputLineCount   : 0,
            SchemaFingerprintHash:      orderRow.SchemaFingerprintHash,
            Message:                    BuildPromotedMessage(
                                            headerCount, lineCount,
                                            hasUsableOutput ? outputHeaderCount : 0,
                                            hasUsableOutput ? outputLineCount   : 0,
                                            hasUsableTree, treeIsIdentityOnly))
        {
            OutputTreePromoted = hasUsableTree,
        };
    }

    /// <inheritdoc/>
    public async Task<bool> ClearPromotedOutputTreeAsync(Guid orgId, Guid supplierId, CancellationToken ct)
    {
        var existing = await _poMappingService.GetAsync(orgId, supplierId, ct);
        if (existing?.OutputTree is null) return false;   // idempotent — nothing to un-promote

        await _poMappingService.UpsertAsync(orgId, supplierId, existing with { OutputTree = null }, ct);
        return true;
    }

    // ── Format honesty ────────────────────────────────────────────────────────

    /// <summary>
    /// The supplier's configured output format, read from the LIVE delivery config — the same row the
    /// controller reads to decide which format to transform into. Null when no delivery config exists
    /// or its format is blank/unrecognised, in which case there is nothing for a tree to contradict.
    /// </summary>
    private async Task<OutputFormat?> ResolveSupplierOutputFormatAsync(
        Guid orgId, Guid supplierId, CancellationToken ct)
    {
        var raw = await _db.SupplierDeliveryConfigs
            .AsNoTracking()
            .Where(c => c.OrgId == orgId && c.SupplierId == supplierId)
            .Select(c => c.OutputFormat)
            .FirstOrDefaultAsync(ct);

        return Enum.TryParse<OutputFormat>(raw, ignoreCase: true, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// May this tree be stored against this supplier and reported as saved? Two questions, both shared
    /// with the transform so promote can never claim something delivery will refuse:
    ///
    /// <list type="bullet">
    ///   <item><description><see cref="OrderMappingOverrideReader.HasUsableOutputTree"/> — does it
    ///     contribute anything at all? (Renderability comes from <see cref="OutputTreeFormats"/>, the
    ///     source the emitter dispatches on, so a Ubl / UblOrder / X12_850 / EdifactOrders layout can
    ///     no longer be stored and announced as "the file layout you designed" and then kill every
    ///     future order in the emitter.)</description></item>
    ///   <item><description>and, for a tree that RENDERS its own document, does it render the format
    ///     this supplier is configured to receive? A cXML/X12 tree is exempt — it contributes only
    ///     envelope identity, never bytes, so its own Format is not the document's format.</description></item>
    /// </list>
    /// </summary>
    private static bool IsHonestForThisSupplier(OutputNodeTemplate? tree, OutputFormat? supplierFormat)
    {
        if (!OrderMappingOverrideReader.HasUsableOutputTree(tree)) return false;

        if (!OrderMappingOverrideReader.CanRenderTree(tree)) return true;   // envelope-only: no bytes

        return supplierFormat is null || tree!.Format == supplierFormat;
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
        int headerCount, int lineCount, int outputHeaderCount, int outputLineCount,
        bool promotedTree, bool treeIsIdentityOnly)
    {
        var inbound = headerCount + lineCount;
        var output  = outputHeaderCount + outputLineCount;

        var parts = new List<string>();
        if (inbound > 0) parts.Add($"{inbound} source field{(inbound == 1 ? "" : "s")}");
        if (output  > 0) parts.Add($"{output} output field{(output == 1 ? "" : "s")}");
        // Plain language, and only ever a true claim: a cXML/X12 template never applies its layout —
        // only the sender/receiver identity on it is used — so say THAT, not "the file layout".
        if (promotedTree)
            parts.Add(treeIsIdentityOnly
                ? "the sender and receiver identity you set"
                : "the file layout you designed");

        return $"Saved {string.Join(" and ", parts)} to this supplier's reusable mapping. " +
               "Future uploads from this supplier reuse it.";
    }

    /// <summary>
    /// Clear reason when there is nothing to promote, so the UI never shows an empty success.
    /// Distinguishes "no per-order mapping at all" from "a mapping exists but none of its fields
    /// map to a known canonical field / output rule".
    /// </summary>
    private static string BuildNothingToPromoteMessage(
        OrderMappingOverride? @override, OutputFormat? supplierFormat)
    {
        if (@override is null)
            return "Nothing to save — this order has no custom field mapping yet. " +
                   "Wire some fields (or edit the output mapping) first, then save.";

        var tree = @override.OutputTree;

        // The honest answer for a cXML/X12 template: its nodes are never used, so drawing them saved
        // nothing. Say what WOULD be saved instead of implying the layout is the problem.
        if (OrderMappingOverrideReader.IsEnvelopeOnlyTree(tree))
            return "Nothing to save — a cXML or EDI document is built to the standard's own layout, " +
                   "so the boxes drawn here are not used. Set this supplier's sender and receiver " +
                   "identity (or wire some fields), then save.";

        // A layout drawn for a format nothing can render from a box tree (UBL / Peppol / EDIFACT /
        // X12 850). Saying "saved your layout" here is what bricked suppliers: the layout was stored,
        // adopted, and then failed every future order inside the emitter.
        if (tree is not null && !OrderMappingOverrideReader.CanRenderTree(tree))
            return $"Nothing to save — a {DisplayName(tree.Format)} document is built to the standard's own " +
                   "layout, so a box tree cannot produce one. Design the output as XML, JSON or CSV, " +
                   "or wire some fields, then save.";

        // A perfectly good layout — for the wrong format. Name both sides so the fix is obvious.
        if (tree is not null && supplierFormat is not null && tree.Format != supplierFormat)
            return $"Nothing to save — this layout is designed as {DisplayName(tree.Format)}, but this " +
                   $"supplier receives {DisplayName(supplierFormat.Value)}. Switch the layout to " +
                   $"{DisplayName(supplierFormat.Value)} (or change the supplier's format), then save.";

        return "Nothing to save — the current field mapping has no source or output rules that map " +
               "to a known field. Wire at least one field, then save.";
    }

    /// <summary>Plain-language format name for a user-facing message (never the enum spelling).</summary>
    private static string DisplayName(OutputFormat format) => format switch
    {
        OutputFormat.Json          => "JSON",
        OutputFormat.Xml           => "XML",
        OutputFormat.Csv           => "CSV",
        OutputFormat.CXml          => "cXML",
        OutputFormat.Ubl           => "UBL",
        // NOT "Peppol UBL order". The UBL emitter produces a plain OASIS UBL 2.1 Order and declares
        // no Peppol profile; naming one in a message the operator reads sells BIS conformance the
        // product does not offer.
        OutputFormat.UblOrder      => "UBL 2.1 Order",
        OutputFormat.X12           => "X12",
        OutputFormat.X12_850       => "X12 850",
        OutputFormat.EdifactOrders => "EDIFACT ORDERS",
        _                          => format.ToString(),
    };
}
