using System.Text.RegularExpressions;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;

namespace ProcuLink.Transform.Documents;

/// <summary>
/// Fills the typed order the standard writers read from a document's read, by meaning, never by key. Only what a
/// writer reads is mapped. Meanings are document-relative: a purchase order's sender is the buyer. The order is
/// never attached to a DbContext.
/// </summary>
public static partial class StandardOrderAdapter
{
    private const string NoLines = "at least one line";

    /// <summary>The order, or what the read lacks for <paramref name="needs"/> (never both): each item the carrier's label, a line need with its first failing line.</summary>
    public static (PurchaseOrderEntity? Order, IReadOnlyList<string> Missing) ToOrder(
        ReadResult read, FlowFieldList fields, string? tableKey, IReadOnlyList<string> needs, Guid orgId) =>
        Build(read, fields, tableKey, needs, a => new PurchaseOrderEntity
        {
            OrgId = orgId,
            PoNumber = a.Text(FieldMeanings.DocumentNumber) ?? "",
            OrderDate = a.Date(FieldMeanings.IssueDate),
            Currency = a.Text(FieldMeanings.Currency)?.ToUpperInvariant() ?? "",
            BuyerName = a.Text(FieldMeanings.SenderName),
            BuyerTaxId = a.Text(FieldMeanings.SenderVatId),
            SupplierName = a.Text(FieldMeanings.ReceiverName),
            // One code fills both: X12 needs the buyer's (BP), every writer needs the supplier's.
            Lines = a.Rows.Select((r, i) => new PurchaseOrderLineEntity
            {
                LineNumber = a.LineNumber(i),
                SupplierItemCode = a.Cell(r, FieldMeanings.LineItemCode),
                BuyerItemCode = a.Cell(r, FieldMeanings.LineItemCode) ?? "",
                Description = a.Cell(r, FieldMeanings.LineDescription),
                Quantity = a.Number(r, FieldMeanings.LineQuantity),
                Unit = a.Cell(r, FieldMeanings.LineUnit),
                UnitPrice = a.Number(r, FieldMeanings.LineUnitPrice),
            }).ToList(),
        });

    /// <summary>What a standard's adapter reads a read through: values by meaning, never by key.</summary>
    internal sealed class Access(
        Func<string, string?> text, IReadOnlyList<ReadRow> rows, Func<ReadRow, string, string?> cell, IReadOnlyList<decimal?> numbers)
    {
        public string? Text(string meaning) => text(meaning);
        public IReadOnlyList<ReadRow> Rows => rows;
        public string? Cell(ReadRow row, string meaning) => cell(row, meaning);
        public decimal Number(ReadRow row, string meaning) => FieldValueParser.Number(Cell(row, meaning)) ?? 0;
        public decimal HeaderNumber(string meaning) => FieldValueParser.Number(Text(meaning)) ?? 0;
        public DateOnly Date(string meaning) => FieldValueParser.TryDate(Text(meaning), out var d) ? d : default;
        public DateOnly? OptionalDate(string meaning) => FieldValueParser.TryDate(Text(meaning), out var d) ? d : null;
        // The document's own line numbers only when every row has a distinct positive whole one; else 1..n.
        public int LineNumber(int index) =>
            numbers.All(n => n > 0 && n <= int.MaxValue && n % 1 == 0) && numbers.Distinct().Count() == numbers.Count ? (int)numbers[index]! : index + 1;
    }

    /// <summary>The built value, or what the read lacks for <paramref name="needs"/> (never both). Shared by every standard's adapter.</summary>
    internal static (T? Value, IReadOnlyList<string> Missing) Build<T>(
        ReadResult read, FlowFieldList fields, string? tableKey, IReadOnlyList<string> needs, Func<Access, T> build, bool creditNote = false) where T : class
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
                if (!Present(need, Text(need), creditNote)) missing.Add(Field(need)?.Label ?? Humanise(need));
            }
            else if (rows.Count == 0)
            {
                if (!missing.Contains(NoLines)) missing.Add(NoLines);
            }
            else
            {
                var line = Enumerable.Range(1, rows.Count).FirstOrDefault(n => !Present(need, Cell(rows[n - 1], need), creditNote));
                if (line > 0) missing.Add($"{Column(need)?.Label ?? Humanise(need)} (line {line})");
            }
        }
        if (missing.Count > 0) return (null, missing);

        var numbers = rows.Select(r => FieldValueParser.Number(Cell(r, FieldMeanings.LineNumber))).ToList();
        return (build(new Access(Text, rows, Cell, numbers)), []);
    }

    // Numbers count only with a single reading (an ambiguous "1.500" is missing, never guessed), within the writers' own guards.
    // A credit note may be printed negative (StandardOutputs writes it positive): its quantity need only be non-zero, its price any number.
    private static bool Present(string meaning, string? value, bool creditNote) => value is not null && meaning switch
    {
        FieldMeanings.IssueDate => FieldValueParser.TryDate(value, out _),
        FieldMeanings.Currency => FieldValueParser.IsCurrency(value.ToUpperInvariant()),
        FieldMeanings.LineQuantity => FieldValueParser.Number(value) is { } q && (creditNote ? q != 0 : q > 0),
        FieldMeanings.LineUnitPrice => FieldValueParser.Number(value) is { } p && (creditNote || p >= 0),
        FieldMeanings.TotalNet or FieldMeanings.TotalTax or FieldMeanings.TotalGross or FieldMeanings.LineAmount
            => FieldValueParser.Number(value) is not null,
        FieldMeanings.SenderEndpointId or FieldMeanings.ReceiverEndpointId => ElectronicAddress().IsMatch(value),
        _ => true,
    };

    // A scheme and an id. EM (email) counts as present because XRechnung accepts it; Peppol reports EM through the
    // rule check (CL008) like any other bad scheme, so it is never refused before writing.
    [GeneratedRegex(@"^(\d{4}|EM):\S+$")]
    private static partial Regex ElectronicAddress();

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Humanise(string meaning) => meaning == FieldMeanings.Iban ? "IBAN" : char.ToUpperInvariant(meaning[0]) + meaning[1..].Replace('_', ' ');
}
