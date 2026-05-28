namespace ProcuLink.Core.Entities;

public class AsnPackageLineEntity
{
    public Guid    Id             { get; set; }
    public Guid    PackageId      { get; set; }
    public Guid    OrganisationId { get; set; }

    public string? BuyerItemCode    { get; set; }
    public string? SupplierItemCode { get; set; }
    public decimal Quantity         { get; set; }
    public string  UnitCode         { get; set; } = "EA";

    // Navigation
    public AsnPackageEntity? Package { get; set; }
}
