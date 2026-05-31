namespace ProcuLink.Core.Entities;

/// <summary>
/// A versioned definition of what a supplier will accept on a PO.
/// Multiple versions per (org, supplier); exactly one is Active at a time.
/// </summary>
public class SupplierAcceptanceProfile
{
    public Guid     Id            { get; set; }
    public Guid     OrgId         { get; set; }
    public Guid     SupplierId    { get; set; }
    public int      VersionNo     { get; set; }
    /// <summary>draft | active | archived</summary>
    public string   Status        { get; set; } = "draft";
    public string?  Protocol      { get; set; }
    public string?  OutputFormat  { get; set; }
    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo   { get; set; }
    public string?  CreatedBy     { get; set; }
    public DateTime CreatedAt     { get; set; }

    public List<SupplierAcceptanceRule> Rules { get; set; } = new();
    public Organisation Organisation { get; set; } = null!;
}
