using System.Xml.Linq;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// The Peppol BIS Billing invoice declares a profile that nothing in this repo verifies. Both
/// halves of that sentence are load-bearing, and this file pins both — in both directions.
///
/// ── The defect ───────────────────────────────────────────────────────────────────
///
/// <c>PeppolBisInvoiceTransformService</c> writes <c>cbc:CustomizationID</c> and
/// <c>cbc:ProfileID</c> from two public constants. <c>PeppolBisValidator</c> then read those two
/// elements back and compared them to the same two constants:
///
///     else if (customization != PeppolBisInvoiceTransformService.CustomizationId)
///     else if (profile       != PeppolBisInvoiceTransformService.ProfileId)
///
/// The only production caller builds the document with that emitter and validates it on the next
/// line (<c>InvoiceController.ValidatePeppol</c>), so both comparisons could not fail, and their
/// passing was folded into an <c>isValid</c> returned beside the declared identifiers. A caller
/// reading that response saw a conformance claim backed by a check of itself.
///
/// This is the invoice-side twin of the order-side defect fixed in
/// <see cref="UblOrderDeclaresNoPeppolProfileTests"/>, whose scope note explicitly excused this
/// file — "backed by <c>PeppolBisValidator</c>, which checks the BT rules and reports its gaps
/// honestly". True of the BT rules; not true of these two identifiers.
///
/// ── Why the fix here is NOT the fix used for the order ───────────────────────────
///
/// The order path removed the declaration. This one keeps it and removes only the check, so this
/// guard has to hold a line in both directions: the check must not come back, and the declaration
/// must not be stripped "for consistency".
///
/// Stripping it would not make the document more honest, it would make it unroutable. Peppol
/// composes the document type identifier as
/// <c>&lt;syntax specific id&gt;##&lt;customization id&gt;::&lt;version&gt;</c> (Policy for use of Identifiers
/// 4.4.0, POLICY 20); that identifier is the SMP lookup key and the AS4 Action, so the
/// CustomizationID is a substring of the routing key. Absent it, a sender access point cannot
/// address the document at all. It is also two fatal Schematron asserts (CEN <c>BR-01</c>,
/// <c>PEPPOL-EN16931-R004</c>), with <c>PEPPOL-EN16931-R001</c>/<c>R007</c> for ProfileID.
///
/// And the same is true of ORDERS — checked 2026-08-14, because the tempting justification for
/// the asymmetry is that an order without a CustomizationID is "merely undeclared" while an
/// invoice is rejected. It is false: Peppol BIS Order-only 3 makes both elements 1..1 and
/// <c>PEPPOL-T01-B00101</c>/<c>B00102</c> are equally fatal. The real reason the order could drop
/// them is that the order path stopped offering Peppol output at all, leaving a plain OASIS UBL
/// 2.1 Order that ProcuLink delivers over HTTPS/email/SFTP. This format token is named "peppol"
/// and has no such fallback.
/// </summary>
public class PeppolBisInvoiceConformanceIsUnverifiedTests
{
    private static readonly XNamespace Cbc =
        "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    // ── Direction 1: nothing may adjudicate the declared profile ─────────────────

    /// <summary>
    /// The behavioural guard, and the one the mutation test targets. Reinstating either rule —
    /// by constant, by literal, or by any other spelling — reddens this.
    /// </summary>
    [Fact]
    public void Validator_ReportsNoVerdictOnTheDeclaredProfile()
    {
        var result = new PeppolBisValidator().Validate(Emit());

        result.Issues.Should().NotContain(i => i.RuleId == "PLK-BIS-CustomizationID",
            "the emitter writes CustomizationID from a constant and the only caller validates that " +
            "same document, so any rule about it compares a value with itself");
        result.Issues.Should().NotContain(i => i.RuleId == "PLK-BIS-ProfileID");
        result.Issues.Should().NotContain(i => i.BusinessTerm == "BT-24");

        // Anti-vacuity: a validator that reported nothing at all would satisfy every absence above.
        result.IsValid.Should().BeTrue("our own complete document must still clear the real checks");
        result.Issues.Should().NotBeEmpty(
            "the fixture omits both endpoint schemeIDs' siblings only partially — warnings must still " +
            "be produced, or this suite is asserting against a validator that does nothing");
    }

    /// <summary>
    /// The same, stated against a document whose profile identifiers are WRONG. This is the input
    /// the deleted rule existed to catch, and the point is that catching it was never evidence:
    /// production cannot produce this document. It must now pass, and pass loudly.
    /// </summary>
    [Fact]
    public void Validator_AcceptsADocumentWhoseDeclaredProfileIsNotBisBilling()
    {
        var xml = Emit()
            .Replace(PeppolBisInvoiceTransformService.CustomizationId, "urn:not-peppol")
            .Replace(PeppolBisInvoiceTransformService.ProfileId, "urn:not-a-profile");

        var result = new PeppolBisValidator().Validate(xml);

        result.IsValid.Should().BeTrue(
            "the validator has no evidence about the profile and must not pretend otherwise. Issues: {0}",
            string.Join(" | ", result.Issues.Select(i => $"{i.Severity}:{i.RuleId}:{i.Message}")));
    }

    // ── Direction 2: the declaration itself must not be stripped ─────────────────

    [Fact]
    public void EmittedInvoice_StillDeclaresTheBisBillingCustomizationAndProfile()
    {
        var root = XDocument.Parse(Emit()).Root!;

        root.Element(Cbc + "CustomizationID")!.Value.Should().Be(
            PeppolBisInvoiceTransformService.CustomizationId,
            "it is a substring of the Peppol document type identifier (POLICY 20) — the SMP lookup " +
            "key and AS4 Action. Removing it does not soften a claim, it makes the document " +
            "unaddressable, and this format token is named \"peppol\"");
        root.Element(Cbc + "ProfileID")!.Value.Should().Be(
            PeppolBisInvoiceTransformService.ProfileId);
    }

    /// <summary>
    /// UBL 2.1 content models are ordered <c>xsd:sequence</c>es: both identifiers are optional but
    /// must precede <c>cbc:ID</c>. A reordering that kept every element would still be invalid.
    /// </summary>
    [Fact]
    public void EmittedInvoice_KeepsTheProfileIdentifiersAheadOfInvoiceNumber()
    {
        var names = XDocument.Parse(Emit()).Root!
            .Elements().Select(e => e.Name.LocalName).ToList();

        names.IndexOf("CustomizationID").Should().BeLessThan(names.IndexOf("ID"));
        names.IndexOf("ProfileID").Should().BeLessThan(names.IndexOf("ID"));
        names.IndexOf("CustomizationID").Should().BeGreaterThanOrEqualTo(0, "anti-vacuity: IndexOf returns -1 when absent");
    }

    // ── The source of the circular comparison itself ─────────────────────────────

    /// <summary>
    /// The class guard rather than the instance. The behavioural tests above run the validator on
    /// ONE fixture; a comparison reintroduced behind a condition that fixture does not reach would
    /// pass them. So the file is scanned for the comparison directly.
    ///
    /// Comments are stripped, because this file and the validator both quote the deleted lines on
    /// purpose — a fix nobody can find the reason for gets undone.
    /// </summary>
    [Fact]
    public void ValidatorSource_DoesNotCompareAgainstTheEmittersOwnConstants()
    {
        var path = Path.Combine(RepoRoot(), "ProcuLink.Transform/Output/PeppolBisValidator.cs");
        File.Exists(path).Should().BeTrue("if the file moved, repoint this guard rather than " +
            "letting it read nothing");

        var raw = File.ReadAllText(path);
        raw.Length.Should().BeGreaterThan(500, "anti-vacuity: the file must really have been read");

        var code = WithoutComments(raw);
        code.Should().NotContain("PeppolBisInvoiceTransformService.CustomizationId",
            "reading the emitter's constant back out of the emitter's own document is the defect");
        code.Should().NotContain("PeppolBisInvoiceTransformService.ProfileId");
        code.Should().NotContain("PLK-BIS-CustomizationID");
        code.Should().NotContain("PLK-BIS-ProfileID");
        code.Should().Contain("sealed class",
            "anti-vacuity: stripping comments must not have emptied the file");
        code.Should().Contain("PLK-BIS-TaxCalc",
            "anti-vacuity: the real rules must still be present in the scanned text");
    }

    /// <summary>
    /// MUST-FLAG CONTROL for the scan above, in both directions. Stripping comments is exactly how
    /// a scanner goes quiet while reading green, so the deleted comparison is replayed verbatim as
    /// it shipped and must still be caught — while the prose recording its removal must not be.
    /// </summary>
    [Fact]
    public void SourceScan_StillSeesTheComparisonWhenItIsCodeRatherThanCommentary()
    {
        // The line as it stood at c19f747c — PeppolBisValidator.cs:113.
        const string asShipped =
            "        else if (customization != PeppolBisInvoiceTransformService.CustomizationId)";
        WithoutComments(asShipped).Should().Contain("PeppolBisInvoiceTransformService.CustomizationId",
            "an executable comparison is code, not commentary, and must survive the strip");

        // And the record of its removal, which must not.
        const string asDocumented =
            "        /// compared them to PeppolBisInvoiceTransformService.CustomizationId — the emitter's own";
        WithoutComments(asDocumented).Should().NotContain("PeppolBisInvoiceTransformService.CustomizationId");
        WithoutComments("        // PLK-BIS-ProfileID — removed, see the class summary")
            .Should().NotContain("PLK-BIS-ProfileID");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>Blanks <c>//</c> and <c>///</c> comment lines, so prose about the defect is not read as the defect.</summary>
    private static string WithoutComments(string source) =>
        string.Join("\n", source.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string Emit() =>
        new PeppolBisInvoiceTransformService(new PeppolPartyOptions
        {
            SellerName       = "Northwind Trading OÜ",
            SellerEndpointId = "0192:998765432",
            BuyerName        = "Fabrikam AS",
            BuyerEndpointId  = "0088:1234567890123",
        }).BuildDocument(Invoice(), new[] { Line() }).ToString();

    private static InvoiceEntity Invoice() => new()
    {
        Id            = Guid.NewGuid(),
        InvoiceNumber = "INV-PEPPOL-GUARD",
        IssueDate     = new DateOnly(2026, 8, 14),
        Currency      = "EUR",
        SubTotal      = 100m,
        TaxTotal      = 20m,
        GrandTotal    = 120m,
        Status        = "approved",
    };

    private static InvoiceLineEntity Line() => new()
    {
        Id          = Guid.NewGuid(),
        LineNumber  = 1,
        Description = "Steel bracket",
        Quantity    = 10m,
        UnitCode    = "EA",
        UnitPrice   = 10m,
        TaxRate     = 0.20m,
        LineTotal   = 100m,
    };

    /// <summary>
    /// Walks up from the test binary to the directory holding <c>ProcuLink.slnx</c> — this repo's
    /// solution file (there is no <c>.sln</c>). Same marker the sibling order-side guard uses, so a
    /// repo layout change moves both together.
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcuLink.slnx"))) dir = dir.Parent;
        dir.Should().NotBeNull($"could not find ProcuLink.slnx walking up from {AppContext.BaseDirectory}");
        return dir!.FullName;
    }
}
