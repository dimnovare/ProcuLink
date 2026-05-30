using FluentAssertions;
using ProcuLink.Api.Services.StarterTemplates;
using ProcuLink.Transform.Mapping;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Verifies that the Erply and Directo starter templates:
///  1. Deserialize correctly from their embedded JSON fixtures.
///  2. Round-trip through <see cref="PoMappingEngine.Apply"/> producing a
///     fully-populated <see cref="ProcuLink.Transform.Mapping.MappedOrder"/>
///     (all canonical header fields + all canonical line fields non-null).
///  3. GetAll() returns exactly the two expected templates.
/// </summary>
public sealed class StarterTemplateServiceTests
{
    private static IStarterTemplateService CreateService() => new StarterTemplateService();

    // ── GetAll ────────────────────────────────────────────────────────────────

    [Fact]
    public void GetAll_ReturnsExactlyErplyAndDirecto()
    {
        var svc = CreateService();
        var templates = svc.GetAll();

        templates.Should().HaveCount(2);
        templates.Select(t => t.Id).Should().BeEquivalentTo(["erply", "directo"]);
    }

    // ── Erply fixture ─────────────────────────────────────────────────────────

    [Fact]
    public void ErplyFixture_DeserializesToValidPoMappingConfig()
    {
        var svc = CreateService();
        var erply = svc.GetAll().Single(t => t.Id == "erply");

        erply.Erp.Should().Be("Erply");
        erply.Config.Should().NotBeNull();
        erply.Config.Header.Should().ContainKeys("PoNumber", "OrderDate", "BuyerName", "Currency");
        erply.Config.Lines.Should().ContainKeys("BuyerItemCode", "Description", "Quantity", "Unit", "UnitPrice");

        // Verify exact ExternalField values from spec
        erply.Config.Header["PoNumber"].ExternalField.Should().Be("number");
        erply.Config.Header["OrderDate"].ExternalField.Should().Be("date");
        erply.Config.Header["BuyerName"].ExternalField.Should().Be("clientName");
        erply.Config.Header["Currency"].ExternalField.Should().Be("currencyCode");

        erply.Config.Lines["BuyerItemCode"].ExternalField.Should().Be("code");
        erply.Config.Lines["Description"].ExternalField.Should().Be("itemName");
        erply.Config.Lines["Quantity"].ExternalField.Should().Be("amount");
        erply.Config.Lines["Unit"].ExternalField.Should().Be("unitName");
        erply.Config.Lines["UnitPrice"].ExternalField.Should().Be("price");
    }

    [Fact]
    public void ErplyFixture_RoundTripsThoughMappingEngine_AllCanonicalFieldsPopulated()
    {
        var svc = CreateService();
        var erply = svc.GetAll().Single(t => t.Id == "erply");

        // Synthetic header row — keys are the ExternalField values from the template
        IReadOnlyDictionary<string, string> headerRow = new Dictionary<string, string>
        {
            ["number"]       = "PO-2026-001",
            ["date"]         = "2026-01-15",
            ["clientName"]   = "Acme OÜ",
            ["currencyCode"] = "EUR",
        };

        // Synthetic line row
        IReadOnlyDictionary<string, string> lineRow = new Dictionary<string, string>
        {
            ["code"]     = "SKU-A1",
            ["itemName"] = "Widget Alpha",
            ["amount"]   = "100",
            ["unitName"] = "PCS",
            ["price"]    = "12.50",
        };

        var result = PoMappingEngine.Apply(headerRow, [lineRow], erply.Config);

        result.PoNumber.Should().Be("PO-2026-001");
        result.OrderDate.Should().Be("2026-01-15"); // DateFormat is identity here
        result.BuyerName.Should().Be("Acme OÜ");
        result.Currency.Should().Be("EUR");

        result.Lines.Should().HaveCount(1);
        var line = result.Lines[0];
        line.BuyerItemCode.Should().Be("SKU-A1");
        line.Description.Should().Be("Widget Alpha");
        line.Quantity.Should().Be("100");
        line.Unit.Should().Be("PCS");
        line.UnitPrice.Should().Be("12.50");
    }

    // ── Directo fixture ───────────────────────────────────────────────────────

    [Fact]
    public void DirectoFixture_DeserializesToValidPoMappingConfig()
    {
        var svc = CreateService();
        var directo = svc.GetAll().Single(t => t.Id == "directo");

        directo.Erp.Should().Be("Directo");
        directo.Config.Should().NotBeNull();
        directo.Config.Header.Should().ContainKeys("PoNumber", "OrderDate", "BuyerName", "Currency");
        directo.Config.Lines.Should().ContainKeys("BuyerItemCode", "Description", "Quantity", "Unit", "UnitPrice");

        // Verify exact ExternalField values from spec
        directo.Config.Header["PoNumber"].ExternalField.Should().Be("number");
        directo.Config.Header["OrderDate"].ExternalField.Should().Be("date");
        directo.Config.Header["BuyerName"].ExternalField.Should().Be("customer_name");
        directo.Config.Header["Currency"].ExternalField.Should().Be("currency");

        directo.Config.Lines["BuyerItemCode"].ExternalField.Should().Be("row_item");
        directo.Config.Lines["Description"].ExternalField.Should().Be("row_description");
        directo.Config.Lines["Quantity"].ExternalField.Should().Be("row_quantity");
        directo.Config.Lines["Unit"].ExternalField.Should().Be("unit");
        directo.Config.Lines["UnitPrice"].ExternalField.Should().Be("row_price");
    }

    [Fact]
    public void DirectoFixture_RoundTripsThoughMappingEngine_AllCanonicalFieldsPopulated()
    {
        var svc = CreateService();
        var directo = svc.GetAll().Single(t => t.Id == "directo");

        IReadOnlyDictionary<string, string> headerRow = new Dictionary<string, string>
        {
            ["number"]        = "DO-2026-042",
            ["date"]          = "2026-03-10",
            ["customer_name"] = "Baltic Trade AS",
            ["currency"]      = "EUR",
        };

        IReadOnlyDictionary<string, string> lineRow = new Dictionary<string, string>
        {
            ["row_item"]        = "DRC-001",
            ["row_description"] = "Bearing 6205",
            ["row_quantity"]    = "50",
            ["unit"]            = "PCS",
            ["row_price"]       = "8.75",
        };

        var result = PoMappingEngine.Apply(headerRow, [lineRow], directo.Config);

        result.PoNumber.Should().Be("DO-2026-042");
        result.OrderDate.Should().Be("2026-03-10");
        result.BuyerName.Should().Be("Baltic Trade AS");
        result.Currency.Should().Be("EUR");

        result.Lines.Should().HaveCount(1);
        var line = result.Lines[0];
        line.BuyerItemCode.Should().Be("DRC-001");
        line.Description.Should().Be("Bearing 6205");
        line.Quantity.Should().Be("50");
        line.Unit.Should().Be("PCS");
        line.UnitPrice.Should().Be("8.75");
    }
}
