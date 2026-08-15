using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Conformance;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// The emitted UBL order must not declare a Peppol BIS profile, and no surface that reports on it
/// may say that it does.
///
/// ── The defect ───────────────────────────────────────────────────────────────────
///
/// Every order ProcuLink emitted as UBL self-declared as Peppol BIS Order-only 3.0:
///
///     new XElement(Cbc + "CustomizationID", "urn:fdc:peppol.eu:poacc:trns:order:3"),
///     new XElement(Cbc + "ProfileID",       "urn:fdc:peppol.eu:poacc:bis:order_only:3"),
///
/// Those two elements are not decoration. A receiving Peppol access point ROUTES and VALIDATES on
/// them, so writing them is a claim of BIS conformance made to the counterparty's software — and
/// nothing in either repo can back it. There is no Schematron; <c>PeppolBisValidator</c> is
/// invoice-only; and the one order-side check, <c>UblProfileChecker</c>, asserted merely that the
/// two elements were NON-EMPTY. The same emitter had just written them, so the check could not
/// fail. That always-passing result was rendered under "Matches the standard" with a green badge,
/// beside a "Download report" button whose Markdown carried <c>- **Profile:** {ProfileName}</c> —
/// so a customer could forward a file asserting BIS conformance on our behalf.
///
/// The product already knew. <c>src/lib/standards/catalog.ts</c> records the Peppol BIS Order 3.0
/// row as <c>transform: "planned"</c> with, in capitals, "Outbound: BIS-CONFORMANT OUTPUT IS NOT
/// OFFERED AND MUST NOT BE ADVERTISED", and FE #91 withdrew the claim from the website. The
/// emitted document, the in-app conformance panel and the downloadable report were missed.
///
/// ── What is emitted instead, and why that is correct UBL 2.1 ─────────────────────
///
/// Nothing. Both elements are simply absent, which is what a plain, non-Peppol UBL 2.1 Order
/// document looks like. In the OASIS UBL 2.1 Order-2 schema
/// (docs.oasis-open.org/ubl/os-UBL-2.1/xsd/maindoc/UBL-Order-2.1.xsd) both are declared
/// <c>minOccurs="0" maxOccurs="1"</c>:
///
///     &lt;xsd:element ref="cbc:CustomizationID" minOccurs="0" maxOccurs="1"&gt;
///     &lt;xsd:element ref="cbc:ProfileID"       minOccurs="0" maxOccurs="1"&gt;
///
/// while <c>cbc:ID</c> and <c>cbc:IssueDate</c> are <c>minOccurs="1"</c>. Omitting an optional
/// element cannot make the document schema-invalid. Substituting some other identifier would not
/// be neutral either: UBL defines CustomizationID as "a user-defined customization of UBL for a
/// specific use", and ProcuLink applies no customization — it emits the base document. So the
/// honest UBL 2.1 Order declares no customization and no profile, and a receiver that requires a
/// specific profile now learns that from its own validator instead of from a claim we made up.
///
/// <c>cbc:UBLVersionID</c> stays at 2.1. It names the UBL version the document really is, which is
/// checkable, and it is not a profile assertion.
///
/// ── Why this test asserts on bytes ───────────────────────────────────────────────
///
/// The defect is in the document a supplier's system parses, so every assertion here runs against
/// the serialized XML the transformer actually returns — never an intermediate object.
/// </summary>
public class UblOrderDeclaresNoPeppolProfileTests
{
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    /// <summary>
    /// The two identifiers, verbatim as they were emitted. A guard that does not carry its own
    /// origin defect is decoration — reintroducing either of these must redden this file.
    /// </summary>
    private const string PeppolCustomizationId = "urn:fdc:peppol.eu:poacc:trns:order:3";
    private const string PeppolProfileId       = "urn:fdc:peppol.eu:poacc:bis:order_only:3";

    /// <summary>Every output format the UBL order transformer claims.</summary>
    public static TheoryData<OutputFormat> UblFormats => new() { OutputFormat.Ubl, OutputFormat.UblOrder };

    // ── The emitted document ─────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(UblFormats))]
    public async Task EmittedOrder_DeclaresNoPeppolIdentifier(OutputFormat format)
    {
        var xml = await EmitAsync(format);

        xml.Should().NotContain(PeppolCustomizationId,
            "a receiving access point routes and validates on cbc:CustomizationID — emitting the " +
            "Peppol value claims a conformance nothing in this repo verifies");
        xml.Should().NotContain(PeppolProfileId,
            "cbc:ProfileID names the Peppol BIS Order-only 3.0 profile; ProcuLink does not produce " +
            "BIS-conformant output (src/lib/standards/catalog.ts: transform: \"planned\")");
        xml.Should().NotContain("peppol",
            "no Peppol identifier of any spelling belongs in a document we cannot validate as Peppol");
    }

    [Theory]
    [MemberData(nameof(UblFormats))]
    public async Task EmittedOrder_OmitsCustomizationIdAndProfileIdEntirely(OutputFormat format)
    {
        var doc = XDocument.Parse(await EmitAsync(format));

        doc.Descendants(Cbc + "CustomizationID").Should().BeEmpty(
            "both are minOccurs=\"0\" in the OASIS UBL 2.1 Order-2 schema, so a plain UBL 2.1 Order " +
            "omits them; declaring one asserts a customization ProcuLink does not apply");
        doc.Descendants(Cbc + "ProfileID").Should().BeEmpty();
    }

    /// <summary>
    /// The anti-vacuity floor. Every assertion above is an absence, and an emitter that returned an
    /// empty string, or a bare root, would satisfy all of them while producing nothing a supplier
    /// could read. So the same bytes are pinned as a real UBL 2.1 Order carrying its own mandatory
    /// elements.
    /// </summary>
    [Theory]
    [MemberData(nameof(UblFormats))]
    public async Task EmittedOrder_IsStillARealUbl21Order(OutputFormat format)
    {
        var doc  = XDocument.Parse(await EmitAsync(format));
        var root = doc.Root!;

        root.Name.LocalName.Should().Be("Order");
        root.Name.NamespaceName.Should().Be("urn:oasis:names:specification:ubl:schema:xsd:Order-2");

        root.Element(Cbc + "UBLVersionID")!.Value.Should().Be("2.1",
            "the UBL VERSION is a checkable fact about the document and stays; only the PROFILE claim goes");
        root.Element(Cbc + "ID")!.Value.Should().Be("PO-UBL-PEPPOL-GUARD",
            "cbc:ID is minOccurs=\"1\" — mandatory where CustomizationID/ProfileID are not");
        root.Element(Cbc + "IssueDate")!.Value.Should().Be("2026-05-28");
        root.Descendants().Count(e => e.Name.LocalName == "OrderLine").Should().Be(1);
    }

    // ── The conformance report, and its downloadable form ────────────────────────

    [Theory]
    [MemberData(nameof(UblFormats))]
    public async Task ConformanceReport_NamesNoPeppolProfile(OutputFormat format)
    {
        var report = new ConformanceService().Check(await EmitAsync(format), format);

        report.Profile.Should().Be(StandardsProfile.Ubl21Order, "the report must really be the UBL one");
        report.ProfileName.Should().NotContainEquivalentOf("peppol",
            "ConformancePanel.tsx renders profileName directly beneath \"Matches the standard\" with a " +
            "pass badge, so a Peppol name there reports BIS conformance to the operator");
        report.ProfileName.Should().NotContainEquivalentOf("BIS");
        report.ProfileName.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [MemberData(nameof(UblFormats))]
    public async Task DownloadableReport_AssertsNoPeppolConformance(OutputFormat format)
    {
        var markdown = new ConformanceService().Check(await EmitAsync(format), format).ToMarkdown();

        markdown.Should().NotContainEquivalentOf("peppol",
            "the report is downloadable, so a customer can forward it — it must not assert on our " +
            "behalf a conformance we never checked");
        markdown.Should().NotContainEquivalentOf("BIS Order");
        markdown.Should().Contain("- **Profile:**", "the anti-vacuity floor: the report must really render");

        // The scope statement, which is now made PER ROW instead of in one blanket sentence.
        //
        // This assertion used to pin the literal "Not a schema validation and not a certification".
        // That sentence was false in both directions on this very document, and pinning it is why it
        // survived: most rows are self-checks whose asserted value the emitter itself just wrote, so
        // they cannot fail on our own output — while `ubl.xsd` IS a schema validation, against the
        // vendored OASIS UBL 2.1 grammar. Under-claiming a real check is its own false statement.
        markdown.Should().Contain("**Self-check.**",
            "a forwardable PASS must say which of its rows only restate ProcuLink's own output back to us");
        markdown.Should().Contain("Published schema",
            "and must not bury the one verdict here that a third party actually produced");
        markdown.Should().Contain("not a certification against any profile",
            "a forwardable PASS that does not state its scope reads as a conformance guarantee");
    }

    /// <summary>
    /// The check that made the old report circular: <c>CustomizationID</c>/<c>ProfileID</c> were
    /// required to be non-empty, and the emitter had just written them, so the assertion could
    /// never fail on our own output. Nothing may require them again — UBL 2.1 does not.
    /// </summary>
    [Fact]
    public async Task ConformanceReport_DoesNotRequireTheOptionalProfileElements()
    {
        var report = new ConformanceService().Check(await EmitAsync(OutputFormat.Ubl), OutputFormat.Ubl);

        report.Checks.Should().NotContain(c => c.Code == "ubl.customizationID",
            "requiring an element the schema marks minOccurs=\"0\" is not a UBL 2.1 rule, and " +
            "requiring one the emitter itself wrote is not a check at all");
        report.Checks.Should().NotContain(c => c.Code == "ubl.profileID");
        report.OverallPass.Should().BeTrue("our own output must still pass the checks that remain");
        report.Checks.Should().HaveCountGreaterThan(5, "anti-vacuity: real checks must still run");
    }

    // ── The emitted-document constants themselves ────────────────────────────────

    /// <summary>
    /// The class guard, not the instance. The audit's diagnosis was that the claim MOVED — out of
    /// marketing copy, which was scanned, into the product, which was not. So the two files that
    /// decide what a supplier receives are scanned for the identifiers directly: a constant
    /// declared but not yet wired, or wired somewhere this suite does not exercise, is caught here.
    ///
    /// Scope, stated rather than assumed: this covers the ORDER path only.
    /// <c>PeppolBisInvoiceTransformService</c> deliberately emits BIS Billing 3.0 identifiers, and
    /// that remains true — see <see cref="PeppolBisInvoiceConformanceIsUnverifiedTests"/> for why
    /// the invoice keeps a declaration the order dropped.
    ///
    /// This note used to end "and is backed by <c>PeppolBisValidator</c>, which checks the BT rules
    /// and reports its gaps honestly". Corrected 2026-08-14: true of the BT rules, but the two
    /// PROFILE identifiers were not among them in any meaningful sense — the validator compared
    /// them against the emitter's own constants on the emitter's own output, so those two checks
    /// could not fail. They are deleted. The sentence is left visible rather than quietly swapped,
    /// because it is what let the invoice-side twin of this defect survive the order-side fix.
    /// </summary>
    [Theory]
    [InlineData("ProcuLink.Transform/Output/UblOrderTransformService.cs")]
    [InlineData("ProcuLink.Transform/Conformance/UblProfileChecker.cs")]
    public void OrderPathSource_CarriesNoPeppolIdentifier(string relativePath)
    {
        var path = Path.Combine(RepoRoot(), relativePath);
        File.Exists(path).Should().BeTrue($"{relativePath} is the file this guard is named after — " +
            "if it moved, repoint the guard rather than letting it read nothing");

        var raw = File.ReadAllText(path);
        raw.Length.Should().BeGreaterThan(500, "anti-vacuity: the file must really have been read");

        var code = WithoutComments(raw);
        code.Should().NotContain(PeppolCustomizationId);
        code.Should().NotContain(PeppolProfileId);
        code.Should().NotContain("urn:fdc:peppol.eu");
        code.Should().Contain("sealed class", "anti-vacuity: stripping comments must not have emptied the file");
    }

    /// <summary>
    /// MUST-FLAG CONTROL for the scan above, in both directions.
    ///
    /// Comments are stripped so that the note recording why the claim was REMOVED does not read as
    /// the claim — <see cref="UblOrderTransformService"/>'s summary quotes both identifiers verbatim
    /// on purpose, because a fix nobody can find the reason for gets undone. But stripping is
    /// exactly how a scan goes quiet while reading green, so the emitting form is replayed here
    /// verbatim as it shipped and must still be caught.
    /// </summary>
    [Fact]
    public void SourceScan_StillSeesTheIdentifierWhenItIsEmittedRatherThanDescribed()
    {
        // The line as it stood at 5db0b05 — UblOrderTransformService.cs:114-115.
        const string asShipped = """
                        new XElement(Cbc + "CustomizationID",     PeppolBisCustomizationId),
                        new XElement(Cbc + "ProfileID",           "urn:fdc:peppol.eu:poacc:bis:order_only:3"),
            """;
        WithoutComments(asShipped).Should().Contain(PeppolProfileId,
            "an emitted literal is code, not commentary, and must survive the strip");

        // And the record of the removal, which must not.
        const string asDocumented = "/// <c>cbc:ProfileID</c> = <c>urn:fdc:peppol.eu:poacc:bis:order_only:3</c>, which declared it a";
        WithoutComments(asDocumented).Should().NotContain("peppol");
        WithoutComments("        // urn:fdc:peppol.eu:poacc:trns:order:3 — removed, see the summary")
            .Should().NotContain("peppol");
    }

    /// <summary>Blanks <c>//</c> and <c>///</c> comment lines, so prose about the defect is not read as the defect.</summary>
    private static string WithoutComments(string source) =>
        string.Join("\n", source.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static async Task<string> EmitAsync(OutputFormat format)
    {
        var result = await new UblOrderTransformService().TransformAsync(BuildOrder(), format, CancellationToken.None);
        using var ms = new MemoryStream();
        await result.Content.CopyToAsync(ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static PurchaseOrderEntity BuildOrder()
    {
        var order = new PurchaseOrderEntity
        {
            Id         = Guid.NewGuid(),
            OrgId      = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            SupplierId = Guid.Parse("00000000-0000-0000-0000-000000000002"),
            PoNumber   = "PO-UBL-PEPPOL-GUARD",
            OrderDate  = new DateOnly(2026, 5, 28),
            Currency   = "EUR",
            Status     = "ready",
        };

        order.Lines = new List<PurchaseOrderLineEntity>
        {
            new()
            {
                LineNumber       = 1,
                BuyerItemCode    = "BUYER-001",
                SupplierItemCode = "SUP-ABC-001",
                Description      = "Widget Type A",
                Quantity         = 10m,
                Unit             = "EA",
                UnitPrice        = 125.00m,
                NeedsReview      = false,
                Confidence       = 1.0f,
            },
        };

        return order;
    }

    /// <summary>
    /// Walks up from the test binary to the repository root — the directory holding
    /// <c>ProcuLink.slnx</c>, which is this repo's solution file (there is no <c>.sln</c>). Same
    /// marker <c>OrphanDetector.FindRepoRoot</c> uses, so a repo layout change moves both together.
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcuLink.slnx"))) dir = dir.Parent;
        dir.Should().NotBeNull($"could not find ProcuLink.slnx walking up from {AppContext.BaseDirectory}");
        return dir!.FullName;
    }
}
