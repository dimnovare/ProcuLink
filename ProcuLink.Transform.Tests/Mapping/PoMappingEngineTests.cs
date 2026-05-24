using FluentAssertions;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Mapping;

namespace ProcuLink.Transform.Tests.Mapping;

public class PoMappingEngineTests
{
    private static PoMappingConfig SimpleConfig() => new()
    {
        HasHeaderRecord = true,
        Separator = ",",
        Header = new Dictionary<string, FieldMappingEntry>
        {
            ["PoNumber"]  = new() { ExternalField = "PO_NUMBER" },
            ["OrderDate"] = new() { ExternalField = "ORDER_DATE", FieldManipulators = new()
            {
                new() { Type = "DateFormat", Params = new() { "dd/MM/yyyy", "yyyy-MM-dd" } }
            }},
            ["BuyerName"] = new() { FixedValue = "Nordic Distribution" },
            ["Currency"]  = new() { ExternalField = "CURR" },
        },
        Lines = new Dictionary<string, FieldMappingEntry>
        {
            ["LineNumber"]    = new() { ExternalField = "LINE" },
            ["BuyerItemCode"] = new() { ExternalField = "ITEM" },
            ["Description"]   = new() { ExternalField = "DESC" },
            ["Quantity"]      = new() { ExternalField = "QTY" },
            ["Unit"]          = new() { ExternalField = "UNIT" },
            ["UnitPrice"]     = new() { ExternalField = "PRICE" },
        }
    };

    [Fact]
    public void Apply_MapsHeaderFieldsFromFirstRow()
    {
        var headerRow = new Dictionary<string, string>
        {
            ["PO_NUMBER"]  = "PO-001",
            ["ORDER_DATE"] = "24/05/2026",
            ["CURR"]       = "EUR",
        };
        var lineRows = new List<IReadOnlyDictionary<string, string>>
        {
            new Dictionary<string, string>
            {
                ["LINE"] = "1", ["ITEM"] = "SKU123", ["DESC"] = "Widget",
                ["QTY"]  = "10", ["UNIT"] = "EA", ["PRICE"] = "9.99",
            }
        };

        var result = PoMappingEngine.Apply(headerRow, lineRows, SimpleConfig());

        result.PoNumber.Should().Be("PO-001");
        result.OrderDate.Should().Be("2026-05-24");
        result.BuyerName.Should().Be("Nordic Distribution");
        result.Currency.Should().Be("EUR");
        result.Lines.Should().HaveCount(1);
        result.Lines[0].BuyerItemCode.Should().Be("SKU123");
        result.Lines[0].Quantity.Should().Be("10");
    }

    [Fact]
    public void Apply_MissingColumn_YieldsNull()
    {
        var headerRow = new Dictionary<string, string>();
        var result = PoMappingEngine.Apply(headerRow, new List<IReadOnlyDictionary<string, string>>(), SimpleConfig());
        result.PoNumber.Should().BeNull();
    }

    [Fact]
    public void Apply_EmptyLines_ReturnsEmptyLinesList()
    {
        var headerRow = new Dictionary<string, string> { ["PO_NUMBER"] = "X" };
        var result = PoMappingEngine.Apply(headerRow, new List<IReadOnlyDictionary<string, string>>(), SimpleConfig());
        result.Lines.Should().BeEmpty();
    }
}
