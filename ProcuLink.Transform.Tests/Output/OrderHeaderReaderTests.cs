using System.Text.Json;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// Direct coverage for the shared <see cref="OrderHeaderReader"/> helper that consolidates the four
/// former <c>ExtractBuyerName</c> copies (Csv/Json/Mapped/Scriban). Pins the column-first contract,
/// the CanonicalJson fallback, and the new fail-safe-on-malformed-JSON behaviour (returns empty,
/// never throws — now logged rather than silently swallowed).
/// </summary>
public class OrderHeaderReaderTests
{
    private static PurchaseOrderEntity Order(string? buyerName, JsonDocument? canonical) =>
        new()
        {
            Id = Guid.NewGuid(), OrgId = Guid.NewGuid(), SupplierId = Guid.NewGuid(),
            PoNumber = "PO-1", OrderDate = new DateOnly(2026, 6, 8), Currency = "EUR",
            Status = "ready", BuyerName = buyerName, CanonicalJson = canonical,
        };

    [Fact]
    public void PrefersColumn_OverCanonicalJson()
    {
        var order = Order("Column Buyer", JsonDocument.Parse("""{"buyerName":"Stale JSON Buyer"}"""));
        OrderHeaderReader.ExtractBuyerName(order).Should().Be("Column Buyer");
    }

    [Theory]
    [InlineData("""{"buyerName":"Northwind Trading OÜ"}""", "Northwind Trading OÜ")]
    [InlineData("""{"BuyerName":"PascalCase Buyer"}""", "PascalCase Buyer")]
    public void FallsBackToCanonicalJson_WhenColumnEmpty(string json, string expected)
    {
        var order = Order(buyerName: null, JsonDocument.Parse(json));
        OrderHeaderReader.ExtractBuyerName(order).Should().Be(expected);
    }

    [Fact]
    public void ReturnsEmpty_WhenNeitherColumnNorJsonHasBuyer()
    {
        var order = Order(buyerName: null, JsonDocument.Parse("""{"poNumber":"PO-1"}"""));
        OrderHeaderReader.ExtractBuyerName(order).Should().BeEmpty();
    }

    [Fact]
    public void ReturnsEmpty_WhenCanonicalJsonIsNull()
    {
        OrderHeaderReader.ExtractBuyerName(Order(buyerName: null, canonical: null)).Should().BeEmpty();
    }

    [Fact]
    public void ReturnsEmpty_WhenCanonicalJsonIsNotAnObject()
    {
        // A non-object root (e.g. a JSON array) is malformed for header purposes — return empty,
        // never throw.
        var order = Order(buyerName: null, JsonDocument.Parse("""["not","an","object"]"""));
        OrderHeaderReader.ExtractBuyerName(order).Should().BeEmpty();
    }
}
