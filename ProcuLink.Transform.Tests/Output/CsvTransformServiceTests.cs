using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Output;

public class CsvTransformServiceTests
{
    // The full fixed CSV header AFTER the additive address/contact enrichment (2026-06).
    // The first 10 columns are the pre-existing fixed shape; the last 13 are the appended
    // ship-to / bill-to / contact columns. This is a DELIBERATE additive schema change for
    // ALL orders (founder-approved "all canonicals"; no live CSV suppliers) — not byte-identical.
    private const string ExpectedHeader =
        "PoNumber,OrderDate,Currency,BuyerName,SupplierItemCode,Description,Quantity,Unit,UnitPrice,LineTotal," +
        "ShipToName,ShipToStreet,ShipToCity,ShipToPostalCode,ShipToCountry," +
        "BillToName,BillToStreet,BillToCity,BillToPostalCode,BillToCountry," +
        "ContactName,ContactEmail,ContactPhone";

    private static PurchaseOrderEntity BuildOrder(IEnumerable<PurchaseOrderLineEntity>? lines = null)
    {
        var order = new PurchaseOrderEntity
        {
            Id         = Guid.NewGuid(),
            OrgId      = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            SupplierId = Guid.Parse("00000000-0000-0000-0000-000000000002"),
            PoNumber   = "PO-CSV-001",
            BuyerName  = "Acme Buyer Ltd",
            OrderDate  = new DateOnly(2026, 5, 28),
            Currency   = "EUR",
            Status     = "ready",
        };

        order.Lines = (lines ?? new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, BuyerItemCode = "BUYER-001", SupplierItemCode = "SUP-ABC-001",
                Description = "Widget Type A", Quantity = 10m, Unit = "EA", UnitPrice = 125.00m,
                NeedsReview = false, Confidence = 1.0f,
            }
        }).ToList();

        return order;
    }

    private static async Task<string[]> Rows(TransformResult result)
    {
        result.Content.Position = 0;
        using var reader = new StreamReader(result.Content);
        var text = await reader.ReadToEndAsync();
        return text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void CanTransform_ReturnsTrueForCsvOnly()
    {
        var svc = new CsvTransformService();
        svc.CanTransform(OutputFormat.Csv).Should().BeTrue();
        svc.CanTransform(OutputFormat.Json).Should().BeFalse();
    }

    [Fact]
    public async Task TransformAsync_Header_CarriesAddressColumns()
    {
        var svc  = new CsvTransformService();
        var rows = await Rows(await svc.TransformAsync(BuildOrder(), OutputFormat.Csv, CancellationToken.None));

        rows[0].Should().Be(ExpectedHeader);
    }

    [Fact]
    public async Task TransformAsync_NoAddressData_EmitsEmptyAddressColumns()
    {
        // An order with no address/contact data still carries the columns (deliberate schema change),
        // but every appended cell is empty — the header fields and line fields are unchanged.
        var svc  = new CsvTransformService();
        var rows = await Rows(await svc.TransformAsync(BuildOrder(), OutputFormat.Csv, CancellationToken.None));

        rows.Should().HaveCount(2); // header + 1 line
        // 23 columns; the last 13 (address/contact) are all empty → row ends with 13 trailing commas.
        rows[1].Should().StartWith("PO-CSV-001,2026-05-28,EUR,Acme Buyer Ltd,SUP-ABC-001,");
        rows[1].Should().EndWith(",,,,,,,,,,,,,"); // 13 empty trailing columns
        rows[1].Split(',').Should().HaveCount(23);
    }

    [Fact]
    public async Task TransformAsync_WithAddresses_PopulatesAddressColumns()
    {
        var order = BuildOrder();
        order.ShipToName       = "REDACTED-PARTY";
        order.ShipToStreet     = "REDACTED-ADDRESS";
        order.ShipToCity       = "REDACTED-ADDRESS";
        order.ShipToPostalCode = "63040";
        order.ShipToCountry    = "FRANCE";
        order.BillToName       = "REDACTED-PARTY";
        order.BillToStreet     = "REDACTED-ADDRESS";
        order.BillToCity       = "REDACTED-ADDRESS";
        order.BillToPostalCode = "63000";
        order.BillToCountry    = "FRANCE";
        order.ContactName      = "REDACTED-NAME";
        order.ContactEmail     = "redacted@example.invalid";
        order.ContactPhone     = "REDACTED-PHONE";

        var svc  = new CsvTransformService();
        var rows = await Rows(await svc.TransformAsync(order, OutputFormat.Csv, CancellationToken.None));

        var cells = rows[1].Split(',');
        cells.Should().HaveCount(23);
        // ShipTo* = columns 10..14
        cells[10].Should().Be("REDACTED-PARTY");
        cells[11].Should().Be("REDACTED-ADDRESS");
        cells[12].Should().Be("REDACTED-ADDRESS");
        cells[13].Should().Be("63040");
        cells[14].Should().Be("FRANCE");
        // BillTo* = columns 15..19
        cells[15].Should().Be("REDACTED-PARTY");
        cells[19].Should().Be("FRANCE");
        // Contact* = columns 20..22
        cells[20].Should().Be("REDACTED-NAME");
        cells[21].Should().Be("redacted@example.invalid");
        cells[22].Should().Be("REDACTED-PHONE");
    }

    [Fact]
    public async Task TransformAsync_AddressFieldWithComma_IsRfc4180Escaped()
    {
        var order = BuildOrder();
        order.ShipToStreet = "REDACTED-ADDRESS";

        var svc  = new CsvTransformService();
        var rows = await Rows(await svc.TransformAsync(order, OutputFormat.Csv, CancellationToken.None));

        rows[1].Should().Contain("\"REDACTED-ADDRESS\"");
    }

    [Fact]
    public async Task TransformAsync_LineNeedsReview_ThrowsTransformValidationException()
    {
        var lines = new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, SupplierItemCode = null, Quantity = 1m, UnitPrice = 10m,
                NeedsReview = true, Confidence = 0.5f,
            }
        };

        var svc = new CsvTransformService();
        var act = async () => await svc.TransformAsync(BuildOrder(lines: lines), OutputFormat.Csv, CancellationToken.None);
        await act.Should().ThrowAsync<TransformValidationException>();
    }
}
