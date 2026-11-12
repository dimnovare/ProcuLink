namespace ProcuLink.Core.Documents;

public sealed record DocumentCheck(string Code, bool Passed, string? FieldKey, string Message);

/// <summary>What a read document must pass before it can go out without a person looking at it.</summary>
public static class DocumentChecks
{
    /// <summary>A required field below this confidence needs a person. Boundary of the red confidence band.</summary>
    public const double ReviewConfidence = 0.75;

    public const string FieldsUnconfirmed = "fields_unconfirmed";
    public const string RequiredMissing = "required_missing";
    public const string TypeInvalid = "type_invalid";
    public const string LowConfidence = "low_confidence";
    public const string NotOnPage = "not_on_page";
    public const string Duplicate = "duplicate";
    public const string TextTruncated = "text_truncated";
    public const string ArithmeticMismatch = "arithmetic_mismatch";

    /// <summary>Failed checks only; an empty list means the document passed.</summary>
    public static IReadOnlyList<DocumentCheck> Run(ReadResult read, FlowFieldList fields, bool fieldsConfirmed, bool duplicate)
    {
        var failed = new List<DocumentCheck>();
        void Fail(string code, string? key, string message) => failed.Add(new DocumentCheck(code, false, key, message));

        if (!fieldsConfirmed) Fail(FieldsUnconfirmed, null, "The fields to read have not been confirmed yet.");

        foreach (var flowField in fields.Fields.Where(f => f.Required))
        {
            var field = read.Fields.FirstOrDefault(f => f.Key == flowField.Key);
            if (field is { NotOnPage: true })
                Fail(NotOnPage, flowField.Key, $"{flowField.Label} is not on the page.");
            else if (field is null || string.IsNullOrWhiteSpace(field.Value))
                Fail(RequiredMissing, flowField.Key, $"{flowField.Label} is required but was not found.");
            else if (field.Confidence < ReviewConfidence)
                Fail(LowConfidence, flowField.Key, $"{flowField.Label} was hard to read. Please check it.");
        }

        // The flow's type decides, and only for fields the flow lists: the AI's guess about a new field holds nothing up.
        foreach (var field in read.Fields.Where(f => !string.IsNullOrWhiteSpace(f.Value)))
        {
            var type = fields.Fields.FirstOrDefault(f => f.Key == field.Key)?.Type;
            if (type is not null && !ValueIsValid(type, field.Value!))
                Fail(TypeInvalid, field.Key, $"{field.Label} is not {Describe(type)} we can read.");
        }

        // Values come from documents: an amount too large for decimal cannot be checked, so a person looks rather than the read aborting.
        try { Arithmetic(read, fields, Fail); }
        catch (OverflowException)
        {
            Fail(ArithmeticMismatch, read.Fields.FirstOrDefault(f => f.Meaning == FieldMeanings.TotalGross)?.Key, "An amount is too large to check.");
        }

        if (duplicate) Fail(Duplicate, null, "A document with this number was already received.");
        if (read.Truncated) Fail(TextTruncated, null, "The document was too long to read in full.");
        return failed;
    }

    // Only rules whose every meaning is on the read and parses fire; a flow without money meanings never fails here.
    // Once the flow lists fields its meanings decide (a listed key's, null included; unlisted extras are ignored);
    // before that the read's own meanings stand.
    private static void Arithmetic(ReadResult read, FlowFieldList flow, Action<string, string?, string> fail)
    {
        string? FieldMeaning(ReadField f) => flow.Fields.Count == 0 ? f.Meaning : flow.Fields.FirstOrDefault(x => x.Key == f.Key)?.Meaning;
        ReadField? Find(string meaning) => read.Fields.FirstOrDefault(f => FieldMeaning(f) == meaning);
        // An ambiguous reading ("1.000": one or a thousand) cannot be checked, so the rule skips.
        static decimal? Num(string? s) => FieldValueParser.NumberCandidates(s) is [var only] ? only : null;

        var dp = DecimalPlaces(Find(FieldMeanings.Currency)?.Value);
        var unit = (decimal)Math.Pow(10, -dp);
        decimal R(decimal v) => Math.Round(v, dp, MidpointRounding.AwayFromZero);
        bool Header(string meaning, out decimal v, out string key)
        {
            var f = Find(meaning);
            key = f?.Key ?? "";
            var n = f is null ? null : Num(f.Value);
            v = n ?? 0;
            return n is not null;
        }

        var haveNet = Header(FieldMeanings.TotalNet, out var net, out var netKey);
        if (haveNet && Header(FieldMeanings.TotalTax, out var tax, out _) && Header(FieldMeanings.TotalGross, out var gross, out var grossKey)
            && R(net) + R(tax) != R(gross))
            fail(ArithmeticMismatch, grossKey, "Net plus tax doesn't equal the total.");

        string? ColumnMeaning(ReadTable t, ReadColumn c) => flow.Tables.Count == 0
            ? c.Meaning
            : flow.Tables.FirstOrDefault(x => x.Key == t.Key)?.Columns.FirstOrDefault(x => x.Key == c.Key)?.Meaning;
        var table = read.Tables.FirstOrDefault(t => t.Columns.Any(c => ColumnMeaning(t, c) == FieldMeanings.LineAmount));
        if (table is null || table.Rows.Count == 0) return;
        string? Col(string meaning) => table.Columns.FirstOrDefault(c => ColumnMeaning(table, c) == meaning)?.Key;
        decimal? Cell(ReadRow row, string? key) =>
            key is not null && row.Cells.TryGetValue(key, out var c) ? Num(c.Value) : null;

        var amountKey = Col(FieldMeanings.LineAmount);
        var amounts = table.Rows.Select(r => Cell(r, amountKey)).ToList();
        if (haveNet && amounts.All(a => a is not null) && Math.Abs(amounts.Sum(a => R(a!.Value)) - R(net)) > unit)
            fail(ArithmeticMismatch, table.Key, "The lines don't add up to the net total.");

        var qtyKey = Col(FieldMeanings.LineQuantity);
        var priceKey = Col(FieldMeanings.LineUnitPrice);
        for (var i = 0; i < table.Rows.Count; i++)
        {
            if (Cell(table.Rows[i], qtyKey) is { } q && Cell(table.Rows[i], priceKey) is { } p && amounts[i] is { } a
                && Math.Abs(R(q * p) - R(a)) > unit)
            {
                fail(ArithmeticMismatch, table.Key, $"Line {i + 1}: quantity × price doesn't equal the amount.");
                break;
            }
        }
    }

    // ISO 4217 minor units; absent or unknown currency means 2.
    private static int DecimalPlaces(string? currency) => currency?.Trim().ToUpperInvariant() switch
    {
        "JPY" or "KRW" or "CLP" or "ISK" or "VND" => 0,
        "BHD" or "KWD" or "OMR" or "JOD" or "TND" or "IQD" or "LYD" => 3,
        _ => 2,
    };

    /// <summary>The document number, trimmed, upper-cased and without whitespace; null when there is none.</summary>
    public static string? DedupKeyOf(ReadResult read)
    {
        var value = read.Fields.FirstOrDefault(f => f.Meaning == FieldMeanings.DocumentNumber)?.Value;
        return string.IsNullOrWhiteSpace(value)
            ? null
            : string.Concat(value.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
    }

    public static string StatusFor(IReadOnlyList<DocumentCheck> failed) =>
        failed.Count > 0 ? DocumentStatus.NeedsReview : DocumentStatus.Ready;

    // Types without a parser (text, code) always pass.
    private static bool ValueIsValid(string type, string v) => type switch
    {
        FieldTypes.Number or FieldTypes.Money => FieldValueParser.TryNumber(v, out _),
        FieldTypes.Date => FieldValueParser.TryDate(v, out _),
        FieldTypes.Iban => FieldValueParser.IsIban(v.Replace(" ", "")),
        FieldTypes.VatId => FieldValueParser.IsVatId(v),
        FieldTypes.Currency => FieldValueParser.IsCurrency(v.Trim()),
        FieldTypes.Email => FieldValueParser.IsEmail(v),
        _ => true,
    };

    private static string Describe(string type) => type switch
    {
        FieldTypes.Number or FieldTypes.Money => "a number",
        FieldTypes.Date => "a date",
        FieldTypes.Iban => "an IBAN",
        FieldTypes.VatId => "a VAT number",
        FieldTypes.Currency => "a currency code",
        FieldTypes.Email => "an email address",
        _ => "a value",
    };
}
