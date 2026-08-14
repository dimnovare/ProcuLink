using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Conformance;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Transform.Tests.Conformance;

/// <summary>
/// The downloadable report must say which of its rows are evidence and which are ProcuLink talking
/// to itself.
///
/// <para><b>The defect.</b> Every row rendered as an undifferentiated PASS under a Download button,
/// beneath a doc comment inviting the customer to forward the file "to a supplier or an access
/// point as evidence". For cXML and X12 that verdict is a tautology: the emitter writes a constant
/// and the checker then asserts the same constant. Verified pairs, both directions checked against
/// the source — <c>X12TransformService</c> writes <c>GS01</c>=<c>"PO"</c> and <c>ST01</c>=<c>"850"</c>
/// while <c>X12ProfileChecker</c> asserts exactly those; <c>UblOrderTransformService</c> writes
/// <c>cbc:UBLVersionID</c>=<c>"2.1"</c> while <c>UblProfileChecker</c> asserts <c>"2.1"</c>. Two
/// further checks are defeated by emitter fallbacks added to keep elements non-empty, so a
/// buyer-less order still passes "Buyer party name present."</para>
///
/// <para><b>Why these tests are shaped this way.</b> A test written on ProcuLink's own conformant
/// output CANNOT detect a circular check — the emitted value and the asserted value match, the
/// check is silent, and the report is green either way. So none of these assert that a document
/// passes. They assert what the report CLAIMS: that a self-check is labelled one, that a report
/// containing no third-party verdict says so in those words, and that the single genuinely external
/// check keeps its standing rather than being flattened into the same disclaimer.</para>
///
/// <para><b>Both directions.</b> Over-claiming (a tautology presented as evidence) and
/// under-claiming (a real OASIS schema verdict disclaimed away) are both false statements, and the
/// product shipped both at once. Each has a test here.</para>
/// </summary>
public class ConformanceEvidenceClassTests
{
    private readonly ConformanceService _svc = new();

    // ── The one real external verdict keeps its standing ────────────────────────

    [Fact]
    public async Task TheUblSchemaCheckIsTheOnlyRowClassedAsThirdPartyEvidence()
    {
        var report = _svc.Check(await EmitUblAsync(), OutputFormat.Ubl);

        report.Checks.Should().Contain(c => c.Code == "ubl.xsd",
            "anti-vacuity: the schema check must really be in the report");

        report.Checks
            .Where(c => c.Evidence == ConformanceEvidence.ExternalArtifact)
            .Select(c => c.Code)
            .Should().BeEquivalentTo(new[] { "ubl.xsd" },
                "only validation against the vendored OASIS grammar is a verdict ProcuLink did not author");
    }

    [Fact]
    public async Task TheReportDoesNotDisclaimAwayTheSchemaValidationItReallyRan()
    {
        var markdown = _svc.Check(await EmitUblAsync(), OutputFormat.Ubl).ToMarkdown();

        markdown.Should().Contain("Published schema",
            "the OASIS XSD verdict is the strongest thing in this file and must be legible as such");
        markdown.Should().NotContain("Not a schema validation",
            "a schema validation DID run — the retired blanket disclaimer was false in this direction, " +
            "and under-claiming a real check is its own false statement");
    }

    // ── The self-checks are labelled, not presented as evidence ─────────────────

    [Theory]
    [InlineData("ubl.version")]    // emitter writes cbc:UBLVersionID 2.1; checker asserts "2.1"
    [InlineData("ubl.currency")]   // emitter falls back to "EUR" so the element is never empty
    [InlineData("ubl.buyerParty")] // emitter falls back to a placeholder buyer name
    public async Task ACheckThatOnlyRestatesTheEmittersOwnConstantIsClassedAsASelfCheck(string code)
    {
        var report = _svc.Check(await EmitUblAsync(), OutputFormat.Ubl);

        report.Checks.Should().ContainSingle(c => c.Code == code)
            .Which.Evidence.Should().Be(ConformanceEvidence.SelfCheck,
                $"'{code}' asserts a value this codebase's own emitter wrote, so passing it says " +
                "nothing about the document");
    }

    [Theory]
    [InlineData("x12.gs.po")]  // emitter writes GS01 "PO";  checker asserts "PO"
    [InlineData("x12.st850")]  // emitter writes ST01 "850"; checker asserts "850"
    public async Task TheCircularX12ChecksAreClassedAsSelfChecks(string code)
    {
        var report = _svc.Check(await EmitX12Async(), OutputFormat.X12_850);

        report.Checks.Should().ContainSingle(c => c.Code == code)
            .Which.Evidence.Should().Be(ConformanceEvidence.SelfCheck);
    }

    /// <summary>
    /// The control the brief asks for, and the one a happy-path test cannot provide: a report whose
    /// rows are ALL self-checks must say so in the report itself. X12 and cXML have no vendored
    /// grammar, so every row in them is ProcuLink's own reading — and the file is forwardable.
    /// </summary>
    [Theory]
    [InlineData(OutputFormat.X12_850)]
    [InlineData(OutputFormat.CXml)]
    public async Task AReportWithNoThirdPartyVerdictSaysSoInWords(OutputFormat format)
    {
        var document = format == OutputFormat.CXml ? await EmitCxmlAsync() : await EmitX12Async();
        var report   = _svc.Check(document, format);

        report.Checks.Should().NotBeEmpty("anti-vacuity: the report must really contain checks");
        report.Checks.Should().OnlyContain(c => c.Evidence == ConformanceEvidence.SelfCheck,
            "no vendored grammar exists for this format");

        report.ToMarkdown().Should()
            .Contain("No row in this report was validated against a published schema",
                "the reader of a forwarded PASS must not have to infer the absence of third-party evidence");
    }

    /// <summary>
    /// The default is the weaker claim. If a future check omits the evidence argument it must land
    /// as a self-check, never inherit third-party standing by silence — the failure mode this repo
    /// keeps hitting is an unclassified value falling through to the favourable reading.
    /// </summary>
    [Fact]
    public void AnUnclassifiedCheckDefaultsToTheWeakerClaim()
    {
        var check = new ConformanceCheck("some.new.check", ConformanceSeverity.Error, true, "…", "ref");

        check.Evidence.Should().Be(ConformanceEvidence.SelfCheck);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────

    private static async Task<string> EmitUblAsync() =>
        ReadAll((await new UblOrderTransformService()
            .TransformAsync(SampleEntity(), OutputFormat.Ubl, CancellationToken.None)).Content);

    private static async Task<string> EmitX12Async() =>
        ReadAll((await new X12TransformService()
            .TransformAsync(SampleEntity(), OutputFormat.X12, CancellationToken.None)).Content);

    private static async Task<string> EmitCxmlAsync() =>
        ReadAll((await new CxmlTransformService()
            .TransformAsync(SampleEntity(), OutputFormat.CXml, CancellationToken.None)).Content);

    private static string ReadAll(Stream s)
    {
        s.Position = 0;
        using var sr = new StreamReader(s);
        return sr.ReadToEnd();
    }

    private static PurchaseOrderEntity SampleEntity() => new()
    {
        Id         = Guid.NewGuid(),
        OrgId      = Guid.NewGuid(),
        SupplierId = Guid.NewGuid(),
        PoNumber   = "PO-CONF-001",
        OrderDate  = new DateOnly(2026, 6, 9),
        Currency   = "EUR",
        Lines = new List<PurchaseOrderLineEntity>
        {
            new() { LineNumber = 1, BuyerItemCode = "BUYER-001", SupplierItemCode = "SUP-001",
                    Description = "Widget A", Quantity = 10m, Unit = "EA", UnitPrice = 12.50m, NeedsReview = false },
            new() { LineNumber = 2, BuyerItemCode = "BUYER-002", SupplierItemCode = "SUP-002",
                    Description = "Widget B", Quantity = 3m,  Unit = "EA", UnitPrice = 99.00m, NeedsReview = false },
        },
    };
}
