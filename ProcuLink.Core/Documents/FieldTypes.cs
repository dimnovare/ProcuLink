namespace ProcuLink.Core.Documents;

public static class FieldTypes
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Money = "money";
    public const string Currency = "currency";
    public const string Date = "date";
    public const string Iban = "iban";
    public const string VatId = "vat_id";
    public const string Email = "email";
    public const string Code = "code";

    public static readonly IReadOnlyList<string> All = [Text, Number, Money, Currency, Date, Iban, VatId, Email, Code];
}
