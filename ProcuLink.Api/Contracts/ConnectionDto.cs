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
    string BuyerItemCode, string SupplierItemCode, float? Confidence, string Source);

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
    bool? TestPassed = null, DateTime? TestedAt = null, string? TestResultJson = null,
    // Set when this revision's SAVED config carries a fault the write path now refuses: an endpoint
    // the transport policy rejects (written before TLS enforcement reached this path), a credential
    // sitting in the extra-headers map, or both. Delivery continues — refusing a stored bundle
    // mid-flight would turn a security weakness into an outage — so this is how the operator finds
    // out. Null when fine. Never contains the URL or a header value: those are precisely the strings
    // that would copy the secret into the editor. Mirrors
    // DeliveryConfigResponse.InsecureTransportWarning so both editors report the same blob the same
    // way.
    string? InsecureTransportWarning = null);

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

// ── Phase 2 (extensible canonical) — user-defined spine fields ──────────────

/// <summary>
/// A user-defined canonical field DEFINITION for the mapper "+ Add field" affordance.
/// Mirrors <see cref="ProcuLink.Core.Entities.CanonicalFieldDef"/> minus the soft-delete /
/// audit columns (an active def is always non-deleted). <paramref name="Order"/> maps to the
/// <c>display_order</c> column and drives the canonical-pane ordering.
/// </summary>
public record CanonicalFieldDto(
    Guid Id, string Key, string Label, string Scope, string Type,
    string? StandardsRef, int Order);

/// <summary>
/// Body for POST .../canonical-fields. <paramref name="Order"/> is optional — when omitted the
/// new field is appended after the current max display_order for the (org, connection) scope.
/// </summary>
public record CreateCanonicalFieldRequest(
    string Key, string Label, string? Scope, string? Type, string? StandardsRef, int? Order);
