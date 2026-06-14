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
/// One AI/learned source→canonical (or canonical→output) mapping suggestion for an order,
/// rendered in the mapper as an accept/reject ghost wire.
/// Serializes to the frontend <c>MappingSuggestion</c> shape:
/// <c>{ targetKey, sourceId, confidence, reason, sourceKind }</c>.
/// </summary>
/// <param name="TargetKey">Target/output field path OR canonical field key being suggested a source for.</param>
/// <param name="SourceId">Suggested source: a SourceToken id (raw/structured) or a canonical field key.</param>
/// <param name="Confidence">0..1 — rendered as a confidence ring.</param>
/// <param name="Reason">Short human reason (e.g. "Saved supplier mapping: column 'Ihre Materialnr' → ManufacturerPartNumber").</param>
/// <param name="SourceKind">"canonical" | "raw" | "custom" — provenance of the source.</param>
public sealed record MappingSuggestionDto(
    string TargetKey,
    string SourceId,
    double Confidence,
    string Reason,
    string SourceKind);

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
