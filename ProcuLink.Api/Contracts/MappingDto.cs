namespace ProcuLink.Api.Contracts;

/// <summary>
/// Request to create or update a buyer-to-supplier item code mapping
/// </summary>
public class CreateMappingRequest
{
    public string BuyerItemCode { get; set; } = string.Empty;
    public string SupplierItemCode { get; set; } = string.Empty;
}

/// <summary>
/// Request to resolve missing supplier item codes on a purchase order
/// </summary>
public class ResolveRequest
{
    /// <summary>
    /// If true, save the resolutions as mappings for future auto-apply
    /// </summary>
    public bool SaveMappings { get; set; }

    /// <summary>
    /// List of line resolutions to apply
    /// </summary>
    public List<LineResolution> LineResolutions { get; set; } = new();
}

/// <summary>
/// Resolution for a single line item
/// </summary>
public class LineResolution
{
    public int LineNumber { get; set; }
    public string SupplierItemCode { get; set; } = string.Empty;
}
