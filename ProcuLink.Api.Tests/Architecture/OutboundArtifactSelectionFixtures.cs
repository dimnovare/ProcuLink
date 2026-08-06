using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// Compiled specimens for <see cref="OutboundArtifactSelectionIlScanner"/>.
///
/// <para><b>None of these ever runs.</b> They exist to be COMPILED — the guard's fixture tests read
/// their IL, never their behaviour, so the specimens are produced by the same C# compiler that
/// produces the production code they stand in for. Nothing here touches a database, and the
/// <c>IQueryable</c> parameters are never enumerated.</para>
///
/// <para><b>Why fixtures at all, when the guard also runs against the real tree.</b> The two
/// answer different questions and the progress log has a named trap for confusing them: a test that
/// exercises a guard against a fixture proves the guard's PLUMBING, not its COVERAGE of the repo —
/// and a fixture tree in which the scanned code does not exist passes identically whether the guard
/// works or is completely dead. So this file proves the scanner can tell the shapes apart, and
/// <c>DeliverableArtifactSelectionIsRoutedTests</c> separately proves it finds the real sites in
/// the real assemblies. Neither is sufficient alone.</para>
///
/// <para>The first specimen is the original defect, reproduced character for character from the
/// seven sites WP-35 rewrote. If the guard cannot fail on that, it does not work.</para>
/// </summary>
[ExcludeFromCodeCoverage]
internal static class OutboundArtifactSelectionFixtures
{
    // ── Specimens that MUST be reported as unrouted selections ────────────────

    /// <summary>
    /// The defect, verbatim. Three lines, repeated at seven production sites before WP-35, correct
    /// at every one of them until re-processing began appending an artifact that is newer than the
    /// deliverable one by construction.
    /// </summary>
    internal static OutboundArtifact? TheOriginalDefect(PurchaseOrderEntity order)
    {
        var artifact = order.OutboundArtifacts
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefault();

        return artifact;
    }

    /// <summary>
    /// The same defect on the query side, and <c>async</c> — so the specimen also exercises the
    /// state-machine unwrap. Read the stub the compiler leaves under this name and there are no
    /// calls in it at all.
    /// </summary>
    internal static async Task<OutboundArtifact?> TheOriginalDefectOnTheQuerySide(
        IQueryable<OutboundArtifact> artifacts, Guid orderId, CancellationToken ct) =>
        await artifacts
            .Where(a => a.OrderId == orderId)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The defect surrounded by every source-text signal that a regex would read as compliance: the
    /// rule named in a comment, the rule named in a commented-out call, and the rule's own type name
    /// present as a string literal. The code underneath still selects the newest artifact raw.
    ///
    /// <para>This is the specimen that pins WHY the guard reads IL. A regex over source cannot tell
    /// code from a comment — the log records a mounting assertion that matched inside
    /// <c>{/* */}</c> with the real element deleted, and a gate that re-anchored onto an explanatory
    /// comment and went silently blind. Both stayed green with the defect fully restored.</para>
    /// </summary>
    internal static OutboundArtifact? TheDefectWithRoutingOnlyInCommentsAndStrings(PurchaseOrderEntity order)
    {
        // Routed through OutboundArtifactSelection.NewestDeliverable — this claim is false.
        // var artifact = OutboundArtifactSelection.NewestDeliverable(order.OutboundArtifacts);
        var note = "OutboundArtifactSelection.NewestDeliverable";
        _ = note;

        return order.OutboundArtifacts
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefault();
    }

    /// <summary>
    /// The projected form: order by recency, then take the id. The element type at the reducer is
    /// <c>Guid?</c>, not <c>OutboundArtifact</c> — so a guard that keyed on the REDUCER would see
    /// nothing here. Two production sites are written exactly this way
    /// (<c>StrandedReadyOrderDetectionService</c> and <c>ReplayService</c>), which is why the
    /// scanner keys on the ORDERING instead.
    /// </summary>
    internal static Guid? TheDefectProjectedToAnId(PurchaseOrderEntity order) =>
        order.OutboundArtifacts
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefault();

    /// <summary>
    /// The aggregate form: no ordering at all, just the newest timestamp. This is the shape of the
    /// stranded-ready sweep's candidate-query cutoff, where an unfiltered <c>Max</c> moves the
    /// cutoff past the last genuine delivery attempt and makes an already-delivered order look
    /// stranded — on a timer, with no human in the loop.
    /// </summary>
    internal static DateTime TheDefectAsAnAggregate(PurchaseOrderEntity order) =>
        order.OutboundArtifacts.Max(a => a.CreatedAt);

    /// <summary>
    /// The selection lifted into a lambda the compiler moves to its own method. Reported under the
    /// name a human can find, not under <c>&lt;&gt;c.&lt;…&gt;b__0</c>.
    /// </summary>
    internal static List<OutboundArtifact?> TheDefectInsideALambda(IEnumerable<PurchaseOrderEntity> orders) =>
        orders
            .Select(o => o.OutboundArtifacts.OrderByDescending(a => a.CreatedAt).FirstOrDefault())
            .ToList();

    /// <summary>
    /// Listing every artifact, newest first, for display. Deliberately reported as a selection: a
    /// recency ordering is a recency ordering, and the scanner does not try to guess intent from
    /// what happens next. Production has exactly one site of this shape — the order-detail DTO —
    /// and it is answered by a written allowlist entry rather than by a heuristic, so the exemption
    /// is visible in a diff.
    /// </summary>
    internal static List<Guid> ListsEveryArtifactNewestFirst(PurchaseOrderEntity order) =>
        order.OutboundArtifacts
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => a.Id)
            .ToList();

    /// <summary>
    /// Two picks, one consultation of the rule — the shape that defeated the first version of this
    /// guard. <c>StrandedReadyOrderDetectionService.RunAsync</c> is written exactly like this
    /// (a <c>Max(CreatedAt)</c> cutoff and then the artifact it dispatches), and while the guard
    /// asked only "does the method mention the rule anywhere", deleting the discriminator from
    /// EITHER pick left the other one's evidence behind and the mutant survived.
    /// </summary>
    internal static (DateTime Cutoff, OutboundArtifact? Chosen) SelectsTwiceButRoutesOnce(
        IQueryable<OutboundArtifact> artifacts, Guid orderId)
    {
        var cutoff = artifacts
            .Where(a => a.OrderId == orderId)
            .Max(a => a.CreatedAt);

        var chosen = artifacts
            .Where(a => a.OrderId == orderId)
            .Deliverable()
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefault();

        return (cutoff, chosen);
    }

    // ── Specimens that MUST NOT be reported as violations ─────────────────────

    /// <summary>The same two picks, each with its own consultation. This is the production shape.</summary>
    internal static (DateTime? Cutoff, OutboundArtifact? Chosen) SelectsTwiceAndRoutesTwice(
        IQueryable<OutboundArtifact> artifacts, Guid orderId)
    {
        var cutoff = artifacts
            .Where(a => a.OrderId == orderId
                     && !a.FileKey.Contains(OutboundArtifactSelection.ReprocessKeyMarker))
            .Max(a => (DateTime?)a.CreatedAt);

        var chosen = artifacts
            .Where(a => a.OrderId == orderId)
            .Deliverable()
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefault();

        return (cutoff, chosen);
    }

    /// <summary>
    /// One pick written with a tie-breaker. Two ordering operators, but <c>ThenBy</c> cannot start a
    /// pick, so the floor stays at one and a single consultation clears it.
    /// </summary>
    internal static OutboundArtifact? OrdersOnceWithASecondaryThenBy(PurchaseOrderEntity order) =>
        order.OutboundArtifacts
            .Where(a => !OutboundArtifactSelection.IsReprocessed(a))
            .OrderByDescending(a => a.CreatedAt)
            .ThenBy(a => a.Id)
            .FirstOrDefault();

    /// <summary>The in-memory rewrite: the whole question delegated to the rule.</summary>
    internal static OutboundArtifact? RoutedThroughNewestDeliverable(PurchaseOrderEntity order) =>
        OutboundArtifactSelection.NewestDeliverable(order.OutboundArtifacts);

    /// <summary>
    /// The query-side rewrite: <c>Deliverable()</c> narrows, then the ordering runs — so the
    /// selection IS present in this body and the routing has to be proved, not assumed.
    /// </summary>
    internal static async Task<OutboundArtifact?> RoutedThroughTheDeliverableExtension(
        IQueryable<OutboundArtifact> artifacts, Guid orderId, CancellationToken ct) =>
        await artifacts
            .Where(a => a.OrderId == orderId)
            .Deliverable()
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The expression-tree rewrite, where the marker is compared inside a <c>Where</c> that EF
    /// translates to SQL. C# inlines the <c>const</c>, so this body holds a bare string literal and
    /// no reference to the type that declares it — which is exactly why the scanner accepts the
    /// literal, and why it reads that literal off the production constant instead of hard-coding it.
    /// </summary>
    internal static DateTime? RoutedThroughTheInlinedMarkerConstant(
        IQueryable<OutboundArtifact> artifacts, Guid orderId) =>
        artifacts
            .Where(a => a.OrderId == orderId
                     && !a.FileKey.Contains(OutboundArtifactSelection.ReprocessKeyMarker))
            .Max(a => (DateTime?)a.CreatedAt);

    /// <summary>
    /// Selection in a lifted lambda, routing evidence in the enclosing body. Both halves have to be
    /// attributed to the same method or this reads as an unrouted violation.
    /// </summary>
    internal static OutboundArtifact? RoutesFromTheBodyWhileSelectingInALambda(
        IEnumerable<PurchaseOrderEntity> orders)
    {
        var newestPerOrder = orders
            .Select(o => o.OutboundArtifacts.OrderByDescending(a => a.CreatedAt).First())
            .ToList();

        return OutboundArtifactSelection.NewestDeliverable(newestPerOrder);
    }

    // ── Specimens that MUST NOT be reported as selections at all ──────────────

    /// <summary>
    /// Lookup by identity. The caller already knows which artifact it wants, so there is no recency
    /// question to get wrong — this is the shape of the per-artifact download and of the delivery
    /// dispatcher's own re-read. If the detector ever broadened to "touches
    /// <c>OutboundArtifacts</c>", this specimen is what would catch it.
    /// </summary>
    internal static async Task<OutboundArtifact?> LooksUpOneArtifactByIdentity(
        IQueryable<OutboundArtifact> artifacts, Guid artifactId, CancellationToken ct) =>
        await artifacts
            .Where(a => a.Id == artifactId)
            .FirstOrDefaultAsync(ct);

    /// <summary>Bulk work over every artifact — a retention sweep or an erasure. Nothing is picked.</summary>
    internal static List<string> TakesEveryArtifactsKey(IEnumerable<OutboundArtifact> artifacts) =>
        artifacts.Where(a => a.BlobPurgedAt == null).Select(a => a.FileKey).ToList();

    /// <summary>
    /// A recency ordering over a DIFFERENT entity. Present because the generic-argument test is the
    /// only thing standing between this guard and reporting most of the codebase:
    /// <c>OrderByDescending</c> is one of the commonest calls there is.
    /// </summary>
    internal static DeliveryAttempt? OrdersSomethingElseByRecency(IEnumerable<DeliveryAttempt> attempts) =>
        attempts.OrderByDescending(a => a.AttemptedAt).FirstOrDefault();
}
