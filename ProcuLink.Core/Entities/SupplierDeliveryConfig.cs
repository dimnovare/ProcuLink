namespace ProcuLink.Core.Entities;

public class SupplierDeliveryConfig
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid SupplierId { get; set; }

    /// <summary>'http' | 'sftp' | 'ftps' | 'email' | 'erp_erply' | 'erp_directo' (legacy: 'smtp', 'ftp')</summary>
    public string Protocol { get; set; } = string.Empty;

    /// <summary>When true, dispatch fires automatically after TransformAsync completes.</summary>
    public bool AutoDeliver { get; set; }

    /// <summary>
    /// WP-33 — <b>auto-send when clean</b>. When true, a fully-resolved order for this supplier is
    /// evaluated for unattended sending the moment parse completes, with no operator click.
    ///
    /// <para><b>Currently in DRY RUN (stage 1).</b> Turning this on records what WOULD have been
    /// sent, in <see cref="AutoSendDryRun"/>, and sends nothing. Stage 2 makes the same decision
    /// move a real order; until then this switch cannot transmit a purchase order.</para>
    ///
    /// <para><b>Why this is not <see cref="AutoDeliver"/>.</b> They gate different doors.
    /// <see cref="AutoDeliver"/> decides whether dispatch follows a transform that a HUMAN already
    /// asked for — it is the difference between one click and two, and every existing
    /// <c>AutoDeliver = true</c> supplier chose it on that understanding. This flag decides whether
    /// the transform happens at all without anyone asking: the difference between one click and
    /// zero. Folding them together would silently promote every live <c>AutoDeliver</c> supplier to
    /// unattended sending on deploy, which is precisely the outcome the staged rollout exists to
    /// prevent.</para>
    ///
    /// <para>It lives on the delivery config, so a supplier with nowhere to send cannot be opted in
    /// at all — the "must have a delivery config" precondition is structural rather than a check
    /// someone has to remember. Read LIVE, never from a pinned connection revision: this is an
    /// operator's standing instruction about automation, not part of the document contract an order
    /// was ingested under, and turning it off must take effect now rather than on the next publish.</para>
    /// </summary>
    public bool AutoTransform { get; set; }

    /// <summary>
    /// Non-secret JSONB: endpoint URL, host, remote path, extra headers, timeout, etc.
    ///
    /// SCALE-GATED / SECURITY NOTE: this column is stored in CLEARTEXT (no encryption).
    /// That is BY DESIGN and NOT a P2 secret-at-rest issue: every SECRET (passwords, API
    /// keys, bearer tokens, basic-auth, OAuth2 client secrets, SFTP/FTP credentials) is
    /// kept out of here and stored AES-GCM encrypted in <see cref="EncryptedCredentials"/>.
    /// ConfigJson holds only non-secret connection metadata. INVARIANT to preserve: never
    /// write a credential/secret into ConfigJson — if a new delivery option needs a secret,
    /// add it to the encrypted credential payload instead. See
    /// docs/audit/2026-06-12-scale-gated-constraints.md.
    ///
    /// <para><b>Enforced, not merely documented, for the extra-headers map.</b> Every entry of
    /// <c>headers</c> is applied to the outbound request by <c>HttpDeliveryDispatcher</c>, so an
    /// operator typing <c>Authorization: Bearer …</c> there was writing a live credential into this
    /// cleartext column. Both write paths — the live delivery-config upsert and the connection
    /// revision draft input — now refuse one, through the single classifier
    /// <c>DeliveryConfigTransport.FindCredentialHeaders</c>.</para>
    ///
    /// <para><b>Two deliberate limits, and an asymmetry between the write paths.</b> Enforcement is
    /// on WRITE only: a config saved before it existed keeps delivering, because refusing at
    /// dispatch would strand orders. The live delivery-config save grandfathers a header whose name
    /// AND value are already stored, because the delivery editor has no headers field and
    /// round-trips the stored map on every save — refusing that echo would lock an operator out of
    /// every unrelated edit with no way to remove the header; adding one, or rotating its value, is
    /// still refused. The connection revision draft input refuses FLAT, with no grandfathering: it
    /// is caller-supplied input, and the paths that carry an already-live bundle forward
    /// (clone-from-active, rollback, publish, republish-from-live) never reach the validator, so
    /// nothing already live is stranded by the stricter rule. Both cases are surfaced by
    /// <c>DeliveryConfigResponse.InsecureTransportWarning</c> (mirrored on <c>ConnectionRevisionDto</c>
    /// so both editors report the same blob the same way) and by a dispatch-time log that names the
    /// header and never its value.</para>
    /// </summary>
    public string ConfigJson { get; set; } = "{}";

    /// <summary>Authenticated encrypted credential payload. Empty string means no credentials configured.</summary>
    public string EncryptedCredentials { get; set; } = string.Empty;

    /// <summary>
    /// The output format this supplier requires — one of 'xml' | 'csv' | 'cxml' | 'json' | 'ubl' | 'x12'.
    /// When set, "send to supplier" auto-transforms the order into this format before delivery.
    /// Null means the caller must specify a format explicitly.
    /// </summary>
    public string? OutputFormat { get; set; }

    /// <summary>
    /// Non-secret cXML network identities (From/To/Sender domain + identity) as cleartext JSON, or null.
    /// Drives the <c>&lt;Header&gt;</c> credentials of generated cXML so the wire carries the supplier's
    /// REAL cXML network identity (e.g. Coupa <c>NetworkId</c>) instead of ProcuLink's internal
    /// OrgId / SupplierId GUIDs. Only consulted when <see cref="OutputFormat"/> is <c>cxml</c>; null =
    /// legacy GUID identities. The Sender SharedSecret is a SECRET and lives encrypted in
    /// <see cref="EncryptedCxmlSharedSecret"/>, NOT here (same cleartext invariant as
    /// <see cref="ConfigJson"/>).
    /// </summary>
    public string? CxmlConfigJson { get; set; }

    /// <summary>
    /// AES-GCM encrypted cXML Sender <c>SharedSecret</c> (same encryption as
    /// <see cref="EncryptedCredentials"/>), or null/empty when no shared secret is configured.
    /// </summary>
    public string? EncryptedCxmlSharedSecret { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
    public Supplier Supplier { get; set; } = null!;
}
