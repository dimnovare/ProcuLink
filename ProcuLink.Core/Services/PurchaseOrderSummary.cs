namespace ProcuLink.Core.Services;

/// <summary>
/// Lightweight projection used for list views.
/// Does not include line-level data.
/// </summary>
public record PurchaseOrderSummary(
    Guid Id,
    string PoNumber,
    string SupplierName,
    DateOnly OrderDate,
    string Status,
    int LineCount,
    int UnresolvedCount,
    DateTime CreatedAt
);
