// ProcuLink.Api/Contracts/OrdersSummaryDto.cs
namespace ProcuLink.Api.Contracts;

/// <summary>
/// Per-status order counts for the authenticated org.
/// Keys are order status strings (e.g. "pending_review", "delivered").
/// </summary>
public record OrdersSummaryDto(
    Dictionary<string, int> ByStatus,
    int                     Total
);
