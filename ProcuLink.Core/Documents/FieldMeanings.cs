namespace ProcuLink.Core.Documents;

public static class FieldMeanings
{
    public const string DocumentNumber = "document_number";
    public const string IssueDate = "issue_date";
    public const string DueDate = "due_date";
    public const string SenderName = "sender_name";
    public const string SenderVatId = "sender_vat_id";
    public const string ReceiverName = "receiver_name";
    public const string Currency = "currency";
    public const string TotalNet = "total_net";
    public const string TotalTax = "total_tax";
    public const string TotalGross = "total_gross";
    public const string Iban = "iban";
    public const string PaymentReference = "payment_reference";

    public const string LineDescription = "line_description";
    public const string LineQuantity = "line_quantity";
    public const string LineUnit = "line_unit";
    public const string LineUnitPrice = "line_unit_price";
    public const string LineAmount = "line_amount";
    public const string LineTaxRate = "line_tax_rate";
    public const string LineItemCode = "line_item_code";

    public static readonly IReadOnlyList<string> All =
    [
        DocumentNumber, IssueDate, DueDate, SenderName, SenderVatId, ReceiverName, Currency,
        TotalNet, TotalTax, TotalGross, Iban, PaymentReference,
        LineDescription, LineQuantity, LineUnit, LineUnitPrice, LineAmount, LineTaxRate, LineItemCode,
    ];
}
