namespace ProcuLink.Api.Contracts;

/// <summary>Full order response including lines and artifacts.</summary>
public record OrderDto(
    Guid       Id,
    string     PoNumber,
    Guid       SupplierId,
    string     SupplierName,
    string     OrderDate,   // ISO-8601 date string (yyyy-MM-dd) — DateOnly not well-supported by all JSON clients
    string     Currency,
    string     Status,
    string?    SourceFileKey,
    DateTime   CreatedAt,
    DateTime   UpdatedAt,
    IReadOnlyList<OrderLineDto>    Lines,
    IReadOnlyList<ArtifactDto>     Artifacts
);

/// <summary>Single purchase order line in the API response.</summary>
public record OrderLineDto(
    Guid     Id,
    int      LineNumber,
    string   BuyerItemCode,
    string?  SupplierItemCode,
    string?  Description,
    decimal  Quantity,
    string?  Unit,
    decimal  UnitPrice,
    float    Confidence,
    bool     NeedsReview,
    AiMappingSuggestionDto? AiSuggestion
);

public record AiMappingSuggestionDto(
    string SupplierItemCode,
    float Confidence,
    string Reason,
    string Provenance
);

/// <summary>Outbound artifact reference in the order response.</summary>
public record ArtifactDto(
    Guid     Id,
    string   Format,
    string   FileKey,
    DateTime CreatedAt
);
