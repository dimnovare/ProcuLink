using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Conformance;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Transform.Tests.Conformance;

/// <summary>
/// Group V8 — standards conformance reports.
///
/// The core correctness assertion: the document each transform ACTUALLY produces
/// passes its named profile. The negative cases prove a doc missing a mandatory
/// element / segment fails with the specifically-named check (and that the report
/// is still produced, never throwing).
/// </summary>
public class ConformanceServiceTests
{
    private readonly ConformanceService _svc = new();

    // ── Fixtures ────────────────────────────────────────────────────────────────

    // A structurally-valid UN/EDIFACT ORDERS D.96A interchange (default ISO 9735
    // delimiters): UNB · UNH(ORDERS:D:96A:UN) · BGM · DTM · 2×(LIN+QTY+PRI) · UNT · UNZ.
    // The UNT 0074 count (10) is the inclusive UNH..UNT segment count
    // (UNH·BGM·DTM·LIN·QTY·PRI·LIN·QTY·PRI·UNT).
    private const string EdifactOrdersSample =
        "UNB+UNOC:3+SENDER+RECEIVER+260609:0000+1'" +
        "UNH+1+ORDERS:D:96A:UN'" +
        "BGM+220+PO-CONF-001+9'" +
        "DTM+137:20260609:102'" +
        "LIN+1++BUYER-001:BP'" +
        "QTY+21:10'" +
        "PRI+AAA:12.50'" +
        "LIN+2++BUYER-002:BP'" +
        "QTY+21:3'" +
        "PRI+AAA:99.00'" +
        "UNT+10+1'" +
        "UNZ+1+1'";

    private static PurchaseOrderEntity SampleEntity() => new()
    {
        Id        = Guid.NewGuid(),
        OrgId     = Guid.NewGuid(),
        SupplierId = Guid.NewGuid(),
        PoNumber  = "PO-CONF-001",
        OrderDate = new DateOnly(2026, 6, 9),
        Currency  = "EUR",
        Lines = new List<PurchaseOrderLineEntity>
        {
            new() { LineNumber = 1, BuyerItemCode = "BUYER-001", SupplierItemCode = "SUP-001",
                    Description = "Widget A", Quantity = 10m, Unit = "EA", UnitPrice = 12.50m, NeedsReview = false },
            new() { LineNumber = 2, BuyerItemCode = "BUYER-002", SupplierItemCode = "SUP-002",
                    Description = "Widget B", Quantity = 3m,  Unit = "EA", UnitPrice = 99.00m, NeedsReview = false },
        },
    };

    private const string IDocSample = """
        <ORDERS05>
          <IDOC BEGIN="1">
            <E1EDK01><CURCY>EUR</CURCY><BELNR>4501450099</BELNR></E1EDK01>
            <E1EDK02><QUALF>001</QUALF><BELNR>4501450099</BELNR><DATUM>20260603</DATUM></E1EDK02>
            <E1EDP01>
              <POSEX>00010</POSEX><MENGE>50.000</MENGE><MENEE>EA</MENEE><VPREI>1.05</VPREI><NETWR>52.5</NETWR>
              <E1EDP19><QUALF>002</QUALF><IDTNR>15728463</IDTNR></E1EDP19>
            </E1EDP01>
            <E1EDS01><SUMME>52.5</SUMME><SUNIT>EUR</SUNIT></E1EDS01>
          </IDOC>
        </ORDERS05>
        """;

    // ── 1. Each transform's real output passes its named profile ────────────────

    [Fact]
    public async Task Cxml_RealTransformOutput_PassesProfile()
    {
        var bytes = await new CxmlTransformService().TransformAsync(SampleEntity(), OutputFormat.CXml, default);
        var text  = ReadAll(bytes.Content);

        var report = _svc.Check(text, OutputFormat.CXml);

        report.Profile.Should().Be(StandardsProfile.CXml12OrderRequest);
        report.OverallPass.Should().BeTrue(report.ToMarkdown());
        report.Checks.Should().OnlyContain(c => c.Passed);
    }

    [Fact]
    public async Task Ubl_RealTransformOutput_PassesProfile()
    {
        var bytes = await new UblOrderTransformService().TransformAsync(SampleEntity(), OutputFormat.Ubl, default);
        var text  = ReadAll(bytes.Content);

        var report = _svc.Check(text, OutputFormat.Ubl);

        report.Profile.Should().Be(StandardsProfile.Ubl21Order);
        report.OverallPass.Should().BeTrue(report.ToMarkdown());
    }

    [Fact]
    public async Task X12_EntityTransformOutput_PassesProfile()
    {
        var bytes = await new X12TransformService().TransformAsync(SampleEntity(), OutputFormat.X12, default);
        var text  = ReadAll(bytes.Content);

        var report = _svc.Check(text, OutputFormat.X12);

        report.Profile.Should().Be(StandardsProfile.X12_850);
        report.OverallPass.Should().BeTrue(report.ToMarkdown());
    }

    [Fact]
    public void Edifact_RealOrdersD96ADocument_PassesProfile()
    {
        var report = _svc.Check(EdifactOrdersSample, OutputFormat.EdifactOrders);

        report.Profile.Should().Be(StandardsProfile.EdifactOrdersD96A);
        report.OverallPass.Should().BeTrue(report.ToMarkdown());
    }

    [Fact]
    public void IDoc_RealOrders05Document_PassesProfile()
    {
        var report = _svc.Check(IDocSample, StandardsProfile.SapIDocOrders05);

        report.Profile.Should().Be(StandardsProfile.SapIDocOrders05);
        report.OverallPass.Should().BeTrue(report.ToMarkdown());
    }

    // ── 2. Missing-mandatory cases fail with the named check ────────────────────

    [Fact]
    public void Cxml_MissingOrderId_FailsWithNamedCheck()
    {
        // Valid cXML envelope but the mandatory OrderRequestHeader/@orderID removed.
        const string doc = """
            <cXML payloadID="x@proculink" timestamp="2026-06-09T00:00:00Z">
              <Header>
                <From><Credential domain="OrgId"><Identity>org</Identity></Credential></From>
                <To><Credential domain="SupplierId"><Identity>sup</Identity></Credential></To>
              </Header>
              <Request deploymentMode="production">
                <OrderRequest>
                  <OrderRequestHeader orderDate="2026-06-09" type="new">
                    <Total><Money currency="EUR">10.00</Money></Total>
                  </OrderRequestHeader>
                  <ItemOut quantity="1" lineNumber="1">
                    <ItemID><SupplierPartID>S1</SupplierPartID></ItemID>
                    <ItemDetail><UnitPrice><Money currency="EUR">10.00</Money></UnitPrice></ItemDetail>
                  </ItemOut>
                </OrderRequest>
              </Request>
            </cXML>
            """;

        var report = _svc.Check(doc, OutputFormat.CXml);

        report.OverallPass.Should().BeFalse();
        var failed = report.Checks.Single(c => c.Code == "cxml.orderRequestHeader.orderID");
        failed.Passed.Should().BeFalse();
        failed.Severity.Should().Be(ConformanceSeverity.Error);
        failed.ProfileRef.Should().Be("OrderRequestHeader/@orderID");
    }

    [Fact]
    public void Ubl_MissingId_FailsWithNamedCheck()
    {
        // Well-formed UBL 2.1 Order with the mandatory cbc:ID omitted.
        const string doc = """
            <Order xmlns="urn:oasis:names:specification:ubl:schema:xsd:Order-2"
                   xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2"
                   xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2">
              <cbc:UBLVersionID>2.1</cbc:UBLVersionID>
              <cbc:CustomizationID>urn:fdc:peppol.eu:poacc:trns:order:3</cbc:CustomizationID>
              <cbc:ProfileID>urn:fdc:peppol.eu:poacc:bis:order_only:3</cbc:ProfileID>
              <cbc:IssueDate>2026-06-09</cbc:IssueDate>
              <cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
              <cac:BuyerCustomerParty><cac:Party><cac:PartyName><cbc:Name>Buyer</cbc:Name></cac:PartyName></cac:Party></cac:BuyerCustomerParty>
              <cac:OrderLine><cac:LineItem>
                <cbc:ID>1</cbc:ID>
                <cbc:Quantity unitCode="EA">1</cbc:Quantity>
                <cac:Item><cbc:Name>X</cbc:Name></cac:Item>
              </cac:LineItem></cac:OrderLine>
            </Order>
            """;

        var report = _svc.Check(doc, OutputFormat.Ubl);

        report.OverallPass.Should().BeFalse();
        var failed = report.Checks.Single(c => c.Code == "ubl.id");
        failed.Passed.Should().BeFalse();
        failed.ProfileRef.Should().Be("cbc:ID");
    }

    [Fact]
    public async Task X12_MissingPo1_FailsWithNamedCheck()
    {
        // Strip the PO1 detail lines from a real 850 to break the mandatory cardinality.
        var bytes = await new X12TransformService().TransformAsync(SampleEntity(), OutputFormat.X12, default);
        var real = ReadAll(bytes.Content);
        var withoutPo1 = string.Concat(real.Split('~')
            .Where(s => !s.TrimStart('\r', '\n').StartsWith("PO1", StringComparison.Ordinal))
            .Select(s => s + "~"))
            .Replace("~~", "~"); // tidy doubled terminators from the removal

        var report = _svc.Check(withoutPo1, OutputFormat.X12_850);

        report.OverallPass.Should().BeFalse();
        var failed = report.Checks.Single(c => c.Code == "x12.po1.cardinality");
        failed.Passed.Should().BeFalse();
        failed.ProfileRef.Should().Be("PO1 (baseline item data, 1..n)");
    }

    [Fact]
    public void Edifact_WrongMessageType_FailsWithNamedCheck()
    {
        // Take a real ORDERS D.96A message and corrupt the UNH message identifier.
        var corrupted = EdifactOrdersSample.Replace("ORDERS:D:96A:UN", "ORDERS:D:01B:UN");

        var report = _svc.Check(corrupted, OutputFormat.EdifactOrders);

        report.OverallPass.Should().BeFalse();
        var failed = report.Checks.Single(c => c.Code == "edifact.unh.messageType");
        failed.Passed.Should().BeFalse();
        failed.ProfileRef.Should().Be("UNH S009 (ORDERS:D:96A:UN)");
    }

    [Fact]
    public void IDoc_MissingLineSegment_FailsWithNamedCheck()
    {
        const string doc = """
            <ORDERS05>
              <IDOC BEGIN="1">
                <E1EDK01><BELNR>4501450099</BELNR></E1EDK01>
                <E1EDK02><QUALF>001</QUALF><BELNR>4501450099</BELNR></E1EDK02>
              </IDOC>
            </ORDERS05>
            """;

        var report = _svc.Check(doc, StandardsProfile.SapIDocOrders05);

        report.OverallPass.Should().BeFalse();
        var failed = report.Checks.Single(c => c.Code == "idoc.e1edp01.cardinality");
        failed.Passed.Should().BeFalse();
    }

    // ── 3. Robustness: malformed input never throws ─────────────────────────────

    [Fact]
    public void MalformedXml_ProducesReport_DoesNotThrow()
    {
        var report = _svc.Check("<cXML><unclosed>", OutputFormat.CXml);

        report.OverallPass.Should().BeFalse();
        report.Checks.Should().Contain(c => c.Code == "cxml.wellformed" && !c.Passed);
    }

    [Fact]
    public void GarbageX12_ProducesReport_DoesNotThrow()
    {
        var report = _svc.Check("not an edi document at all", OutputFormat.X12);

        report.OverallPass.Should().BeFalse();
        report.Checks.Should().Contain(c => c.Code == "x12.isa" && !c.Passed);
    }

    // ── 4. Format/profile mapping + unsupported-format guard ────────────────────

    [Theory]
    [InlineData(OutputFormat.CXml, StandardsProfile.CXml12OrderRequest)]
    [InlineData(OutputFormat.Ubl, StandardsProfile.Ubl21Order)]
    [InlineData(OutputFormat.UblOrder, StandardsProfile.Ubl21Order)]
    [InlineData(OutputFormat.X12, StandardsProfile.X12_850)]
    [InlineData(OutputFormat.X12_850, StandardsProfile.X12_850)]
    [InlineData(OutputFormat.EdifactOrders, StandardsProfile.EdifactOrdersD96A)]
    public void ProfileForFormat_MapsStandardsFormats(OutputFormat format, StandardsProfile expected)
    {
        _svc.SupportsFormat(format).Should().BeTrue();
        _svc.ProfileForFormat(format).Should().Be(expected);
    }

    [Theory]
    [InlineData(OutputFormat.Csv)]
    [InlineData(OutputFormat.Json)]
    [InlineData(OutputFormat.Xml)]
    public void GenericFormats_HaveNoNamedProfile(OutputFormat format)
    {
        _svc.SupportsFormat(format).Should().BeFalse();
        _svc.ProfileForFormat(format).Should().BeNull();
        Action act = () => _svc.Check("anything", format);
        act.Should().Throw<NotSupportedException>();
    }

    // ── 5. Markdown variant renders deterministically ───────────────────────────

    [Fact]
    public async Task ToMarkdown_RendersProfileNameAndResult()
    {
        var bytes = await new X12TransformService().TransformAsync(SampleEntity(), OutputFormat.X12, default);
        var report = _svc.Check(ReadAll(bytes.Content), OutputFormat.X12_850);

        var md = report.ToMarkdown();

        md.Should().Contain("ANSI X12 850 Purchase Order");
        md.Should().Contain("004010");
        md.Should().Contain("**Result:** PASS");
        // Deterministic — re-rendering the same report is byte-identical.
        md.Should().Be(report.ToMarkdown());
    }

    private static string ReadAll(Stream s)
    {
        s.Position = 0;
        using var sr = new StreamReader(s);
        return sr.ReadToEnd();
    }
}
