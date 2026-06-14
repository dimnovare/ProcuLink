namespace ProcuLink.Core.Entities;

/// <summary>
/// Group V4 — the well-known GLOBAL rule catalog, expressed as concrete <see cref="RuleDefinition"/>
/// seeds. This is the bridge the North Star asks for: the previously DESCRIPTIVE catalog (docs /
/// non-executable) and the standards references in <c>docs/standards-matrix.md</c> become real
/// definitions an org can BIND to. Each org gets its own copy of these seeds (org-scoped), so an
/// org can extend / override its catalog without affecting another tenant.
///
/// <para>
/// Every operator used here is one the existing <c>SupplierAcceptanceService</c> executor already
/// understands — no new operators are introduced. <see cref="CodeFor"/> is the deterministic
/// <c>{fieldPath}.{operator}</c> key the backfill uses to match existing free-floating rules to a
/// definition.
/// </para>
/// </summary>
public static class RuleCatalog
{
    /// <summary>The deterministic, org-unique code for a (fieldPath, operator) pair.</summary>
    public static string CodeFor(string fieldPath, string @operator) => $"{fieldPath}.{@operator}";

    /// <summary>
    /// A template for a seeded definition (org-independent shape). The seeder materialises one
    /// <see cref="RuleDefinition"/> per org from each template.
    /// </summary>
    public sealed record CatalogEntry(
        string Code, string Title, string? Description,
        string Scope, string FieldPath, string Operator,
        string DefaultSeverity, string? DefaultExpectedValue, string? ParamHint,
        string? UblRef, string? EdifactRef, string? X12Ref, string? CxmlRef);

    private static CatalogEntry Entry(
        string scope, string fieldPath, string @operator, string title, string? description,
        string defaultSeverity = "error", string? defaultExpectedValue = null, string? paramHint = null,
        string? ubl = null, string? edifact = null, string? x12 = null, string? cxml = null) =>
        new(CodeFor(fieldPath, @operator), title, description, scope, fieldPath, @operator,
            defaultSeverity, defaultExpectedValue, paramHint, ubl, edifact, x12, cxml);

    /// <summary>
    /// The seeded definitions, in stable order. Standards refs mirror
    /// <c>docs/standards-matrix.md § Canonical PO Model fields</c>.
    /// </summary>
    public static readonly IReadOnlyList<CatalogEntry> Entries = new List<CatalogEntry>
    {
        // ── Line scope ────────────────────────────────────────────────────────
        Entry("line", "supplierItemCode", "required",
            "Supplier item code is required",
            "The supplier's own item code must be resolved before delivery. Unresolved lines go to review.",
            ubl: "cac:Item/cac:SellersItemIdentification/cbc:ID",
            edifact: "LIN C212 (SA)", x12: "PO107/PO109 (vendor-qualified)",
            cxml: "ItemID/SupplierPartID"),

        Entry("line", "buyerItemCode", "required",
            "Buyer item code is required",
            "The buyer's own item code is the mapping lookup key; it must be present on every line.",
            ubl: "cac:Item/cac:BuyersItemIdentification/cbc:ID",
            edifact: "LIN C212 (IN)", x12: "PO107/PO109 (buyer-qualified)",
            cxml: "ItemID/BuyerPartID"),

        Entry("line", "description", "required",
            "Line description is required",
            "Some suppliers reject lines without a free-text item description.",
            defaultSeverity: "warning",
            ubl: "cac:Item/cbc:Description", edifact: "IMD C273/7008",
            x12: "PID05", cxml: "ItemDetail/Description"),

        Entry("line", "quantity", "min",
            "Quantity at least",
            "Reject (or warn on) lines whose quantity is below a supplier minimum.",
            defaultExpectedValue: "1", paramHint: "Numeric minimum, e.g. 1",
            ubl: "cbc:Quantity", edifact: "QTY C186/6060", x12: "PO102", cxml: "ItemOut/@quantity"),

        Entry("line", "quantity", "greater_than",
            "Quantity greater than",
            "Reject lines whose quantity is not strictly greater than a threshold (e.g. zero).",
            defaultExpectedValue: "0", paramHint: "Numeric threshold, e.g. 0",
            ubl: "cbc:Quantity", edifact: "QTY C186/6060", x12: "PO102", cxml: "ItemOut/@quantity"),

        Entry("line", "unitPrice", "required",
            "Unit price is required",
            "CSV / XML / cXML transforms require a unit price on each line.",
            ubl: "cac:Price/cbc:PriceAmount", edifact: "PRI C509/5118",
            x12: "PO104", cxml: "ItemOut/UnitPrice/Money"),

        Entry("line", "unitPrice", "greater_than",
            "Unit price greater than",
            "Reject lines whose unit price is not strictly greater than a threshold.",
            defaultExpectedValue: "0", paramHint: "Numeric threshold, e.g. 0",
            ubl: "cac:Price/cbc:PriceAmount", edifact: "PRI C509/5118",
            x12: "PO104", cxml: "ItemOut/UnitPrice/Money"),

        Entry("line", "unitPrice", "max",
            "Unit price at most",
            "Warn when a unit price exceeds an expected ceiling (catch decimal / currency errors).",
            defaultSeverity: "warning", defaultExpectedValue: "100000",
            paramHint: "Numeric maximum",
            ubl: "cac:Price/cbc:PriceAmount", edifact: "PRI C509/5118",
            x12: "PO104", cxml: "ItemOut/UnitPrice/Money"),

        // ── Order scope ───────────────────────────────────────────────────────
        Entry("order", "currency", "required",
            "Currency is required",
            "An ISO 4217 currency code must be present on the order.",
            ubl: "cbc:DocumentCurrencyCode", edifact: "CUX C504/6347",
            x12: "CUR02", cxml: "Total/Money/@currency"),

        Entry("order", "currency", "in",
            "Currency in allowed list",
            "Restrict the order currency to the set this supplier accepts.",
            defaultExpectedValue: "EUR,USD,GBP",
            paramHint: "Comma-separated ISO 4217 codes, e.g. EUR,USD",
            ubl: "cbc:DocumentCurrencyCode", edifact: "CUX C504/6347",
            x12: "CUR02", cxml: "Total/Money/@currency"),

        Entry("order", "currency", "equals",
            "Currency equals",
            "Require a single fixed currency (e.g. a EUR-only supplier).",
            defaultExpectedValue: "EUR", paramHint: "ISO 4217 code, e.g. EUR",
            ubl: "cbc:DocumentCurrencyCode", edifact: "CUX C504/6347",
            x12: "CUR02", cxml: "Total/Money/@currency"),

        Entry("order", "buyerName", "required",
            "Buyer name is required",
            "Some suppliers require the buying entity's name for routing / audit.",
            defaultSeverity: "warning",
            ubl: "cac:BuyerCustomerParty/cac:Party/cac:PartyName/cbc:Name",
            edifact: "NAD BY", x12: "N1*BY", cxml: "Contact[@role='buyer']/Name"),

        // ── Phase 2 (D slice) lossless-mapping validation seeds ─────────────────
        // All advisory (warning): printed-date / label / VAT formats evolve — flag for review, never
        // hard-block. date_sanity reads the ORIGINAL printed string from the lossless SourceCapture
        // raw bag (resolved via the order-scope field path "sourceDate"); there is no typed raw-date
        // column. not_label / vat_format resolve from the first shipTo party.
        Entry("order", "sourceDate", "date_sanity",
            "Delivery date is unambiguous",
            "Flag printed dates where day and month are both ≤ 12 (MM/DD vs DD/MM flip risk, e.g. 06/12). Reads the original printed string from the lossless source capture.",
            defaultSeverity: "warning",
            ubl: "cbc:RequestedDeliveryPeriod/cbc:StartDate", edifact: "DTM C507/2380",
            x12: "DTM02", cxml: "DeliveryDate"),

        Entry("order", "shipToCity", "not_label",
            "Ship-to city is not a label",
            "Catch a parser that swept a label cell (e.g. 'UIDNr', 'City') into the ship-to city.",
            defaultSeverity: "warning", defaultExpectedValue: "City,VAT,UID,UIDNr,Label,Tel,Fax",
            paramHint: "Comma-separated label words to reject",
            ubl: "cac:Delivery/cac:DeliveryLocation/cac:Address/cbc:CityName",
            edifact: "NAD DP C059/3164", x12: "N4*01", cxml: "ShipTo/Address/City"),

        Entry("line", "lineAmount", "line_amount_reconcile",
            "Line amount reconciles with qty × price",
            "Reject lines where the printed line amount diverges from quantity × unit price beyond tolerance.",
            defaultSeverity: "warning", defaultExpectedValue: "0.01",
            paramHint: "Absolute tolerance, e.g. 0.01",
            ubl: "cbc:LineExtensionAmount", edifact: "MOA C516/5004",
            x12: "PO103", cxml: "ItemOut/@lineNumber"),

        Entry("order", "shipToVat", "vat_format",
            "Ship-to VAT id is well-formed",
            "Check the ship-to VAT id has a plausible country prefix + length (advisory shape check, not a checksum).",
            defaultSeverity: "warning",
            ubl: "cac:PartyTaxScheme/cbc:CompanyID", edifact: "RFF VA",
            x12: "REF*VX", cxml: "Party/IdReference[@domain='vat']"),
    };
}
