using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

/// <summary>
/// WP-35 — the single owner of the question "which of an order's artifacts is the one it would
/// deliver?", and of the storage namespace that answers it.
///
/// <para><b>Why this type exists.</b> Before WP-35 every artifact an order held was a deliverable
/// one, so five separate paths could each answer the question with the same three lines —
/// <c>OrderByDescending(a =&gt; a.CreatedAt).FirstOrDefault()</c> — and be right. Re-processing breaks
/// that assumption: it appends an artifact the operator asked to SEE, not to send, and that
/// artifact is by definition the newest. Left alone, the ops requeue
/// (<c>OpsController</c>), redeliver and retry-delivery (<c>OrdersController</c>), the
/// stranded-ready sweep (<c>StrandedReadyOrderDetectionService</c>) and the transform's
/// already-done branch (<c>OrderTransformService</c>) would each have silently re-pointed at a
/// preview. The stranded sweep is the sharpest of the five because no human is in its loop: it
/// re-drives delivery on a timer.</para>
///
/// <para><b>Why a storage namespace and not a flag.</b> A re-processed artifact genuinely lives
/// somewhere else — it is written under <see cref="ReprocessKeySegment"/> rather than
/// <c>artifacts</c> — so the discriminator is a fact about the object, readable by anyone listing
/// the bucket, and needs no column. It also survives the degenerate case a revision-id comparison
/// cannot: re-processing an order against the very revision it is already pinned to would produce
/// an artifact whose provenance is indistinguishable from its original's.</para>
///
/// <para><b>What this does NOT do.</b> It does not hide re-processed artifacts from the record.
/// The passport, the order DTO's artifact list, and the per-artifact download all continue to see
/// every row — being able to inspect the re-processed output is the point of producing it. Only
/// the "what would this order send?" question is narrowed.</para>
/// </summary>
public static class OutboundArtifactSelection
{
    /// <summary>
    /// The storage key segment that marks an artifact as a re-processed preview rather than the
    /// order's deliverable output. Originals are written under <c>artifacts</c> by
    /// <c>OrderTransformService</c>.
    /// </summary>
    public const string ReprocessKeySegment = "reprocessed";

    /// <summary>
    /// The delimited form actually matched, so a supplier or order id that merely happened to
    /// contain the word cannot be mistaken for the segment.
    /// </summary>
    public const string ReprocessKeyMarker = "/" + ReprocessKeySegment + "/";

    /// <summary>
    /// The key a re-processed artifact is stored under. Mirrors the transform's
    /// <c>{org}/{order}/artifacts/{artifactId}{ext}</c> shape with the segment swapped, so the
    /// blob-retention sweep and the erasure path — which key off nothing but the string — keep
    /// working unchanged.
    /// </summary>
    public static string BuildReprocessKey(Guid orgId, Guid orderId, Guid artifactId, string extension) =>
        $"{orgId}/{orderId}/{ReprocessKeySegment}/{artifactId}{extension}";

    /// <summary>True when this key names a re-processed preview.</summary>
    public static bool IsReprocessedKey(string? fileKey) =>
        fileKey is not null && fileKey.Contains(ReprocessKeyMarker, StringComparison.Ordinal);

    /// <summary>True when this artifact is a re-processed preview rather than a deliverable output.</summary>
    public static bool IsReprocessed(OutboundArtifact artifact) =>
        IsReprocessedKey(artifact.FileKey);

    /// <summary>
    /// The artifact an order would deliver: its newest NON-re-processed output, or null when it has
    /// none. In-memory form, for the controller paths that already hold a loaded
    /// <c>order.OutboundArtifacts</c>.
    /// </summary>
    public static OutboundArtifact? NewestDeliverable(IEnumerable<OutboundArtifact> artifacts) =>
        artifacts
            .Where(a => !IsReprocessedKey(a.FileKey))
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefault();

    /// <summary>
    /// Query-side form of the same rule, for the background paths that must not materialise an
    /// order's whole artifact history. Translates to a SQL <c>NOT LIKE</c>; the expression is
    /// written inline rather than delegating to <see cref="IsReprocessedKey"/> because EF cannot
    /// translate a method call into SQL.
    /// </summary>
    public static IQueryable<OutboundArtifact> Deliverable(this IQueryable<OutboundArtifact> source) =>
        source.Where(a => !a.FileKey.Contains(ReprocessKeyMarker));
}
