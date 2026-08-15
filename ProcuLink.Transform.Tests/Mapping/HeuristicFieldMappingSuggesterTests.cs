using FluentAssertions;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Mapping;

namespace ProcuLink.Transform.Tests.Mapping;

public class HeuristicFieldMappingSuggesterTests
{
    private static IReadOnlyList<FieldMappingSuggestion> Suggest(params string[] columns)
        => HeuristicFieldMappingSuggester.Suggest(columns);

    private static FieldMappingSuggestion? For(IReadOnlyList<FieldMappingSuggestion> all, string canonicalField)
        => all.SingleOrDefault(s => s.CanonicalField == canonicalField);

    // ── Empty / null handling ─────────────────────────────────────────────────

    [Fact]
    public void Suggest_ReturnsEmpty_ForNoColumns()
    {
        HeuristicFieldMappingSuggester.Suggest(Array.Empty<string>()).Should().BeEmpty();
        HeuristicFieldMappingSuggester.Suggest(null).Should().BeEmpty();
    }

    [Fact]
    public void Suggest_IgnoresBlankAndWhitespaceColumns()
    {
        var result = Suggest("", "   ", "\t");
        result.Should().BeEmpty();
    }

    // ── Provenance + confidence contract ──────────────────────────────────────

    [Fact]
    public void Suggest_AlwaysTagsSourceHeuristic_AndConfidenceInRange()
    {
        var result = Suggest("PO Number", "Order Date", "SKU", "Qty", "Unit Price");

        result.Should().NotBeEmpty();
        result.Should().OnlyContain(s => s.Source == "heuristic");
        result.Should().OnlyContain(s => s.Confidence >= 0 && s.Confidence <= 1);
        result.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.Reason));
    }

    // ── Header field matching ─────────────────────────────────────────────────

    [Theory]
    [InlineData("PO Number")]
    [InlineData("po_number")]
    [InlineData("PONUM")]
    [InlineData("Order ID")]
    [InlineData("Purchase Order No")]
    public void Suggest_MapsPoNumberAliases(string column)
    {
        var match = For(Suggest(column), "PoNumber");
        match.Should().NotBeNull();
        match!.SuggestedColumn.Should().Be(column);
        match.Confidence.Should().BeGreaterThanOrEqualTo(0.45);
    }

    [Theory]
    [InlineData("Order Date")]
    [InlineData("order_date")]
    [InlineData("PO Date")]
    [InlineData("Document Date")]
    public void Suggest_MapsOrderDateAliases(string column)
    {
        For(Suggest(column), "OrderDate").Should().NotBeNull();
    }

    [Theory]
    [InlineData("Buyer Name")]
    [InlineData("Customer")]
    [InlineData("Company Name")]
    public void Suggest_MapsBuyerNameAliases(string column)
    {
        For(Suggest(column), "BuyerName").Should().NotBeNull();
    }

    [Theory]
    [InlineData("Currency")]
    [InlineData("CURR")]
    [InlineData("Currency Code")]
    public void Suggest_MapsCurrencyAliases(string column)
    {
        For(Suggest(column), "Currency").Should().NotBeNull();
    }

    // ── Line field matching ───────────────────────────────────────────────────

    [Theory]
    [InlineData("SKU")]
    [InlineData("Item Code")]
    [InlineData("Article")]
    [InlineData("Article Number")]
    [InlineData("Product Code")]
    [InlineData("Part No")]
    public void Suggest_MapsBuyerItemCodeAliases(string column)
    {
        var match = For(Suggest(column), "BuyerItemCode");
        match.Should().NotBeNull();
        match!.SuggestedColumn.Should().Be(column);
    }

    [Theory]
    [InlineData("Qty")]
    [InlineData("Quantity")]
    [InlineData("Quantity Ordered")]
    [InlineData("qty_ordered")]
    public void Suggest_MapsQuantityAliases(string column)
    {
        For(Suggest(column), "Quantity").Should().NotBeNull();
    }

    [Theory]
    [InlineData("Unit Price")]
    [InlineData("Price")]
    [InlineData("Net")]
    [InlineData("Unit Cost")]
    [InlineData("Net Price")]
    public void Suggest_MapsUnitPriceAliases(string column)
    {
        For(Suggest(column), "UnitPrice").Should().NotBeNull();
    }

    [Theory]
    [InlineData("Description")]
    [InlineData("Item Description")]
    [InlineData("Product Name")]
    public void Suggest_MapsDescriptionAliases(string column)
    {
        For(Suggest(column), "Description").Should().NotBeNull();
    }

    [Theory]
    [InlineData("UOM")]
    [InlineData("Unit of Measure")]
    public void Suggest_MapsUnitAliases(string column)
    {
        For(Suggest(column), "Unit").Should().NotBeNull();
    }

    [Theory]
    [InlineData("Line")]
    [InlineData("Line No")]
    [InlineData("Row")]
    [InlineData("Position")]
    public void Suggest_MapsLineNumberAliases(string column)
    {
        For(Suggest(column), "LineNumber").Should().NotBeNull();
    }

    // ── Disambiguation: "Unit" vs "Unit Price" ────────────────────────────────

    [Fact]
    public void Suggest_DoesNotMapUnitPriceColumnToUnit()
    {
        // A column literally called "Unit Price" must NOT be claimed by the Unit field.
        var result = Suggest("Unit Price");
        For(result, "Unit").Should().BeNull("'Unit Price' is a price, not a unit-of-measure");
        For(result, "UnitPrice").Should().NotBeNull();
    }

    [Fact]
    public void Suggest_SeparatesUnitAndUnitPriceWhenBothPresent()
    {
        var result = Suggest("UOM", "Unit Price");

        var unit = For(result, "Unit");
        var unitPrice = For(result, "UnitPrice");

        unit.Should().NotBeNull();
        unit!.SuggestedColumn.Should().Be("UOM");
        unitPrice.Should().NotBeNull();
        unitPrice!.SuggestedColumn.Should().Be("Unit Price");
    }

    // ── Each source column is assigned to at most one canonical field ─────────

    [Fact]
    public void Suggest_AssignsEachColumnToAtMostOneField()
    {
        var result = Suggest("PO Number", "Order Date", "SKU", "Description", "Qty", "Unit", "Unit Price", "Currency");

        var columns = result.Select(s => s.SuggestedColumn).ToList();
        columns.Should().OnlyHaveUniqueItems("a source column must not be suggested for two canonical fields");
    }

    // ── Full realistic CSV header row ─────────────────────────────────────────

    [Fact]
    public void Suggest_MapsRealisticCsvHeaderRow()
    {
        var headers = new[]
        {
            "PO_Number", "Order_Date", "Buyer", "Currency",
            "Line", "Item_Code", "Description", "Quantity", "UOM", "Unit_Price",
        };

        var result = Suggest(headers);

        For(result, "PoNumber")!.SuggestedColumn.Should().Be("PO_Number");
        For(result, "OrderDate")!.SuggestedColumn.Should().Be("Order_Date");
        For(result, "BuyerName")!.SuggestedColumn.Should().Be("Buyer");
        For(result, "Currency")!.SuggestedColumn.Should().Be("Currency");
        For(result, "LineNumber")!.SuggestedColumn.Should().Be("Line");
        For(result, "BuyerItemCode")!.SuggestedColumn.Should().Be("Item_Code");
        For(result, "Description")!.SuggestedColumn.Should().Be("Description");
        For(result, "Quantity")!.SuggestedColumn.Should().Be("Quantity");
        For(result, "Unit")!.SuggestedColumn.Should().Be("UOM");
        For(result, "UnitPrice")!.SuggestedColumn.Should().Be("Unit_Price");

        // All ten canonical fields resolved, no column reused.
        result.Should().HaveCount(10);
        result.Select(s => s.SuggestedColumn).Should().OnlyHaveUniqueItems();
    }

    // ── Unrelated columns produce no suggestions ──────────────────────────────

    [Fact]
    public void Suggest_OmitsUnrelatedColumns()
    {
        var result = Suggest("Warehouse Aisle", "Random Notes", "Foobar");
        result.Should().BeEmpty();
    }

    [Fact]
    public void Suggest_ReturnsResultsInCanonicalFieldDisplayOrder()
    {
        // Provide columns in a scrambled order; header fields should still come first.
        var result = Suggest("Unit Price", "SKU", "PO Number", "Currency", "Quantity");

        var fields = result.Select(s => s.CanonicalField).ToList();
        var poIdx = fields.IndexOf("PoNumber");
        var currencyIdx = fields.IndexOf("Currency");
        var skuIdx = fields.IndexOf("BuyerItemCode");
        var priceIdx = fields.IndexOf("UnitPrice");

        poIdx.Should().BeLessThan(skuIdx, "header fields precede line fields");
        currencyIdx.Should().BeLessThan(skuIdx);
        skuIdx.Should().BeLessThan(priceIdx, "BuyerItemCode precedes UnitPrice in the line order");
    }

    // ── Normalize helper ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("PO_Number", "ponumber")]
    [InlineData("PO Number", "ponumber")]
    [InlineData("PO-Number", "ponumber")]
    [InlineData("po.number", "ponumber")]
    [InlineData("  Qty  ", "qty")]
    [InlineData("", "")]
    public void Normalize_StripsSeparatorsAndLowercases(string raw, string expected)
    {
        HeuristicFieldMappingSuggester.Normalize(raw).Should().Be(expected);
    }

    // ── The accept floor ──────────────────────────────────────────────────────
    //
    // MinAcceptScore is the ONLY floor between the scorer and the operator's screen.
    // The mapper editor (project-proculink, src/components/bridge/PoMappingEditor.tsx)
    // renders every suggestion the API returns and applies no threshold of its own —
    // it used to hold ADOPT_THRESHOLD = 0.50 while this constant said 0.45, so the
    // backend's floor was dead and the band between them was scored, serialized, sent,
    // and dropped without ever being drawn. The tests below pin the number and the two
    // things that make it true: nothing this scorer emits falls under it, and nothing in
    // the field table can drift to make that stop holding.

    [Fact]
    public void MinAcceptScore_IsTheNumberTheMapperEditorRendersFrom()
    {
        // Changing this is a two-repo change. The editor has no floor of its own, so
        // lowering it here is what puts un-renderable suggestions back on the wire;
        // raising it here is what silently stops surfacing candidates the operator
        // used to see. Neither is wrong — both need the other end looked at.
        HeuristicFieldMappingSuggester.MinAcceptScore.Should().Be(0.50);
    }

    [Fact]
    public void Suggest_NeverEmitsAnythingBelowTheFloor()
    {
        var corpus = new[]
        {
            "PO Number", "Customer PO Number", "Order Date", "Buyer Name", "Currency",
            "Line", "SKU", "Manufacturer Part Number", "Description", "Qty",
            "UOM", "Unit Price", "Extended Amount", "Warehouse", "Notes",
            "supplier_article_code_for_this_line", "Delivery Date", "Net", "Row", "Item",
        };

        var result = Suggest(corpus);

        result.Should().NotBeEmpty();
        result.Should().OnlyContain(s => s.Confidence >= HeuristicFieldMappingSuggester.MinAcceptScore);
    }

    /// <summary>
    /// The partial-match branch scores <c>0.5 + 0.4·(hits/|Tokens|) − lengthPenalty</c>. With a
    /// single token hit on a header long enough to take the 0.05 penalty, that is
    /// <c>0.45 + 0.4/|Tokens|</c> — which sits at exactly the floor at eight signal tokens and
    /// dips under it at nine. So "no suggestion is emitted below the floor" is not a property of
    /// the constant; it is a property of the field table staying under that width. Adding a ninth
    /// alias to a field's <c>tokens</c> list is a one-line change that would quietly reopen the
    /// invisible band, which is why the bound is asserted rather than trusted.
    /// </summary>
    [Fact]
    public void NoCanonicalFieldHasMoreSignalTokensThanTheFloorAllows()
    {
        var tooWide = HeuristicFieldMappingSuggester.CanonicalFields
            .Where(f => f.Tokens.Count > MaxSignalTokensPerField)
            .Select(f => $"{f.Name} ({f.Tokens.Count} tokens)")
            .ToList();

        tooWide.Should().BeEmpty(
            "a field with more than {0} signal tokens can score a single-token match below " +
            "MinAcceptScore, which the API would emit and the mapper editor would never draw",
            MaxSignalTokensPerField);
    }

    [Theory]
    [InlineData(MaxSignalTokensPerField, true)]      // exactly at the bound — still renderable
    [InlineData(MaxSignalTokensPerField + 1, false)] // one token wider — falls into the dead band
    public void SignalTokenCount_DecidesWhetherAWeakMatchClearsTheFloor(int tokenCount, bool clearsFloor)
    {
        // A header long enough (>24 chars) to take the 0.05 length penalty, containing exactly
        // one of the field's signal tokens and none of its aliases — the weakest match the
        // partial-overlap branch can produce for a field of this width.
        const string hitToken = "zeta";
        var column = hitToken + new string('x', 25);

        var tokens = new[] { hitToken }
            .Concat(Enumerable.Range(0, tokenCount - 1).Select(i => $"tok{i}qqq"))
            .ToArray();

        var field = new HeuristicFieldMappingSuggester.CanonicalField(
            "Synthetic",
            exact: Array.Empty<string>(),
            tokens: tokens,
            mustHaveAny: new[] { hitToken });

        var (score, _) = HeuristicFieldMappingSuggester.Score(field, column);

        score.Should().BeApproximately(0.45 + 0.4 / tokenCount, 1e-9,
            "the weakest partial match a field of this width can produce is 0.5 + 0.4/N - 0.05");
        (score >= HeuristicFieldMappingSuggester.MinAcceptScore).Should().Be(clearsFloor);
    }

    /// <summary>Widest signal-token list a canonical field may carry — see
    /// <see cref="NoCanonicalFieldHasMoreSignalTokensThanTheFloorAllows"/>.</summary>
    private const int MaxSignalTokensPerField = 8;
}
