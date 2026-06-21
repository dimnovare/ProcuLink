using FluentAssertions;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// F-1 Seam A — golden, per-format unit tests for <see cref="SourceTokenLineIndexer"/>: the PURE
/// helper that maps a source-token id to the 1-based LINE ORDINAL it addresses (or null when the id
/// is header-scoped / has no per-line position). The injection logic uses this to decide which line's
/// row bag a <c>Group=="line"</c> token belongs in, so <b>line[2]'s bag never sees line[1]'s value</b>.
/// </summary>
public class SourceTokenLineIndexerTests
{
    // ── CSV / XLSX: cell:r{n}c{c} — n is the 1-based row; row 1 is the header, data rows are 2.. ──

    [Theory]
    [InlineData("cell:r2c1", 1)]   // first data row → line 1
    [InlineData("cell:r2c5", 1)]
    [InlineData("cell:r3c1", 2)]   // second data row → line 2
    [InlineData("cell:r10c4", 9)]
    public void Cell_DataRow_MapsToOrdinal_RowMinusOne(string id, int expected)
    {
        SourceTokenLineIndexer.LineOrdinalOf(id).Should().Be(expected);
    }

    [Fact]
    public void Cell_HeaderRow_HasNoOrdinal()
    {
        // Row 1 is the header row — header-scope, never a line.
        SourceTokenLineIndexer.LineOrdinalOf("cell:r1c3").Should().BeNull();
    }

    // ── XML / cXML / UBL / IDoc: XPath with a 1-based [n] predicate on the repeating line element ──

    [Theory]
    [InlineData("/Order/Lines/Line[1]/Qty", 1)]
    [InlineData("/Order/Lines/Line[2]/Qty", 2)]
    [InlineData("/Order/Lines/Line[2]/@qty", 2)]   // an attribute under line 2
    [InlineData("/Order/Lines/Line[12]/ItemCode", 12)]
    public void Xpath_PositionalPredicate_MapsToOrdinal(string id, int expected)
    {
        SourceTokenLineIndexer.LineOrdinalOf(id).Should().Be(expected);
    }

    [Fact]
    public void Xpath_DeepestPredicate_IsTheLineOrdinal()
    {
        // The innermost repeating group is the line; the deepest [n] addresses it.
        SourceTokenLineIndexer.LineOrdinalOf("/cXML/Request/OrderRequest/ItemOut[3]/ItemID/SupplierPartID")
            .Should().Be(3);
    }

    [Fact]
    public void Xpath_NoPredicate_HasNoOrdinal()
    {
        // A non-repeating element (single occurrence) carries no [n] — header-scope.
        SourceTokenLineIndexer.LineOrdinalOf("/Order/Header/PoNumber").Should().BeNull();
    }

    // ── EDIFACT / X12: seg:{TAG}[{n}].el… — n is the 1-based tag occurrence; for the anchor (LIN/PO1)
    //    and the realistic one-segment-per-line layout, occurrence n == line n. ──

    [Theory]
    [InlineData("seg:LIN[1].el1", 1)]
    [InlineData("seg:LIN[2].el1", 2)]
    [InlineData("seg:QTY[2].el1.c2", 2)]
    [InlineData("seg:PO1[3].el2", 3)]
    [InlineData("seg:PRI[10].el1.c2", 10)]
    public void Segment_Occurrence_MapsToOrdinal(string id, int expected)
    {
        SourceTokenLineIndexer.LineOrdinalOf(id).Should().Be(expected);
    }

    // ── JSON: json:/.../{index}/... — the first 0-based array index → 1-based ordinal. ──

    [Theory]
    [InlineData("json:/lines/0/sku", 1)]
    [InlineData("json:/lines/1/sku", 2)]
    [InlineData("json:/items/4/qty", 5)]
    public void JsonPointer_ArrayIndex_MapsToOrdinal(string id, int expected)
    {
        SourceTokenLineIndexer.LineOrdinalOf(id).Should().Be(expected);
    }

    [Fact]
    public void JsonPointer_NoArrayIndex_HasNoOrdinal()
    {
        SourceTokenLineIndexer.LineOrdinalOf("json:/header/orderNumber").Should().BeNull();
    }

    // ── Unmatched / header-scope ids ──

    [Theory]
    [InlineData("raw:Order Number")]   // PDF/email raw_fields — no ordinal
    [InlineData("")]
    [InlineData("totally-unknown-id")]
    public void Unmatched_Id_HasNoOrdinal(string id)
    {
        SourceTokenLineIndexer.LineOrdinalOf(id).Should().BeNull();
    }

    [Fact]
    public void Null_Id_HasNoOrdinal()
    {
        SourceTokenLineIndexer.LineOrdinalOf(null!).Should().BeNull();
    }
}
