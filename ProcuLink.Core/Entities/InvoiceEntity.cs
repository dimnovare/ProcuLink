namespace ProcuLink.Core.Entities;

/// <summary>
/// A received supplier invoice, normalised to canonical form.
/// Status flow: "pending_review" → "approved" → "forwarded"
/// </summary>
public class InvoiceEntity
{
    public Guid    Id             { get; set; }
    public Guid    OrganisationId { get; set; }
    public Guid?   SupplierId     { get; set; }
    public Guid?   BuyerId        { get; set; }

    public string  InvoiceNumber  { get; set; } = string.Empty;
    public DateOnly IssueDate     { get; set; }
    public DateOnly? DueDate      { get; set; }
    public string  Currency       { get; set; } = "EUR";
    public string? PaymentTerms   { get; set; }
    public string? BuyerRef       { get; set; }
    public string? SupplierRef    { get; set; }

    public decimal SubTotal       { get; set; }
    public decimal TaxTotal       { get; set; }
    public decimal GrandTotal     { get; set; }

    /// <summary>parsing | pending_review | approved | forwarded | failed</summary>
    public string  Status         { get; set; } = "pending_review";

    public string? SourceFileName { get; set; }
    public string? SourceFileKey  { get; set; }

    public DateTime CreatedAt     { get; set; }
    public DateTime UpdatedAt     { get; set; }

    // Navigation
    public Organisation?           Organisation { get; set; }
    public List<InvoiceLineEntity> Lines        { get; set; } = new();
}
