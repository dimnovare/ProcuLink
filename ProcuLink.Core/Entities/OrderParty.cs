namespace ProcuLink.Core.Entities;

/// <summary>
/// Phase 1 lossless capture: a named party (address + tax/EDI id) on a purchase order.
/// Child of <see cref="PurchaseOrderEntity"/>; one row per ship-to / bill-to / remit-to.
/// Table <c>order_parties</c> (migration <c>AddLosslessCanonicalCapture</c>). All value
/// columns nullable — a document may carry only some.
/// </summary>
public class OrderParty
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid OrgId { get; set; }
    /// <summary>"shipTo" | "billTo" | "remitTo" | "buyer" | "supplier".</summary>
    public string Role { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Street { get; set; }
    public string? City { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? Vat { get; set; }
    public string? RegNr { get; set; }
    public string? EdiCode { get; set; }
    public string? Reference { get; set; }
    public string? ContactName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }

    public PurchaseOrderEntity Order { get; set; } = null!;
}
