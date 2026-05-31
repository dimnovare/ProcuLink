namespace ProcuLink.Core.Entities;

/// <summary>
/// Immutable record of a supplier code change on a mapping row.
/// Written whenever UpsertAsync overwrites an existing SupplierItemCode.
/// </summary>
public class MappingCorrection
{
    public Guid     Id                   { get; set; }
    public Guid     OrgId                { get; set; }
    public Guid     MappingId            { get; set; }
    public string   OldSupplierItemCode  { get; set; } = string.Empty;
    public string   NewSupplierItemCode  { get; set; } = string.Empty;
    /// <summary>manual | ai_accepted | imported</summary>
    public string   Source               { get; set; } = "manual";
    public DateTime CorrectedAt          { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
    public ItemMapping  Mapping       { get; set; } = null!;
}
