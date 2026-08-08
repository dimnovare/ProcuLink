using System.Text;
using FluentAssertions;
using ProcuLink.Transform.Catalog;

namespace ProcuLink.Transform.Tests.Catalog;

/// <summary>
/// Tests for manufacturer part number (MPN) as a first-class catalog field.
///
/// Why it matters: a distributor sells one manufacturer part under its OWN code, so when a
/// punchout order names only the manufacturer part (see <c>Fixtures/real-cxml-1.2-ariba-punchout-mpn-differs.xml</c>,
/// where <c>SupplierPartID</c> is the buying network's internal id and resolves against nothing)
/// the MPN column is the only thing that can still match the line. Every header spelling covered
/// here is one a real feed ships.
///
/// The regression guard below is the important one: <c>"manufacturer part id"</c> used to alias to
/// <c>external_id</c> — the idempotent RE-SYNC key — where it was both useless for matching and
/// able to collide with a genuine ERP id.
/// </summary>
public class CatalogManufacturerPartMappingTests
{
    private static Stream Csv(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "Catalog", "Fixtures");

    private static Stream OpenFixture(string name) => File.OpenRead(Path.Combine(FixtureDir, name));

    // ── CSV alias detection ───────────────────────────────────────────────────

    [Fact]
    public async Task ParseCsv_MpnColumn_LandsInManufacturerPartNumber()
    {
        var csv = "code,name,price,mpn\n" +
                  "SUP-77,Barcode scanner,129.00,REDACTED-ORDER-DATA\n";

        var result = await SupplierCatalogFileParser.ParseCsvAsync(Csv(csv), CancellationToken.None);

        var d = result.Drafts.Should().ContainSingle().Subject;
        d.Code.Should().Be("SUP-77");
        d.Price.Should().Be(129.00m);
        d.ManufacturerPartNumber.Should().Be("REDACTED-ORDER-DATA");
        d.ManufacturerName.Should().BeNull("the feed carries no brand column");
        // The raw value is stored verbatim; the normalised key has exactly one writer
        // (SupplierCatalogService.UpsertManyAsync), so the parser must NOT pre-compute it.
        d.ManufacturerPartNumberNormalized.Should().BeNull();
    }

    [Fact]
    public async Task ParseCsv_GermanHeader_HerstellernummerAndHersteller_AreMapped()
    {
        // German distributor feed: "Herstellernummer" = the MANUFACTURER's number,
        // "Hersteller" = the manufacturer's name.
        var csv = "sku;bezeichnung;herstellernummer;hersteller\n" +
                  "A-1;Etikettendrucker;ZD4A042-D0EM00EZ;Zebra\n";

        var result = await SupplierCatalogFileParser.ParseCsvAsync(Csv(csv), CancellationToken.None);

        var d = result.Drafts.Should().ContainSingle().Subject;
        d.Code.Should().Be("A-1");
        d.ManufacturerPartNumber.Should().Be("ZD4A042-D0EM00EZ");
        d.ManufacturerName.Should().Be("Zebra");
    }

    [Fact]
    public async Task ParseCsv_DistributorOwnCodeShape_OriginalArtNoIsTheMpn_ArtnumIsTheSupplierCode()
    {
        // A measured distributor price feed (2026-07-24): ARTNUM is the distributor's OWN code and
        // ORIGINAL_ART_NO is the manufacturer part — getting these the wrong way round files
        // every supplier code as an MPN.
        //
        // ARTNUM / SHORT_EN / YOUR_PRICE_NET are vendor-specific spellings and are mapped by the
        // per-source overrides, exactly as the live source is configured. ORIGINAL_ART_NO and
        // MANUFACTURER carry NO override on purpose: they must be picked up by the global
        // aliases, which is the behaviour under test.
        var csv = "ARTNUM;SHORT_EN;YOUR_PRICE_NET;ORIGINAL_ART_NO;MANUFACTURER\n" +
                  "10001;Zebra ZD421;130,41;ZD4A042;Zebra\n" +
                  "10002;REDACTED-PARTY QD2430;89,90;REDACTED-ITEM;REDACTED-PARTY\n";
        var overrides = new Dictionary<string, string>
        {
            ["ARTNUM"] = "code",
            ["SHORT_EN"] = "name",
            ["YOUR_PRICE_NET"] = "price",
        };

        var result = await SupplierCatalogFileParser.ParseCsvAsync(
            Csv(csv), CancellationToken.None, overrides, null);

        result.Drafts.Should().HaveCount(2);

        var first = result.Drafts[0];
        first.Code.Should().Be("10001");                      // ← ARTNUM (override)
        first.Name.Should().Be("Zebra ZD421");
        first.Price.Should().Be(130.41m);                     // comma decimal, locale-tolerant
        first.ManufacturerPartNumber.Should().Be("ZD4A042");  // ← ORIGINAL_ART_NO (alias)
        first.ManufacturerName.Should().Be("Zebra");          // ← MANUFACTURER (alias)
        first.ExternalId.Should().BeNull("ORIGINAL_ART_NO is the MPN now, not the re-sync key");

        var second = result.Drafts[1];
        second.Code.Should().Be("10002");
        second.ManufacturerPartNumber.Should().Be("REDACTED-ITEM");
        second.ManufacturerName.Should().Be("REDACTED-PARTY");
    }

    // ── REGRESSION GUARD: "Manufacturer Part ID" was retargeted off external_id ──

    [Fact]
    public async Task ParseCsv_ManufacturerPartId_LandsInMpn_NotExternalId()
    {
        // The CIF 3.0 / cXML spelling. It used to alias to external_id; a revert must fail here.
        var csv = "code,name,Manufacturer Part ID\n" +
                  "SUP-77,Barcode scanner,REDACTED-ORDER-DATA\n";

        var result = await SupplierCatalogFileParser.ParseCsvAsync(Csv(csv), CancellationToken.None);

        var d = result.Drafts.Should().ContainSingle().Subject;
        d.ManufacturerPartNumber.Should().Be("REDACTED-ORDER-DATA");
        d.ExternalId.Should().BeNull(
            "Manufacturer Part ID is the cross-party match key, NOT the idempotent re-sync key");
    }

    [Fact]
    public async Task ParseCsv_ErpIdAndManufacturerPartId_BothSurvive_InTheirOwnFields()
    {
        // The sharpest form of the same guard. Under the OLD alias both columns targeted
        // external_id, and "first mapping to a canonical field wins" meant erp_id took it and the
        // manufacturer part was DROPPED ENTIRELY — a silent data loss, not a mis-file.
        var csv = "code,erp_id,Manufacturer Part ID\n" +
                  "SUP-77,ERP-7,REDACTED-ORDER-DATA\n";

        var result = await SupplierCatalogFileParser.ParseCsvAsync(Csv(csv), CancellationToken.None);

        var d = result.Drafts.Should().ContainSingle().Subject;
        d.ExternalId.Should().Be("ERP-7");
        d.ManufacturerPartNumber.Should().Be("REDACTED-ORDER-DATA");
    }

    [Fact]
    public void MapHeaderColumns_ManufacturerPartId_TargetsManufacturerPartNumber()
    {
        var map = SupplierCatalogFileParser.MapHeaderColumns(new[] { "Manufacturer Part ID" });

        map.Should().ContainKey(0);
        map[0].Should().Be("manufacturer_part_number");
        map.Should().NotContainValue("external_id");
    }

    // ── "artikelnummer" is deliberately NOT an MPN alias ──────────────────────

    [Fact]
    public async Task ParseCsv_Artikelnummer_IsNotTreatedAsAManufacturerPartNumber()
    {
        // In German catalogues "Artikelnummer" means the VENDOR's own article number. Aliasing it
        // to the MPN would file every supplier code as a manufacturer part and produce confident
        // cross-supplier matches that are simply wrong.
        var csv = "sku,name,artikelnummer\n" +
                  "A-1,Widget,VENDOR-9911\n";

        var result = await SupplierCatalogFileParser.ParseCsvAsync(Csv(csv), CancellationToken.None);

        var d = result.Drafts.Should().ContainSingle().Subject;
        d.Code.Should().Be("A-1");
        d.ManufacturerPartNumber.Should().BeNull();
    }

    [Fact]
    public void MapHeaderColumns_Artikelnummer_NeverTargetsManufacturerPartNumber()
    {
        var map = SupplierCatalogFileParser.MapHeaderColumns(new[] { "artikelnummer" });

        map.Should().NotContainValue("manufacturer_part_number");
    }

    [Theory]
    // The German MPN spellings that DO map — the contrast that proves the lookup is exact-key and
    // not a substring test ("herstellerartikelnummer" contains "artikelnummer").
    [InlineData("herstellernummer")]
    [InlineData("hersteller_nr")]
    [InlineData("herstellernr")]
    [InlineData("herstellerartikelnummer")]
    [InlineData("hersteller_artikelnummer")]
    public void MapHeaderColumns_GermanManufacturerPartAliases_TargetMpn(string header)
    {
        var map = SupplierCatalogFileParser.MapHeaderColumns(new[] { header });

        map.Should().ContainKey(0);
        map[0].Should().Be("manufacturer_part_number");
    }

    [Fact]
    public void CanonicalFields_IncludeTheTwoNewManufacturerTargets()
    {
        // Overrides are only honoured for canonical targets, so a per-source mapping cannot point
        // at either field unless both are declared here.
        SupplierCatalogFileParser.CanonicalFields
            .Should().Contain("manufacturer_part_number")
            .And.Contain("manufacturer_name");
    }

    // ── cXML catalog Index ────────────────────────────────────────────────────

    [Fact]
    public async Task ParseCxmlIndex_Bare_CarriesManufacturerPartAndName()
    {
        var result = await SupplierCatalogFileParser.ParseCxmlIndexAsync(
            OpenFixture("cxml-index-bare.xml"), CancellationToken.None);

        result.Format.Should().Be("cxml_index");
        result.Drafts.Should().HaveCount(3);

        var first = result.Drafts[0];
        first.Code.Should().Be("2403916");                          // SupplierPartID
        first.ExternalId.Should().Be("30670483");                   // unchanged by the MPN work
        first.ManufacturerPartNumber.Should().Be("REDACTED-ORDER-DATA");         // ManufacturerPartID
        first.ManufacturerName.Should().Be("REDACTED-ORDER-DATA");              // ManufacturerName

        result.Drafts[1].ManufacturerPartNumber.Should().Be("REDACTED-ORDER-DATA");
        result.Drafts[1].ManufacturerName.Should().Be("Deltaco");
        result.Drafts[2].ManufacturerPartNumber.Should().Be("REDACTED-ITEM");
        result.Drafts[2].ManufacturerName.Should().Be("Gembird");

        // The honesty report must list both columns, otherwise the mapping UI shows them as
        // unmapped source data the operator has to wire up by hand.
        result.HeaderColumns.Should().Contain("ManufacturerPartID").And.Contain("ManufacturerName");
    }

    [Fact]
    public async Task ParseCxmlIndex_SoapWrapped_CarriesManufacturerPartAndName()
    {
        var result = await SupplierCatalogFileParser.ParseCxmlIndexAsync(
            OpenFixture("cxml-index-soap.xml"), CancellationToken.None);

        result.Drafts.Should().HaveCount(2);
        result.Drafts[0].Code.Should().Be("34762852");
        result.Drafts[0].ManufacturerPartNumber.Should().Be("REDACTED-ORDER-DATA");
        result.Drafts[0].ManufacturerName.Should().Be("REDACTED-PARTY");
        result.Drafts[1].ManufacturerPartNumber.Should().Be("REDACTED-ORDER-DATA");
        result.Drafts[1].ManufacturerName.Should().Be("REDACTED-PARTY");
    }

    [Fact]
    public async Task ParseXml_RoutedToCxmlIndex_StillCarriesManufacturerFields()
    {
        // The auto-routing path (the one a live catalog pull actually takes) must not lose them.
        var result = await SupplierCatalogFileParser.ParseXmlAsync(
            OpenFixture("cxml-index-bare.xml"), CancellationToken.None);

        result.Format.Should().Be("cxml_index");
        result.Drafts[0].ManufacturerPartNumber.Should().Be("REDACTED-ORDER-DATA");
        result.Drafts[0].ManufacturerName.Should().Be("REDACTED-ORDER-DATA");
    }
}
