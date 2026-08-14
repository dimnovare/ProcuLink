using System.Globalization;
using System.Xml.Linq;

namespace ProcuLink.Transform.Output;

/// <summary>
/// A single rule outcome from <see cref="PeppolBisValidator"/>.
/// </summary>
/// <param name="RuleId">Our rule code (e.g. "PLK-BIS-CustomizationID"). NOT an official Schematron id.</param>
/// <param name="BusinessTerm">The EN 16931 BT-/BG- business term the rule guards, when applicable.</param>
/// <param name="Severity">"error" = mandatory and missing/invalid; "warning" = recommended/best-effort.</param>
/// <param name="Message">Human-readable explanation.</param>
public sealed record PeppolValidationIssue(
    string RuleId,
    string? BusinessTerm,
    string Severity,
    string Message);

/// <summary>
/// Outcome of a validation run.
/// </summary>
public sealed record PeppolValidationResult(
    bool IsValid,
    IReadOnlyList<PeppolValidationIssue> Issues)
{
    public IEnumerable<PeppolValidationIssue> Errors   => Issues.Where(i => i.Severity == "error");
    public IEnumerable<PeppolValidationIssue> Warnings => Issues.Where(i => i.Severity == "warning");
}

/// <summary>
/// Lightweight Peppol BIS Billing 3.0 mandatory-field validator.
///
/// HONEST SCOPE (offer⇔works):
///   This is NOT a Schematron / EN 16931 conformance validator. It checks the
///   high-value MANDATORY business terms that, if missing, mean the document is
///   definitely not a usable BIS Billing 3.0 invoice — plus a few arithmetic
///   reconciliation checks. A passing result means "the structural essentials are
///   present and the totals reconcile", NOT "this will pass a Peppol Access Point".
///
///   FUTURE WORK (explicitly not done here): run the official PEPPOL-EN16931-UBL
///   Schematron + EN16931-UBL-validation rule sets (codelist checks, calc rules
///   BR-CO-*, BR-S-*, scheme-id validation, etc.). That is the full-conformance leg.
///
/// ── What this validator does NOT check, and why it used to look like it did ──
///
///   BT-24 <c>CustomizationID</c> and <c>ProfileID</c> are NOT checked here, and a passing
///   result says nothing about either. Until 2026-08-14 this class did check them: it read
///   both elements and compared them to
///   <see cref="PeppolBisInvoiceTransformService.CustomizationId"/> and
///   <see cref="PeppolBisInvoiceTransformService.ProfileId"/> — the emitter's own constants.
///
///   The only production caller (<c>InvoiceController.ValidatePeppol</c>) builds the document
///   with that same emitter and hands it straight to this validator, so both comparisons read
///   back a string the emitter had just written from the same constant. They could not fail.
///   An assertion that cannot fail is not evidence, and this one was reported to the caller as
///   part of an <c>isValid</c> that a counterparty might rely on.
///
///   This is the same defect that was removed from the ORDER path (see <c>UblProfileChecker</c>
///   and <c>UblOrderDeclaresNoPeppolProfileTests</c>). There it was fixed by dropping the
///   declaration as well as the check; here the declaration STAYS and only the check goes. That
///   asymmetry is deliberate and its reasoning — including the plausible-but-false justification
///   for it — is written out in <see cref="PeppolBisInvoiceTransformService"/>. Read it there
///   before changing either file. What is gone is only the pretence that anything verifies them.
///
///   Verifying them for real means running the official PEPPOL-EN16931-UBL Schematron. There is
///   none in this repo. Until there is, the honest statement is the one the API now returns:
///   the profile is declared, and it is unverified.
///
/// Errors checked (mandatory):
///   BT-1  Invoice number present
///   BT-2  Issue date present and ISO-8601
///   BT-3  Invoice type code present
///   BT-5  Document currency present
///   BT-34 Seller endpoint ID present
///   BT-49 Buyer endpoint ID present
///   BT-27 Seller name present
///   BT-44 Buyer name present
///   BG-23 At least one VAT category with a category code + tax scheme
///   BG-22 LegalMonetaryTotal present with PayableAmount
///   BG-25 At least one invoice line
///   Calc  Sum(line LineExtensionAmount) == LegalMonetaryTotal/LineExtensionAmount
///   Calc  TaxExclusive + TaxAmount == TaxInclusive (== PayableAmount)
///
/// Warnings (recommended-but-not-fatal here):
///   BT-31/BT-48 Seller/Buyer VAT id absent
///   Endpoint schemeID absent
/// </summary>
public sealed class PeppolBisValidator
{
    private static readonly XNamespace CbcNs =
        "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace CacNs =
        "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";

    // Tolerance for monetary reconciliation (covers F2 rounding).
    private const decimal Tolerance = 0.01m;

    public PeppolValidationResult Validate(string ublXml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(ublXml);
        }
        catch (Exception ex)
        {
            return new PeppolValidationResult(false, new[]
            {
                new PeppolValidationIssue("PLK-BIS-WellFormed", null, "error",
                    $"Document is not well-formed XML: {ex.Message}")
            });
        }
        return Validate(doc);
    }

    public PeppolValidationResult Validate(byte[] ublBytes)
        => Validate(System.Text.Encoding.UTF8.GetString(ublBytes));

    public PeppolValidationResult Validate(XDocument doc)
    {
        var issues = new List<PeppolValidationIssue>();
        var root = doc.Root;

        if (root is null || root.Name.LocalName != "Invoice")
        {
            issues.Add(new("PLK-BIS-Root", null, "error",
                "Root element must be <Invoice> in the UBL Invoice-2 namespace."));
            return new PeppolValidationResult(false, issues);
        }

        // BT-24 CustomizationID and ProfileID are deliberately NOT checked — see the class
        // summary. Both were compared against the emitter's own constants on a document the
        // emitter had just produced, so neither comparison could fail. Reinstating either is
        // pinned by PeppolBisInvoiceConformanceIsUnverifiedTests.

        // ── BT-1 Invoice number ─────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(Cbc(root, "ID")))
            issues.Add(new("PLK-BIS-ID", "BT-1", "error", "Missing invoice number (BT-1, cbc:ID)."));

        // ── BT-2 Issue date ─────────────────────────────────────────────────
        var issueDate = Cbc(root, "IssueDate");
        if (string.IsNullOrWhiteSpace(issueDate))
            issues.Add(new("PLK-BIS-IssueDate", "BT-2", "error", "Missing issue date (BT-2)."));
        else if (!IsIsoDate(issueDate))
            issues.Add(new("PLK-BIS-IssueDate", "BT-2", "error",
                $"Issue date '{issueDate}' is not ISO-8601 (yyyy-MM-dd)."));

        // ── BT-3 Invoice type code ──────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(Cbc(root, "InvoiceTypeCode")))
            issues.Add(new("PLK-BIS-TypeCode", "BT-3", "error", "Missing invoice type code (BT-3)."));

        // ── BT-5 Document currency ──────────────────────────────────────────
        var currency = Cbc(root, "DocumentCurrencyCode");
        if (string.IsNullOrWhiteSpace(currency))
            issues.Add(new("PLK-BIS-Currency", "BT-5", "error", "Missing document currency code (BT-5)."));

        // ── Parties (BT-34/BT-49 endpoints, BT-27/BT-44 names, VAT warnings) ─
        ValidateParty(root, "AccountingSupplierParty", "Seller", "BT-34", "BT-27", "BT-31", issues);
        ValidateParty(root, "AccountingCustomerParty", "Buyer",  "BT-49", "BT-44", "BT-48", issues);

        // ── BG-23 VAT breakdown ─────────────────────────────────────────────
        var taxCategory = Descendant(root, "TaxCategory");
        if (taxCategory is null)
            issues.Add(new("PLK-BIS-TaxCategory", "BG-23", "error",
                "Missing VAT breakdown (BG-23, cac:TaxTotal/cac:TaxSubtotal/cac:TaxCategory)."));
        else
        {
            if (string.IsNullOrWhiteSpace(Cbc(taxCategory, "ID")))
                issues.Add(new("PLK-BIS-TaxCategoryId", "BT-118", "error",
                    "VAT category code missing (BT-118)."));
            var scheme = Descendant(taxCategory, "TaxScheme");
            if (scheme is null || string.IsNullOrWhiteSpace(Cbc(scheme, "ID")))
                issues.Add(new("PLK-BIS-TaxScheme", null, "error",
                    "VAT category is missing its TaxScheme/ID."));
        }

        // ── BG-22 monetary totals ───────────────────────────────────────────
        var lmt = Descendant(root, "LegalMonetaryTotal");
        if (lmt is null)
            issues.Add(new("PLK-BIS-LegalMonetaryTotal", "BG-22", "error",
                "Missing document totals (BG-22, cac:LegalMonetaryTotal)."));
        else if (ParseAmount(Cbc(lmt, "PayableAmount")) is null)
            issues.Add(new("PLK-BIS-PayableAmount", "BT-115", "error",
                "Missing payable amount (BT-115)."));

        // ── BG-25 lines ─────────────────────────────────────────────────────
        var lineEls = root.Descendants().Where(d => d.Name.LocalName == "InvoiceLine").ToList();
        if (lineEls.Count == 0)
            issues.Add(new("PLK-BIS-Lines", "BG-25", "error",
                "Invoice has no lines (BG-25, cac:InvoiceLine)."));

        // ── Calc: sum(line LineExtensionAmount) == header LineExtensionAmount ─
        if (lmt is not null && lineEls.Count > 0)
        {
            var lineSum = lineEls
                .Select(l => ParseAmount(Cbc(l, "LineExtensionAmount")) ?? 0m)
                .Sum();
            var headerLineExt = ParseAmount(Cbc(lmt, "LineExtensionAmount"));
            if (headerLineExt is not null && Math.Abs(headerLineExt.Value - lineSum) > Tolerance)
                issues.Add(new("PLK-BIS-LineSum", "BR-CO-10", "error",
                    $"Sum of line amounts ({lineSum:F2}) does not equal LegalMonetaryTotal/LineExtensionAmount ({headerLineExt:F2})."));
        }

        // ── Calc: TaxExclusive + TaxAmount == TaxInclusive ──────────────────
        if (lmt is not null)
        {
            var taxExclusive = ParseAmount(Cbc(lmt, "TaxExclusiveAmount"));
            var taxInclusive = ParseAmount(Cbc(lmt, "TaxInclusiveAmount"));
            var taxAmount    = ParseAmount(
                Descendant(root, "TaxTotal") is { } tt ? Cbc(tt, "TaxAmount") : null);
            if (taxExclusive is not null && taxInclusive is not null && taxAmount is not null &&
                Math.Abs(taxExclusive.Value + taxAmount.Value - taxInclusive.Value) > Tolerance)
                issues.Add(new("PLK-BIS-TaxCalc", "BR-CO-15", "error",
                    $"TaxExclusiveAmount ({taxExclusive:F2}) + TaxAmount ({taxAmount:F2}) ≠ TaxInclusiveAmount ({taxInclusive:F2})."));
        }

        var hasErrors = issues.Any(i => i.Severity == "error");
        return new PeppolValidationResult(!hasErrors, issues);
    }

    private static void ValidateParty(
        XElement root, string wrapperName, string label,
        string endpointBt, string nameBt, string vatBt,
        List<PeppolValidationIssue> issues)
    {
        var wrapper = Descendant(root, wrapperName);
        var party   = wrapper is null ? null : Descendant(wrapper, "Party");
        if (party is null)
        {
            issues.Add(new($"PLK-BIS-{label}Party", null, "error",
                $"Missing {label.ToLowerInvariant()} party (cac:{wrapperName}/cac:Party)."));
            return;
        }

        // Endpoint ID (mandatory for Peppol routing).
        var endpoint = Cbc(party, "EndpointID");
        if (string.IsNullOrWhiteSpace(endpoint))
            issues.Add(new($"PLK-BIS-{label}Endpoint", endpointBt, "error",
                $"Missing {label.ToLowerInvariant()} electronic address ({endpointBt}, cbc:EndpointID)."));
        else
        {
            var schemeAttr = party.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "EndpointID")?
                .Attribute("schemeID")?.Value;
            if (string.IsNullOrWhiteSpace(schemeAttr))
                issues.Add(new($"PLK-BIS-{label}EndpointScheme", endpointBt, "warning",
                    $"{label} EndpointID has no schemeID (EAS code); required for live Peppol routing."));
        }

        // Party name (mandatory).
        var name = Descendant(party, "PartyName") is { } pn ? Cbc(pn, "Name") : null;
        var legal = Descendant(party, "PartyLegalEntity") is { } ple ? Cbc(ple, "RegistrationName") : null;
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(legal))
            issues.Add(new($"PLK-BIS-{label}Name", nameBt, "error",
                $"Missing {label.ToLowerInvariant()} name ({nameBt})."));

        // VAT id (recommended → warning).
        var vat = Descendant(party, "PartyTaxScheme") is { } pts ? Cbc(pts, "CompanyID") : null;
        if (string.IsNullOrWhiteSpace(vat))
            issues.Add(new($"PLK-BIS-{label}Vat", vatBt, "warning",
                $"{label} VAT identifier ({vatBt}) absent; many BIS rules require it for taxed invoices."));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string? Cbc(XElement el, string localName)
        => el.Elements().FirstOrDefault(e =>
                e.Name.Namespace == CbcNs && e.Name.LocalName == localName)?.Value
           ?? el.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    private static XElement? Descendant(XElement el, string localName)
        => el.Descendants().FirstOrDefault(d =>
               (d.Name.Namespace == CacNs || d.Name.Namespace == CbcNs)
                   ? d.Name.LocalName == localName
                   : d.Name.LocalName == localName);

    private static bool IsIsoDate(string raw)
        => DateOnly.TryParseExact(raw, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static decimal? ParseAmount(string? raw)
        => decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
}
