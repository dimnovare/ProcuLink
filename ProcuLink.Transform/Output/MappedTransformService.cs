using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Mapping;
using ProcuLink.Transform.Tokenizing;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Override-aware output builder (heart-piece-flex Phase 2). When an order carries a per-order
/// <see cref="OrderMappingOverride"/> with a usable <see cref="OutputMappingConfig"/>, this service
/// builds the CSV or JSON document FROM the override instead of the fixed columns. Each
/// <see cref="OutputFieldRule"/> resolves its value (a canonical field, a custom field, or a fixed
/// literal) and runs it through <c>ManipulatorRegistry</c> with the EXACT same semantics as
/// <c>PoMappingEngine.ResolveField</c> — no new templating engine.
///
/// v1 supports CSV + JSON only (the two flat formats). XML/UBL/cXML/X12 are intentionally out of
/// scope and route to the existing fixed transformers. The same NeedsReview / null-SupplierItemCode
/// validation guard the fixed transforms enforce is applied here, so an override can never deliver
/// an unresolved order.
///
/// This service is NOT registered as an <see cref="ITransformService"/>: it is invoked explicitly by
/// the override-aware branch in <c>OrderTransformService.TransformAsync</c>, which guards the
/// "override present AND format supported" condition. Default (no override) behaviour is unchanged.
/// </summary>
public sealed class MappedTransformService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// True only for the flat formats the override builder drives NATIVELY (it emits the document
    /// itself from the output-field rules). CSV + JSON. The structured formats (XML/cXML/UBL/X12)
    /// apply overrides differently — by resolving an effective entity and feeding it to the fixed
    /// transform — see <see cref="SupportsOverrideFormat"/>.
    /// </summary>
    public static bool SupportsOverride(OutputFormat format) =>
        format is OutputFormat.Csv or OutputFormat.Json;

    /// <summary>
    /// True for ANY entity-based output format an override can influence: the two flat formats the
    /// override builder drives natively (CSV/JSON), PLUS the structured formats where overrides are
    /// applied by resolving an effective <see cref="PurchaseOrderEntity"/> (header/line canonical
    /// values overridden) and feeding it to the existing fixed transform (XML/cXML/UBL/X12).
    /// The canonical-model output formats (<see cref="OutputFormat.UblOrder"/> etc.) are out of scope.
    /// </summary>
    public static bool SupportsOverrideFormat(OutputFormat format) => format is
        OutputFormat.Csv or OutputFormat.Json or
        OutputFormat.Xml or OutputFormat.CXml or OutputFormat.Ubl or OutputFormat.X12;

    /// <summary>
    /// Build the override-driven output document. Throws <see cref="TransformValidationException"/>
    /// if any line is unresolved (same guard as the fixed transforms), and
    /// <see cref="ArgumentException"/> if asked for an unsupported format (the caller guards this).
    ///
    /// When <paramref name="sourceTokens"/> is non-null and the override carries a
    /// <see cref="OrderMappingOverride.SourceMap"/>, the SourceMap re-derive step runs BEFORE
    /// the output rules: effective canonical values are computed from the token list first, then
    /// the output rules read those effective values. Passing <c>null</c> or an empty list is
    /// equivalent to no SourceMap — the default (no remap) path is unchanged.
    ///
    /// <para><paramref name="catalogLookup"/> (Phase 2): an OPTIONAL, pre-loaded supplier-catalog
    /// dictionary keyed by <c>Code</c> / <c>Barcode</c> / <c>ExternalId</c> (resolved ONCE by the
    /// caller, org+supplier scoped — this service performs NO database access). When a line's
    /// resolved <c>SupplierItemCode</c> / <c>ManufacturerPartNumber</c> matches a catalog row, the
    /// row's price/code/unit/barcode are injected into the line value bag under the reserved keys
    /// the <c>LoadCatalogProduct</c> manipulator reads (<c>__catalog_price</c> etc.) BEFORE the
    /// manipulators run. Null/absent or no match = byte-identical to today (no reserved keys → the
    /// manipulator returns "" exactly as before). Catalog values are SUGGESTIONS — they never
    /// overwrite a PO field, only feed a manipulator the author explicitly selected.</para>
    /// </summary>
    public TransformResult Build(
        PurchaseOrderEntity        order,
        OrderMappingOverride       @override,
        OutputFormat               format,
        IReadOnlyList<SourceToken>? sourceTokens = null,
        IReadOnlyDictionary<string, SupplierProduct>? catalogLookup = null)
    {
        ValidateOrder(order);

        var output = @override.Output
            ?? throw new ArgumentException("Override has no output mapping config.", nameof(@override));

        // Materialise a non-null token list once (empty if none supplied).
        IReadOnlyList<SourceToken> tokens = sourceTokens ?? Array.Empty<SourceToken>();

        return format switch
        {
            OutputFormat.Csv  => BuildCsv(order, @override, output, tokens, catalogLookup),
            OutputFormat.Json => BuildJson(order, @override, output, tokens, catalogLookup),
            _ => throw new ArgumentException(
                     $"MappedTransformService does not support format '{format}' (v1: CSV + JSON only).",
                     nameof(format)),
        };
    }

    // ── CSV ────────────────────────────────────────────────────────────────────

    private static TransformResult BuildCsv(
        PurchaseOrderEntity        order,
        OrderMappingOverride       @override,
        OutputMappingConfig        output,
        IReadOnlyList<SourceToken> tokens,
        IReadOnlyDictionary<string, SupplierProduct>? catalogLookup)
    {
        // Stable column order: header rules first (emitted once as a leading section is awkward for a
        // flat CSV, so header values are repeated on every line as leading columns), then line columns.
        // Preserve the dictionary insertion order so the editor's ordering round-trips.
        var headerCols = output.Header.ToList();
        var lineCols   = output.Lines.ToList();

        var sb = new StringBuilder();

        // Header row: header-field output paths, then line-field output paths.
        var headerNames = headerCols.Select(c => c.Value.OutputPath)
            .Concat(lineCols.Select(c => c.Value.OutputPath));
        sb.AppendLine(string.Join(",", headerNames.Select(Escape)));

        // Resolve header values once (header scope).
        // SourceMap re-derive runs first (no-op when SourceMap is absent/empty).
        var headerRow    = SourceMapReDerive.ApplyToHeaderRow(BuildHeaderRow(order, @override, tokens), @override, tokens);
        var headerValues = headerCols
            .Select(c => ResolveRule(c.Value, headerRow, lineScope: false) ?? string.Empty)
            .ToList();

        foreach (var line in order.Lines.OrderBy(l => l.LineNumber))
        {
            // SourceMap re-derive for each line row.
            var lineRow = SourceMapReDerive.ApplyToLineRow(BuildLineRow(order, @override, line, catalogLookup, tokens), @override, tokens);

            var lineValues = lineCols
                .Select(c => ResolveRule(c.Value, lineRow, lineScope: true) ?? string.Empty)
                .ToList();

            sb.AppendLine(string.Join(",", headerValues.Concat(lineValues).Select(Escape)));
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return new TransformResult(new MemoryStream(bytes), "text/csv", ".csv");
    }

    // ── JSON ───────────────────────────────────────────────────────────────────

    private static TransformResult BuildJson(
        PurchaseOrderEntity        order,
        OrderMappingOverride       @override,
        OutputMappingConfig        output,
        IReadOnlyList<SourceToken> tokens,
        IReadOnlyDictionary<string, SupplierProduct>? catalogLookup)
    {
        // SourceMap re-derive for header row.
        var headerRow = SourceMapReDerive.ApplyToHeaderRow(BuildHeaderRow(order, @override, tokens), @override, tokens);

        var header = new Dictionary<string, string?>();
        foreach (var (_, rule) in output.Header)
            header[rule.OutputPath] = ResolveRule(rule, headerRow, lineScope: false) ?? string.Empty;

        var lines = new List<Dictionary<string, string?>>();
        foreach (var line in order.Lines.OrderBy(l => l.LineNumber))
        {
            // SourceMap re-derive for each line row.
            var lineRow = SourceMapReDerive.ApplyToLineRow(BuildLineRow(order, @override, line, catalogLookup, tokens), @override, tokens);
            var obj = new Dictionary<string, string?>();
            foreach (var (_, rule) in output.Lines)
                obj[rule.OutputPath] = ResolveRule(rule, lineRow, lineScope: true) ?? string.Empty;
            lines.Add(obj);
        }

        var payload = new
        {
            header,
            lines,
            generatedAt = DateTime.UtcNow.ToString("O"),
        };

        var json  = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        return new TransformResult(new MemoryStream(bytes), "application/json", ".json");
    }

    // ── Value resolution (mirrors PoMappingEngine.ResolveField semantics) ────────

    /// <summary>
    /// Resolves one rule. Precedence for the source value:
    /// <list type="number">
    ///   <item><b>Expression</b> (Scriban) when non-blank — evaluated with <c>order</c> (and, when
    ///         <paramref name="lineScope"/> is true, <c>line</c>) in scope. A compile/eval error is
    ///         non-fatal: it falls back to the canonical/fixed value so the transform never crashes.</item>
    ///   <item><b>FixedValue</b>.</item>
    ///   <item><b>CanonicalField</b> looked up in <paramref name="row"/>.</item>
    /// </list>
    /// The chosen value is then run through the manipulator chain — identical order and semantics to
    /// <c>PoMappingEngine.ResolveField</c>. The expression layer is purely additive: with no
    /// <c>Expression</c> set, behaviour is byte-for-byte identical to before.
    /// </summary>
    internal static string? ResolveRule(
        OutputFieldRule rule, IReadOnlyDictionary<string, string> row, bool lineScope)
    {
        string? value = ResolveExpressionOrField(
            rule.Expression,
            fallbackSourceToken: rule.SourceToken,
            fallbackFixedValue: rule.FixedValue,
            fallbackCanonicalField: rule.CanonicalField,
            row: row,
            lineScope: lineScope);

        foreach (var m in rule.FieldManipulators ?? new List<ManipulatorEntry>())
        {
            var manipulator = ManipulatorRegistry.Resolve(m.Type, m.Params);
            value = manipulator.Apply(value, row);
        }

        return value;
    }

    /// <summary>
    /// Shared value-selection helper for the output-rule path. Precedence (F-1):
    /// <c>Expression → SourceToken → FixedValue → CanonicalField</c>. Expression wins when present and
    /// non-blank; then a bound source token (looked up as <c>row["src::"+token]</c>); then the supplied
    /// fixed value; then the named field in <paramref name="row"/>. On an expression compile/eval
    /// failure, falls through to the token/fixed/field value (never throws). A <paramref name="fallbackSourceToken"/>
    /// id that is NOT present in the bag falls through too — it never emits a literal "src::…".
    /// </summary>
    internal static string? ResolveExpressionOrField(
        string? expression,
        string? fallbackSourceToken,
        string? fallbackFixedValue,
        string? fallbackCanonicalField,
        IReadOnlyDictionary<string, string> row,
        bool lineScope)
    {
        if (!string.IsNullOrWhiteSpace(expression))
        {
            var result = lineScope
                ? ScribanFieldEvaluator.EvaluateLine(expression, row)
                : ScribanFieldEvaluator.EvaluateHeader(expression, row);

            if (result.Ok)
                return result.Value;

            // Expression failed — fall through to the token/field/fixed value so the transform survives
            // (fail-open is intentional and UNCHANGED). Log the failure so an authoring mistake in a
            // mapping expression is observable instead of silently producing the fallback value.
            TransformDiagnostics.CreateLogger(nameof(MappedTransformService)).LogWarning(
                "Output-field expression failed to evaluate ({Scope}); falling back to the " +
                "source-token/field/fixed value. Error: {Error}",
                lineScope ? "line" : "header", result.Error);
        }

        // F-1 Seam B: a bound source token resolves through the reserved src:: namespace the row builder
        // injects. A not-found token id falls through to FixedValue/CanonicalField (never crashes, never
        // emits a literal "src::…").
        if (!string.IsNullOrWhiteSpace(fallbackSourceToken)
            && row.TryGetValue($"src::{fallbackSourceToken}", out var tokenValue))
            return tokenValue;

        return fallbackFixedValue
            ?? (fallbackCanonicalField is not null && row.TryGetValue(fallbackCanonicalField, out var v) ? v : null);
    }

    /// <summary>
    /// Header-scope field bag: the recognised canonical header fields plus any header-scoped custom
    /// fields. Keys match the canonical names accepted in <see cref="OutputFieldRule.CanonicalField"/>.
    /// Manipulators that read sibling columns (Concat/Fallback) see this same bag as their row.
    ///
    /// V5 additions (additive — all keys default to empty when the field is null):
    /// <c>SubTotal</c>, <c>TaxTotal</c>, <c>GrandTotal</c>, <c>PaymentTerms</c>,
    /// <c>RequestedDeliveryDate</c>. These keys are always present; existing templates and
    /// overrides that do not reference them are unaffected (the fixed transforms never read
    /// this bag). Adding a key to the row bag cannot change fixed-transform output.
    ///
    /// <para>WP-14 widening: the bag now carries EVERY business column on
    /// <see cref="PurchaseOrderEntity"/> — notably the whole <c>ShipTo*</c> delivery-address block and
    /// the <c>BillTo*</c> invoicing block, plus <c>Contact*</c>, <c>Incoterms</c>,
    /// <c>ShippingMethod</c>, <c>BuyerOrderRef</c>, <c>BuyerTaxId</c> and <c>DocumentType</c>.
    /// Same additive contract: present-but-empty when the column is null, and inert until a rule
    /// names the key. Completeness is enforced by reflection in
    /// <c>CanonicalRowCompletenessTests.HeaderRow_ExposesEveryBusinessFieldOnTheEntity</c>, so a new
    /// column cannot silently become unbindable — add it here or add a named exclusion there.</para>
    ///
    /// <para>F-1 Seam A (bind ANY source field): when <paramref name="sourceTokens"/> is non-null,
    /// every header-scope token (<c>Group != "line"</c>, OR a line-scope token whose addressed ordinal
    /// matches no line) is appended under the reserved key <c>"src::"+token.Id</c> with its VERBATIM
    /// value (never numeric-parsed — EU <c>1.234,56</c> survives). A rule (or Scriban <c>order["src::…"]</c>)
    /// can then bind that arbitrary source field. The keys are INERT unless a rule names them, so an
    /// order with no <c>src::</c> binding is byte-identical to today (proven by the byte-parity oracle).
    /// The <c>::</c> separator cannot collide with a canonical name or a guarded custom-field key.</para>
    /// </summary>
    internal static Dictionary<string, string> BuildHeaderRow(
        PurchaseOrderEntity order, OrderMappingOverride @override,
        IReadOnlyList<SourceToken>? sourceTokens = null)
    {
        // V5: SubTotal/TaxTotal/GrandTotal prefer the parser-stated value; fall back to derivation.
        // Derivation mirrors ScribanOrderModel: sum of (Qty * UnitPrice) for all lines.
        // When the entity already carries a stated value, use it; compute only when null.
        // Defence-in-depth: the sum is overflow-guarded so a pathological qty/price can never throw
        // an OverflowException up this row-building path (the structural row bag falls back to 0).
        // NOTE: a total that overflows is CORRUPT, not legitimately 0 — the native delivery path
        // (Build → ValidateOrder → GuardLineSumOverflow) holds such an order for review BEFORE this
        // row builder runs, so a corrupt total can never be DELIVERED as 0. This 0 fallback only
        // protects the non-delivery reuse paths (preview / OutputTemplateEmitter), and it is logged
        // so the degradation is observable rather than silent.
        static decimal SafeLineSum(PurchaseOrderEntity o)
        {
            try
            {
                return o.Lines.Sum(l => l.Quantity * l.UnitPrice);
            }
            catch (OverflowException ex)
            {
                TransformDiagnostics.CreateLogger(nameof(MappedTransformService)).LogWarning(
                    ex,
                    "Order {OrderId} (PO {PoNumber}): line-sum total overflowed decimal range; " +
                    "row total degraded to 0 for this non-delivery row build. The native delivery path " +
                    "holds this order for review instead of emitting a corrupt 0.",
                    o.Id, o.PoNumber);
                return 0m;
            }
        }
        static decimal DeriveSubTotal(PurchaseOrderEntity o)   => o.SubTotal   ?? SafeLineSum(o);
        static decimal DeriveTaxTotal(PurchaseOrderEntity o)   => o.TaxTotal   ?? 0m;
        static decimal DeriveGrandTotal(PurchaseOrderEntity o) => o.GrandTotal ?? SafeLineSum(o);

        var row = new Dictionary<string, string>
        {
            ["PoNumber"]               = order.PoNumber ?? string.Empty,
            ["OrderDate"]              = order.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["BuyerName"]              = OrderHeaderReader.ExtractBuyerName(order),
            ["Currency"]               = order.Currency ?? string.Empty,
            ["SupplierName"]           = order.Supplier?.Name ?? order.SupplierName ?? string.Empty,
            // V5: totals (derived when not stated) and remaining header enrichment.
            ["SubTotal"]               = DeriveSubTotal(order).ToString(CultureInfo.InvariantCulture),
            ["TaxTotal"]               = DeriveTaxTotal(order).ToString(CultureInfo.InvariantCulture),
            ["GrandTotal"]             = DeriveGrandTotal(order).ToString(CultureInfo.InvariantCulture),
            ["PaymentTerms"]           = order.PaymentTerms ?? string.Empty,
            ["RequestedDeliveryDate"]  = order.RequestedDeliveryDate.HasValue
                                             ? order.RequestedDeliveryDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                                             : string.Empty,

            // WP-14: the rest of the parsed header. The row bag is the ONLY vocabulary a custom
            // output mapping / designed output tree can bind, so anything missing here is
            // unreachable no matter how correctly it was parsed and persisted. Before this block a
            // physical purchase order could not emit a DELIVERY ADDRESS through any custom output
            // path — the fixed CsvTransformService already emitted ShipTo columns from these very
            // same typed columns, so the data was present and only the vocabulary was short.
            // Every key is INERT until a rule names it (proven by the unbound-fields guard in
            // CanonicalRowCompletenessTests), so this widening changes no existing document.
            // Kept in sync by the reflection-driven completeness test: a new business column on
            // PurchaseOrderEntity fails the build until it is exposed here or explicitly excluded.
            ["DocumentType"]           = order.DocumentType ?? string.Empty,

            // Buyer-side contact + commercial terms.
            ["ContactName"]            = order.ContactName ?? string.Empty,
            ["ContactEmail"]           = order.ContactEmail ?? string.Empty,
            ["ContactPhone"]           = order.ContactPhone ?? string.Empty,
            ["Incoterms"]              = order.Incoterms ?? string.Empty,
            ["ShippingMethod"]         = order.ShippingMethod ?? string.Empty,
            ["BuyerOrderRef"]          = order.BuyerOrderRef ?? string.Empty,
            ["BuyerTaxId"]             = order.BuyerTaxId ?? string.Empty,

            // Ship-to block — the delivery address. Not optional on a physical PO.
            ["ShipToName"]             = order.ShipToName ?? string.Empty,
            ["ShipToDeliverTo"]        = order.ShipToDeliverTo ?? string.Empty,
            ["ShipToStreet"]           = order.ShipToStreet ?? string.Empty,
            ["ShipToCity"]             = order.ShipToCity ?? string.Empty,
            ["ShipToPostalCode"]       = order.ShipToPostalCode ?? string.Empty,
            ["ShipToCountry"]          = order.ShipToCountry ?? string.Empty,
            ["ShipToEmail"]            = order.ShipToEmail ?? string.Empty,
            ["ShipToPhone"]            = order.ShipToPhone ?? string.Empty,

            // Bill-to block — the invoicing address.
            ["BillToName"]             = order.BillToName ?? string.Empty,
            ["BillToDeliverTo"]        = order.BillToDeliverTo ?? string.Empty,
            ["BillToStreet"]           = order.BillToStreet ?? string.Empty,
            ["BillToCity"]             = order.BillToCity ?? string.Empty,
            ["BillToPostalCode"]       = order.BillToPostalCode ?? string.Empty,
            ["BillToCountry"]          = order.BillToCountry ?? string.Empty,
            ["BillToEmail"]            = order.BillToEmail ?? string.Empty,
            ["BillToPhone"]            = order.BillToPhone ?? string.Empty,
        };

        foreach (var cf in @override.CustomFields)
        {
            if (!string.Equals(cf.Scope, "header", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(cf.Key)) continue;
            row[cf.Key] = cf.Value ?? string.Empty;
        }

        // F-1 Seam A: append header-scope source tokens under the reserved "src::" namespace. A line
        // token whose ordinal can't be matched to a line is header-scope too (still bindable). Verbatim
        // values; appended LAST so they can never shadow a canonical/custom key (TryGetValue order is
        // irrelevant — the reserved keys are distinct, and only a rule that NAMES one emits it).
        if (sourceTokens is { Count: > 0 })
        {
            foreach (var t in sourceTokens)
            {
                if (t is null || string.IsNullOrEmpty(t.Id)) continue;
                var isLineScoped = string.Equals(t.Group, "line", StringComparison.Ordinal)
                                   && SourceTokenLineIndexer.LineOrdinalOf(t.Id) is not null;
                if (isLineScoped) continue; // belongs in a specific line bag, not the header bag
                row[$"src::{t.Id}"] = t.Value ?? string.Empty;
            }
        }

        return row;
    }

    /// <summary>
    /// Line-scope field bag for one line: the recognised canonical line fields, plus header fields
    /// (so a line rule can reference order-level values), plus any line-scoped custom field's value
    /// for this line number. Header custom fields are included too.
    ///
    /// <para>WP-14 widening: the bag now carries EVERY business column on
    /// <see cref="PurchaseOrderLineEntity"/> — <c>TaxAmount</c>, <c>ManufacturerPartNumber</c>,
    /// <c>ManufacturerName</c>, <c>CustomerPartNumber</c>, <c>DiscountPercent</c>, <c>Unspsc</c>,
    /// <c>Recipient</c>, <c>ContractNumber</c> and <c>NetAmount</c>. Additive and inert exactly like
    /// the header block; kept complete by reflection in
    /// <c>CanonicalRowCompletenessTests.LineRow_ExposesEveryBusinessFieldOnTheEntity</c>.</para>
    ///
    /// <para>Phase 2 catalog wiring: when <paramref name="catalogLookup"/> is non-null and this line's
    /// resolved <c>SupplierItemCode</c> / <c>ManufacturerPartNumber</c> matches a catalog row, the
    /// row's price/code/unit/barcode are injected under the reserved keys the <c>LoadCatalogProduct</c>
    /// manipulator reads (<c>__catalog_price</c> / <c>__catalog_code</c> / <c>__catalog_unit</c> /
    /// <c>__catalog_barcode</c>). The match logic mirrors <c>ScribanOrderModel.BuildLine</c> (supplier
    /// item code first, then manufacturer part number). Null/no-match → no reserved keys → the
    /// manipulator returns "" exactly as it did before this wiring. The price is a typed decimal
    /// column formatted with <see cref="CultureInfo.InvariantCulture"/> (a clean machine value, never
    /// re-parsed from a locale string).</para>
    ///
    /// <para>F-1 Seam A: when <paramref name="sourceTokens"/> is non-null, this line's bag also carries
    /// (a) every header-scope <c>src::</c> token (via the inner <see cref="BuildHeaderRow"/> call) AND
    /// (b) ONLY the <c>Group=="line"</c> tokens whose addressed ordinal (<see cref="SourceTokenLineIndexer"/>)
    /// matches THIS line's 1-based position among the order's lines — so line 2 never reads line 1's
    /// value. Values are verbatim; keys are inert unless a rule names them (byte-identical when unbound).</para>
    /// </summary>
    internal static Dictionary<string, string> BuildLineRow(
        PurchaseOrderEntity order, OrderMappingOverride @override, PurchaseOrderLineEntity line,
        IReadOnlyDictionary<string, SupplierProduct>? catalogLookup = null,
        IReadOnlyList<SourceToken>? sourceTokens = null)
    {
        // Start from the header bag so line rules can reference order-level fields. The header-scope
        // src:: tokens are injected by this call; the line-scope ones are injected below for THIS line.
        var row = BuildHeaderRow(order, @override, sourceTokens);

        row["LineNumber"]       = line.LineNumber.ToString(CultureInfo.InvariantCulture);
        row["BuyerItemCode"]    = line.BuyerItemCode ?? string.Empty;
        row["SupplierItemCode"] = line.SupplierItemCode ?? string.Empty;
        row["Description"]      = line.Description ?? string.Empty;
        row["Quantity"]         = line.Quantity.ToString(CultureInfo.InvariantCulture);
        row["Unit"]             = line.Unit ?? string.Empty;
        row["UnitPrice"]        = line.UnitPrice.ToString(CultureInfo.InvariantCulture);
        row["LineTotal"]        = SafeMultiply(line.Quantity, line.UnitPrice).ToString(CultureInfo.InvariantCulture);
        // V5 additive line fields: stated extended amount (falls back to computed),
        // per-line tax rate, and per-line delivery date. The computed fallback is overflow-guarded so
        // a pathological qty/price can never throw an OverflowException up the CSV/JSON build path.
        row["LineAmount"]       = (line.LineAmount ?? SafeMultiply(line.Quantity, line.UnitPrice))
                                      .ToString(CultureInfo.InvariantCulture);
        row["TaxRate"]          = line.TaxRate.HasValue
                                      ? line.TaxRate.Value.ToString(CultureInfo.InvariantCulture)
                                      : string.Empty;
        row["DeliveryDate"]     = line.DeliveryDate.HasValue
                                      ? line.DeliveryDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                                      : string.Empty;

        // WP-14: the rest of the parsed line. Same reasoning as the header block — a column absent
        // from this bag cannot be bound by any custom output mapping or designed tree, so the
        // manufacturer part number, the customer part number, the discount and the net/tax amounts
        // were unreachable despite being parsed and persisted. Nullable decimals follow the TaxRate
        // precedent exactly (invariant culture, string.Empty when absent — never a misleading 0).
        // Kept in sync by the reflection-driven completeness test.
        row["TaxAmount"]              = line.TaxAmount.HasValue
                                            ? line.TaxAmount.Value.ToString(CultureInfo.InvariantCulture)
                                            : string.Empty;
        row["ManufacturerPartNumber"] = line.ManufacturerPartNumber ?? string.Empty;
        row["ManufacturerName"]       = line.ManufacturerName ?? string.Empty;
        row["CustomerPartNumber"]     = line.CustomerPartNumber ?? string.Empty;
        row["DiscountPercent"]        = line.DiscountPercent.HasValue
                                            ? line.DiscountPercent.Value.ToString(CultureInfo.InvariantCulture)
                                            : string.Empty;
        row["Unspsc"]                 = line.Unspsc ?? string.Empty;
        row["Recipient"]              = line.Recipient ?? string.Empty;
        row["ContractNumber"]         = line.ContractNumber ?? string.Empty;
        row["NetAmount"]              = line.NetAmount.HasValue
                                            ? line.NetAmount.Value.ToString(CultureInfo.InvariantCulture)
                                            : string.Empty;

        foreach (var cf in @override.CustomFields)
        {
            if (!string.Equals(cf.Scope, "line", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(cf.Key)) continue;
            var val = cf.LineValues is not null && cf.LineValues.TryGetValue(line.LineNumber, out var lv)
                ? lv
                : string.Empty;
            row[cf.Key] = val;
        }

        InjectCatalogRow(row, line, catalogLookup);

        // F-1 Seam A: inject ONLY this line's source tokens. The line's position is its 1-based rank in
        // the LineNumber-ordered sequence (so the n-th data row's tokens land in the n-th line's bag,
        // robust to LineNumber gaps). A line token addressing a different ordinal is skipped here — it
        // is never read by this line, so line 2 can never see line 1's value.
        if (sourceTokens is { Count: > 0 })
        {
            var position = LinePosition(order, line);
            foreach (var t in sourceTokens)
            {
                if (t is null || string.IsNullOrEmpty(t.Id)) continue;
                if (!string.Equals(t.Group, "line", StringComparison.Ordinal)) continue;
                var ordinal = SourceTokenLineIndexer.LineOrdinalOf(t.Id);
                if (ordinal is null || ordinal.Value != position) continue;
                row[$"src::{t.Id}"] = t.Value ?? string.Empty;

                // F-1 Phase 4: ALSO write a RELATIVE alias whose key is identical across lines (the
                // ordinal stripped). One rule bound to the relative key then resolves to THIS line's own
                // value — so a repeating non-canonical column can be bound once for EVERY line. Inert
                // (byte-identical) unless a rule actually names the relative key.
                var rel = SourceTokenLineIndexer.RelativeLineKey(t.Id);
                if (rel is not null) row[$"src::{rel}"] = t.Value ?? string.Empty;
            }
        }

        return row;
    }

    /// <summary>
    /// The 1-based position of <paramref name="line"/> among the order's lines, ordered by
    /// <c>LineNumber</c> (the same ordering the CSV/JSON/emitter row loops use). This is matched against
    /// a source token's addressed ordinal so per-line tokens land in the correct line's bag. Falls back
    /// to <c>1</c> if the line is somehow not found in the collection (defensive; never throws).
    /// </summary>
    private static int LinePosition(PurchaseOrderEntity order, PurchaseOrderLineEntity line)
    {
        var pos = 0;
        foreach (var l in order.Lines.OrderBy(x => x.LineNumber))
        {
            pos++;
            if (ReferenceEquals(l, line)) return pos;
        }
        return 1;
    }

    /// <summary>
    /// Pre-injects the matched catalog row's fields into <paramref name="row"/> under the reserved
    /// keys the <c>LoadCatalogProduct</c> manipulator reads, so that manipulator resolves REAL values
    /// on the native CSV/JSON path. Resolution mirrors <c>ScribanOrderModel.BuildLine</c>: match by
    /// the line's resolved supplier item code first, then its manufacturer part number, against the
    /// pre-loaded (org+supplier-scoped) lookup. No match / null lookup → no keys added → the
    /// manipulator returns "" exactly as before. Price is a typed <see cref="decimal"/> column
    /// formatted with <see cref="CultureInfo.InvariantCulture"/> (a clean machine value — never
    /// re-parsed from a locale string).
    /// </summary>
    private static void InjectCatalogRow(
        Dictionary<string, string> row,
        PurchaseOrderLineEntity line,
        IReadOnlyDictionary<string, SupplierProduct>? catalogLookup)
    {
        if (catalogLookup is null) return;

        SupplierProduct? product = null;
        if (!string.IsNullOrWhiteSpace(line.SupplierItemCode))
            catalogLookup.TryGetValue(line.SupplierItemCode, out product);
        if (product is null && !string.IsNullOrWhiteSpace(line.ManufacturerPartNumber))
            catalogLookup.TryGetValue(line.ManufacturerPartNumber, out product);

        if (product is null) return;

        // Reserved keys consumed by LoadCatalogProductManipulator. Price is the only numeric field —
        // format it invariantly (machine value), the rest are raw strings.
        row["__catalog_price"]   = product.Price.HasValue
                                       ? product.Price.Value.ToString(CultureInfo.InvariantCulture)
                                       : string.Empty;
        row["__catalog_code"]    = product.Code ?? string.Empty;
        row["__catalog_unit"]    = product.Unit ?? string.Empty;
        row["__catalog_barcode"] = product.Barcode ?? string.Empty;
    }

    // ── Shared with the fixed transforms ─────────────────────────────────────────

    /// <summary>
    /// Same unresolved-line guard as <c>CsvTransformService</c> / <c>JsonTransformService</c>, PLUS a
    /// corrupt-total guard: a line whose <c>Quantity × UnitPrice</c> overflows the decimal range is
    /// corrupt, not legitimately zero. Rather than let the row builder silently degrade that total to
    /// 0 and DELIVER a financially-wrong document, such lines are HELD for review here — the same
    /// "flag the line for review" mechanism (<see cref="TransformValidationException"/>) the
    /// unresolved-line guard uses, with a clear per-line problem message.
    /// </summary>
    private static void ValidateOrder(PurchaseOrderEntity order)
    {
        var unresolved = order.Lines
            .Where(l => l.NeedsReview || string.IsNullOrWhiteSpace(l.SupplierItemCode))
            .Select(l => l.LineNumber)
            .OrderBy(n => n)
            .ToList();

        if (unresolved.Count > 0)
            throw new TransformValidationException(unresolved);

        GuardLineSumOverflow(order);
    }

    /// <summary>
    /// Holds an order for review when any line's <c>Quantity × UnitPrice</c> overflows the decimal
    /// range. A corrupt total must never be DELIVERED as a silent 0 (the founder-bug class of "delivers
    /// blind"), so this surfaces a clear <see cref="TransformValidationException"/> (the standard hold
    /// mechanism) instead. Also logs a warning so the rejection is observable. The vast majority of
    /// orders never trip this; for them it is a couple of multiplies and a no-op.
    /// </summary>
    private static void GuardLineSumOverflow(PurchaseOrderEntity order)
    {
        List<LineProblem>? problems = null;
        foreach (var l in order.Lines)
        {
            try
            {
                _ = l.Quantity * l.UnitPrice;
            }
            catch (OverflowException ex)
            {
                (problems ??= new List<LineProblem>()).Add(new LineProblem(
                    l.LineNumber, LineProblemKind.MissingOrZeroPrice,
                    $"Line {l.LineNumber}: extended amount (quantity × unit price) overflows the " +
                    "supported numeric range; held for review to avoid delivering a corrupt total."));

                TransformDiagnostics.CreateLogger(nameof(MappedTransformService)).LogWarning(
                    ex,
                    "Order {OrderId} (PO {PoNumber}) line {LineNumber}: quantity ({Quantity}) × unit " +
                    "price ({UnitPrice}) overflowed decimal; holding the order for review rather than " +
                    "delivering a degraded 0 total.",
                    order.Id, order.PoNumber, l.LineNumber, l.Quantity, l.UnitPrice);
            }
        }

        if (problems is { Count: > 0 })
        {
            var lineNumbers = problems.Select(p => p.LineNumber).Distinct().OrderBy(n => n).ToList();
            throw new TransformValidationException(lineNumbers, problems);
        }
    }

    /// <summary>
    /// Overflow-safe decimal multiply for the derived line/extended amounts. A pathological
    /// quantity × unit-price can overflow <see cref="decimal"/>; the row bag must never throw on the
    /// CSV/JSON build path, so an overflow degrades to 0 rather than crashing the preview/transform.
    /// The native delivery path already holds such an order for review (GuardLineSumOverflow) BEFORE
    /// reaching here, so a corrupt amount cannot be delivered as 0; this 0 fallback only protects the
    /// non-delivery reuse paths (preview / OutputTemplateEmitter), and the degradation is logged so it
    /// is observable rather than silent.
    /// </summary>
    private static decimal SafeMultiply(decimal a, decimal b)
    {
        try { return a * b; }
        catch (OverflowException ex)
        {
            TransformDiagnostics.CreateLogger(nameof(MappedTransformService)).LogWarning(
                ex,
                "Derived amount {A} × {B} overflowed decimal range; row value degraded to 0 for this " +
                "non-delivery row build.",
                a, b);
            return 0m;
        }
    }

    /// <summary>RFC 4180: wrap in double-quotes if the value contains comma, quote, or newline. Internal so the OutputNode CSV emitter escapes byte-identically.</summary>
    internal static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
