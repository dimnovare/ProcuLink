using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;

namespace ProcuLink.Transform.Documents;

/// <summary>
/// Fills the typed order the standard writers read from a document's read, by meaning, never by key. Only what a
/// writer reads is mapped. Meanings are document-relative: a purchase order's sender is the buyer. The order is
/// never attached to a DbContext.
/// </summary>
public static class StandardOrderAdapter
{
    private const string NoLines = "at least one line";

    /// <summary>The order, or what the read lacks for <paramref name="needs"/> (never both): each item the carrier's label, a line need with its first failing line.</summary>
    public static (PurchaseOrderEntity? Order, IReadOnlyList<string> Missing) ToOrder(
        ReadResult read, FlowFieldList fields, string? tableKey, IReadOnlyList<string> needs, Guid orgId)
    {
        ReadField? Field(string meaning) => read.Fields.FirstOrDefault(f => FieldMeanings.Of(f, fields) == meaning);
        string? Text(string meaning) => Blank(Field(meaning)?.Value);
        var table = tableKey is null ? null : read.Tables.FirstOrDefault(t => t.Key == tableKey);
        var rows = table?.Rows ?? [];
        var columns = (table?.Columns ?? []).ToLookup(c => FieldMeanings.Of(table!, c, fields));
        ReadColumn? Column(string meaning) => columns[meaning].FirstOrDefault();
        string? Cell(ReadRow row, string meaning) =>
            Column(meaning) is { } c && row.Cells.TryGetValue(c.Key, out var cell) ? Blank(cell.Value) : null;

        var missing = new List<string>();
        foreach (var need in needs)
        {
            if (!need.StartsWith("line_", StringComparison.Ordinal))
            {
                if (!Present(need, Text(need))) missing.Add(Field(need)?.Label ?? Humanise(need));
            }
            else if (rows.Count == 0)
            {
                if (!missing.Contains(NoLines)) missing.Add(NoLines);
            }
            else
            {
                var line = Enumerable.Range(1, rows.Count).FirstOrDefault(n => !Present(need, Cell(rows[n - 1], need)));
                if (line > 0) missing.Add($"{Column(need)?.Label ?? Humanise(need)} (line {line})");
            }
        }
        if (missing.Count > 0) return (null, missing);

        // The document's own line numbers only when every row has a distinct positive whole one; else 1..n.
        var numbers = rows.Select(r => FieldValueParser.Number(Cell(r, FieldMeanings.LineNumber))).ToList();
        var own = numbers.All(n => n > 0 && n <= int.MaxValue && n % 1 == 0) && numbers.Distinct().Count() == numbers.Count;
        FieldValueParser.TryDate(Text(FieldMeanings.IssueDate), out var date);
        return (new PurchaseOrderEntity
        {
            OrgId = orgId,
            PoNumber = Text(FieldMeanings.DocumentNumber) ?? "",
            OrderDate = date,
            Currency = Text(FieldMeanings.Currency)?.ToUpperInvariant() ?? "",
            BuyerName = Text(FieldMeanings.SenderName),
            BuyerTaxId = Text(FieldMeanings.SenderVatId),
            SupplierName = Text(FieldMeanings.ReceiverName),
            // One code fills both: X12 needs the buyer's (BP), every writer needs the supplier's.
            Lines = rows.Select((r, i) => new PurchaseOrderLineEntity
            {
                LineNumber = own ? (int)numbers[i]! : i + 1,
                SupplierItemCode = Cell(r, FieldMeanings.LineItemCode),
                BuyerItemCode = Cell(r, FieldMeanings.LineItemCode) ?? "",
                Description = Cell(r, FieldMeanings.LineDescription),
                Quantity = FieldValueParser.Number(Cell(r, FieldMeanings.LineQuantity)) ?? 0,
                Unit = Cell(r, FieldMeanings.LineUnit),
                UnitPrice = FieldValueParser.Number(Cell(r, FieldMeanings.LineUnitPrice)) ?? 0,
            }).ToList(),
        }, []);
    }

    // Numbers count only with a single reading (an ambiguous "1.500" is missing, never guessed), within the writers' own guards.
    private static bool Present(string meaning, string? value) => value is not null && meaning switch
    {
        FieldMeanings.IssueDate => FieldValueParser.TryDate(value, out _),
        FieldMeanings.Currency => FieldValueParser.IsCurrency(value.ToUpperInvariant()),
        FieldMeanings.LineQuantity => FieldValueParser.Number(value) > 0,
        FieldMeanings.LineUnitPrice => FieldValueParser.Number(value) >= 0,
        _ => true,
    };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Humanise(string meaning) => char.ToUpperInvariant(meaning[0]) + meaning[1..].Replace('_', ' ');
}
