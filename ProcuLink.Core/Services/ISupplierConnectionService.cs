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
/// Thrown when a caller puts a value in <see cref="ConnectionRevisionDraftInput.CredentialsRef"/>.
///
/// <para>That field holds the AES-GCM ciphertext of a supplier's delivery credentials, and the
/// dispatchers decrypt it to authenticate the outbound request. The ciphertext is encrypted with no
/// associated data, so it is bound to the deployment key and to nothing else — not to an org, not to
/// a supplier — which means any blob that decrypts, decrypts for every tenant. Accepting one from a
/// request body therefore let a caller nominate credentials they never proved they own.</para>
///
/// <para>Nothing legitimate is lost: the revision read path returns only <c>HasCredentials</c>, a
/// bool, so a client cannot obtain its own ciphertext through this API and can only ever be echoing
/// one it acquired elsewhere. Credentials reach a revision the way they always did in production —
/// saved on the supplier delivery config, where the server encrypts them, then copied forward
/// internally by clone-from-active, rollback and republish-from-live, none of which read this
/// input.</para>
///
/// <para>Derives from <see cref="ArgumentException"/> so an unprepared handler still maps it to 400.
/// <see cref="PolicyMessage"/> is the operator-facing text without ArgumentException's
/// <c>(Parameter '…')</c> suffix, and it never quotes the submitted value.</para>
/// </summary>
public sealed class ClientSuppliedCredentialsRefException : ArgumentException
{
    /// <summary>Machine-readable code returned alongside the message.</summary>
    public const string Code = "credentials_ref_not_accepted";

    /// <summary>Operator-facing text, free of the ArgumentException parameter suffix.</summary>
    public const string Explanation =
        "Delivery credentials cannot be set on a connection revision directly. Save them on the " +
        "supplier's delivery configuration, where they are encrypted, and they will be carried into " +
        "the revision for you.";

    public string PolicyMessage => Explanation;

    public ClientSuppliedCredentialsRefException()
        : base(Explanation, nameof(ConnectionRevisionDraftInput.CredentialsRef))
    {
    }
}

/// <summary>
/// Evidence from one test-pack run over a revision (launch batch 3): the outcome, when it ran,
/// and an honest summary JSON (replay leg + conformance leg + any execution error).
/// </summary>
public sealed record ConnectionTestEvidence(bool Passed, DateTime TestedAt, string SummaryJson);

/// <summary>Outcome of <see cref="ISupplierConnectionService.MarkTestAsync"/>.</summary>
public enum ConnectionTestStatus
{
    /// <summary>Connection/revision not found in this org.</summary>
    NotFound,
    /// <summary>Revision is published/archived — only draft/test revisions can be tested.</summary>
    InvalidStatus,
    /// <summary>The test pack ran (pass OR fail — see the evidence) and the revision is marked test.</summary>
    Completed,
}

public sealed record ConnectionTestOutcome(ConnectionTestStatus Status, ConnectionTestEvidence? Evidence);

/// <summary>Outcome of <see cref="ISupplierConnectionService.PublishAsync"/>.</summary>
public enum ConnectionPublishOutcome
{
    /// <summary>Connection/revision not found in this org.</summary>
    NotFound,
    /// <summary>Revision is already published/archived (frozen).</summary>
    InvalidStatus,
    /// <summary>
    /// Evidence gate: the revision has no PASSING test evidence newer than its last content
    /// update. Run tests on the revision before publishing. Pre-existing published revisions
    /// (e.g. the V1 backfilled rev-1 rows) are untouched — the gate only applies to NEW
    /// publish attempts on draft/test revisions.
    /// </summary>
    EvidenceRequired,
    /// <summary>Published: revision frozen, prior published revision archived, active pointer flipped.</summary>
    Published,
}

/// <summary>Outcome of <see cref="ISupplierConnectionService.RollbackAsync"/>.</summary>
public enum ConnectionRollbackStatus
{
    /// <summary>Connection/target revision not found in this org.</summary>
    NotFound,
    /// <summary>Target is not a previously-published (now archived) revision of this connection.</summary>
    InvalidTarget,
    /// <summary>Rollback completed: target's bundle cloned into a new published revision, pointer moved.</summary>
    Completed,
}

public sealed record ConnectionRollbackOutcome(
    ConnectionRollbackStatus Status,
    Entities.SupplierConnectionRevision? NewRevision,
    string? Message);

/// <summary>Outcome of <see cref="ISupplierConnectionService.RepublishLiveDeliveryAsync"/>.</summary>
public enum DeliveryRepublishStatus
{
    /// <summary>Revision authority is off, the supplier has no connection, or no published active
    /// revision exists — the live delivery config already governs delivery; nothing to republish.</summary>
    NotGoverned,
    /// <summary>The live delivery config already matches the active revision's snapshot — no new
    /// version was created (idempotent: a no-op save does not spawn an identical revision).</summary>
    Unchanged,
    /// <summary>A new published revision was created snapshotting the live delivery config, the prior
    /// published revision archived, and the active pointer moved.</summary>
    Republished,
}

/// <summary>Result of routing a live delivery-config edit into the versioned connection.</summary>
public sealed record DeliveryRepublishOutcome(DeliveryRepublishStatus Status, int? NewVersionNo);

/// <summary>
/// How a supplier's delivery is GOVERNED, for honest display in the delivery-config editor: when
/// revision authority routes delivery through a published connection revision, the editor must
/// tell the operator delivery follows that versioned snapshot — not the raw live row they are
/// looking at — and which version is live.
/// </summary>
/// <param name="LiveMatchesActiveDelivery">Null when not governed or no live row to compare; true ⇒
/// the live delivery config equals the active revision's snapshot; false ⇒ the live row differs
/// from what governs delivery.</param>
public sealed record DeliveryGovernanceInfo(
    bool RevisionGoverned,
    int? ActiveVersionNo,
    bool? LiveMatchesActiveDelivery);

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

    /// <summary>
    /// Executes the REAL test pack over a revision and stores the evidence on it
    /// (TestResultJson + TestedAt + TestPassed): (a) replays the revision over recent orders via
    /// the replay engine (0 orders = pass-with-note), (b) conformance-checks a replayed output
    /// against its named standards profile where the format is resolvable (skip-with-note
    /// otherwise). NEVER delivers (no side effects beyond the stored evidence). Failures are
    /// stored honestly, not thrown. Returns null when the revision is not found in this org.
    /// </summary>
    Task<ConnectionTestEvidence?> RunTestPackAsync(Guid orgId, Guid connectionId, Guid revisionId, CancellationToken ct);

    /// <summary>
    /// Runs the test pack (<see cref="RunTestPackAsync"/>), stores the evidence, and marks the
    /// revision <c>test</c> (still editable). The evidence is returned for the caller to surface.
    /// </summary>
    Task<ConnectionTestOutcome> MarkTestAsync(Guid orgId, Guid connectionId, Guid revisionId, CancellationToken ct);

    /// <summary>
    /// Publishes a draft/test revision: freezes it, archives the prior published revision, and flips
    /// the connection's active pointer — all in one transaction. EVIDENCE-GATED: the revision must
    /// carry PASSING test evidence newer than its last content update
    /// (<see cref="ConnectionPublishOutcome.EvidenceRequired"/> otherwise). Pre-existing published
    /// revisions are untouched by the gate.
    /// </summary>
    Task<ConnectionPublishOutcome> PublishAsync(Guid orgId, Guid connectionId, Guid revisionId, string? publishedBy, CancellationToken ct);

    /// <summary>
    /// Rolls the connection back to a previously-published (now archived) revision: clones the
    /// target's full bundle (including item-mapping child rows) into a NEW revision (next version
    /// number, status <c>published</c>, CreatedBy <c>rollback:{user}</c>), archives the currently
    /// published revision, and moves the active pointer to the clone. The target itself stays
    /// archived/immutable. The evidence gate does NOT apply — rollback restores a bundle that was
    /// already published before.
    /// </summary>
    Task<ConnectionRollbackOutcome> RollbackAsync(
        Guid orgId, Guid connectionId, Guid targetRevisionId, string? requestedBy, CancellationToken ct);

    /// <summary>
    /// Archives a revision. If it was the active published one, the connection's active pointer is
    /// cleared. Returns null if not found in this org.
    /// </summary>
    Task<bool?> ArchiveAsync(Guid orgId, Guid connectionId, Guid revisionId, CancellationToken ct);

    /// <summary>
    /// "Honest + route-to-versioned" — when revision authority is ON and the supplier is governed by
    /// a connection with a PUBLISHED active revision, snapshots the supplier's CURRENT live delivery
    /// config (protocol + config json + auto-deliver + encrypted credentials + output format) into a
    /// NEW published revision, cloning the rest of the active bundle, archives the prior published
    /// revision and moves the active pointer. This makes a live delivery-config edit actually govern
    /// future orders instead of being overridden by the now-stale published snapshot. No-op
    /// (<see cref="DeliveryRepublishStatus.NotGoverned"/>) when the flag is off / no connection / no
    /// published active revision — the live config already governs in those cases. No-op
    /// (<see cref="DeliveryRepublishStatus.Unchanged"/>) when the live delivery already equals the
    /// active snapshot. Bypasses the evidence gate by design (like <see cref="RollbackAsync"/>): the
    /// bytes come from the operator's explicit live edit.
    /// </summary>
    Task<DeliveryRepublishOutcome> RepublishLiveDeliveryAsync(
        Guid orgId, Guid supplierId, string? publishedBy, CancellationToken ct);

    /// <summary>
    /// Describes how this supplier's delivery is governed (for honest editor display): whether
    /// revision authority routes delivery via a published connection revision, which version, and
    /// that revision's delivery snapshot so the caller can show whether the live row is in sync.
    /// </summary>
    Task<DeliveryGovernanceInfo> DescribeDeliveryGovernanceAsync(
        Guid orgId, Guid supplierId, CancellationToken ct);
}
