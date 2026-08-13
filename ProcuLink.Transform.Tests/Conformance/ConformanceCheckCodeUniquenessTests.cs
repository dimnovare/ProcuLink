using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Conformance;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Transform.Tests.Conformance;

/// <summary>
/// One property, for every <see cref="IProfileChecker"/>: a report never contains the same
/// <see cref="ConformanceCheck.Code"/> twice.
///
/// <para><b>Why this is a rule and not a nicety.</b> <c>Code</c> is documented in
/// <c>ConformanceModels.cs</c> as the "stable machine code for the check". It crosses the wire
/// to the frontend in <c>ConformanceCheckDto</c>, it is what callers key on
/// (<c>report.Checks.Single(c =&gt; c.Code == "…")</c> throws outright on a duplicate), and
/// <see cref="ConformanceReport.ToMarkdown"/> renders one table row per check — so a duplicated
/// code puts the same named check in the downloadable evidence report twice, saying two
/// different things. That report leaves the product.</para>
///
/// <para><b>Assert on counts, not sets.</b> A set-equality assertion — "the malformed path
/// reports the same code SET as the well-formed path" — cannot see this: duplicates collapse in
/// a set. That is exactly how the original defect survived. Every assertion here counts.</para>
///
/// <para><b>Every profile × every input.</b> The theory is driven from
/// <c>Enum.GetValues&lt;StandardsProfile&gt;()</c> rather than a hand-listed five, so a profile
/// added later is covered the day it is added. Each profile is fed the whole document corpus,
/// not just its own format: a checker must hold the property on any input, and feeding a UBL
/// document to the X12 checker is a legitimate way to reach a branch its own fixtures miss.</para>
/// </summary>
public class ConformanceCheckCodeUniquenessTests
{
    private readonly ConformanceService _svc = new();

    public static IEnumerable<object[]> AllProfiles() =>
        Enum.GetValues<StandardsProfile>().Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public async Task NoReportContainsADuplicateCheckCode(StandardsProfile profile)
    {
        var corpus = await DocumentCorpusAsync();
        var violations = new List<string>();

        foreach (var (inputName, documentText) in corpus)
        {
            var report = _svc.Check(documentText, profile);

            // Counts, deliberately: Distinct()/set comparison hides the exact defect this guards.
            var duplicated = report.Checks
                .GroupBy(c => c.Code, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var g in duplicated)
                violations.Add(
                    $"{profile} · {inputName} input · code '{g.Key}' appears {g.Count()}× " +
                    $"(messages: {string.Join(" | ", g.Select(c => c.Message))})");

            report.Checks.Count.Should().Be(
                report.Checks.Select(c => c.Code).Distinct(StringComparer.Ordinal).Count(),
                $"the {profile} report for the '{inputName}' input must have one check per distinct code");
        }

        violations.Should().BeEmpty(
            "ConformanceCheck.Code is a stable machine key — it is a React list key in the frontend " +
            "panel, callers select on it with Single(), and ToMarkdown() renders one row per check, " +
            "so a repeated code is a duplicated row in the evidence report a customer forwards to a " +
            "supplier");
    }

    // ── Document corpus ─────────────────────────────────────────────────────────
    //
    // Malformed and empty reach the well-formedness / envelope-gate branch of every checker.
    // The "valid …" entries reach each checker's full happy path. The "lineless …" entries reach
    // the zero-lines else-arms in UblProfileChecker / CxmlProfileChecker, which add the same
    // check codes the populated arm adds — a third distinct path, and the one a fixture built
    // only from real transform output never visits.

    private static async Task<IReadOnlyList<(string Name, string Text)>> DocumentCorpusAsync() => new[]
    {
        ("malformed",      "<<< not a valid document >>>"),
        ("empty",          string.Empty),
        ("valid cXML",     await TransformAsync(new CxmlTransformService(), OutputFormat.CXml)),
        ("valid UBL",      await TransformAsync(new UblOrderTransformService(), OutputFormat.Ubl)),
        ("valid X12",      await TransformAsync(new X12TransformService(), OutputFormat.X12)),
        ("valid EDIFACT",  EdifactOrdersSample),
        ("valid IDoc",     IDocSample),
        ("lineless UBL",   LinelessUbl),
        ("lineless cXML",  LinelessCxml),
    };

    private static async Task<string> TransformAsync(ITransformService svc, OutputFormat format)
    {
        var result = await svc.TransformAsync(SampleEntity(), format, default);
        result.Content.Position = 0;
        using var reader = new StreamReader(result.Content);
        return await reader.ReadToEndAsync();
    }

    private static PurchaseOrderEntity SampleEntity() => new()
    {
        Id         = Guid.NewGuid(),
        OrgId      = Guid.NewGuid(),
        SupplierId = Guid.NewGuid(),
        PoNumber   = "PO-DUP-001",
        OrderDate  = new DateOnly(2026, 6, 9),
        Currency   = "EUR",
        Lines = new List<PurchaseOrderLineEntity>
        {
            new() { LineNumber = 1, BuyerItemCode = "BUYER-001", SupplierItemCode = "SUP-001",
                    Description = "Widget A", Quantity = 10m, Unit = "EA", UnitPrice = 12.50m, NeedsReview = false },
        },
    };

    // A structurally-valid UN/EDIFACT ORDERS D.96A interchange (default ISO 9735 delimiters).
    // UNT 0074 (7) is the inclusive UNH..UNT count (UNH·BGM·DTM·LIN·QTY·PRI·UNT).
    private const string EdifactOrdersSample =
        "UNB+UNOC:3+SENDER+RECEIVER+260609:0000+1'" +
        "UNH+1+ORDERS:D:96A:UN'" +
        "BGM+220+PO-DUP-001+9'" +
        "DTM+137:20260609:102'" +
        "LIN+1++BUYER-001:BP'" +
        "QTY+21:10'" +
        "PRI+AAA:12.50'" +
        "UNT+7+1'" +
        "UNZ+1+1'";

    private const string IDocSample = """
        <ORDERS05>
          <IDOC BEGIN="1">
            <E1EDK01><CURCY>EUR</CURCY><BELNR>4501450099</BELNR></E1EDK01>
            <E1EDK02><QUALF>001</QUALF><BELNR>4501450099</BELNR><DATUM>20260603</DATUM></E1EDK02>
            <E1EDP01>
              <POSEX>00010</POSEX><MENGE>50.000</MENGE><MENEE>EA</MENEE><VPREI>1.05</VPREI><NETWR>52.5</NETWR>
              <E1EDP19><QUALF>002</QUALF><IDTNR>15728463</IDTNR></E1EDP19>
            </E1EDP01>
          </IDOC>
        </ORDERS05>
        """;

    // Well-formed UBL 2.1 Order carrying every mandatory header element and zero cac:OrderLine.
    private const string LinelessUbl = """
        <Order xmlns="urn:oasis:names:specification:ubl:schema:xsd:Order-2"
               xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2"
               xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2">
          <cbc:UBLVersionID>2.1</cbc:UBLVersionID>
          <cbc:ID>PO-DUP-001</cbc:ID>
          <cbc:IssueDate>2026-06-09</cbc:IssueDate>
          <cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
          <cac:BuyerCustomerParty><cac:Party><cac:PartyName><cbc:Name>Buyer</cbc:Name></cac:PartyName></cac:Party></cac:BuyerCustomerParty>
        </Order>
        """;

    // Well-formed cXML 1.2 OrderRequest envelope carrying zero ItemOut lines.
    private const string LinelessCxml = """
        <cXML payloadID="x@proculink" timestamp="2026-06-09T00:00:00Z">
          <Header>
            <From><Credential domain="OrgId"><Identity>org</Identity></Credential></From>
            <To><Credential domain="SupplierId"><Identity>sup</Identity></Credential></To>
          </Header>
          <Request deploymentMode="production">
            <OrderRequest>
              <OrderRequestHeader orderID="PO-DUP-001" orderDate="2026-06-09" type="new">
                <Total><Money currency="EUR">0.00</Money></Total>
              </OrderRequestHeader>
            </OrderRequest>
          </Request>
        </cXML>
        """;
}
