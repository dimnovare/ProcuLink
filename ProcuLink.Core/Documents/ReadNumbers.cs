using System.Globalization;

namespace ProcuLink.Core.Documents;

/// <summary>One number style per document, decided by the values in it that can be read only one way.</summary>
public static class ReadNumbers
{
    /// <summary>Below the review line: a number nothing in its document settles is shown as one to check (it never holds a send).</summary>
    public const double UnsureConfidence = 0.5;

    /// <summary>
    /// "1.500" is one and a half or fifteen hundred. When the document's other numbers prove its decimal mark ("0,04", "1.234,50"),
    /// it is rewritten so it reads only that way ("1500", or "1,5" when the mark is its own); when they prove none, or both, it is marked to check.
    /// </summary>
    public static ReadResult Settle(ReadResult read)
    {
        static bool Numeric(string type) => type is FieldTypes.Number or FieldTypes.Money;
        bool NumericCell(ReadTable t, string key) => t.Columns.Any(c => c.Key == key && Numeric(c.Type));
        var marks = read.Fields.Where(f => Numeric(f.Type)).Select(f => f.Value)
            .Concat(read.Tables.SelectMany(t => t.Rows.SelectMany(r => r.Cells.Where(c => NumericCell(t, c.Key)).Select(c => c.Value.Value))))
            .Select(FieldValueParser.DecimalMarkOf).OfType<char>().Distinct().ToList();
        char? mark = marks is [var only] ? only : null;

        (string? Value, double Confidence) One(string? v, double confidence)
        {
            if (FieldValueParser.NumberCandidates(v).Count < 2) return (v, confidence);
            if (mark is not { } m) return (v, Math.Min(confidence, UnsureConfidence));
            var body = FieldValueParser.NumberBody(v)!;
            var sep = body.First(c => c is '.' or ',');
            FieldValueParser.TryNumber(v, out var asDecimal);
            var plain = asDecimal.ToString("0.##########", CultureInfo.InvariantCulture).Replace('.', m);
            // "1,234" with a decimal comma keeps its digits and gains a zero, so it is not three digits after the mark.
            var settled = sep != m ? body.Replace(sep.ToString(), "") : FieldValueParser.NumberCandidates(plain).Count > 1 ? plain + "0" : plain;
            return (v!.Replace(body, settled), confidence);
        }

        var fields = read.Fields.Select(f => Numeric(f.Type) && One(f.Value, f.Confidence) is var (v, c) ? f with { Value = v, Confidence = c } : f).ToList();
        var tables = read.Tables.Select(t => t with
        {
            Rows = t.Rows.Select(r => new ReadRow(r.Cells.ToDictionary(x => x.Key,
                x => NumericCell(t, x.Key) && One(x.Value.Value, x.Value.Confidence) is var (v, c) ? x.Value with { Value = v, Confidence = c } : x.Value))).ToList(),
        }).ToList();
        return new ReadResult(read.KindLabel, fields, tables, read.Truncated);
    }
}
