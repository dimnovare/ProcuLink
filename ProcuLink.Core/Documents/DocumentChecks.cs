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

        foreach (var field in read.Fields.Where(f => !string.IsNullOrWhiteSpace(f.Value) && !ValueIsValid(f.Type, f.Value)))
            Fail(TypeInvalid, field.Key, $"{field.Label} is not {Describe(field.Type)} we can read.");

        if (duplicate) Fail(Duplicate, null, "A document with this number was already received.");
        if (read.Truncated) Fail(TextTruncated, null, "The document was too long to read in full.");
        return failed;
    }

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
