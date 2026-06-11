namespace ProcuLink.Api.Contracts;

/// <summary>A connection in a list/summary view (Group V1).</summary>
public record ConnectionSummaryDto(
    Guid Id, Guid SupplierId, string Name,
    Guid? ActiveRevisionId, int? ActiveVersionNo,
    DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>A connection with its revision list (the Connection page header).</summary>
public record ConnectionDetailDto(
    Guid Id, Guid SupplierId, string Name,
    Guid? ActiveRevisionId, DateTime CreatedAt, DateTime UpdatedAt,
    IReadOnlyList<ConnectionRevisionSummaryDto> Revisions);

public record ConnectionRevisionSummaryDto(
    Guid Id, int VersionNo, string Status,
    DateTime? EffectiveFrom, DateTime? EffectiveTo, DateTime? PublishedAt, DateTime CreatedAt);

public record ConnectionItemMappingDto(
    string BuyerItemCode, string SupplierItemCode, float Confidence, string Source);

/// <summary>The full revision bundle (the Connection page tabs).</summary>
public record ConnectionRevisionDto(
    Guid Id, Guid ConnectionId, int VersionNo, string Status,
    DateTime? EffectiveFrom, DateTime? EffectiveTo, DateTime? PublishedAt, DateTime CreatedAt,
    string? InputMappingJson, string? OutputMappingJson, string? OutputFormat,
    string? DeliveryProtocol, string? DeliveryConfigJson, bool DeliveryAutoDeliver,
    bool HasCredentials,
    Guid? AcceptanceProfileId, int? AcceptanceVersionNo, string CatalogMode,
    IReadOnlyList<ConnectionItemMappingDto> ItemMappings,
    // Launch batch 3 — test evidence (null on never-tested / legacy revisions).
    bool? TestPassed = null, DateTime? TestedAt = null, string? TestResultJson = null);

/// <summary>
/// Launch batch 3 — evidence summary returned by POST .../test: the test pack ran (replay leg +
/// conformance leg), its outcome, and the stored summary JSON. A failed pack is returned honestly
/// (200 with <c>Passed=false</c>) — failure to PASS is not failure to RUN.
/// </summary>
public record ConnectionTestEvidenceDto(bool Passed, DateTime TestedAt, string SummaryJson);

/// <summary>Body for creating a new draft revision (clone-from-active by default).</summary>
public record CreateConnectionRevisionRequest(
    bool CloneFromActive = true,
    ConnectionRevisionBundleDto? Bundle = null);

/// <summary>Body for updating a draft revision bundle.</summary>
public record UpdateConnectionRevisionRequest(ConnectionRevisionBundleDto Bundle);

public record ConnectionRevisionBundleDto(
    string? InputMappingJson,
    string? OutputMappingJson,
    string? OutputFormat,
    string? DeliveryProtocol,
    string? DeliveryConfigJson,
    bool DeliveryAutoDeliver,
    string? CredentialsRef,
    Guid? AcceptanceProfileId,
    int? AcceptanceVersionNo,
    string CatalogMode,
    IReadOnlyList<ConnectionItemMappingDto>? ItemMappings);
