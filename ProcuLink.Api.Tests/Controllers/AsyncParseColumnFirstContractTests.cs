using System.Text.Json;
using FluentAssertions;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// GUARD: the BuyerName / header read path is COLUMN-FIRST.
///
/// The async parse path (<c>OrderIngestionService.ParseStoredFileAsync</c>) writes the
/// TYPED COLUMNS (BuyerName, …) and DELIBERATELY never writes <c>canonical_json</c>;
/// the typed columns are the single source of truth for an order's header. Only the
/// SYNC ingress paths populate <c>canonical_json</c>, which readers must treat strictly
/// as a LEGACY FALLBACK (consulted only when the column is null).
///
/// These tests pin that contract against the canonical DTO reader
/// (<c>OrdersController.ExtractBuyerName</c>): when the column and a (stale) JSON value
/// DISAGREE, the COLUMN wins. A future regression that flips the reader to read
/// canonical_json first — silently disagreeing with every async-parsed order — fails
/// here, in CI, before it can ship.
/// </summary>
public class AsyncParseColumnFirstContractTests
{
    private static JsonDocument Json(string buyerNameKey, string value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(
            new Dictionary<string, string> { [buyerNameKey] = value }));

    private static PurchaseOrderEntity Order(string? column, JsonDocument? canonicalJson) =>
        new()
        {
            Id            = Guid.NewGuid(),
            OrgId         = Guid.NewGuid(),
            SupplierId    = Guid.NewGuid(),
            PoNumber      = "PO-COLUMN-FIRST",
            OrderDate     = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency      = "EUR",
            Status        = "ready",
            CreatedAt     = DateTime.UtcNow,
            UpdatedAt     = DateTime.UtcNow,
            BuyerName     = column,
            CanonicalJson = canonicalJson,
        };

    [Fact]
    public void ExtractBuyerName_ColumnAndJsonDisagree_ColumnWins()
    {
        // The async parse path set the column to "Acme (column)"; a stale legacy
        // canonical_json still carries a different value. The column MUST win — if
        // this ever returns the JSON value, a reader has gone JSON-first.
        var e = Order(column: "Acme (column)", canonicalJson: Json("buyerName", "Stale (json)"));

        OrdersController.ExtractBuyerName(e).Should().Be("Acme (column)",
            "the typed BuyerName column is the source of truth; canonical_json is only a legacy fallback");
    }

    [Fact]
    public void ExtractBuyerName_ColumnSet_NoJson_ReturnsColumn()
    {
        // The dominant async-parse shape: column populated, canonical_json absent.
        var e = Order(column: "Northwind", canonicalJson: null);

        OrdersController.ExtractBuyerName(e).Should().Be("Northwind");
    }

    [Fact]
    public void ExtractBuyerName_ColumnNull_FallsBackToCanonicalJson_Lowercase()
    {
        // Legacy sync-path order: no column, buyer name lives in canonical_json.
        var e = Order(column: null, canonicalJson: Json("buyerName", "Legacy Buyer"));

        OrdersController.ExtractBuyerName(e).Should().Be("Legacy Buyer",
            "with no column the legacy canonical_json fallback supplies the value");
    }

    [Fact]
    public void ExtractBuyerName_ColumnNull_FallsBackToCanonicalJson_PascalCase()
    {
        // The fallback tolerates both camelCase ("buyerName") and PascalCase ("BuyerName").
        var e = Order(column: null, canonicalJson: Json("BuyerName", "Pascal Buyer"));

        OrdersController.ExtractBuyerName(e).Should().Be("Pascal Buyer");
    }

    [Fact]
    public void ExtractBuyerName_ColumnWhitespace_FallsBackToCanonicalJson()
    {
        // A blank/whitespace column is treated as "not present" so the legacy
        // fallback still applies — but a NON-blank column always wins (test above).
        var e = Order(column: "   ", canonicalJson: Json("buyerName", "Fallback Buyer"));

        OrdersController.ExtractBuyerName(e).Should().Be("Fallback Buyer");
    }

    [Fact]
    public void ExtractBuyerName_NeitherSource_ReturnsNull()
    {
        OrdersController.ExtractBuyerName(Order(column: null, canonicalJson: null))
            .Should().BeNull();
    }
}
