using System.Text;
using ClosedXML.Excel;
using FluentAssertions;
using ProcuLink.Transform.Tokenizing;

namespace ProcuLink.Transform.Tests.SourceMap;

/// <summary>
/// Tests for <see cref="SourceTokenizer"/> XLSX tokenisation.
///
/// XLSX fixtures are built in-memory via ClosedXML so the test project requires
/// no binary fixture files and the test data is transparent.
/// </summary>
public class SourceTokenizerXlsxTests
{
    private static readonly SourceTokenizer Tokenizer = new();

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal XLSX workbook in memory from a 2-D string array.
    /// Row 0 = header row, rows 1+ = data rows.
    /// Null cells are left empty (not written) to exercise the "skip empty" path.
    /// </summary>
    private static byte[] BuildXlsx(string?[,] cells)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sheet1");

        for (int r = 0; r < cells.GetLength(0); r++)
        for (int c = 0; c < cells.GetLength(1); c++)
        {
            var v = cells[r, c];
            if (v is not null)
                ws.Cell(r + 1, c + 1).Value = v; // ClosedXML is 1-based
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // ── Basic structure ────────────────────────────────────────────────────────

    [Fact]
    public async Task Xlsx_HeaderRow_EmittedWithHeaderGroup()
    {
        var bytes = BuildXlsx(new string?[,]
        {
            { "PoNumber", "BuyerName", "Currency" },
            { "PO-001",   "Acme Ltd",  "EUR"      }
        });

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".xlsx");

        var headerTokens = tokens.Where(t => t.Group == "header").ToList();
        headerTokens.Should().HaveCount(3);
        headerTokens.Should().Contain(t => t.Id == "cell:r1c1" && t.Value == "PoNumber");
        headerTokens.Should().Contain(t => t.Id == "cell:r1c2" && t.Value == "BuyerName");
        headerTokens.Should().Contain(t => t.Id == "cell:r1c3" && t.Value == "Currency");
    }

    [Fact]
    public async Task Xlsx_DataRow_EmittedWithLineGroup()
    {
        var bytes = BuildXlsx(new string?[,]
        {
            { "PoNumber", "BuyerName" },
            { "PO-001",   "Acme Ltd"  }
        });

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".xlsx");

        var lineTokens = tokens.Where(t => t.Group == "line").ToList();
        lineTokens.Should().HaveCount(2);
        lineTokens.Should().Contain(t => t.Id == "cell:r2c1" && t.Value == "PO-001");
        lineTokens.Should().Contain(t => t.Id == "cell:r2c2" && t.Value == "Acme Ltd");
    }

    [Fact]
    public async Task Xlsx_MultipleDataRows_AllNonEmptyCellsEmitted()
    {
        var bytes = BuildXlsx(new string?[,]
        {
            { "LineNumber", "BuyerItemCode", "Quantity", "UnitPrice" },
            { "1",          "WIDGET-A",      "10",       "5.50"      },
            { "2",          "GADGET-B",      "5",        "12.00"     },
            { "3",          "DOOHICKEY-C",   "1",        "100.00"    }
        });

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".xlsx");

        // 4 header tokens + 12 data tokens (3 rows × 4 cols)
        tokens.Should().HaveCount(16);
        tokens.Should().Contain(t => t.Id == "cell:r4c1" && t.Value == "3");
        tokens.Should().Contain(t => t.Id == "cell:r4c4" && t.Value == "100.00");
    }

    [Fact]
    public async Task Xlsx_TokenIds_MatchExcelRowColumnNumbers()
    {
        // Confirm 1-based row+col numbering matches Excel convention.
        var bytes = BuildXlsx(new string?[,]
        {
            { "A", "B" },
            { "1", "2" }
        });

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".xlsx");

        tokens.Should().Contain(t => t.Id == "cell:r1c1");
        tokens.Should().Contain(t => t.Id == "cell:r1c2");
        tokens.Should().Contain(t => t.Id == "cell:r2c1");
        tokens.Should().Contain(t => t.Id == "cell:r2c2");
        // No r0 or c0 tokens should exist.
        tokens.Should().NotContain(t => t.Id.Contains("r0") || t.Id.Contains("c0"));
    }

    [Fact]
    public async Task Xlsx_DataLabel_IncludesColumnHeaderName()
    {
        var bytes = BuildXlsx(new string?[,]
        {
            { "PoNumber", "Currency" },
            { "PO-42",    "USD"      }
        });

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".xlsx");

        var poCell = tokens.First(t => t.Id == "cell:r2c1");
        poCell.Label.Should().Contain("PoNumber");

        var currencyCell = tokens.First(t => t.Id == "cell:r2c2");
        currencyCell.Label.Should().Contain("Currency");
    }

    [Fact]
    public async Task Xlsx_EmptyCells_NotEmitted()
    {
        // Row 2 has a gap at col 2 — that cell should not appear in tokens.
        var bytes = BuildXlsx(new string?[,]
        {
            { "A",   "B",  "C"   },
            { "val", null, "val2" }
        });

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".xlsx");

        // cell:r2c2 is empty and must not be emitted.
        tokens.Should().NotContain(t => t.Id == "cell:r2c2");
        // The two non-empty data cells must be present.
        tokens.Should().Contain(t => t.Id == "cell:r2c1");
        tokens.Should().Contain(t => t.Id == "cell:r2c3");
    }

    [Fact]
    public async Task Xlsx_EmptyWorkbook_ReturnsEmptyList()
    {
        // A workbook with no data at all should return empty, not throw.
        using var wb = new XLWorkbook();
        wb.AddWorksheet("Empty");
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var bytes = ms.ToArray();

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".xlsx");

        tokens.Should().BeEmpty();
    }

    [Fact]
    public async Task Xlsx_GarbageBytes_ReturnsEmptyList()
    {
        // Non-XLSX content should return empty, not throw.
        var bytes = Encoding.UTF8.GetBytes("this is not an xlsx file");

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".xlsx");

        tokens.Should().BeEmpty();
    }

    [Fact]
    public async Task Xlsx_HeaderGroupTokens_HaveLabelPrefixedWithHeader()
    {
        var bytes = BuildXlsx(new string?[,]
        {
            { "Description" },
            { "Widget A"    }
        });

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".xlsx");

        var headerToken = tokens.First(t => t.Group == "header");
        headerToken.Label.Should().StartWith("Header:");
    }
}
