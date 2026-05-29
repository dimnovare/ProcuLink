namespace ProcuLink.Api.Contracts;

/// <summary>
/// Response for GET /api/orders/{id}/mapping-preview.
/// Read-only snapshot of source fields → canonical spine → AI-suggested supplier codes.
/// Returned before the user commits/transforms — safe to call on any order in any status.
/// </summary>
public record MappingPreviewDto(
    string OrderId,
    string? SourceFormat,
    double? DetectedConfidence,
    List<MappingPreviewLineDto> Lines
);

/// <summary>
/// Per-line entry in the mapping preview.
/// </summary>
public record MappingPreviewLineDto(
    int LineNumber,
    /// <summary>Raw source fields as key→value pairs (buyerItemCode, description, quantity, unit, unitPrice).</summary>
    Dictionary<string, string?> SourceFields,
    /// <summary>Always "supplierItemCode" — the canonical field this line maps into.</summary>
    string CanonicalField,
    string? BuyerItemCode,
    /// <summary>AI-suggested supplier code; null when no suggestion exists.</summary>
    string? AiSuggestedSupplierCode,
    /// <summary>AI confidence in [0,1]; null when no suggestion exists. Sourced directly from the stored entity — never fabricated.</summary>
    double? Confidence,
    string? Provenance,
    string? Reason,
    /// <summary>"resolved" when supplierItemCode is already set; "suggested" when an AI suggestion exists; "unresolved" otherwise.</summary>
    string Status
);
