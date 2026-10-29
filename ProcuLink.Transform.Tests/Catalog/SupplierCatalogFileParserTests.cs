using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using FluentAssertions;
using ProcuLink.Core.Catalog;
using ProcuLink.Transform.Catalog;
using SharpCompress.Common;
using SharpCompress.Writers;

namespace ProcuLink.Transform.Tests.Catalog;

/// <summary>
/// Tests for the shared catalog parser extracted from <c>SuppliersController</c> (BE-1).
///
///  • Behaviour parity: alias mapping, delimiter detection, quote trimming, and EU
///    comma-decimal handling must match the original in-controller logic byte-for-byte
///    (the controller-level <c>SuppliersControllerCatalogTests</c> stay green unmodified
///    as the upload-path regression gate).
///  • New hardening (H4): the row cap on both formats, XLSX zip-bomb entry-size guard,
///    and forged-dimension rejection BEFORE the workbook is loaded.
/// </summary>
public class SupplierCatalogFileParserTests
{
    private static Stream Csv(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    private static MemoryStream BuildXlsx(string[] header, IEnumerable<string?[]> rows)
    {
        var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("Sheet1");
            for (var c = 0; c < header.Length; c++)
                ws.Cell(1, c + 1).Value = header[c];

            var r = 2;
            foreach (var row in rows)
            {
                for (var c = 0; c < row.Length; c++)
                    if (row[c] is not null)
                        ws.Cell(r, c + 1).Value = row[c];
                r++;
            }
            wb.SaveAs(ms);
        }
        ms.Position = 0;
        return ms;
    }

    // ── Alias mapping parity (CSV + XLSX) ─────────────────────────────────────

    [Fact]
    public async Task ParseCsv_MapsAliases_AndTrimsQuotes()
    {
        var csv = "sku,description,unit_price,gtin,erp_id\n" +
                  "\"RES-220\",\"Resistor 220 Ohm\",0.04,4001234000010,ERP-7\n";

        var result = await SupplierCatalogFileParser.ParseCsvAsync(Csv(csv), CancellationToken.None);

        result.Format.Should().Be("csv");
        result.HeaderColumns.Should().Equal("sku", "description", "unit_price", "gtin", "erp_id");
        var d = result.Drafts.Should().ContainSingle().Subject;
        d.Code.Should().Be("RES-220");
        d.Name.Should().Be("Resistor 220 Ohm");
        d.Price.Should().Be(0.04m);
        d.Barcode.Should().Be("4001234000010");
        d.ExternalId.Should().Be("ERP-7");
    }

    [Fact]
    public void ParseXlsx_MapsAliases_SameAsCsv()
    {
        using var xlsx = BuildXlsx(
            new[] { "sku", "description", "unit_price", "gtin" },
            new[] { new string?[] { "RES-220", "Resistor 220 Ohm", "0.04", "4001234000010" } });

        var result = SupplierCatalogFileParser.ParseXlsx(xlsx);

        result.Format.Should().Be("xlsx");
        var d = result.Drafts.Should().ContainSingle().Subject;
        d.Code.Should().Be("RES-220");
        d.Name.Should().Be("Resistor 220 Ohm");
        d.Price.Should().Be(0.04m);
        d.Barcode.Should().Be("4001234000010");
    }

    [Fact]
    public async Task ParseCsv_SemicolonDelimiter_IsDetected()
    {
        var csv = "code;name;price\nA-1;Widget;9.50\n";

        var result = await SupplierCatalogFileParser.ParseCsvAsync(Csv(csv), CancellationToken.None);

        var d = result.Drafts.Should().ContainSingle().Subject;
        d.Code.Should().Be("A-1");
        d.Price.Should().Be(9.50m);
    }

    [Fact]
    public async Task ParseCsv_NoCodeColumn_ReturnsEmpty_WithHeaders()
    {
        var csv = "name,price\nSomething,1.00\n";

        var result = await SupplierCatalogFileParser.ParseCsvAsync(Csv(csv), CancellationToken.None);

        result.Drafts.Should().BeEmpty();
        result.HeaderColumns.Should().Equal("name", "price");
    }

    // ── EU comma-decimal handling (locale-bug fix, plan 2026-07-02) ────────────
    // The price parse is now locale-tolerant (last of '.'/',' is the decimal separator).
    // This CORRECTS the earlier invariant-only behaviour that silently turned the EU
    // comma-decimal "9,99" into 999 — a real data-integrity bug for distributor feeds
    // (EU feeds ship prices like "674,68"). Full coverage in CatalogFormatAndMappingTests.

    [Theory]
    [InlineData("0.04", "0.04")]
    [InlineData("1,234.56", "1234.56")] // US thousands.decimal
    [InlineData("9,99", "9.99")]         // EU comma decimal — now correct (was 999)
    public async Task ParseCsv_PriceParsing_IsLocaleTolerant(string raw, string expected)
    {
        var csv = $"code;name;price\nA-1;Widget;{raw}\n"; // ';' delimiter so ',' stays inside the cell

        var result = await SupplierCatalogFileParser.ParseCsvAsync(Csv(csv), CancellationToken.None);

        var d = result.Drafts.Should().ContainSingle().Subject;
        d.Price.Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    // ── Extension routing parity ──────────────────────────────────────────────

    [Fact]
    public async Task ParseByFileName_UnknownExtension_FallsBackToCsv()
    {
        var csv = "code,name\nA-1,Widget\n";

        var result = await SupplierCatalogFileParser.ParseByFileNameAsync(Csv(csv), "catalog.txt", CancellationToken.None);

        result.Format.Should().Be("csv");
        result.Drafts.Should().ContainSingle().Which.Code.Should().Be("A-1");
    }

    [Fact]
    public async Task ParseByFileName_XlsxExtension_RoutesToXlsx()
    {
        using var xlsx = BuildXlsx(new[] { "code" }, new[] { new string?[] { "A-1" } });

        var result = await SupplierCatalogFileParser.ParseByFileNameAsync(xlsx, "catalog.xlsx", CancellationToken.None);

        result.Format.Should().Be("xlsx");
        result.Drafts.Should().ContainSingle().Which.Code.Should().Be("A-1");
    }

    // ── H4: row cap ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ParseCsv_RowCap_AbortsOverCap()
    {
        var sb = new StringBuilder("code\n");
        for (var i = 0; i < SupplierCatalogFileParser.MaxCatalogRows + 1; i++)
            sb.Append("C-").Append(i).Append('\n');

        var act = () => SupplierCatalogFileParser.ParseCsvAsync(Csv(sb.ToString()), CancellationToken.None);

        await act.Should().ThrowAsync<CatalogTooLargeException>()
            .WithMessage("*row limit*");
    }

    [Fact]
    public async Task ParseCsv_ExactlyAtRowCap_Parses()
    {
        // Boundary: cap rows exactly must NOT throw.
        var sb = new StringBuilder("code\n");
        for (var i = 0; i < SupplierCatalogFileParser.MaxCatalogRows; i++)
            sb.Append("C-").Append(i).Append('\n');

        var result = await SupplierCatalogFileParser.ParseCsvAsync(Csv(sb.ToString()), CancellationToken.None);

        result.Drafts.Should().HaveCount(SupplierCatalogFileParser.MaxCatalogRows);
    }

    // ── H4: forged XLSX dimension rejected BEFORE the workbook is loaded ───────

    [Fact]
    public void ParseXlsx_ForgedDimension_RejectedBeforeWorkbookLoad()
    {
        // A handcrafted zip whose worksheet part DECLARES ~1M rows. The guard must throw
        // from the zip pre-scan — XLWorkbook never sees the stream (the zip is not even a
        // complete/valid workbook, so reaching ClosedXML would throw a different error).
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("xl/worksheets/sheet1.xml");
            using var w = new StreamWriter(entry.Open());
            w.Write("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                    "<dimension ref=\"A1:G1048576\"/><sheetData/></worksheet>");
        }
        ms.Position = 0;

        var act = () => SupplierCatalogFileParser.ParseXlsx(ms);

        act.Should().Throw<CatalogTooLargeException>().WithMessage("*row limit*");
    }

    // ── H4: oversized declared zip entry rejected ─────────────────────────────

    [Fact]
    public void ParseXlsx_ZipEntryDeclaringOver64MB_Rejected()
    {
        // 65 MB of zeros compresses to ~64 KB but the entry DECLARES 65 MB uncompressed —
        // the guard reads the declared length and rejects before any inflation happens.
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.SmallestSize);
            using var s = entry.Open();
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < 65; i++) s.Write(zeros, 0, zeros.Length);
        }
        ms.Position = 0;

        var act = () => SupplierCatalogFileParser.ParseXlsx(ms);

        act.Should().Throw<CatalogTooLargeException>().WithMessage("*64 MB*");
    }

    [Fact]
    public void ParseXlsx_LegitimateSmallWorkbook_PassesGuards()
    {
        using var xlsx = BuildXlsx(
            new[] { "code", "name" },
            Enumerable.Range(1, 50).Select(i => new string?[] { $"C-{i}", $"Item {i}" }));

        var result = SupplierCatalogFileParser.ParseXlsx(xlsx);

        result.Drafts.Should().HaveCount(50);
    }

    // ── Unsupported zip compression (Deflate64) — SharpCompress repack fallback ─
    // The catalog zip-bomb pre-guard itself opens worksheet parts, so an exotic-compression
    // workbook fails there before XLWorkbook. The repack must run and the FULL guard must
    // re-apply on the repacked stream. BZip2 stands in for Deflate64 (identical BCL rejection;
    // SharpCompress can write BZip2 but not Deflate64). See XlsxOrderParserTests for detail.

    [Fact]
    public void ParseXlsx_WorkbookWithUnsupportedZipCompression_RepacksAndParses()
    {
        using var standard = BuildXlsx(
            new[] { "code", "name", "unit_price" },
            new[] { new string?[] { "RES-220", "Resistor 220 Ohm", "0.04" } });
        using var exotic = RepackWithBZip2(standard);

        var result = SupplierCatalogFileParser.ParseXlsx(exotic);

        result.Format.Should().Be("xlsx");
        var d = result.Drafts.Should().ContainSingle().Subject;
        d.Code.Should().Be("RES-220");
        d.Name.Should().Be("Resistor 220 Ohm");
        d.Price.Should().Be(0.04m);
    }

    /// <summary>
    /// Re-packs every part of <paramref name="standardXlsx"/> into a zip whose entries use BZip2 —
    /// a compression method the .NET BCL <c>ZipArchive</c> cannot read.
    /// </summary>
    private static MemoryStream RepackWithBZip2(Stream standardXlsx)
    {
        standardXlsx.Position = 0;
        var exotic = new MemoryStream();
        using (var src = new ZipArchive(standardXlsx, ZipArchiveMode.Read, leaveOpen: true))
        using (var writer = WriterFactory.OpenWriter(exotic, ArchiveType.Zip, new WriterOptions(CompressionType.BZip2) { LeaveStreamOpen = true }))
        {
            foreach (var entry in src.Entries)
            {
                using var es = entry.Open();
                using var buf = new MemoryStream();
                es.CopyTo(buf);
                buf.Position = 0;
                writer.Write(entry.FullName, buf, entry.LastWriteTime.DateTime);
            }
        }
        exotic.Position = 0;
        return exotic;
    }

    // ── Mapping-report helpers (consumed by test-fetch) ───────────────────────

    [Fact]
    public void MapHeaderColumns_FirstAliasWins_UnknownColumnsUnmapped()
    {
        var header = new[] { "sku", "item_code", "description", "weird_column" };

        var map = SupplierCatalogFileParser.MapHeaderColumns(header);

        map.Should().Contain(new KeyValuePair<int, string>(0, "code"));   // sku wins
        map.Should().NotContainKey(1);                                    // item_code loses (code taken)
        map.Should().Contain(new KeyValuePair<int, string>(2, "name"));
        map.Should().NotContainKey(3);                                    // unknown column unmapped
    }

    // ── JSON catalog parser (http API pull, B4) ───────────────────────────────

    private static Stream Json(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    [Fact]
    public async Task ParseJson_TopLevelArray_MapsAliases_NumberAndStringPrices()
    {
        var json = """
        [
          { "sku": "RES-220", "description": "Resistor 220 Ohm", "unit_price": 0.04, "gtin": "4001234000010", "erp_id": "ERP-7" },
          { "sku": "CAP-100", "name": "Capacitor", "price": "1.25", "currency": "EUR", "uom": "pcs" }
        ]
        """;

        var result = await SupplierCatalogFileParser.ParseJsonAsync(Json(json), CancellationToken.None);

        result.Format.Should().Be("json");
        result.Drafts.Should().HaveCount(2);

        var a = result.Drafts[0];
        a.Code.Should().Be("RES-220");
        a.Name.Should().Be("Resistor 220 Ohm");
        a.Price.Should().Be(0.04m);          // JSON number flattened to invariant text then parsed
        a.Barcode.Should().Be("4001234000010");
        a.ExternalId.Should().Be("ERP-7");

        var b = result.Drafts[1];
        b.Code.Should().Be("CAP-100");
        b.Price.Should().Be(1.25m);          // string price
        b.Currency.Should().Be("EUR");
        b.Unit.Should().Be("pcs");
    }

    [Fact]
    public async Task ParseJson_ObjectWrappedArray_IsUnwrapped()
    {
        // Common catalog-API envelope: { "products": [ ... ] }
        var json = """{ "products": [ { "code": "A-1", "name": "Widget" } ], "total": 1 }""";

        var result = await SupplierCatalogFileParser.ParseJsonAsync(Json(json), CancellationToken.None);

        result.Drafts.Should().ContainSingle().Which.Code.Should().Be("A-1");
    }

    [Fact]
    public async Task ParseJson_HeaderColumns_AreUnionOfPropertyNames_FirstSeenOrder()
    {
        var json = """
        [
          { "sku": "A-1", "description": "Widget", "warehouse_zone": "Z1" },
          { "sku": "B-2", "currency": "EUR" }
        ]
        """;

        var result = await SupplierCatalogFileParser.ParseJsonAsync(Json(json), CancellationToken.None);

        // The header report drives the test-fetch mapped/unmapped honesty view.
        result.HeaderColumns.Should().Equal("sku", "description", "warehouse_zone", "currency");
        var colMap = SupplierCatalogFileParser.MapHeaderColumns(result.HeaderColumns.ToList());
        colMap.Values.Should().Contain("code");
        colMap.Values.Should().Contain("name");
        result.HeaderColumns.Should().Contain("warehouse_zone"); // unmapped, still reported
    }

    [Fact]
    public async Task ParseJson_NoCodeProperty_ReturnsEmpty()
    {
        var json = """[ { "name": "Widget", "price": 1.00 } ]""";

        var result = await SupplierCatalogFileParser.ParseJsonAsync(Json(json), CancellationToken.None);

        result.Drafts.Should().BeEmpty("no row carries a product code");
    }

    [Fact]
    public async Task ParseJson_NonArrayRoot_ReturnsEmpty()
    {
        var json = """{ "message": "no catalog here" }""";

        var result = await SupplierCatalogFileParser.ParseJsonAsync(Json(json), CancellationToken.None);

        result.Format.Should().Be("json");
        result.Drafts.Should().BeEmpty();
        result.HeaderColumns.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseJson_MalformedJson_ThrowsInvalidData()
    {
        var act = () => SupplierCatalogFileParser.ParseJsonAsync(Json("{ not json"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ParseJson_RowCap_AbortsOverLimit()
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < SupplierCatalogFileParser.MaxCatalogRows + 1; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"code\":\"C-").Append(i).Append("\"}");
        }
        sb.Append(']');

        var act = () => SupplierCatalogFileParser.ParseJsonAsync(Json(sb.ToString()), CancellationToken.None);

        await act.Should().ThrowAsync<CatalogTooLargeException>().WithMessage("*row limit*");
    }

    [Fact]
    public async Task ParseByFileName_JsonExtension_RoutesToJson()
    {
        var json = """[ { "code": "A-1", "name": "Widget" } ]""";

        var result = await SupplierCatalogFileParser.ParseByFileNameAsync(Json(json), "catalog.json", CancellationToken.None);

        result.Format.Should().Be("json");
        result.Drafts.Should().ContainSingle().Which.Code.Should().Be("A-1");
    }

    [Fact]
    public async Task ParseByContentType_JsonContentType_RoutesToJson_EvenWithoutExtension()
    {
        var json = """[ { "code": "A-1" } ]""";

        var result = await SupplierCatalogFileParser.ParseByContentTypeAsync(
            Json(json), "application/json; charset=utf-8", fileName: null, CancellationToken.None);

        result.Format.Should().Be("json");
        result.Drafts.Should().ContainSingle().Which.Code.Should().Be("A-1");
    }

    [Fact]
    public async Task ParseByContentType_CsvContentType_RoutesToCsv()
    {
        var csv = "code,name\nA-1,Widget\n";

        var result = await SupplierCatalogFileParser.ParseByContentTypeAsync(
            Csv(csv), "text/csv", fileName: null, CancellationToken.None);

        result.Format.Should().Be("csv");
        result.Drafts.Should().ContainSingle().Which.Code.Should().Be("A-1");
    }
    // ── Present fields: which catalog columns the file actually carried ──────
    //
    // A re-import must only overwrite the columns the file spoke about. Every parser therefore
    // reports the set of fields it found a column for; a blank cell inside a present column is
    // still "present" (that is how a supplier removes a value), an absent column is not.

    private static Stream Utf8(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    [Fact]
    public async Task ParseCsv_PresentFields_AreExactlyTheMappedColumns()
    {
        var result = await SupplierCatalogFileParser.ParseCsvAsync(
            Csv("code,price,internal_notes\nRES-220,0.05,ignore me\n"), CancellationToken.None);

        result.PresentFields.Should().Be(CatalogFields.Price,
            "a price-update file says nothing about names, units or barcodes, and an unmapped "
            + "column is not a catalog field");
    }

    [Fact]
    public async Task ParseCsv_PresentFields_IncludeAColumnWhoseCellIsBlank()
    {
        var result = await SupplierCatalogFileParser.ParseCsvAsync(
            Csv("sku,description,uom,unit_price,gtin\nRES-220,Resistor,PCS,0.04,\n"), CancellationToken.None);

        result.PresentFields.Should().Be(
            CatalogFields.Name | CatalogFields.Unit | CatalogFields.Price | CatalogFields.Barcode);
        result.Drafts.Should().ContainSingle().Which.Barcode.Should().BeNull("the cell itself is blank");
    }

    [Fact]
    public async Task ParseCsv_Headerless_PresentFields_AreThePositionalMappings()
    {
        var overrides = new Dictionary<string, string>
        {
            ["__noheader__"] = "true",
            ["0"] = "code",
            ["2"] = "price",
        };

        var result = await SupplierCatalogFileParser.ParseCsvAsync(
            Csv("RES-220,not mapped,0.05\n"), CancellationToken.None, overrides, encoding: null);

        result.PresentFields.Should().Be(CatalogFields.Price);
        result.Drafts.Should().ContainSingle().Which.Price.Should().Be(0.05m);
    }

    [Fact]
    public void ParseXlsx_PresentFields_AreExactlyTheMappedColumns()
    {
        using var xlsx = BuildXlsx(
            new[] { "code", "price", "internal_notes" },
            new[] { new string?[] { "RES-220", "0.05", "ignore me" } });

        var result = SupplierCatalogFileParser.ParseXlsx(xlsx);

        result.PresentFields.Should().Be(CatalogFields.Price);
    }

    [Fact]
    public async Task ParseJson_PresentFields_AreTheUnionOfMappedKeysAcrossAllObjects()
    {
        // No object has a name key → name absent. One object states "barcode": null → a stated
        // blank, so barcode counts as present for the whole file.
        var json = """[ { "code": "A-1", "price": 1.5 }, { "code": "A-2", "barcode": null } ]""";

        var result = await SupplierCatalogFileParser.ParseJsonAsync(Json(json), CancellationToken.None);

        result.PresentFields.Should().Be(CatalogFields.Price | CatalogFields.Barcode);
    }

    [Fact]
    public async Task ParseGenericXml_PresentFields_CountAnEmptyElement_ButNotAMissingOne()
    {
        // <Barcode/> is XML's blank cell — present. No row has a Name element — absent.
        var xml = "<Products>"
                + "<Product><Code>A-1</Code><Price>1.50</Price><Barcode/></Product>"
                + "<Product><Code>A-2</Code><Price>2.00</Price><Barcode/></Product>"
                + "</Products>";

        var result = await SupplierCatalogFileParser.ParseXmlAsync(Utf8(xml), CancellationToken.None);

        result.Format.Should().Be("xml");
        result.PresentFields.Should().Be(CatalogFields.Price | CatalogFields.Barcode);
        result.Drafts.Should().HaveCount(2).And.OnlyContain(d => d.Barcode == null && d.Name == null);
    }

    [Fact]
    public async Task ParseGenericXml_EmptyElement_DoesNotDisplaceALaterValueForTheSameField()
    {
        // Presence tracking must not change a draft value: an empty <EAN/> ahead of a populated
        // <Barcode> (both alias to barcode) still yields the populated value, as it always did.
        var xml = "<Products>"
                + "<Product><Code>A-1</Code><EAN/><Barcode>4001234000010</Barcode></Product>"
                + "<Product><Code>A-2</Code><EAN/><Barcode>4001234000027</Barcode></Product>"
                + "</Products>";

        var result = await SupplierCatalogFileParser.ParseXmlAsync(Utf8(xml), CancellationToken.None);

        result.Drafts.Select(d => d.Barcode).Should().Equal("4001234000010", "4001234000027");
        result.PresentFields.Should().Be(CatalogFields.Barcode);
    }

    [Fact]
    public async Task ParseGenericXml_AttributeStyleRows_PresentFields_AreTheMappedAttributes()
    {
        var xml = "<Result><StoItem Code=\"A-1\" Price=\"1.50\"/><StoItem Code=\"A-2\" Price=\"2.00\"/></Result>";

        var result = await SupplierCatalogFileParser.ParseXmlAsync(Utf8(xml), CancellationToken.None);

        result.Drafts.Should().HaveCount(2);
        result.PresentFields.Should().Be(CatalogFields.Price);
    }

    [Fact]
    public async Task ParseCxmlIndex_PresentFields_AreTheElementsTheItemsCarried()
    {
        // A cXML Index has no barcode element at all, so re-pulling one must never wipe barcodes
        // that a CSV import supplied.
        var xml = "<Index><IndexItem><IndexItemAdd>"
                + "<ItemID><SupplierPartID>A-1</SupplierPartID></ItemID>"
                + "<ItemDetail><UnitPrice><Money currency=\"EUR\">7.01</Money></UnitPrice>"
                + "<Description>Widget</Description><UnitOfMeasure>EA</UnitOfMeasure></ItemDetail>"
                + "</IndexItemAdd></IndexItem></Index>";

        var result = await SupplierCatalogFileParser.ParseXmlAsync(Utf8(xml), CancellationToken.None);

        result.Format.Should().Be("cxml_index");
        result.PresentFields.Should().Be(
            CatalogFields.Name | CatalogFields.Unit | CatalogFields.Price | CatalogFields.Currency);
    }

    [Fact]
    public async Task ParseCif_PresentFields_IncludeTheHeaderBlockCurrency()
    {
        var cif = "CIF_I_V3.0\nCURRENCY: EUR\nFIELDNAMES: Supplier Part ID,Unit Price\nDATA\nA-1,7.01\nENDOFDATA\n";

        var result = await SupplierCatalogFileParser.ParseCifAsync(Utf8(cif), CancellationToken.None);

        result.Format.Should().Be("cif");
        result.PresentFields.Should().Be(CatalogFields.Price | CatalogFields.Currency,
            "CURRENCY: supplies every row's currency, so the file does speak about it");
    }
}
