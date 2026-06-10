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
    int RecentLimit = 20);

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
    IReadOnlyList<ReplayValidationFlipDto> ValidationFlips);

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

/// <summary>One rule whose pass/fail status differs between the current and replayed validation.</summary>
public sealed record ReplayValidationFlipDto(
    string Code,
    int? LineNumber,
    /// <summary>"pass" or "fail" under the order's current bound profile (null when the rule did not exist there).</summary>
    string? CurrentStatus,
    /// <summary>"pass" or "fail" under the replayed revision's bound profile (null when the rule does not exist there).</summary>
    string? DraftStatus,
    string Message);
