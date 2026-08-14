using System.Globalization;
using System.Text;
using System.Xml.Linq;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Peppol wedge — Track A (ProcuLink-builds, OUTBOUND/generation side).
///
/// Generates a Peppol BIS Billing 3.0 UBL 2.1 Invoice from an approved
/// <see cref="InvoiceEntity"/>. This is purely the document-GENERATION leg.
///
/// HONEST SCOPE (offer⇔works):
///   * DECLARED BUT NOT VERIFIED — <c>cbc:CustomizationID</c> (BT-24) and <c>cbc:ProfileID</c>.
///     These two are emitted, and nothing in either repo checks that the document they sit on
///     actually satisfies the profile they name. They are written anyway, and the distinction
///     matters, so it is stated rather than left to be inferred:
///
///       They are the document's TYPE, not a quality claim. Peppol composes the document type
///       identifier as "&lt;syntax specific id&gt;##&lt;customization id&gt;::&lt;version&gt;" (Peppol Policy for
///       use of Identifiers 4.4.0, POLICY 20), and that identifier is the SMP lookup key and the
///       AS4 Action. The CustomizationID is literally a substring of it. A document without one
///       is therefore not a leniently-declared invoice — it is one a sender access point cannot
///       address at all, before any validation runs. Dropping it also trips two fatal Schematron
///       asserts, BR-01 (CEN) and PEPPOL-EN16931-R004; dropping ProfileID trips
///       PEPPOL-EN16931-R001 and R007.
///
///       DO NOT "fix" this by mirroring the UBL ORDER path, and do not re-derive the reason.
///       That path removed the same two elements (UblOrderTransformService,
///       UblOrderDeclaresNoPeppolProfileTests) and it is tempting to read the two decisions as
///       inconsistent. They are not, and the tempting justification for the difference is FALSE:
///       an order without a CustomizationID is NOT "merely undeclared" where an invoice would be
///       rejected. Checked against the sources on 2026-08-14 — Peppol BIS Order-only 3 makes both
///       elements 1..1 and PEPPOL-T01-B00101/B00102 are equally fatal, and POLICY 20 is scoped to
///       all business documents, so an order without one is equally unroutable.
///
///       The actual difference is a PRODUCT one. The order path stopped offering Peppol output
///       entirely (src/lib/standards/catalog.ts: transform "planned", "MUST NOT BE ADVERTISED"),
///       so stripping the ids left a plain OASIS UBL 2.1 Order — independently useful, since
///       ProcuLink delivers orders over HTTPS/email/SFTP rather than the Peppol network. This
///       format token is named "peppol" and has no such fallback: a caller asking for it and
///       receiving a profile-less document gets an artifact with no consumer. So the choice here
///       is to keep the declaration and be explicit that it is unverified, or to withdraw the
///       format. It is NOT to emit an unaddressable document under the name "peppol".
///
///       What was removed instead is the appearance of verification: PeppolBisValidator used to
///       compare both elements against the constants below, on a document this class had just
///       written from those same constants, and could not fail. See that class, and
///       PeppolBisInvoiceConformanceIsUnverifiedTests.
///
///       Treat the emitted file as INPUT to an access point's validator, never as a conformance
///       result. Nothing here has been accepted by a live access point.
///
///   * COVERED — the structural / high-value mandatory business terms that make a
///     document recognisably a BIS Billing 3.0 invoice and that we can populate
///     from the canonical <see cref="InvoiceEntity"/> + <see cref="PeppolPartyOptions"/>:
///       BT-1   Invoice number          (cbc:ID)
///       BT-2   Issue date              (cbc:IssueDate)
///       BT-3   Invoice type code 380   (cbc:InvoiceTypeCode)
///       BT-5   Document currency       (cbc:DocumentCurrencyCode)
///       BT-9   Payment due date        (cbc:DueDate)
///       BT-10  Buyer reference         (cbc:BuyerReference)
///       BT-34  Seller endpoint ID      (AccountingSupplierParty EndpointID)
///       BT-49  Buyer  endpoint ID      (AccountingCustomerParty EndpointID)
///       BT-27/BT-44 Seller/Buyer name
///       BT-31/BT-48 Seller/Buyer VAT identifier (when supplied)
///       BG-22  Document totals         (cac:LegalMonetaryTotal — BT-106/109/112/115)
///       BG-23  VAT breakdown           (cac:TaxTotal/TaxSubtotal + TaxCategory, BT-110/116/118/119)
///       BG-25  Invoice line            (cac:InvoiceLine — BT-126/129/131/146/153)
///
///   * NOT COVERED (documented as future work — do NOT claim conformance):
///       - Full Schematron / EN 16931 + PEPPOL-EN16931-* rule validation
///         (we ship a lightweight mandatory-field checker, see PeppolBisValidator).
///       - Scheme-ID correctness for EndpointID/PartyIdentification (EAS / ISO 6523),
///         tax-scheme code lists, allowances/charges, delivery party, payment means
///         details (IBAN/BIC), embedded attachments, multi-currency tax, rounding rules.
///       - AS4 / Peppol Access Point TRANSPORT — that is Track B (partner-wrap), out of scope.
///
/// The generator is deterministic and writes only what it can faithfully source;
/// it never invents identifiers. Missing recommended-but-absent fields are simply
/// omitted, and the companion <see cref="PeppolBisValidator"/> reports them so a
/// caller knows the document is not yet network-ready.
/// </summary>
public sealed class PeppolBisInvoiceTransformService : IInvoiceTransformService
{
    /// <summary>
    /// Format token routed by InvoiceService.ForwardAsync. "peppol" is the public
    /// name; the document produced is BIS Billing 3.0 UBL 2.1.
    /// </summary>
    public string Format => "peppol";

    // ── BIS Billing 3.0 fixed identifiers (BT-24 / ProfileID) ──────────────────
    public const string CustomizationId =
        "urn:cen.eu:en16931:2017#compliant#urn:fdc:peppol.eu:2017:poacc:billing:3.0";
    public const string ProfileId =
        "urn:fdc:peppol.eu:2017:poacc:billing:01:1.0";

    // ── UBL 2.1 namespaces ─────────────────────────────────────────────────────
    private static readonly XNamespace UblNs =
        "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    private static readonly XNamespace CbcNs =
        "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace CacNs =
        "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";

    private readonly PeppolPartyOptions _opts;

    /// <summary>
    /// Party details (seller/buyer names, endpoint IDs, VAT ids) live OUTSIDE the
    /// canonical InvoiceEntity (which has no GLN/VAT columns and must not get a
    /// migration in this track). They are supplied via <see cref="PeppolPartyOptions"/>
    /// — defaulting to an empty options object so the service is constructible with
    /// no configuration and still produces a structurally-valid (if party-incomplete)
    /// document, which the validator will then flag.
    /// </summary>
    public PeppolBisInvoiceTransformService(PeppolPartyOptions? opts = null)
    {
        _opts = opts ?? new PeppolPartyOptions();
    }

    public Task<byte[]> TransformAsync(
        InvoiceEntity invoice,
        IReadOnlyList<InvoiceLineEntity> lines,
        CancellationToken ct)
    {
        var doc = BuildDocument(invoice, lines);
        var bytes = Encoding.UTF8.GetBytes(doc.Declaration + Environment.NewLine + doc);
        return Task.FromResult(bytes);
    }

    /// <summary>
    /// Builds the UBL <see cref="XDocument"/>. Exposed so the validator and tests
    /// can inspect the tree without re-parsing bytes.
    /// </summary>
    public XDocument BuildDocument(InvoiceEntity invoice, IReadOnlyList<InvoiceLineEntity> lines)
    {
        var cbc = CbcNs;
        var cac = CacNs;
        var currency = string.IsNullOrWhiteSpace(invoice.Currency) ? "EUR" : invoice.Currency;

        // VAT breakdown (BG-23): we model a single category from the header
        // (Sub→Tax) ratio or the max line tax rate, since the canonical line
        // carries a per-line TaxRate but no category code. BIS requires a category
        // code (S/Z/E/AE/…); we emit "S" (standard) when tax > 0 else "Z" (zero).
        // Honest simplification: mixed-rate invoices are collapsed to one subtotal
        // — documented as a gap above.
        var taxableAmount = invoice.SubTotal;
        var taxAmount     = invoice.TaxTotal;
        var categoryCode  = taxAmount > 0m ? "S" : "Z";
        var percent       = DerivePercent(invoice, lines);

        var root = new XElement(UblNs + "Invoice",
            new XAttribute(XNamespace.Xmlns + "cbc", CbcNs.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "cac", CacNs.NamespaceName),

            // ── BIS identifiers ──────────────────────────────────────────────
            new XElement(cbc + "CustomizationID", CustomizationId),   // BT-24
            new XElement(cbc + "ProfileID",       ProfileId),         // ProfileID

            // ── Header business terms ────────────────────────────────────────
            new XElement(cbc + "ID", invoice.InvoiceNumber),                                 // BT-1
            new XElement(cbc + "IssueDate", invoice.IssueDate.ToString("yyyy-MM-dd")),       // BT-2
            invoice.DueDate.HasValue
                ? new XElement(cbc + "DueDate", invoice.DueDate.Value.ToString("yyyy-MM-dd"))// BT-9
                : null,
            new XElement(cbc + "InvoiceTypeCode", "380"),                                    // BT-3 (commercial invoice)
            new XElement(cbc + "DocumentCurrencyCode", currency),                            // BT-5
            !string.IsNullOrWhiteSpace(invoice.BuyerRef)
                ? new XElement(cbc + "BuyerReference", invoice.BuyerRef)                     // BT-10
                : null,

            // ── Seller party (AccountingSupplierParty) ───────────────────────
            BuildParty(cac, cbc, "AccountingSupplierParty",
                _opts.SellerEndpointId, _opts.SellerEndpointScheme,
                _opts.SellerName, _opts.SellerVatId),

            // ── Buyer party (AccountingCustomerParty) ────────────────────────
            BuildParty(cac, cbc, "AccountingCustomerParty",
                _opts.BuyerEndpointId, _opts.BuyerEndpointScheme,
                _opts.BuyerName, _opts.BuyerVatId),

            // ── Tax total + breakdown (BG-23) ────────────────────────────────
            new XElement(cac + "TaxTotal",
                Amount(cbc + "TaxAmount", taxAmount, currency),                              // BT-110
                new XElement(cac + "TaxSubtotal",
                    Amount(cbc + "TaxableAmount", taxableAmount, currency),                  // BT-116
                    Amount(cbc + "TaxAmount", taxAmount, currency),                          // BT-117
                    new XElement(cac + "TaxCategory",
                        new XElement(cbc + "ID", categoryCode),                              // BT-118
                        new XElement(cbc + "Percent",
                            percent.ToString("F2", CultureInfo.InvariantCulture)),           // BT-119
                        new XElement(cac + "TaxScheme",
                            new XElement(cbc + "ID", "VAT"))))),

            // ── Legal monetary total (BG-22) ─────────────────────────────────
            new XElement(cac + "LegalMonetaryTotal",
                Amount(cbc + "LineExtensionAmount", invoice.SubTotal, currency),             // BT-106
                Amount(cbc + "TaxExclusiveAmount",  invoice.SubTotal, currency),             // BT-109
                Amount(cbc + "TaxInclusiveAmount",  invoice.GrandTotal, currency),           // BT-112
                Amount(cbc + "PayableAmount",       invoice.GrandTotal, currency)),          // BT-115

            // ── Invoice lines (BG-25) ────────────────────────────────────────
            lines.OrderBy(l => l.LineNumber).Select(l => BuildLine(cac, cbc, l, currency))
        );

        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
    }

    private static XElement BuildParty(
        XNamespace cac, XNamespace cbc, string wrapperName,
        string? endpointId, string? endpointScheme, string? name, string? vatId)
    {
        var party = new XElement(cac + "Party");

        // BT-34 / BT-49 endpoint ID. schemeID carries the EAS code (e.g. 0088 GLN,
        // 9930 DE VAT, 0191 EE registry). We emit it when present; absence is a
        // validator-flagged gap, never a fabricated value.
        if (!string.IsNullOrWhiteSpace(endpointId))
        {
            var ep = new XElement(cbc + "EndpointID", endpointId);
            if (!string.IsNullOrWhiteSpace(endpointScheme))
                ep.Add(new XAttribute("schemeID", endpointScheme));
            party.Add(ep);
        }

        // BT-27 / BT-44 party name.
        if (!string.IsNullOrWhiteSpace(name))
            party.Add(new XElement(cac + "PartyName",
                new XElement(cbc + "Name", name)));

        // BT-31 / BT-48 VAT identifier (PartyTaxScheme).
        if (!string.IsNullOrWhiteSpace(vatId))
            party.Add(new XElement(cac + "PartyTaxScheme",
                new XElement(cbc + "CompanyID", vatId),
                new XElement(cac + "TaxScheme",
                    new XElement(cbc + "ID", "VAT"))));

        // PartyLegalEntity registration name (BT-27/BT-44 legal name) mirrors the
        // party name when present — BIS requires a registration name.
        if (!string.IsNullOrWhiteSpace(name))
            party.Add(new XElement(cac + "PartyLegalEntity",
                new XElement(cbc + "RegistrationName", name)));

        return new XElement(cac + wrapperName, party);
    }

    private static XElement BuildLine(
        XNamespace cac, XNamespace cbc, InvoiceLineEntity l, string currency)
    {
        return new XElement(cac + "InvoiceLine",
            new XElement(cbc + "ID", l.LineNumber),                                          // BT-126
            new XElement(cbc + "InvoicedQuantity",
                new XAttribute("unitCode", string.IsNullOrWhiteSpace(l.UnitCode) ? "EA" : l.UnitCode),
                l.Quantity.ToString("F4", CultureInfo.InvariantCulture)),                    // BT-129/130
            Amount(cbc + "LineExtensionAmount", l.LineTotal, currency),                      // BT-131
            new XElement(cac + "Item",
                new XElement(cbc + "Name",
                    string.IsNullOrWhiteSpace(l.Description) ? "Item" : l.Description),       // BT-153
                l.SupplierItemCode is not null
                    ? new XElement(cac + "SellersItemIdentification",
                        new XElement(cbc + "ID", l.SupplierItemCode))                        // BT-155
                    : null,
                l.BuyerItemCode is not null
                    ? new XElement(cac + "BuyersItemIdentification",
                        new XElement(cbc + "ID", l.BuyerItemCode))                           // BT-156
                    : null,
                // Line-level VAT category (BT-151) — same simplification as header.
                new XElement(cac + "ClassifiedTaxCategory",
                    new XElement(cbc + "ID", l.TaxRate > 0m ? "S" : "Z"),
                    new XElement(cbc + "Percent",
                        NormalizePercent(l.TaxRate).ToString("F2", CultureInfo.InvariantCulture)),
                    new XElement(cac + "TaxScheme",
                        new XElement(cbc + "ID", "VAT")))),
            new XElement(cac + "Price",
                Amount(cbc + "PriceAmount", l.UnitPrice, currency)));                        // BT-146
    }

    private static XElement Amount(XName name, decimal value, string currency)
        => new(name,
            new XAttribute("currencyID", currency),
            value.ToString("F2", CultureInfo.InvariantCulture));

    /// <summary>
    /// Derive a single header VAT percent. Prefer the (Sub→Tax) ratio when both are
    /// present and non-zero; otherwise fall back to the max line tax rate. Returned
    /// as a whole percentage (e.g. 20.00 for 20%).
    /// </summary>
    private static decimal DerivePercent(InvoiceEntity inv, IReadOnlyList<InvoiceLineEntity> lines)
    {
        if (inv.SubTotal > 0m && inv.TaxTotal > 0m)
            return Math.Round(inv.TaxTotal / inv.SubTotal * 100m, 2);
        var maxRate = lines.Count > 0 ? lines.Max(l => l.TaxRate) : 0m;
        return NormalizePercent(maxRate);
    }

    /// <summary>
    /// Line TaxRate is stored as a fraction in some paths (0.20) and as a whole
    /// percent in others (20). Normalize: values ≤ 1 are treated as fractions.
    /// </summary>
    private static decimal NormalizePercent(decimal rate)
        => rate > 0m && rate <= 1m ? Math.Round(rate * 100m, 2) : Math.Round(rate, 2);
}
