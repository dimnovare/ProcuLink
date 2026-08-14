namespace ProcuLink.Api.Contracts;

/// <summary>
/// PO Passport v1 — a read-only evidence trail showing how ProcuLink turned a
/// source purchase order into a supplier-ready delivery.
///
/// Every section references already-persisted data; large blobs (source file,
/// generated artifact) are referenced by id/key and never embedded. Sections that
/// are not yet persisted in the data model are returned null/empty and called out
/// in <see cref="Notes"/> so the consumer never mistakes "absent" for "fabricated".
/// </summary>
public record PassportDto(
    PassportOrderMeta              Order,
    PassportSourceArtifact?        SourceArtifact,
    PassportCanonicalSummary       Canonical,
    PassportSupplierProfile?       SupplierProfile,
    IReadOnlyList<PassportValidationResult> ValidationResults,
    IReadOnlyList<PassportMappingDecision>  MappingDecisions,
    IReadOnlyList<PassportManualCorrection> ManualCorrections,
    IReadOnlyList<PassportAiSuggestionOutcome> AiSuggestions,
    PassportOutputArtifact?        OutputArtifact,
    IReadOnlyList<PassportDeliveryAttempt> DeliveryAttempts,
    PassportSupplierResponse?      SupplierResponse,
    string                         FinalStatus,
    IReadOnlyList<PassportTimelineEvent> Timeline,
    /// <summary>
    /// Human-readable notes about data that is genuinely not persisted in the current
    /// model (e.g. structured per-order validation outcomes). Empty when every section
    /// was populated from real data.
    /// </summary>
    IReadOnlyList<string>          Notes
);

/// <summary>Order-level metadata for the passport header.</summary>
public record PassportOrderMeta(
    Guid     OrderId,
    string   PoNumber,
    string   Status,
    Guid     SupplierId,
    string   SupplierName,
    /// <summary>Buyer name extracted from CanonicalJson; null until it has been populated.</summary>
    string?  BuyerName,
    string   Currency,
    string   OrderDate,   // ISO-8601 date string (yyyy-MM-dd)
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool     IsSample
);

/// <summary>
/// Reference to the uploaded source file. The blob itself lives in object storage —
/// this carries only the storage key so the UI can request a signed URL on demand.
/// </summary>
public record PassportSourceArtifact(
    /// <summary>R2/local storage object key for the uploaded source file. Null when the order
    /// was created from an already-parsed payload (e.g. webhook ingress) with no stored file.</summary>
    string?  StorageKey,
    /// <summary>Short label derived from the source file extension (csv/xlsx/pdf/cxml/edi); null when undetermined.</summary>
    string?  DetectedFormat
);

/// <summary>Canonical PO roll-up: counts and money totals derived from the order's lines.</summary>
public record PassportCanonicalSummary(
    int     LineCount,
    string  Currency,
    /// <summary>Sum of Quantity × UnitPrice across all lines.</summary>
    decimal TotalValue,
    /// <summary>Sum of Quantity across all lines.</summary>
    decimal TotalQuantity
);

/// <summary>
/// The supplier acceptance profile / delivery config in effect for this order.
/// No explicit version column exists in the model today, so <see cref="Version"/> is
/// always null and <see cref="LastUpdatedAt"/> is surfaced as the version surrogate.
/// </summary>
public record PassportSupplierProfile(
    /// <summary>Delivery protocol from SupplierDeliveryConfig (http/sftp/erp_erply/…); null when no delivery config exists.</summary>
    string?   Protocol,
    /// <summary>Output format from the supplier profile, when one is configured.</summary>
    string?   OutputFormat,
    /// <summary>Accepted output formats declared on the supplier profile.</summary>
    IReadOnlyList<string> AcceptedFormats,
    /// <summary>Always null — the model has no explicit acceptance-profile version field today.</summary>
    string?   Version,
    /// <summary>UpdatedAt of the delivery config (preferred) or supplier profile — the de-facto version marker.</summary>
    DateTime? LastUpdatedAt
);

/// <summary>
/// A single validation outcome. The current model does not persist structured per-order
/// validation results, so the passport returns an empty list for these and notes it.
/// The shape is defined now so the section is forward-compatible once validation
/// outcomes are persisted.
/// </summary>
public record PassportValidationResult(
    int?    LineNumber,
    string  Severity,   // info | warning | error
    /// <summary>
    /// pass | fail — the OUTCOME of the check, and the only field that carries it.
    ///
    /// <para>Severity is not a substitute. <see cref="ProcuLink.Api.Services.InvariantValidator"/>
    /// deliberately emits a row for every check it performed, passing ones included, so a
    /// rule-less order cannot show a vacuous green "Passed" — and it stamps those rows with the
    /// severity the rule would carry IF it failed. A passing invariant at severity "error" is
    /// therefore normal and expected.</para>
    ///
    /// <para>This field was missing from the DTO until WP-39 §4.1. Because it was, a clean
    /// delivered order's audit trail read "3 validation issues": severity was the only signal
    /// left, and every consumer that reached for it was wrong.</para>
    /// </summary>
    string  Status,
    string  Code,
    string  Message
);

/// <summary>Per-line mapping decision: buyer item code → supplier item code, with how it was decided.</summary>
public record PassportMappingDecision(
    int     LineNumber,
    string  BuyerItemCode,
    string? SupplierItemCode,
    /// <summary>deterministic (resolved from item_mappings or a manual correction) | ai (an AI suggestion is still attached) | unresolved.</summary>
    string  Source,
    /// <summary>Model score 0..1, or null when nothing scored this line — see
    /// PurchaseOrderLineEntity.Confidence. Null is the normal answer.</summary>
    float?  Confidence
);

/// <summary>
/// A manual correction recorded against the order, reconstructed from audit events
/// (Resolved / AiSuggestionsBulkAccepted / MarkedRejected).
/// </summary>
public record PassportManualCorrection(
    string   Action,
    DateTime At,
    /// <summary>The raw audit payload as JSON text (counts, reason, savedMappings flag, etc.). Null when no payload.</summary>
    string?  Detail
);

/// <summary>
/// Outcome of an AI mapping suggestion for a line. "Accepted" / "Rejected" cannot be
/// derived for lines whose suggestion metadata was already cleared on resolve; only
/// suggestions still attached to a line are reported here, with Status = "pending".
/// </summary>
public record PassportAiSuggestionOutcome(
    int     LineNumber,
    string  SuggestedSupplierItemCode,
    float?  Confidence,
    string? Reason,
    string? Provenance,
    /// <summary>pending — a suggestion is still attached and awaiting a human decision.</summary>
    string  Status
);

/// <summary>
/// Reference to the generated supplier-ready artifact. Like the source artifact, the
/// blob is referenced by id/key, never embedded.
/// </summary>
public record PassportOutputArtifact(
    Guid     ArtifactId,
    string   Format,
    string   FileKey,
    DateTime CreatedAt,
    /// <summary>
    /// SHA-256 hex of the exact generated bytes, as recorded at transform time — the fingerprint an
    /// operator checks a downloaded copy against. Read straight from the artifact row; never
    /// recomputed here (a hash computed at read time would prove only that the read succeeded).
    /// Null for legacy artifacts and when hashing failed at transform time.
    /// </summary>
    string?  ArtifactSha256
);

/// <summary>One delivery attempt against the supplier endpoint.</summary>
public record PassportDeliveryAttempt(
    int      AttemptNumber,
    string   Status,
    string   Channel,
    string   Destination,
    DateTime AttemptedAt,
    int?     ResponseCode,
    /// <summary>
    /// When OUR dispatch call returned success — see <see cref="Core.Entities.DeliveryAttempt.TransportAcceptedAt"/>.
    /// Named <c>AcknowledgedAt</c> until 2026-08-14, which is what let the passport print "Accepted".
    /// It is our clock, not a supplier's verdict, and no client may render it as one.
    /// </summary>
    DateTime? TransportAcceptedAt,
    string?  RejectionReason,
    string?  ErrorMessage,
    /// <summary>
    /// The artifact THIS attempt dispatched — an order can hold several artifacts and several
    /// attempts, so the passport names the pairing instead of implying one. Recovered from the
    /// attempt's deterministic idempotency key (the only durable attempt→artifact record;
    /// <c>delivery_attempts</c> carries no artifact FK) and confirmed against this order's artifact
    /// rows. Null when nothing proves the link — a legacy/test-fire attempt with no key, or a key
    /// naming an artifact this order does not own. Null must render as NO download offer: we never
    /// guess which bytes went out.
    /// </summary>
    Guid?    ArtifactId,
    /// <summary>
    /// SHA-256 hex of the payload bytes ACTUALLY dispatched on this attempt, as recorded at
    /// dispatch time. Equal to the artifact's own hash when the delivered payload matched what the
    /// transform produced. Null when the attempt failed before the payload was downloaded.
    /// </summary>
    string?  ArtifactSha256
);

/// <summary>
/// What is known about the supplier's side of the delivery, assembled from the existing delivery
/// data (no dependency on the separate OrderConfirmation model being built elsewhere).
///
/// <para>Only <c>rejected</c> is a supplier VERDICT. <c>delivered</c> says the handover succeeded
/// and nothing more — ProcuLink parses no functional acknowledgement (997 / CONTRL /
/// <c>ApplicationResponse</c> / MDN / cXML <c>&lt;Response&gt;</c>) on any channel.</para>
/// </summary>
public record PassportSupplierResponse(
    /// <summary>
    /// <c>delivered</c> | <c>rejected</c> | <c>unknown</c> — derived from order status + the latest
    /// delivery attempt.
    ///
    /// <para>This emitted <c>acknowledged</c> until 2026-08-14, and every successful delivery
    /// satisfied it, so the passport rendered "Accepted" / "Acknowledged by supplier" for orders no
    /// supplier had answered — including SFTP, FTPS and SMTP, which have no back-channel at all.
    /// <c>delivered</c> replaces it because that is the whole of what the transport observed. Do not
    /// reintroduce an <c>acknowledged</c> value until something actually parses an acknowledgement.</para>
    /// </summary>
    string    Outcome,
    /// <summary>
    /// When OUR dispatch call returned success. See
    /// <see cref="Core.Entities.DeliveryAttempt.TransportAcceptedAt"/>; not a supplier acknowledgement.
    /// </summary>
    DateTime? TransportAcceptedAt,
    string?   RejectionReason,
    int?      ResponseCode,
    /// <summary>
    /// The supplier endpoint's raw response body as captured on the latest attempt — on a rejection,
    /// and (since 2026-08-14) on a 2xx too, because a 2xx can carry an application-level refusal and
    /// dropping the body left an operator debugging a silent rejection with nothing to read.
    ///
    /// <para><b>Untrusted, supplier-controlled bytes.</b> Bounded to
    /// <see cref="Core.Entities.DeliveryAttempt.MaxResponseBodyLength"/>. A client must attribute it
    /// to the supplier and must never present it as ProcuLink's own words; ProcuLink does not parse
    /// it and draws no conclusion from it.</para>
    /// </summary>
    string?   ResponseBody
);

/// <summary>One ordered step in the order's lifecycle, sourced from audit_events.</summary>
public record PassportTimelineEvent(
    string   Action,
    DateTime At,
    /// <summary>Raw audit payload as JSON text; null when the event carried no payload.</summary>
    string?  Detail
);
