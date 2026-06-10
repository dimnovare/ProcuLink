using ProcuLink.Api.Contracts;

namespace ProcuLink.Api.Services;

/// <summary>
/// Group V2 — REPLAY / impact testing. Runs historical orders through a connection revision
/// (typically a DRAFT being evaluated before publish) and returns a per-order DIFF vs. the order's
/// CURRENT result, WITHOUT mutating any state and WITHOUT delivering. All methods are org-scoped.
/// Lives in the Api project (not Core) because its result shape is the Api.Contracts replay DTOs.
/// </summary>
public interface IReplayService
{
    /// <summary>
    /// Replay the given orders (or a recent window for the revision's supplier) through the revision,
    /// returning output / effective-value / validation diffs. Returns null when the connection or
    /// revision is not found in this org. Bounded at <see cref="ReplayService.MaxOrders"/> orders.
    /// </summary>
    Task<ReplayResponse?> ReplayAsync(
        Guid orgId, Guid connectionId, Guid revisionId, ReplayRequest request, CancellationToken ct);
}
