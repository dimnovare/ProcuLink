using ProcuLink.Core.Canonical;

namespace ProcuLink.Api.Contracts;

/// <summary>
/// Summary view of a purchase order for list endpoints
/// </summary>
public class PurchaseOrderSummaryDto
{
    public Guid Id { get; set; }
    public string PoNumber { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public string BuyerName { get; set; } = string.Empty;
    public DateTime OrderDate { get; set; }
    public AutomationStatus AutomationStatus { get; set; }
    public DateTime CreatedAt { get; set; }
    public int LineCount { get; set; }
    public decimal TotalValue { get; set; }
    public string Currency { get; set; } = string.Empty;
}
