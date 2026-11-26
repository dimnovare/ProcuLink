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
    public const string NewSender = "new_sender";
    public const string TextTruncated = "text_truncated";
    public const string ArithmeticMismatch = "arithmetic_mismatch";
    public const string OutputRules = "output_rules";
    public const string CustomCheck = "custom_check";

    /// <summary>Failed checks only; an empty list means the document passed.</summary>
    public static IReadOnlyList<DocumentCheck> Run(ReadResult read, FlowFieldList fields, bool fieldsConfirmed, bool duplicate, bool newSender = false, string? unconfirmedFrom = null)
    {
        var failed = new List<DocumentCheck>();
        void Fail(string code, string? key, string message) => failed.Add(new DocumentCheck(code, false, key, message));

        if (!fieldsConfirmed) Fail(FieldsUnconfirmed, null, "The fields to read have not been confirmed yet.");

        foreach (var flowField in fields.Fields.Where(f => f.Required))
        {
            var field = read.Fields.FirstOrDefault(f => f.Key == flowField.Key);
            if (field is { NotOnPage: true })
                Fail(NotOnPage, flowField.Key, $"{flowField.Label} isn't in the page's text. Check it against the page.");
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
        // The same hold as a new address, so it blocks an automatic send and alerts the same way.
        if (unconfirmedFrom is not null) Fail(NewSender, null, $"We couldn't confirm this email really came from {unconfirmedFrom}. Check it before sending.");
        else if (newSender) Fail(NewSender, null, "First document from this email address. Check it before sending.");
        if (read.Truncated) Fail(TextTruncated, null, "The document was too long to read in full.");
        return failed;
    }

    // Only rules whose every meaning is on the read and parses fire; a flow without money meanings never fails here.
    // Meanings come from FieldMeanings.Of; an ambiguous number (FieldValueParser.Number is null) cannot be checked, so the rule skips.
    private static void Arithmetic(ReadResult read, FlowFieldList flow, Action<string, string?, string> fail)
    {
        ReadField? Find(string meaning) => read.Fields.FirstOrDefault(f => FieldMeanings.Of(f, flow) == meaning);

        var dp = DecimalPlaces(Find(FieldMeanings.Currency)?.Value);
        var unit = (decimal)Math.Pow(10, -dp);
        decimal R(decimal v) => Math.Round(v, dp, MidpointRounding.AwayFromZero);
        bool Header(string meaning, out decimal v, out string key)
        {
            var f = Find(meaning);
            key = f?.Key ?? "";
            var n = f is null ? null : FieldValueParser.Number(f.Value);
            v = n ?? 0;
            return n is not null;
        }

        var haveNet = Header(FieldMeanings.TotalNet, out var net, out var netKey);
        var haveGross = Header(FieldMeanings.TotalGross, out var gross, out var grossKey);
        if (haveNet && haveGross && Header(FieldMeanings.TotalTax, out var tax, out _) && R(net) + R(tax) != R(gross))
            fail(ArithmeticMismatch, grossKey, "Net plus tax doesn't equal the total.");

        var table = read.Tables.FirstOrDefault(t => t.Columns.Any(c => FieldMeanings.Of(t, c, flow) == FieldMeanings.LineAmount));
        if (table is null || table.Rows.Count == 0) return;
        string? Col(string meaning) => table.Columns.FirstOrDefault(c => FieldMeanings.Of(table, c, flow) == meaning)?.Key;
        decimal? Cell(ReadRow row, string? key) =>
            key is not null && row.Cells.TryGetValue(key, out var c) ? FieldValueParser.Number(c.Value) : null;

        var amountKey = Col(FieldMeanings.LineAmount);
        var amounts = table.Rows.Select(r => Cell(r, amountKey)).ToList();
        // Lines priced with tax included (receipts) add up to the gross total instead; either one passes.
        var lines = amounts.All(a => a is not null) ? amounts.Sum(a => R(a!.Value)) : (decimal?)null;
        // A net worked out from the other totals (not on the page) is not the document's own figure to check the lines against.
        if (haveNet && Find(FieldMeanings.TotalNet)!.NotOnPage is false && lines is { } sum && Math.Abs(sum - R(net)) > unit && !(haveGross && Math.Abs(sum - R(gross)) <= unit))
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

    /// <summary>Starts the dedup key of a document without a number, which is built from its file hash.</summary>
    public const string FileHashKeyPrefix = "sha256:";

    /// <summary>A document number as it is compared: whitespace removed, upper-cased.</summary>
    public static string NormalizeNumber(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();

    /// <summary>The document number as it was read, trimmed; null when there is none.</summary>
    public static string? NumberOf(ReadResult read)
    {
        var value = read.Fields.FirstOrDefault(f => f.Meaning == FieldMeanings.DocumentNumber)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>The document number, trimmed, upper-cased and without whitespace; null when there is none.</summary>
    public static string? DedupKeyOf(ReadResult read) => NumberOf(read) is { } number ? NormalizeNumber(number) : null;

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
