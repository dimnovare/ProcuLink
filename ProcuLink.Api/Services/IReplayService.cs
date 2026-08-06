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

    /// <summary>
    /// WP-35 — act on a replay result: re-process ONE historical order under the revision and
    /// PERSIST the output as a new artifact, appended alongside everything the order already holds.
    ///
    /// <para>This is the only method on this service that writes. It writes exactly one artifact
    /// row plus one audit event, and it changes nothing else — not the order's status, not its
    /// pin, and not any existing artifact. <b>It never delivers</b>: producing the output an
    /// operator asked to see is not a decision to send it, and the artifact is written outside the
    /// deliverable namespace so no send path can pick it up (see
    /// <see cref="ProcuLink.Core.Services.OutboundArtifactSelection"/>).</para>
    ///
    /// <para>Idempotent by construction: the artifact's id is derived from the order, the revision
    /// and the rendered bytes, so a repeat — a double-click, a retried request, or a Hangfire
    /// refetch — resolves to the same row rather than a second one.</para>
    /// </summary>
    Task<ReprocessOutcome> ReprocessAsync(
        Guid orgId, Guid connectionId, Guid revisionId, Guid orderId, string? actor, CancellationToken ct);
}
