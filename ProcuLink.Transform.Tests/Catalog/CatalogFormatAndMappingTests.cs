using System.Text;
using FluentAssertions;
using ProcuLink.Transform.Catalog;

namespace ProcuLink.Transform.Tests.Catalog;

/// <summary>
/// Tests for the real-distributor-feed additions (plan 2026-07-02): per-source column mapping
/// (named + positional), locale-tolerant decimals, quoted-field CSV, and the new XML/cXML/CIF
/// parsers + content-sniffing auto-detect. Fixtures are trimmed from real vendor files
/// (customer-identifying data stripped) under <c>Fixtures/</c>.
/// </summary>
public class CatalogFormatAndMappingTests
{
    static CatalogFormatAndMappingTests()
    {
        // cp1252 is not in the default .NET Core encoding provider.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "Catalog", "Fixtures");

    private static Stream Open(string name) => File.OpenRead(Path.Combine(FixtureDir, name));
    private static Stream Csv(string s, Encoding? enc = null) => new MemoryStream((enc ?? Encoding.UTF8).GetBytes(s));

    // ── D6: quoted-field CSV split ────────────────────────────────────────────

    [Fact]
    public void SplitDelimitedLine_KeepsQuotedDelimiters()
    {
        var parts = SupplierCatalogFileParser.SplitDelimitedLine("A,\"B, still B\",C", ',');
        parts.Should().Equal("A", "B, still B", "C");
    }

    [Fact]
    public void SplitDelimitedLine_UnescapesDoubledQuotes()
    {
        var parts = SupplierCatalogFileParser.SplitDelimitedLine("\"say \"\"hi\"\"\",x", ',');
        parts.Should().Equal("say \"hi\"", "x");
    }

    [Fact]
    public async Task ParseCsv_QuotedCommaInName_NotShredded()
    {
        var csv = "code,name,price\nA-1,\"Widget, deluxe\",12.50\n";
        var result = await SupplierCatalogFileParser.ParseCsvAsync(Csv(csv), CancellationToken.None);
        var d = result.Drafts.Should().ContainSingle().Subject;
        d.Name.Should().Be("Widget, deluxe");
        d.Price.Should().Be(12.50m);
    }

    // ── Locale-tolerant decimals (REDACTED-PARTY comma-decimal, memory locale bug) ───

    [Theory]
    [InlineData("0.04", 0.04)]
    [InlineData("674,68", 674.68)]     // EU comma decimal (REDACTED-PARTY) — the bug this fixes
    [InlineData("1.234,56", 1234.56)]  // EU thousands.decimal
    [InlineData("1,234.56", 1234.56)]  // US thousands.decimal
    [InlineData("9,99", 9.99)]         // lone comma with 2 decimals → decimal, not 999
    [InlineData("1000", 1000)]
    public void ParseCatalogDecimal_HandlesLocales(string raw, double expected)
    {
        SupplierCatalogFileParser.ParseCatalogDecimal(raw).Should().Be((decimal)expected);
    }

    // ── Per-source column mapping: named (REDACTED-PARTY) ──────────────────────────────

    [Fact]
    public async Task ParseJson_SamrsFixture_ZeroRowsWithoutMapping()
    {
        var result = await SupplierCatalogFileParser.ParseJsonAsync(Open("redacted-fixture"), CancellationToken.None);
        // "Id"/"Name"/"Price" are not global aliases → no code column → nothing usable.
        result.Drafts.Should().BeEmpty();
        result.HeaderColumns.Should().Contain("Id").And.Contain("Name").And.Contain("Price");
    }

    [Fact]
    public async Task ParseJson_SamrsFixture_MapsWithOverrides()
    {
        var overrides = new Dictionary<string, string> { ["Id"] = "code", ["Name"] = "name", ["Price"] = "price" };
        var result = await SupplierCatalogFileParser.ParseJsonAsync(Open("redacted-fixture"), CancellationToken.None, overrides);
        result.Drafts.Should().HaveCount(5);
        var first = result.Drafts[0];
        first.Code.Should().Be("923");
        first.Name.Should().Be("REDACTED-ORDER-DATA");
        first.Price.Should().Be(73250m);
    }

    // ── Per-source column mapping: positional (Ingram/Also headerless CSV) ────

    [Fact]
    public async Task ParseCsv_PositionalMapping_HeaderlessComma()
    {
        // Ingram-shape: comma-delimited, NO header. Map by index; first line is DATA.
        var csv = "C,CAT,B793,0710193,DESC,DESC2,NMS,GS108UK,00000017.87,0,Y\n" +
                  "C,CAT,B793,0710342,DESC,DESC2,NMS,GS105UK,00000013.75,0,Y\n";
        var overrides = new Dictionary<string, string>
        {
            ["__noheader__"] = "true",
            ["3"] = "code",     // Ingram SKU
            ["7"] = "external_id",
            ["8"] = "price",
        };
        var result = await SupplierCatalogFileParser.ParseCsvAsync(Csv(csv), CancellationToken.None, overrides, null);
        result.Drafts.Should().HaveCount(2);
        result.Drafts[0].Code.Should().Be("0710193");
        result.Drafts[0].Price.Should().Be(17.87m);
        result.Drafts[0].ExternalId.Should().Be("GS108UK");
    }

    [Fact]
    public async Task ParseCsv_PositionalMapping_TabDelimitedCp1252_Also()
    {
        // Also/Actebis-shape: tab-delimited, cp1252, NO header.
        var line = "1009318\tREDACTED-PARTYID\tPrint\tSupplies\tPapers\tEPSON paper\t0\t36.65\tC13S041068\tEPSON\n";
        var overrides = new Dictionary<string, string>
        {
            ["__noheader__"] = "true",
            ["0"] = "code",
            ["1"] = "barcode",
            ["7"] = "price",
        };
        var enc = System.Text.Encoding.GetEncoding("windows-1252");
        var result = await SupplierCatalogFileParser.ParseCsvAsync(Csv(line, enc), CancellationToken.None, overrides, enc);
        var d = result.Drafts.Should().ContainSingle().Subject;
        d.Code.Should().Be("1009318");
        d.Barcode.Should().Be("REDACTED-PARTYID");
        d.Price.Should().Be(36.65m);
    }

    [Fact]
    public async Task ParseCsv_NamedMapping_REDACTED-PARTY()
    {
        // REDACTED-PARTY-shape: ';'-delimited, header present, comma-decimal price.
        var csv = "ProductId;ReferenceNo;EAN;Manufacturer;Price_B2B_Regular\n" +
                  "6480518;REDACTED-ITEM;;Microsoft;674,68\n";
        var overrides = new Dictionary<string, string>
        {
            ["ReferenceNo"] = "code",
            ["EAN"] = "barcode",
            ["Price_B2B_Regular"] = "price",
        };
        var result = await SupplierCatalogFileParser.ParseCsvAsync(Csv(csv), CancellationToken.None, overrides, null);
        var d = result.Drafts.Should().ContainSingle().Subject;
        d.Code.Should().Be("REDACTED-ITEM");
        d.Price.Should().Be(674.68m);
    }

    [Fact]
    public async Task MapHeaderColumns_OverrideWinsOverAlias()
    {
        // "price" would alias to price; override retargets an aliased column too.
        var header = new List<string> { "sku", "price", "descr" };
        var overrides = new Dictionary<string, string> { ["descr"] = "name" };
        var map = SupplierCatalogFileParser.MapHeaderColumns(header, overrides);
        map[0].Should().Be("code");   // sku alias
        map[1].Should().Be("price");  // price alias
        map[2].Should().Be("name");   // override
    }

    [Fact]
    public void MapHeaderColumns_IgnoresNonCanonicalOverrideTargets()
    {
        var header = new List<string> { "a", "b" };
        var overrides = new Dictionary<string, string> { ["a"] = "not_a_field", ["b"] = "code" };
        var map = SupplierCatalogFileParser.MapHeaderColumns(header, overrides);
        map.Should().NotContainValue("not_a_field");
        map[1].Should().Be("code");
    }

    // ── cXML Index (bare + SOAP) ──────────────────────────────────────────────

    [Fact]
    public async Task ParseCxmlIndex_Bare()
    {
        var result = await SupplierCatalogFileParser.ParseCxmlIndexAsync(Open("cxml-index-bare.xml"), CancellationToken.None);
        result.Format.Should().Be("cxml_index");
        result.Drafts.Should().HaveCount(3);
        var d = result.Drafts[0];
        d.Code.Should().Be("2403916");
        d.Name.Should().Contain("Mousepad");
        d.Unit.Should().Be("EA");
        d.Price.Should().Be(7.01m);
        d.Currency.Should().Be("EUR");
        d.ExternalId.Should().Be("30670483");
    }

    [Fact]
    public async Task ParseCxmlIndex_SoapWrapped()
    {
        var result = await SupplierCatalogFileParser.ParseCxmlIndexAsync(Open("cxml-index-soap.xml"), CancellationToken.None);
        result.Drafts.Should().HaveCount(2);
        result.Drafts[0].Code.Should().Be("34762852");
        result.Drafts[0].Price.Should().Be(1.84m);
        result.Drafts[0].Currency.Should().Be("EUR");
    }

    [Fact]
    public async Task ParseXml_RoutesCxmlIndex_ByLocalName()
    {
        // The router must detect <Index> even inside a SOAP envelope and use the dedicated parser.
        var result = await SupplierCatalogFileParser.ParseXmlAsync(Open("cxml-index-soap.xml"), CancellationToken.None);
        result.Format.Should().Be("cxml_index");
        result.Drafts.Should().HaveCount(2);
    }

    // ── Generic repeating-element XML (100MEGA StoItem) ───────────────────────

    [Fact]
    public async Task ParseGenericXml_StoItem_FlattensAttributes()
    {
        // 100MEGA: fields are attributes on the repeating <StoItem>. Map PriceDea→price and
        // EAN→barcode explicitly (Code2 also aliases to barcode globally, so the EAN override
        // must win — proves overrides pre-empt aliases).
        var overrides = new Dictionary<string, string> { ["PriceDea"] = "price", ["EAN"] = "barcode" };
        var result = await SupplierCatalogFileParser.ParseGenericXmlAsync(Open("stoitembase.xml"), CancellationToken.None, overrides);
        result.Format.Should().Be("xml");
        result.Drafts.Should().HaveCount(3);
        var d = result.Drafts[0];
        d.Code.Should().Be("REDACTED-ORDER-DATA");   // Code attribute → code alias
        d.Barcode.Should().Be("4711213680519"); // EAN → barcode via override (beats Code2 alias)
        d.Name.Should().Contain("REDACTED-ORDER-DATA");
        d.Price.Should().Be(40.12m);       // PriceDea via override
    }

    [Fact]
    public async Task ParseXml_RoutesGeneric_WhenNoIndex()
    {
        var result = await SupplierCatalogFileParser.ParseXmlAsync(Open("stoitembase.xml"), CancellationToken.None);
        result.Format.Should().Be("xml"); // generic, not cxml_index
    }

    [Fact]
    public void DetectDelimiter_TabWithStrayComma_PicksTab()
    {
        // Also/Actebis regression: a tab-delimited line whose data contains a comma must still
        // detect tab (highest count outside quotes), not comma.
        SupplierCatalogFileParser.DetectDelimiter("a\tb, still b\tc\td").Should().Be('\t');
        SupplierCatalogFileParser.DetectDelimiter("code;name;price").Should().Be(';');
        SupplierCatalogFileParser.DetectDelimiter("code,name,price").Should().Be(',');
    }

    [Fact]
    public async Task ParseGenericXml_PicksShallowestRepeatingElement_NotNestedChildren()
    {
        // 100MEGA regression: the record element (<item>, a direct child of root) must win even
        // though the nested <img> gallery children are far more numerous.
        var xml = "<root>"
                + "<item sku=\"A-1\"><name>Widget</name><gallery><img u=\"1\"/><img u=\"2\"/><img u=\"3\"/></gallery></item>"
                + "<item sku=\"B-2\"><name>Gadget</name><gallery><img u=\"4\"/><img u=\"5\"/><img u=\"6\"/></gallery></item>"
                + "</root>";
        var overrides = new Dictionary<string, string> { ["sku"] = "code" };
        var result = await SupplierCatalogFileParser.ParseGenericXmlAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(xml)), CancellationToken.None, overrides);
        result.Drafts.Should().HaveCount(2); // 2 items, NOT 6 img nodes
        result.Drafts[0].Code.Should().Be("A-1");
    }

    [Fact]
    public async Task ParseGenericXml_SkipsNestedNonScalarChildren()
    {
        var xml = "<root><item sku=\"A-1\"><name>Widget</name><gallery><img u=\"x\"/></gallery></item>" +
                  "<item sku=\"B-2\"><name>Gadget</name></item></root>";
        var overrides = new Dictionary<string, string> { ["sku"] = "code" };
        var result = await SupplierCatalogFileParser.ParseGenericXmlAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(xml)), CancellationToken.None, overrides);
        result.Drafts.Should().HaveCount(2);
        result.Drafts[0].Code.Should().Be("A-1");
        result.Drafts[0].Name.Should().Be("Widget");
    }

    // ── CIF 3.0 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ParseCif_MapsSpacedFieldNames_AndQuotedCommas()
    {
        var result = await SupplierCatalogFileParser.ParseCifAsync(Open("cif-3.0.cif"), CancellationToken.None);
        result.Format.Should().Be("cif");
        result.Drafts.Should().HaveCount(3);
        var d = result.Drafts[0];
        d.Code.Should().Be("13080788");            // "Supplier Part ID" spaced alias
        d.Name.Should().StartWith("example.invalid"); // quoted description parsed whole (not truncated)
        d.Name.Should().Contain("50 cm Rouge");     // full field survived quoted-CSV splitting
        d.Price.Should().Be(4.24m);                 // "Unit Price"
        d.Unit.Should().Be("EA");
        d.Currency.Should().Be("EUR");              // CURRENCY: header default
    }

    // ── Content-sniffing auto-detect ──────────────────────────────────────────

    [Fact]
    public async Task ParseAuto_CifNamedAsXml_DetectedByContent()
    {
        // saint-gobain ships a CIF file with a .xml extension — must sniff CONTENT.
        var result = await SupplierCatalogFileParser.ParseAutoAsync(
            Open("cif-3.0.cif"), contentType: null, fileName: "catalog.xml", CancellationToken.None);
        result.Format.Should().Be("cif");
        result.Drafts.Should().HaveCount(3);
    }

    [Fact]
    public async Task ParseAuto_XmlContent_RoutesToXml()
    {
        var result = await SupplierCatalogFileParser.ParseAutoAsync(
            Open("cxml-index-bare.xml"), contentType: null, fileName: null, CancellationToken.None);
        result.Format.Should().Be("cxml_index");
    }

    [Fact]
    public async Task ParseAuto_JsonContent_RoutesToJson()
    {
        var overrides = new Dictionary<string, string> { ["Id"] = "code", ["Name"] = "name", ["Price"] = "price" };
        var result = await SupplierCatalogFileParser.ParseAutoAsync(
            Open("redacted-fixture"), contentType: null, fileName: null, CancellationToken.None, overrides);
        result.Format.Should().Be("json");
        result.Drafts.Should().HaveCount(5);
    }

    [Fact]
    public async Task ParseAuto_CsvContent_Fallback()
    {
        var result = await SupplierCatalogFileParser.ParseAutoAsync(
            Csv("code,name\nA-1,Widget\n"), contentType: null, fileName: "x.txt", CancellationToken.None);
        result.Format.Should().Be("csv");
        result.Drafts.Should().ContainSingle().Which.Code.Should().Be("A-1");
    }

    // ── ParseByFileName routes new extensions ─────────────────────────────────

    [Fact]
    public async Task ParseByFileName_XmlExtension_RoutesToXml()
    {
        var result = await SupplierCatalogFileParser.ParseByFileNameAsync(
            Open("cxml-index-bare.xml"), "catalog.xml", CancellationToken.None);
        result.Format.Should().Be("cxml_index");
    }

    [Fact]
    public async Task ParseByFileName_CifExtension_RoutesToCif()
    {
        var result = await SupplierCatalogFileParser.ParseByFileNameAsync(
            Open("cif-3.0.cif"), "catalog.cif", CancellationToken.None);
        result.Format.Should().Be("cif");
    }
}
