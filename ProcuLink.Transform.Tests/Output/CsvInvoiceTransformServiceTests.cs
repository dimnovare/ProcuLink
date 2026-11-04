using System.Text;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Output;

public class CsvInvoiceTransformServiceTests
{
    private static InvoiceEntity MakeInvoice() => new()
    {
        Id            = Guid.NewGuid(),
        InvoiceNumber = "INV-001",
        IssueDate     = new DateOnly(2026, 5, 28),
        Currency      = "EUR",
        SubTotal      = 100m,
        TaxTotal      = 20m,
        GrandTotal    = 120m,
        Status        = "approved",
    };

    private static InvoiceLineEntity MakeLine(int num) => new()
    {
        Id          = Guid.NewGuid(),
        LineNumber  = num,
        Description = "Widget",
        Quantity    = 10m,
        UnitCode    = "EA",
        UnitPrice   = 10m,
        TaxRate     = 0.20m,
        LineTotal   = 100m,
    };

    [Fact]
    public async Task TransformAsync_ContainsInvoiceNumber()
    {
        var svc   = new CsvInvoiceTransformService();
        var bytes = await svc.TransformAsync(MakeInvoice(), new[] { MakeLine(1) }, default);
        var csv   = Encoding.UTF8.GetString(bytes);
        csv.Should().Contain("INV-001");
    }

    [Fact]
    public async Task TransformAsync_ContainsLineRow()
    {
        var svc   = new CsvInvoiceTransformService();
        var bytes = await svc.TransformAsync(MakeInvoice(), new[] { MakeLine(1) }, default);
        var csv   = Encoding.UTF8.GetString(bytes);
        csv.Should().Contain("Widget");
        csv.Should().Contain("10");
    }

    // CSV injection: a text cell is neutralised, but a credit note's negative numbers stay numeric
    // (they never pass through CsvTransformService.Cell).
    [Fact]
    public async Task TransformAsync_FormulaInText_IsNeutralised_NegativeNumbersStayNumeric()
    {
        var line = MakeLine(1);
        line.Description = "=HYPERLINK(\"x\")";
        line.Quantity    = -5m;
        line.LineTotal   = -50m;

        var csv = Encoding.UTF8.GetString(await new CsvInvoiceTransformService().TransformAsync(MakeInvoice(), new[] { line }, default));

        csv.Should().Contain("1,,,\"'=HYPERLINK(\"\"x\"\")\",-5,EA,10,0.20,-50");
    }

    [Fact]
    public void Format_IsCsv()
        => new CsvInvoiceTransformService().Format.Should().Be("csv");
}
