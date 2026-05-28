namespace ProcuLink.Core.Entities;

public class InvoiceLineEntity
{
    public Guid    Id             { get; set; }
    public Guid    InvoiceId      { get; set; }
    public Guid    OrganisationId { get; set; }

    public int     LineNumber     { get; set; }
    public string  Description    { get; set; } = string.Empty;
    public decimal Quantity       { get; set; }
    public string  UnitCode       { get; set; } = "EA";
    public decimal UnitPrice      { get; set; }
    public decimal TaxRate        { get; set; }
    public decimal LineTotal      { get; set; }

    public string? BuyerItemCode    { get; set; }
    public string? SupplierItemCode { get; set; }

    // Navigation
    public InvoiceEntity? Invoice { get; set; }
}
