namespace ProcuLink.Core.Services;

/// <summary>
/// Lightweight projection used for list views.
/// Does not include line-level data.
/// </summary>
public record PurchaseOrderSummary(
    Guid Id,
    string PoNumber,
    string SupplierName,
    string? BuyerName,
    DateOnly OrderDate,
    string Status,
    int LineCount,
    int UnresolvedCount,
    decimal TotalValue,
    string Currency,
    string? SourceFormat,
    DateTime CreatedAt
);
