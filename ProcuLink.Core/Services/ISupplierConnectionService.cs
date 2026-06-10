using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

/// <summary>
/// Mutable bundle fields a caller may set when creating or updating a DRAFT revision.
/// Mirrors the first-class columns on <see cref="SupplierConnectionRevision"/> (lifecycle
/// fields are owned by the service, not the caller).
/// </summary>
public sealed record ConnectionRevisionDraftInput(
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
    IReadOnlyList<ConnectionItemMappingInput> ItemMappings);

public sealed record ConnectionItemMappingInput(
    string BuyerItemCode, string SupplierItemCode, float Confidence, string Source);

/// <summary>
/// Group V1 — lifecycle (draft → test → published → archived) for the versioned Supplier
/// Connection. Generalises the <see cref="ISupplierAcceptanceService"/> versioning precedent
/// from "just acceptance" to the whole connection bundle. All methods are org-scoped.
/// </summary>
public interface ISupplierConnectionService
{
    /// <summary>All connections for the org (one per supplier in V1).</summary>
    Task<IReadOnlyList<SupplierConnection>> ListAsync(Guid orgId, CancellationToken ct);

    /// <summary>A connection with its revisions, or null if not found in this org.</summary>
    Task<SupplierConnection?> GetAsync(Guid orgId, Guid connectionId, CancellationToken ct);

    /// <summary>A single revision (with item mappings + test cases), or null if not in this org.</summary>
    Task<SupplierConnectionRevision?> GetRevisionAsync(Guid orgId, Guid connectionId, Guid revisionId, CancellationToken ct);

    /// <summary>
    /// Ensures a connection exists for the supplier (creating one if needed) and returns it.
    /// Org-scoped; the supplier must belong to the org.
    /// </summary>
    Task<SupplierConnection?> EnsureConnectionAsync(Guid orgId, Guid supplierId, string? createdBy, CancellationToken ct);

    /// <summary>
    /// Creates a new DRAFT revision (next version number). When <paramref name="cloneFromActive"/>
    /// is true and an active published revision exists, the draft is cloned from it (this is what
    /// "edit a published connection" does); otherwise the draft is built from <paramref name="input"/>.
    /// Returns null if the connection is not in this org.
    /// </summary>
    Task<SupplierConnectionRevision?> CreateDraftAsync(
        Guid orgId, Guid connectionId, ConnectionRevisionDraftInput? input,
        bool cloneFromActive, string? createdBy, CancellationToken ct);

    /// <summary>
    /// Replaces a draft (or test) revision's mutable bundle. Rejected (returns false) for
    /// published/archived revisions — immutability. Returns null if not found in this org.
    /// </summary>
    Task<bool?> UpdateDraftAsync(
        Guid orgId, Guid connectionId, Guid revisionId, ConnectionRevisionDraftInput input, CancellationToken ct);

    /// <summary>Marks a draft as <c>test</c> (readiness marker; still editable). Returns null if not found.</summary>
    Task<bool?> MarkTestAsync(Guid orgId, Guid connectionId, Guid revisionId, CancellationToken ct);

    /// <summary>
    /// Publishes a draft/test revision: freezes it, archives the prior published revision, and flips
    /// the connection's active pointer — all in one transaction. Returns null if not found, false if
    /// the revision is already published/archived.
    /// </summary>
    Task<bool?> PublishAsync(Guid orgId, Guid connectionId, Guid revisionId, string? publishedBy, CancellationToken ct);

    /// <summary>
    /// Archives a revision. If it was the active published one, the connection's active pointer is
    /// cleared. Returns null if not found in this org.
    /// </summary>
    Task<bool?> ArchiveAsync(Guid orgId, Guid connectionId, Guid revisionId, CancellationToken ct);
}
