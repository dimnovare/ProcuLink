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
    DateTime CreatedAt,

    /// <summary>
    /// True when this row is the onboarding practice order (<c>PurchaseOrderEntity.IsSample</c>).
    /// The list deliberately RETURNS practice orders — the user is sent to one to rehearse the
    /// review flow, so hiding it would strand work they were told to do — but every count the
    /// product reports (billing quota, dashboard KPIs, onboarding milestones) EXCLUDES them.
    /// Without this flag a caller can see the row but cannot tell it apart from real work, which
    /// is how "Received 0" came to sit beside a table listing one order.
    /// </summary>
    bool IsSample = false
);
