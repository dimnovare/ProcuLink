using FluentAssertions;
using ProcuLink.Core.Documents;
using ProcuLink.Transform.Documents;

namespace ProcuLink.Transform.Tests.Documents;

/// <summary>
/// One synthetic purchase order read with every value a strict standard receiver wants, shared by the plan-22
/// writer tests so none of them retypes a value.
/// </summary>
internal static class StrictOrderFixture
{
    public const string PoNumber = "PO-2026-0042";
    public const string OrderDate = "18.11.2026";
    public const string DeliveryDate = "15.12.2026";
    public const string Currency = "EUR";
    public const string PaymentTerms = "30 days net";
    public const string BuyerName = "Example Buyer Ltd";
    public const string BuyerStreet = "Example Street 1";
    public const string BuyerPostcode = "10115";
    public const string BuyerCity = "Berlin";
    public const string BuyerCountry = "Germany";
    public const string BuyerVat = "DE000000000";
    public const string BuyerEmail = "orders@example.com";
    public const string SupplierName = "Example Supplier OU";
    public const string SupplierStreet = "Example Road 2";
    public const string SupplierPostcode = "10111";
    public const string SupplierCity = "Tallinn";
    public const string SupplierCountry = "Estonia";
    public const string SupplierVat = "EE000000000";
    public const string ShipToName = "Example Buyer Ltd Warehouse";
    public const string ShipToStreet = "Warehouse Road 3";
    public const string ShipToPostcode = "20095";
    public const string ShipToCity = "Hamburg";
    public const string ShipToCountry = "Germany";
    public const string TotalNet = "60.00";
    public const string TotalTax = "11.40";
    public const string TotalGross = "71.40";

    private static readonly (string Label, string Value, string? Meaning)[] Header =
    [
        ("PO number", PoNumber, FieldMeanings.DocumentNumber), ("Order date", OrderDate, FieldMeanings.IssueDate),
        ("Delivery date", DeliveryDate, FieldMeanings.DeliveryDate), ("Currency", Currency, FieldMeanings.Currency),
        ("Payment terms", PaymentTerms, null),
        ("Buyer", BuyerName, FieldMeanings.SenderName), ("Buyer street", BuyerStreet, FieldMeanings.SenderStreet),
        ("Buyer postcode", BuyerPostcode, FieldMeanings.SenderPostcode), ("Buyer city", BuyerCity, FieldMeanings.SenderCity),
        ("Buyer country", BuyerCountry, FieldMeanings.SenderCountry), ("Buyer VAT", BuyerVat, FieldMeanings.SenderVatId),
        ("Buyer email", BuyerEmail, FieldMeanings.SenderContactEmail),
        ("Supplier", SupplierName, FieldMeanings.ReceiverName), ("Supplier street", SupplierStreet, FieldMeanings.ReceiverStreet),
        ("Supplier postcode", SupplierPostcode, FieldMeanings.ReceiverPostcode), ("Supplier city", SupplierCity, FieldMeanings.ReceiverCity),
        ("Supplier country", SupplierCountry, FieldMeanings.ReceiverCountry), ("Supplier VAT", SupplierVat, FieldMeanings.ReceiverVatId),
        ("Ship to", ShipToName, FieldMeanings.DeliveryName), ("Ship to street", ShipToStreet, FieldMeanings.DeliveryStreet),
        ("Ship to postcode", ShipToPostcode, FieldMeanings.DeliveryPostcode), ("Ship to city", ShipToCity, FieldMeanings.DeliveryCity),
        ("Ship to country", ShipToCountry, FieldMeanings.DeliveryCountry),
        ("Net", TotalNet, FieldMeanings.TotalNet), ("VAT 19%", TotalTax, FieldMeanings.TotalTax), ("Total", TotalGross, FieldMeanings.TotalGross),
    ];

    private static readonly (string Label, string Meaning)[] Columns =
    [
        ("Item code", FieldMeanings.LineItemCode), ("Description", FieldMeanings.LineDescription), ("Qty", FieldMeanings.LineQuantity),
        ("Unit", FieldMeanings.LineUnit), ("Unit price", FieldMeanings.LineUnitPrice), ("Amount", FieldMeanings.LineAmount),
    ];

    private static readonly string[][] Lines =
    [
        ["A-100", "Hex bolt M8", "100", "pcs", "0.25", "25.00"],
        ["B-200", "Washer M8", "200", "pcs", "0.05", "10.00"],
        ["C-300", "Compression spring", "20", "pcs", "1.25", "25.00"],
    ];

    /// <summary>The order read; header fields whose meaning is in <paramref name="without"/> are left out.</summary>
    public static ReadResult Read(params string[] without)
    {
        ReadRow Row(string[] values) => new(Columns.Select((c, i) => (c, i))
            .ToDictionary(x => FieldKey.Normalise(x.c.Label), x => new ReadCell(values[x.i], 1, null, false, false)));
        return new ReadResult("Purchase order",
            [.. Header.Where(h => h.Meaning is null || !without.Contains(h.Meaning))
                .Select(h => new ReadField(FieldKey.Normalise(h.Label), h.Label, h.Value, "text", h.Meaning, 1, null, false, true, false))],
            [new ReadTable("lines", "Lines", [.. Columns.Select(c => new ReadColumn(FieldKey.Normalise(c.Label), c.Label, "text", c.Meaning))],
                [.. Lines.Select(Row)], true)]);
    }

    /// <summary>
    /// <paramref name="read"/> with one meaning's value replaced, or added keyed by the meaning. A <c>line_</c> meaning
    /// takes one value per row (a single value fills every row).
    /// </summary>
    public static ReadResult With(ReadResult read, string meaning, params string[] values)
    {
        if (!meaning.StartsWith("line_"))
            return new(read.KindLabel, read.Fields.Any(f => f.Meaning == meaning)
                ? [.. read.Fields.Select(f => f.Meaning == meaning ? f with { Value = values[0] } : f)]
                : [.. read.Fields, new ReadField(meaning, meaning, values[0], "text", meaning, 1, null, false, true, false)], read.Tables, read.Truncated);
        var table = read.Tables[0];
        var key = table.Columns.FirstOrDefault(c => c.Meaning == meaning)?.Key ?? meaning;
        var lines = table with
        {
            Columns = table.Columns.Any(c => c.Key == key) ? table.Columns : [.. table.Columns, new ReadColumn(key, meaning, "text", meaning)],
            Rows = [.. table.Rows.Select((r, i) => new ReadRow(new Dictionary<string, ReadCell>(r.Cells) { [key] = new(values[values.Length == 1 ? 0 : i], 1, null, false, false) }))],
        };
        return new(read.KindLabel, read.Fields, [lines, .. read.Tables.Skip(1)], read.Truncated);
    }

    public static ReadResult With(string meaning, params string[] values) => With(Read(), meaning, values);

    /// <summary>The standard's file for <paramref name="read"/> (default: the full fixture); asserts it was written.</summary>
    public static async Task<string> Render(string standardId, ReadResult? read = null)
    {
        var render = await StandardOutputs.RenderAsync(standardId, read ?? Read(), FlowFieldList.Empty, "lines", Guid.Empty, CancellationToken.None);
        render.Ok.Should().BeTrue(render.Detail);
        return await new StreamReader(render.Result!.Content).ReadToEndAsync();
    }
}
