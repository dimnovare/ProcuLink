namespace ProcuLink.Core.Entities;

/// <summary>
/// An Advance Shipping Notice (DESADV) received from a supplier.
/// Status flow: "received" → "matched"
/// </summary>
public class AdvanceShippingNoticeEntity
{
    public Guid     Id                    { get; set; }
    public Guid     OrganisationId        { get; set; }
    public Guid?    SupplierId            { get; set; }

    public string   ShipmentId            { get; set; } = string.Empty;
    public DateOnly DespatchDate          { get; set; }
    public DateOnly? EstimatedDeliveryDate { get; set; }
    public string?  BuyerOrderRef         { get; set; }
    public string?  SupplierRef           { get; set; }

    /// <summary>received | matched | failed</summary>
    public string   Status                { get; set; } = "received";

    public string?  SourceFileName        { get; set; }
    public string?  SourceFileKey         { get; set; }

    public DateTime CreatedAt             { get; set; }
    public DateTime UpdatedAt             { get; set; }

    // Navigation
    public Organisation?          Organisation { get; set; }
    public List<AsnPackageEntity> Packages     { get; set; } = new();
}
