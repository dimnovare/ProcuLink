namespace ProcuLink.Transform.Parsing;

public sealed record ParsedInvoice(
    string InvoiceNumber,
    DateOnly IssueDate,
    DateOnly? DueDate,
    string Currency,
    string? BuyerRef,
    string? SupplierRef,
    string? PaymentTerms,
    decimal SubTotal,
    decimal TaxTotal,
    decimal GrandTotal,
    IReadOnlyList<ParsedInvoiceLine> Lines);

public sealed record ParsedInvoiceLine(
    int LineNumber,
    string Description,
    decimal Quantity,
    string UnitCode,
    decimal UnitPrice,
    decimal TaxRate,
    decimal LineTotal,
    string? BuyerItemCode,
    string? SupplierItemCode);
