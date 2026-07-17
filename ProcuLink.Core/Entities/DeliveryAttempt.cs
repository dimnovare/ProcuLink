namespace ProcuLink.Core.Entities;

public class DeliveryAttempt
{
    public Guid Id { get; set; }
    /// <summary>Null for test-fire attempts not linked to a real order.</summary>
    public Guid? OrderId { get; set; }
    public Guid OrgId { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Deterministic per-artifact delivery idempotency key — a stable function of
    /// (orderId, artifactId). The SAME key is (re)used for every dispatch of the same artifact:
    /// a legitimate backoff retry, and crucially a crash-recovery re-send after a lost ACK. It is
    /// carried on the outbound request per channel (HTTP <c>Idempotency-Key</c> header, email
    /// <c>Message-ID</c>; SFTP/FTPS achieve the same effect via the deterministic overwrite
    /// filename) so a supplier can de-duplicate a re-send. Null on legacy / test-fire rows.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    public DateTime AttemptedAt { get; set; }
    public int? ResponseCode { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>
    /// Set when the supplier endpoint returned a 4xx response indicating an
    /// explicit rejection (as opposed to a transient 5xx / network failure).
    /// </summary>
    public string? RejectionReason { get; set; }
    /// <summary>
    /// Rejection capture: the supplier endpoint's raw response/NACK body, persisted verbatim
    /// (bounded to <see cref="MaxResponseBodyLength"/> chars) for both rejections and transient
    /// failures so operators can diagnose why a delivery was refused. Null when no body was
    /// returned (e.g. network failure before any response).
    /// </summary>
    public string? ResponseBody { get; set; }
    /// <summary>
    /// ACK round-trip: UTC timestamp at which the supplier acknowledged receipt
    /// (HTTP 2xx / successful dispatch). Null until the delivery is confirmed.
    /// </summary>
    public DateTime? AcknowledgedAt { get; set; }
    /// <summary>1-based attempt index within an order's delivery retry sequence; 0 for test-fire rows.</summary>
    public int AttemptNumber { get; set; }

    // ── Provenance (launch batch 3 — nullable; legacy rows stay null) ────────────
    /// <summary>
    /// The connection revision that produced the dispatched artifact (copied from the artifact,
    /// falling back to the order's pin). Plain column, no FK. Null for legacy/test-fire rows.
    /// </summary>
    public Guid? ConnectionRevisionId { get; set; }
    /// <summary>SHA-256 hex of the effective transform config — copied from the artifact's <c>ConfigDigest</c>.</summary>
    public string? ConfigDigest { get; set; }
    /// <summary>
    /// SHA-256 hex of the payload bytes ACTUALLY dispatched on this attempt. Comparing it to the
    /// artifact's <c>ArtifactSha256</c> proves the delivered payload matches the generated artifact
    /// (corruption detection). Null when the attempt failed before the payload was downloaded.
    /// </summary>
    public string? ArtifactSha256 { get; set; }

    /// <summary>Upper bound on persisted <see cref="ResponseBody"/> length — keeps a hostile/huge supplier body from bloating the row.</summary>
    public const int MaxResponseBodyLength = 8_000;

    // ── Status values ───────────────────────────────────────────────────────────
    /// <summary>Terminal: the supplier accepted the payload (HTTP 2xx / successful upload / send).</summary>
    public const string StatusSuccess = "success";
    /// <summary>Terminal: the attempt failed (transient 5xx / network, or an explicit 4xx rejection).</summary>
    public const string StatusFailed = "failed";
    /// <summary>
    /// In-flight marker (A3): persisted+committed BEFORE the actual send so a crash AFTER the
    /// supplier accepts but BEFORE the terminal outcome commits is detectable on recovery. A stuck
    /// re-drive reuses the matching <c>dispatching</c> row (same <see cref="IdempotencyKey"/>) rather
    /// than opening a fresh attempt, so a lost-ACK re-send cannot create a second terminal attempt
    /// row or silently consume the retry budget. Excluded from the retry attempt-cap count until it
    /// is finalised to <see cref="StatusSuccess"/>/<see cref="StatusFailed"/>.
    /// </summary>
    public const string StatusDispatching = "dispatching";

    /// <summary>
    /// Terminal: a send was ATTEMPTED and its outcome is unknown — it may or may not have reached
    /// the supplier, and the channel cannot tell us which. Set when a crash-recovery re-drive
    /// re-adopts an in-flight <c>dispatching</c> row on a
    /// <see cref="Core.Services.Delivery.ResendSafety.Unsafe"/> channel: re-sending could duplicate
    /// the PO, so the order is parked for a human decision instead.
    /// <para>
    /// The marker is committed BEFORE the network write, so its survival does not prove a send
    /// happened: a crash in between — or a cancelled token on shutdown, which escapes
    /// <c>DeliveryService</c>'s <c>catch … when (ex is not OperationCanceledException)</c> — parks
    /// with nothing sent. COUNTS toward the retry attempt cap anyway: an attempt that MIGHT have
    /// reached the supplier has to be budgeted as though it did.
    /// </para>
    /// <para>
    /// Never rewritten to <see cref="StatusSuccess"/> by the operator's "Mark as delivered": that
    /// moves the ORDER to delivered and audits the human's assertion separately. We never
    /// fabricate a supplier ACK we did not observe.
    /// </para>
    /// </summary>
    public const string StatusUnconfirmed = "unconfirmed";

    // Navigation — Order is optional (null for test-fire rows)
    public PurchaseOrderEntity? Order { get; set; }
    public Organisation Organisation { get; set; } = null!;
}
