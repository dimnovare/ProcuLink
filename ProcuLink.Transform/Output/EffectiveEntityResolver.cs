using System.Globalization;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Tokenizing;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Resolves a per-order <see cref="OrderMappingOverride"/> into an <b>effective</b>
/// <see cref="PurchaseOrderEntity"/> whose <i>typed canonical columns</i> already carry the
/// overridden values, so the EXISTING structured-format transforms
/// (<see cref="XmlTransformService"/> / <see cref="CxmlTransformService"/> /
/// <see cref="UblOrderTransformService"/> / <see cref="X12TransformService"/>) can be reused
/// verbatim — no per-format override re-implementation.
///
/// <para><b>Why this exists.</b> The native override builder (<see cref="MappedTransformService"/>)
/// emits CSV/JSON itself from arbitrary output-field paths. The structured formats have a FIXED
/// document shape (a UBL <c>cbc:ID</c>, an X12 <c>BEG03</c>, …), so an arbitrary output path can't
/// be honoured there. What an override CAN do for those formats is change the <i>canonical values</i>
/// that feed the fixed shape. This resolver does exactly that: it runs the same override-apply logic
/// (SourceMap re-derive → output-rule value resolution with Scriban/fixed/manipulators, identical to
/// <see cref="MappedTransformService.ResolveRule"/>) and writes the result back onto a CLONE of the
/// entity's typed properties.</para>
///
/// <para><b>Mapping back to typed columns.</b> Only output rules whose <i>target canonical field</i>
/// is one the typed columns recognise are applied. The target field is matched by the rule's
/// <see cref="OutputFieldRule.OutputPath"/> first, then its dictionary key — case-insensitively —
/// against the recognised header / line field names. An output rule that targets a custom or
/// unrecognised path is ignored for the structured formats (those formats have no slot for it),
/// EXACTLY mirroring how the fixed transform would never emit such a field. SourceMap rules and
/// custom fields still participate as the value source / sibling bag.</para>
///
/// <para><b>Additive + safe.</b> When the override carries no SourceMap and no output rule that
/// targets a recognised canonical field, the returned entity is a value-identical clone — so the
/// downstream fixed transform produces byte-for-byte identical output to the no-override path. A
/// numeric/date value that fails to parse is left at its original typed value (never corrupts the
/// column). The clone is detached (never an EF-tracked instance), so callers can pass a tracked
/// order in without risk of mutating it.</para>
/// </summary>
public static class EffectiveEntityResolver
{
    // Recognised header canonical fields that map onto typed columns.
    // V5: added "RequestedDeliveryDate" (DateOnly?; nullable — a parse failure leaves it null rather than the original).
    //
    // WP-14 deliberately did NOT widen these two sets, even though the row bag grew to the full
    // business field set. This list is the WRITE-BACK set — the names whose output rules OVERWRITE a
    // typed column for the fixed-shape formats — and widening it would not be additive:
    // ResolveTargetField matches a rule by its OutputPath, so an existing customer whose cXML output
    // column happens to be named "ShipToCity" or "Incoterms" (natural column names) would suddenly
    // have that rule rewrite the real column and change the document they receive today. Binding a
    // new name EMITS it everywhere; it never rewrites the entity. Pinned by
    // EffectiveEntityResolverCarriesWidenedFieldsTests.AnOutputColumnNamedAfterANewCanonicalField_*.
    private static readonly HashSet<string> HeaderFields =
        new(StringComparer.OrdinalIgnoreCase) { "PoNumber", "OrderDate", "Currency", "SupplierName", "BuyerName", "RequestedDeliveryDate" };

    // Recognised line canonical fields that map onto typed columns.
    private static readonly HashSet<string> LineFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "LineNumber", "BuyerItemCode", "SupplierItemCode", "Description", "Quantity", "Unit", "UnitPrice",
    };

    /// <summary>
    /// Build an effective entity with the override's canonical-field changes applied to the typed
    /// columns. The input entity is never mutated. <paramref name="sourceTokens"/> is optional and
    /// only used when the override carries a <see cref="OrderMappingOverride.SourceMap"/>.
    /// </summary>
    public static PurchaseOrderEntity Resolve(
        PurchaseOrderEntity         order,
        OrderMappingOverride        @override,
        IReadOnlyList<SourceToken>? sourceTokens = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(@override);

        IReadOnlyList<SourceToken> tokens = sourceTokens ?? Array.Empty<SourceToken>();

        var clone = Clone(order);

        // Only re-derive from a SourceMap when one is actually present, so the no-SourceMap path never
        // round-trips typed values through string formatting (byte-for-byte identical clone).
        var hasSourceMap = @override.SourceMap is { Count: > 0 };

        // ── Header ────────────────────────────────────────────────────────────
        // SourceMap re-derive runs first (no-op when absent), then output-rule overrides that target
        // recognised header canonical fields are written back onto the clone's typed columns.
        var headerRow = SourceMapReDerive.ApplyToHeaderRow(
            MappedTransformService.BuildHeaderRow(order, @override, tokens), @override, tokens);

        // Apply SourceMap-derived header values onto typed columns (SourceMap can change PoNumber etc.
        // even with no Output rules). Skipped when there is no SourceMap.
        if (hasSourceMap)
            ApplyHeaderRowToEntity(clone, headerRow);

        if (@override.Output is { } output)
        {
            foreach (var (key, rule) in output.Header)
            {
                var target = ResolveTargetField(rule, key, HeaderFields);
                if (target is null) continue;
                var value = MappedTransformService.ResolveRule(rule, headerRow, lineScope: false) ?? string.Empty;
                ApplyHeaderField(clone, target, value);
            }
        }

        // ── Lines ─────────────────────────────────────────────────────────────
        foreach (var line in clone.Lines)
        {
            var lineRow = SourceMapReDerive.ApplyToLineRow(
                MappedTransformService.BuildLineRow(order, @override, line, catalogLookup: null, sourceTokens: tokens), @override, tokens);

            if (hasSourceMap)
                ApplyLineRowToEntity(line, lineRow);

            if (@override.Output is { } lineOutput)
            {
                foreach (var (key, rule) in lineOutput.Lines)
                {
                    var target = ResolveTargetField(rule, key, LineFields);
                    if (target is null) continue;
                    var value = MappedTransformService.ResolveRule(rule, lineRow, lineScope: true) ?? string.Empty;
                    ApplyLineField(line, target, value);
                }
            }
        }

        return clone;
    }

    // ── Target-field matching ────────────────────────────────────────────────

    /// <summary>
    /// Decide which recognised canonical field (if any) this output rule targets. The structured
    /// formats have a fixed shape keyed by canonical name, so we match on the rule's
    /// <see cref="OutputFieldRule.OutputPath"/> first, then its dictionary key — both
    /// case-insensitively — against <paramref name="recognised"/>. Returns the canonical key from
    /// <paramref name="recognised"/> (canonical casing) or null when the rule targets something the
    /// typed columns don't model.
    /// </summary>
    private static string? ResolveTargetField(
        OutputFieldRule rule, string ruleKey, HashSet<string> recognised)
    {
        if (!string.IsNullOrWhiteSpace(rule.OutputPath) && recognised.TryGetValue(rule.OutputPath, out var byPath))
            return byPath;
        if (!string.IsNullOrWhiteSpace(ruleKey) && recognised.TryGetValue(ruleKey, out var byKey))
            return byKey;
        return null;
    }

    // ── Apply derived rows onto typed columns ────────────────────────────────

    private static void ApplyHeaderRowToEntity(PurchaseOrderEntity clone, IReadOnlyDictionary<string, string> row)
    {
        foreach (var field in HeaderFields)
            if (row.TryGetValue(field, out var v))
                ApplyHeaderField(clone, field, v);
    }

    private static void ApplyLineRowToEntity(PurchaseOrderLineEntity line, IReadOnlyDictionary<string, string> row)
    {
        foreach (var field in LineFields)
            if (row.TryGetValue(field, out var v))
                ApplyLineField(line, field, v);
    }

    private static void ApplyHeaderField(PurchaseOrderEntity clone, string field, string value)
    {
        switch (field.ToLowerInvariant())
        {
            case "ponumber":
                clone.PoNumber = value;
                break;
            case "currency":
                clone.Currency = value;
                break;
            case "buyername":
                clone.BuyerName = value;
                break;
            case "suppliername":
                // Both the printed supplier name AND the resolved Supplier.Name feed the structured
                // transforms (XML/UBL read Supplier?.Name; keep them consistent).
                clone.SupplierName = value;
                if (clone.Supplier is not null)
                    clone.Supplier.Name = value;
                break;
            case "orderdate":
                if (TryParseDate(value, out var d))
                    clone.OrderDate = d;
                break;
            case "requesteddeliverydate":
                // V5: nullable — a blank value clears the field; a parse failure leaves original.
                if (string.IsNullOrWhiteSpace(value))
                    clone.RequestedDeliveryDate = null;
                else if (TryParseDate(value, out var rd))
                    clone.RequestedDeliveryDate = rd;
                break;
        }
    }

    private static void ApplyLineField(PurchaseOrderLineEntity line, string field, string value)
    {
        switch (field.ToLowerInvariant())
        {
            case "buyeritemcode":
                line.BuyerItemCode = value;
                break;
            case "supplieritemcode":
                line.SupplierItemCode = value;
                break;
            case "description":
                line.Description = value;
                break;
            case "unit":
                line.Unit = value;
                break;
            case "linenumber":
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ln))
                    line.LineNumber = ln;
                break;
            case "quantity":
                if (TryParseDecimal(value, out var qty))
                    line.Quantity = qty;
                break;
            case "unitprice":
                if (TryParseDecimal(value, out var up))
                    line.UnitPrice = up;
                break;
        }
    }

    // ── Parsing helpers (lenient — keep the original typed value on failure) ──

    private static bool TryParseDecimal(string value, out decimal result) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out result);

    private static bool TryParseDate(string value, out DateOnly result)
    {
        if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
            return true;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            result = DateOnly.FromDateTime(dt);
            return true;
        }
        result = default;
        return false;
    }

    // ── Detached clone ───────────────────────────────────────────────────────

    /// <summary>
    /// Shallow value-clone of the entity + its lines (the only state the structured transforms read),
    /// plus a clone of the <see cref="Supplier"/> nav (so overriding SupplierName never mutates the
    /// tracked supplier). Other navigation properties are left null — the transforms don't read them.
    /// </summary>
    private static PurchaseOrderEntity Clone(PurchaseOrderEntity src)
    {
        var clone = new PurchaseOrderEntity
        {
            Id            = src.Id,
            OrgId         = src.OrgId,
            SupplierId    = src.SupplierId,
            PoNumber      = src.PoNumber,
            BuyerName     = src.BuyerName,
            OrderDate     = src.OrderDate,
            Currency      = src.Currency,
            Status        = src.Status,
            SourceFileKey = src.SourceFileKey,
            CanonicalJson = src.CanonicalJson,
            CreatedAt     = src.CreatedAt,
            UpdatedAt     = src.UpdatedAt,
            IsSample      = src.IsSample,
            SupplierName          = src.SupplierName,
            SubTotal              = src.SubTotal,
            TaxTotal              = src.TaxTotal,
            GrandTotal            = src.GrandTotal,
            PaymentTerms          = src.PaymentTerms,
            DocumentType          = src.DocumentType,
            // V5: requested delivery date (in-memory only; not a DB column).
            RequestedDeliveryDate = src.RequestedDeliveryDate,
            // Buyer tax id: the cXML transform reads it for the From/Identity. NOT override-targetable,
            // so carried through verbatim — omitting it here would silently revert the From identity to
            // the configured/legacy value on the preview + delivery path (exactly the address-block bug).
            BuyerTaxId        = src.BuyerTaxId,
            // Contact + ship-to/bill-to address columns: the structured transforms (cXML/UBL/X12/XML)
            // read these to emit <Contact>/<ShipTo>/<BillTo>. They are NOT override-targetable canonical
            // fields, so they're carried through verbatim — omitting them here silently drops every
            // address/contact block on the preview + delivery path (the override/structured path), even
            // though the columns are populated on the source order.
            ContactName       = src.ContactName,
            ContactEmail      = src.ContactEmail,
            ContactPhone      = src.ContactPhone,
            // WP-14: bindable header columns that this clone used to drop. The structured path
            // (XML/cXML/UBL/X12) renders from the CLONE and builds its row bag from the clone's
            // values, so a column missing here arrives EMPTY at a real supplier with no error
            // raised anywhere — the CSV/JSON native path would look perfect while cXML shipped
            // blanks. Carried verbatim; they are not override-targetable (see HeaderFields).
            Incoterms         = src.Incoterms,
            ShippingMethod    = src.ShippingMethod,
            BuyerOrderRef     = src.BuyerOrderRef,
            ShipToName        = src.ShipToName,
            ShipToDeliverTo   = src.ShipToDeliverTo,
            ShipToStreet      = src.ShipToStreet,
            ShipToCity        = src.ShipToCity,
            ShipToPostalCode  = src.ShipToPostalCode,
            ShipToCountry     = src.ShipToCountry,
            ShipToEmail       = src.ShipToEmail,
            ShipToPhone       = src.ShipToPhone,
            BillToName        = src.BillToName,
            BillToDeliverTo   = src.BillToDeliverTo,
            BillToStreet      = src.BillToStreet,
            BillToCity        = src.BillToCity,
            BillToPostalCode  = src.BillToPostalCode,
            BillToCountry     = src.BillToCountry,
            BillToEmail       = src.BillToEmail,
            BillToPhone       = src.BillToPhone,
        };

        if (src.Supplier is not null)
        {
            clone.Supplier = new Supplier
            {
                Id    = src.Supplier.Id,
                OrgId = src.Supplier.OrgId,
                Name  = src.Supplier.Name,
                Code  = src.Supplier.Code,
            };
        }

        clone.Lines = src.Lines
            .Select(l => new PurchaseOrderLineEntity
            {
                Id               = l.Id,
                OrderId          = l.OrderId,
                LineNumber       = l.LineNumber,
                BuyerItemCode    = l.BuyerItemCode,
                SupplierItemCode = l.SupplierItemCode,
                Description      = l.Description,
                Quantity         = l.Quantity,
                Unit             = l.Unit,
                UnitPrice        = l.UnitPrice,
                Confidence       = l.Confidence,
                NeedsReview      = l.NeedsReview,
                LineAmount       = l.LineAmount,
                TaxRate          = l.TaxRate,
                // Per-line VAT amount: the cXML transform emits <ItemDetail>/<Tax> from it. NOT
                // override-targetable; carried verbatim so the structured preview + delivery path keeps it.
                TaxAmount        = l.TaxAmount,
                DeliveryDate     = l.DeliveryDate,
                // WP-14: bindable LINE columns this clone used to drop. Same failure as the header
                // block above, one level down: a rule binding ManufacturerPartNumber or Unspsc on a
                // structured-format supplier resolved against the clone's line and rendered empty,
                // while the identical rule on a CSV supplier worked. Carried verbatim.
                DiscountPercent        = l.DiscountPercent,
                NetAmount              = l.NetAmount,
                ManufacturerPartNumber = l.ManufacturerPartNumber,
                ManufacturerName       = l.ManufacturerName,
                CustomerPartNumber     = l.CustomerPartNumber,
                Unspsc                 = l.Unspsc,
                Recipient              = l.Recipient,
                ContractNumber         = l.ContractNumber,
            })
            .ToList();

        return clone;
    }
}
