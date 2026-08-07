// ProcuLink.Api/Contracts/OrdersSummaryDto.cs
namespace ProcuLink.Api.Contracts;

/// <summary>
/// Per-status order counts for the authenticated org.
/// Keys are order status strings (e.g. "pending_review", "delivered").
/// </summary>
public record OrdersSummaryDto(
    IReadOnlyDictionary<string, int> ByStatus,
    int                     Total,

    /// <summary>
    /// Practice orders held by this org, which <see cref="ByStatus"/> and <see cref="Total"/>
    /// deliberately EXCLUDE. The orders list returns those rows, so a caller pairing this total
    /// with that list needs to know they exist: <c>Total + SampleTotal</c> is the row count the
    /// unfiltered list reports. Reporting a bare 0 beside a table listing one order is the defect
    /// this field closes.
    /// </summary>
    int                     SampleTotal = 0
);
