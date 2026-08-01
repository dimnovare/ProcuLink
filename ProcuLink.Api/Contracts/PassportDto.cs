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
    float   Confidence
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
    DateTime? AcknowledgedAt,
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
/// Supplier acknowledgement / rejection, assembled from the existing delivery data
/// (no dependency on the separate OrderConfirmation model being built elsewhere).
/// </summary>
public record PassportSupplierResponse(
    /// <summary>acknowledged | rejected | unknown — derived from order status + the latest delivery attempt.</summary>
    string    Outcome,
    DateTime? AcknowledgedAt,
    string?   RejectionReason,
    int?      ResponseCode,
    /// <summary>Supplier endpoint's raw response/NACK body, if captured on the latest attempt.</summary>
    string?   ResponseBody
);

/// <summary>One ordered step in the order's lifecycle, sourced from audit_events.</summary>
public record PassportTimelineEvent(
    string   Action,
    DateTime At,
    /// <summary>Raw audit payload as JSON text; null when the event carried no payload.</summary>
    string?  Detail
);
