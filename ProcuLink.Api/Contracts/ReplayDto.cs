using System.Text.Json.Serialization;

namespace ProcuLink.Api.Contracts;

/// <summary>
/// Group V2 — request body for replaying historical orders through a DRAFT (or any read-only)
/// connection revision. Either pass explicit <see cref="OrderIds"/> (capped), or leave them empty
/// to replay the most recent <see cref="RecentLimit"/> orders for the connection's supplier.
/// Replay is non-mutating and never delivers.
/// </summary>
public sealed record ReplayRequest(
    /// <summary>Explicit order ids to replay (org-scoped, capped at <c>ReplayService.MaxOrders</c>). When empty, a recent window is used.</summary>
    IReadOnlyList<Guid>? OrderIds = null,
    /// <summary>When <see cref="OrderIds"/> is empty, replay the most recent N orders for the supplier (default 20, capped).</summary>
    int RecentLimit = 20,
    /// <summary>
    /// Replay flip A — opt-in PARSE-FROM-SOURCE leg. When true, each replayed order that has a
    /// stored source file is re-parsed IN MEMORY under the replayed revision's input-mapping
    /// snapshot (item codes resolved against the revision's item-mapping snapshot), and the
    /// per-order diff vs. the order's CURRENT stored canonical is returned in
    /// <see cref="ReplayOrderDiffDto.ParseLeg"/>. Default false → the response is byte-identical
    /// to the pre-flip contract (no <c>parseLeg</c> field is emitted). Non-mutating: nothing is
    /// written; orders without a stored file / PDF (AI-extracted) sources are skipped with a note.
    /// </summary>
    bool IncludeParseLeg = false);

/// <summary>
/// Group V2 — the full replay result: the revision that was replayed plus one diff per order.
/// </summary>
public sealed record ReplayResponse(
    Guid ConnectionId,
    Guid RevisionId,
    int RevisionVersionNo,
    string RevisionStatus,
    /// <summary>How many orders were actually replayed (after the cap and the recent-window resolution).</summary>
    int OrderCount,
    IReadOnlyList<ReplayOrderDiffDto> Orders);

/// <summary>
/// Group V2 — the diff for one replayed order: the would-be output + validation under the replayed
/// revision, vs. the order's CURRENT result (current output text + current validation summary), with
/// boolean flags summarising whether anything changed. Nothing here is persisted.
/// </summary>
public sealed record ReplayOrderDiffDto(
    Guid OrderId,
    string PoNumber,
    /// <summary>The output format the replayed revision would emit for this order (e.g. "Csv", "Xml").</summary>
    string OutputFormat,

    // ── Output diff ─────────────────────────────────────────────────────────
    /// <summary>True when the replayed revision's output text differs from the order's current output text.</summary>
    bool OutputChanged,
    /// <summary>The order's CURRENT would-be output (re-derived from its current per-order override / fixed transformer). Null if the current output could not be produced (e.g. unresolved lines).</summary>
    string? CurrentOutput,
    /// <summary>The output the DRAFT/replayed revision would produce. Null if it could not be produced.</summary>
    string? DraftOutput,
    /// <summary>Set when the replayed revision's output could not be produced (broken template, unresolved lines, unsupported format). The order is never delivered; this is surfaced for the operator.</summary>
    string? OutputError,

    // ── Canonical / effective-value diff ──────────────────────────────────────
    /// <summary>Per-field effective-value changes the replayed revision's mapping would introduce vs. the order's current effective values (header + line scope).</summary>
    IReadOnlyList<ReplayFieldChangeDto> EffectiveValueChanges,

    // ── Validation diff (reuses SupplierAcceptanceService) ────────────────────
    /// <summary>True when the order's pass/fail validation outcome flips under the replayed revision's bound acceptance profile.</summary>
    bool ValidationChanged,
    ReplayValidationSummaryDto CurrentValidation,
    ReplayValidationSummaryDto DraftValidation,
    /// <summary>Per-rule status flips (pass→fail or fail→pass) the replayed revision's validation introduces.</summary>
    IReadOnlyList<ReplayValidationFlipDto> ValidationFlips,

    // ── Parse-from-source leg (replay flip A — additive, opt-in) ──────────────
    /// <summary>
    /// Parse-from-source diff for this order. Only populated when
    /// <see cref="ReplayRequest.IncludeParseLeg"/> was true; omitted from the JSON entirely when
    /// null so the default replay contract stays byte-identical.
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ReplayParseLegDto? ParseLeg = null);

/// <summary>A single effective canonical-value change (header- or line-scoped).</summary>
public sealed record ReplayFieldChangeDto(
    /// <summary>"header" or "line".</summary>
    string Scope,
    /// <summary>Line number for line-scope changes; null for header-scope.</summary>
    int? LineNumber,
    string Field,
    string? CurrentValue,
    string? DraftValue);

/// <summary>Aggregate pass/fail counts for one side of a validation diff.</summary>
public sealed record ReplayValidationSummaryDto(
    /// <summary>True when no rule failed (an order with no bound profile is considered passing).</summary>
    bool Passed,
    int PassCount,
    int FailCount,
    /// <summary>True when a bound acceptance profile was evaluated; false when there was no profile to evaluate.</summary>
    bool HasProfile);

/// <summary>
/// Replay flip A — the PARSE-FROM-SOURCE result for one replayed order: the order's stored
/// source file re-parsed in memory under the replayed revision's input-mapping snapshot, item
/// codes resolved against the revision's item-mapping snapshot, diffed against the order's
/// CURRENT stored canonical (header fields, line count, review flags, code resolution).
/// Parse DIFFERENCES are informational — a mapping change SHOULD change parsing; only a
/// re-parse FAILURE is a problem. Nothing here is persisted.
/// </summary>
public sealed record ReplayParseLegDto(
    /// <summary>"reparsed" (source re-parsed and diffed) | "skipped" (no file / AI-extracted source) | "failed" (download/parse error — recorded, never thrown).</summary>
    string Status,
    /// <summary>Why the leg was skipped or failed; null for a clean re-parse.</summary>
    string? Note,
    /// <summary>True when ANY difference vs. the current stored canonical was detected.</summary>
    bool ParseChanged,
    /// <summary>Header fields whose value would change under the revision's input mapping (scope "header").</summary>
    IReadOnlyList<ReplayFieldChangeDto> HeaderChanges,
    bool LineCountChanged,
    /// <summary>The order's currently stored line count (null when the leg was skipped/failed before comparing).</summary>
    int? CurrentLineCount,
    /// <summary>The re-parsed line count (null when the leg was skipped/failed).</summary>
    int? ReParsedLineCount,
    /// <summary>Line numbers that would NEWLY need review under this revision (unresolved code or parser-flagged) but are clean today.</summary>
    IReadOnlyList<int> LinesNewlyFlagged,
    /// <summary>Line numbers flagged today that would be clean under this revision.</summary>
    IReadOnlyList<int> LinesNewlyUnflagged,
    /// <summary>Buyer codes unresolved today that the revision's item-mapping snapshot WOULD resolve.</summary>
    IReadOnlyList<ReplayParseCodeChangeDto> CodesNewlyResolved,
    /// <summary>Buyer codes resolved today that would become UNRESOLVED under the revision's snapshot.</summary>
    IReadOnlyList<ReplayParseCodeChangeDto> CodesNewlyUnresolved,
    /// <summary>Buyer codes resolved on both sides but to a DIFFERENT supplier item code.</summary>
    IReadOnlyList<ReplayParseCodeChangeDto> CodesResolvedDifferently);

/// <summary>One per-line supplier-item-code resolution change between the current canonical and the parse-leg re-parse.</summary>
public sealed record ReplayParseCodeChangeDto(
    int LineNumber,
    string? BuyerItemCode,
    /// <summary>The supplier item code stored on the order's current line (null = unresolved today).</summary>
    string? CurrentSupplierItemCode,
    /// <summary>The supplier item code the revision's snapshot would resolve (null = would be unresolved).</summary>
    string? ReParsedSupplierItemCode);

// ── WP-35 — acting on a replay result ────────────────────────────────────────

/// <summary>
/// WP-35 — request body for re-processing ONE historical order under a connection revision. The
/// order is named in the body; the revision is the route's. Re-processing PERSISTS an artifact
/// (unlike replay) but never delivers it.
/// </summary>
public sealed record ReprocessRequest(Guid OrderId);

/// <summary>WP-35 — why a re-process did or did not produce an artifact.</summary>
public enum ReprocessStatus
{
    /// <summary>An artifact for this order under this revision exists (freshly written, or already there).</summary>
    Ok,
    /// <summary>No such connection/revision in this org.</summary>
    RevisionNotFound,
    /// <summary>No such order in this org, or the order does not belong to the revision's supplier.</summary>
    OrderNotFound,
    /// <summary>The revision's output could not be produced for this order — nothing was written.</summary>
    RenderFailed,
    /// <summary>The service was constructed without file storage, so no artifact can be persisted.</summary>
    StorageUnavailable,
}

/// <summary>WP-35 — the result of a re-process attempt.</summary>
public sealed record ReprocessOutcome(
    ReprocessStatus Status,
    ReprocessResponse? Response,
    /// <summary>Operator-facing reason when <see cref="Status"/> is not <see cref="ReprocessStatus.Ok"/>.</summary>
    string? Error);

/// <summary>
/// WP-35 — the artifact a re-process produced, plus the provenance that makes it distinguishable
/// from the order's original output.
/// </summary>
public sealed record ReprocessResponse(
    Guid OrderId,
    string PoNumber,
    /// <summary>The revision whose configuration produced these bytes.</summary>
    Guid RevisionId,
    int RevisionVersionNo,
    Guid ArtifactId,
    string Format,
    string FileKey,
    /// <summary>SHA-256 hex of the exact stored bytes.</summary>
    string? ArtifactSha256,
    DateTime CreatedAt,
    /// <summary>
    /// True when this exact output already existed for this order under this revision, so the call
    /// returned the existing artifact instead of appending a second identical one.
    /// </summary>
    bool Reused,
    /// <summary>
    /// The artifact the order would still DELIVER — unchanged by this re-process, and null only
    /// when the order never had one. Surfaced so an operator can see that re-processing did not
    /// arm anything for sending.
    /// </summary>
    Guid? DeliverableArtifactId);

/// <summary>One rule whose pass/fail status differs between the current and replayed validation.</summary>
public sealed record ReplayValidationFlipDto(
    string Code,
    int? LineNumber,
    /// <summary>"pass" or "fail" under the order's current bound profile (null when the rule did not exist there).</summary>
    string? CurrentStatus,
    /// <summary>"pass" or "fail" under the replayed revision's bound profile (null when the rule does not exist there).</summary>
    string? DraftStatus,
    string Message);
