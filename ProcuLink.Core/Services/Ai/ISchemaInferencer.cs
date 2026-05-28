namespace ProcuLink.Core.Services.Ai;

/// <summary>
/// Build #1 — the one-click setup vision (see docs/format-channel-roadmap.md §5).
/// Given a sample order file, infers the source schema and proposes a mapping
/// onto the canonical PO (PoNumber, OrderDate, Currency, lines[].SupplierItemCode,
/// etc.). The result is read by the frontend and confirmed by the user —
/// nothing is auto-applied to a supplier mapping.
///
/// Implementations are scoped per-request — they read the current tenant from
/// <see cref="ICurrentTenantService"/> (or equivalent test double) to enforce
/// the per-org monthly token cap via <see cref="IAiUsageTracker"/>.
/// </summary>
public interface ISchemaInferencer
{
    /// <summary>
    /// Inspect <paramref name="sample"/> and return the detected fields with
    /// example values and an inferred type per field. Implementations may
    /// return an <see cref="InferredSchema"/> with an empty field list when
    /// they cannot operate (e.g. AI key missing, format unsupported, or the
    /// org has reached its monthly token cap). They MUST NOT throw on these
    /// no-op paths.
    /// </summary>
    Task<InferredSchema> InferSchemaAsync(
        Stream sample,
        string fileName,
        string? contentType,
        CancellationToken ct);

    /// <summary>
    /// Given an inferred schema, propose a mapping from each schema field to
    /// a canonical PO field (PoNumber, OrderDate, Currency,
    /// lines[].SupplierItemCode, ...) with per-field confidence (0..1) and a
    /// short reason for the procurement user. Implementations return an empty
    /// <see cref="ProposedMapping"/> when the AI provider is not configured or
    /// the org is over its monthly cap — they MUST NOT throw on those paths.
    /// </summary>
    Task<ProposedMapping> ProposeMappingAsync(
        InferredSchema schema,
        CancellationToken ct);
}

/// <summary>
/// Result of <see cref="ISchemaInferencer.InferSchemaAsync"/>.
/// </summary>
/// <param name="FileType">
/// Detected file type — one of "csv", "xlsx", "json", "xml", or "unknown".
/// Mirrors the on-disk extension when one is available, otherwise picked from
/// the content sniff.
/// </param>
/// <param name="Fields">Per-source-field metadata.</param>
/// <param name="SampleRows">
/// Up to five sample records as field-name → value dictionaries. Allows the
/// frontend to render a preview table next to the proposed mapping.
/// </param>
public sealed record InferredSchema(
    string FileType,
    IReadOnlyList<SchemaField> Fields,
    IReadOnlyList<IReadOnlyDictionary<string, string?>> SampleRows);

/// <summary>
/// One field discovered in a sample file.
/// </summary>
/// <param name="Name">Display name (column header, JSON property, XML element).</param>
/// <param name="Path">
/// Stable path used for downstream mapping, e.g. <c>"PoNumber"</c> for CSV,
/// <c>"order.header.poNumber"</c> for JSON dotted path, or
/// <c>"/PurchaseOrder/Header/PoNumber"</c> for XML.
/// </param>
/// <param name="InferredType">
/// One of "string", "number", "integer", "date", "boolean", or "unknown".
/// </param>
/// <param name="ExampleValues">Up to five non-null sample values.</param>
public sealed record SchemaField(
    string Name,
    string Path,
    string InferredType,
    IReadOnlyList<string> ExampleValues);

/// <summary>
/// Result of <see cref="ISchemaInferencer.ProposeMappingAsync"/>.
/// </summary>
public sealed record ProposedMapping(IReadOnlyList<FieldMapping> Mappings);

/// <summary>
/// One row in the proposed mapping table.
/// </summary>
/// <param name="SourceField">The source field path (matches <see cref="SchemaField.Path"/>).</param>
/// <param name="TargetCanonicalField">
/// The canonical PO target. Header-level: <c>PoNumber</c>, <c>OrderDate</c>,
/// <c>Currency</c>, <c>BuyerName</c>. Line-level (any canonical line field):
/// <c>lines[].LineNumber</c>, <c>lines[].BuyerItemCode</c>,
/// <c>lines[].SupplierItemCode</c>, <c>lines[].Description</c>,
/// <c>lines[].Quantity</c>, <c>lines[].Unit</c>, <c>lines[].UnitPrice</c>.
/// Empty string when no canonical target is appropriate.
/// </param>
/// <param name="Confidence">0..1 inclusive — UI buckets at 0.9 and 0.7.</param>
/// <param name="Reason">One-line operational explanation for the user.</param>
public sealed record FieldMapping(
    string SourceField,
    string TargetCanonicalField,
    float Confidence,
    string Reason);
