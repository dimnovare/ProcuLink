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

    // ── F-1 Phase 4: RelativeLineKey — strip the per-line ordinal so the key is identical across lines ──

    // CSV / XLSX: cell:r{n}c{c} → cell:c{c} (drop the row, keep the column).
    [Theory]
    [InlineData("cell:r2c1", "cell:c1")]
    [InlineData("cell:r2c5", "cell:c5")]
    [InlineData("cell:r3c5", "cell:c5")]    // a DIFFERENT row, SAME column → SAME relative key
    [InlineData("cell:r10c4", "cell:c4")]
    public void RelativeLineKey_Cell_DropsRow_KeepsColumn(string id, string expected)
    {
        SourceTokenLineIndexer.RelativeLineKey(id).Should().Be(expected);
    }

    [Fact]
    public void RelativeLineKey_CsvDataRows_ShareOneRelativeKey()
    {
        // The whole point: every data row of the same column collapses to ONE relative key.
        SourceTokenLineIndexer.RelativeLineKey("cell:r2c5")
            .Should().Be(SourceTokenLineIndexer.RelativeLineKey("cell:r3c5"));
    }

    [Fact]
    public void RelativeLineKey_HeaderRow_IsNull()
    {
        // Row 1 is the header (header-scope) — no relative alias, consistent with LineOrdinalOf.
        SourceTokenLineIndexer.RelativeLineKey("cell:r1c3").Should().BeNull();
    }

    // XML / cXML / UBL / IDoc: strip the DEEPEST [n] predicate ONLY.
    [Theory]
    [InlineData("/Order/Lines/Line[1]/Qty", "/Order/Lines/Line/Qty")]
    [InlineData("/Order/Lines/Line[2]/Qty", "/Order/Lines/Line/Qty")]   // line 1 & line 2 → SAME key
    [InlineData("/Order/Lines/Line[2]/@qty", "/Order/Lines/Line/@qty")]
    [InlineData("/Order/Lines/Line[12]/ItemCode", "/Order/Lines/Line/ItemCode")]
    [InlineData("/cXML/Request/OrderRequest/ItemOut[3]/ItemID/SupplierPartID",
                "/cXML/Request/OrderRequest/ItemOut/ItemID/SupplierPartID")]
    public void RelativeLineKey_Xpath_StripsDeepestPredicate(string id, string expected)
    {
        SourceTokenLineIndexer.RelativeLineKey(id).Should().Be(expected);
    }

    [Fact]
    public void RelativeLineKey_Xpath_StripsOnlyTheDeepestPredicate()
    {
        // A SHALLOWER predicate (e.g. a fixed parent index) must be LEFT INTACT; only the line one goes.
        SourceTokenLineIndexer.RelativeLineKey("/Order/Group[1]/Lines/Line[2]/Qty")
            .Should().Be("/Order/Group[1]/Lines/Line/Qty");
    }

    [Fact]
    public void RelativeLineKey_Xpath_NoPredicate_IsNull()
    {
        SourceTokenLineIndexer.RelativeLineKey("/Order/Header/PoNumber").Should().BeNull();
    }

    // EDIFACT / X12: seg:{TAG}[{n}].el… → seg:{TAG}.el… (drop the occurrence, keep the element path).
    [Theory]
    [InlineData("seg:LIN[1].el1", "seg:LIN.el1")]
    [InlineData("seg:LIN[2].el1", "seg:LIN.el1")]   // occurrence 1 & 2 → SAME key
    [InlineData("seg:QTY[2].el1.c2", "seg:QTY.el1.c2")]
    [InlineData("seg:PO1[3].el2", "seg:PO1.el2")]
    [InlineData("seg:PRI[10].el1.c2", "seg:PRI.el1.c2")]
    public void RelativeLineKey_Segment_DropsOccurrence(string id, string expected)
    {
        SourceTokenLineIndexer.RelativeLineKey(id).Should().Be(expected);
    }

    // JSON: replace the FIRST array index with '*'.
    [Theory]
    [InlineData("json:/lines/0/sku", "json:/lines/*/sku")]
    [InlineData("json:/lines/1/sku", "json:/lines/*/sku")]   // index 0 & 1 → SAME key
    [InlineData("json:/items/4/qty", "json:/items/*/qty")]
    public void RelativeLineKey_Json_StarsFirstArrayIndex(string id, string expected)
    {
        SourceTokenLineIndexer.RelativeLineKey(id).Should().Be(expected);
    }

    [Fact]
    public void RelativeLineKey_Json_NoArrayIndex_IsNull()
    {
        SourceTokenLineIndexer.RelativeLineKey("json:/header/orderNumber").Should().BeNull();
    }

    // Header / no-ordinal / raw / null → null (no relative alias).
    [Theory]
    [InlineData("raw:Order Number")]
    [InlineData("")]
    [InlineData("totally-unknown-id")]
    public void RelativeLineKey_Unmatched_IsNull(string id)
    {
        SourceTokenLineIndexer.RelativeLineKey(id).Should().BeNull();
    }

    [Fact]
    public void RelativeLineKey_Null_IsNull()
    {
        SourceTokenLineIndexer.RelativeLineKey(null!).Should().BeNull();
    }

    // The relative key must collapse to ONE value per format, but stay DISTINCT from the absolute id —
    // so a rule can choose "this exact cell" vs "this column for every line" unambiguously.
    [Theory]
    [InlineData("cell:r2c5")]
    [InlineData("/Order/Lines/Line[2]/Qty")]
    [InlineData("seg:LIN[2].el1")]
    [InlineData("json:/lines/1/sku")]
    public void RelativeLineKey_DiffersFromAbsoluteId(string id)
    {
        SourceTokenLineIndexer.RelativeLineKey(id).Should().NotBe(id);
    }
}
