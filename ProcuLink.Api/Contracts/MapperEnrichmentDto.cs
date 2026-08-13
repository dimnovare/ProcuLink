namespace ProcuLink.Api.Contracts;

// ─────────────────────────────────────────────────────────────────────────────
// Phase-2b mapper enrichment DTOs.
//
// These serialize (camelCase, the ASP.NET Core default) to EXACTLY the frontend
// types the mapper UI consumes — see project-proculink
// src/lib/api/types.ts (MappingSuggestion / FieldValidationState / CatalogPriceHint)
// and src/lib/api/mapper-ai.ts (the typed client + the decision request body).
//
// Every endpoint backed by these DTOs is org-scoped and returns an honest empty
// list when nothing applies (no saved mapping / no validation / no catalog match /
// no AI key) — it never fabricates a suggestion, a badge, or a price.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One learned or model-derived source→canonical (or canonical→output) mapping suggestion for an
/// order, rendered in the mapper as an accept/reject ghost wire.
/// Serializes to the frontend <c>MappingSuggestion</c> shape:
/// <c>{ targetKey, sourceId, confidence, reason, sourceKind, basis }</c>.
///
/// <para><b><c>Confidence</c> is nullable, and null is the normal answer.</b> It used to be a
/// non-nullable double that the saved-mapping path filled with a hard-coded <c>0.95</c> — a
/// constant, not a measurement — which the mapper then rendered as an "AI confidence 95%" chip in
/// AI-violet. It was wrong twice over: no model ran on that path (the suggester is deliberately
/// not invoked), and a saved supplier mapping is not a probability at all. A suggestion that
/// nothing scored now sends <c>null</c> and says so through <see cref="Basis"/>, and the frontend
/// prints what it is instead of inventing a number for it.</para>
/// </summary>
/// <param name="TargetKey">Target/output field path OR canonical field key being suggested a source for.</param>
/// <param name="SourceId">Suggested source: a SourceToken id (raw/structured) or a canonical field key.</param>
/// <param name="Confidence">
/// 0..1 model score, or <c>null</c> when nothing scored this suggestion. Only ever set when
/// <see cref="Basis"/> is <c>"model"</c>. Never fill this with a placeholder.
/// </param>
/// <param name="Reason">Short human reason (e.g. "Saved supplier mapping: column 'Ihre Materialnr' → ManufacturerPartNumber").</param>
/// <param name="SourceKind">"canonical" | "raw" | "custom" — provenance of the source.</param>
/// <param name="Basis">
/// What this suggestion actually is, so the UI can label it honestly:
/// <c>"saved_mapping"</c> — read back from the supplier's saved PO mapping; a fact about
/// configuration, carrying no probability (<see cref="Confidence"/> is null).
/// <c>"model"</c> — produced by a scorer; <see cref="Confidence"/> carries its real number.
/// </param>
public sealed record MappingSuggestionDto(
    string TargetKey,
    string SourceId,
    double? Confidence,
    string Reason,
    string SourceKind,
    string Basis);

/// <summary>Values for <see cref="MappingSuggestionDto.Basis"/>. Mirrored by the frontend's
/// <c>MappingSuggestion["basis"]</c> union in src/lib/api/types.ts.</summary>
public static class MappingSuggestionBasis
{
    /// <summary>Read back from the supplier's saved PO mapping. A fact, never a probability.</summary>
    public const string SavedMapping = "saved_mapping";

    /// <summary>Produced by a scorer; the DTO's Confidence carries its real number.</summary>
    public const string Model = "model";
}

/// <summary>
/// Per-field validation outcome surfaced as a green/amber badge on the mapper rows.
/// Serializes to the frontend <c>FieldValidationState</c> shape:
/// <c>{ key, state, reason?, blocking? }</c>.
/// </summary>
/// <param name="Key">Field key/path this applies to (canonical key or output path).</param>
/// <param name="State">"valid" | "review" — amber when a rule failed.</param>
/// <param name="Reason">Tooltip reason when <c>state == "review"</c>; null otherwise.</param>
/// <param name="Blocking">True = blocks delivery (error severity); false = advisory only (warning/info).</param>
public sealed record FieldValidationStateDto(
    string Key,
    string State,
    string? Reason,
    bool Blocking);

/// <summary>
/// A catalog price/code variance hint for a resolved line.
/// Serializes to the frontend <c>CatalogPriceHint</c> shape:
/// <c>{ lineKey, catalogCode, catalogPrice, poPrice, variancePercent, currency? }</c>.
/// </summary>
/// <param name="LineKey">Line key this applies to (the mapper's per-line key).</param>
/// <param name="CatalogCode">The matched catalog product code.</param>
/// <param name="CatalogPrice">Catalog list price, or null when the catalog row has no price.</param>
/// <param name="PoPrice">The PO line's unit price.</param>
/// <param name="VariancePercent">(catalog − po)/catalog × 100 (signed), or null when either price is missing/unusable.</param>
/// <param name="Currency">Catalog currency, when known.</param>
public sealed record CatalogPriceHintDto(
    string LineKey,
    string CatalogCode,
    decimal? CatalogPrice,
    decimal? PoPrice,
    decimal? VariancePercent,
    string? Currency);

/// <summary>
/// Request body for <c>POST /api/orders/{id}/ai-suggestion-decisions</c>. Mirrors the
/// frontend <c>recordSuggestionDecision</c> payload: <c>{ targetKey, sourceId, accepted, confidence }</c>.
/// Best-effort telemetry that feeds the V9 confidence-calibration loop — never on the
/// critical path.
/// </summary>
/// <param name="TargetKey">The mapper target/field the decision was about (e.g. "lines[2].supplierItemCode").</param>
/// <param name="SourceId">The suggested source that was accepted or rejected.</param>
/// <param name="Accepted">True if the user kept the suggestion; false if they rejected it.</param>
/// <param name="Confidence">The RAW suggestion confidence (0..1) — recorded verbatim for calibration.</param>
public sealed record RecordSuggestionDecisionRequest(
    string? TargetKey,
    string? SourceId,
    bool Accepted,
    double Confidence);
