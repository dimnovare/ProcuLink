namespace ProcuLink.Core.Canonical;

/// <summary>
/// Supplier profile defining validation rules and capabilities
/// </summary>
public class SupplierProfile
{
    public string SupplierName { get; set; } = string.Empty;
    public bool RequiresSupplierItemCode { get; set; }
    public List<string> RequiredFields { get; set; } = new();
    public bool SupportsPartialAutomation { get; set; }
    public List<string> AcceptedFormats { get; set; } = new(); // e.g. ["XML","CSV"]
}
