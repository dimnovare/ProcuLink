namespace ProcuLink.Core.Services.Delivery;

public sealed record UpsertDeliveryConfigRequest(
    string Protocol,
    bool AutoDeliver,
    string ConfigJson,
    string? CredentialsJson,
    string? OutputFormat = null,
    // Optional cXML network credentials (only meaningful when OutputFormat is cxml). Null/omitted
    // leaves any saved cXML credentials untouched (mirrors CredentialsJson = null = keep).
    CxmlCredentialsInput? CxmlCredentials = null,
    // WP-33 — "auto-send when clean". Distinct from AutoDeliver above: AutoDeliver decides whether
    // dispatch follows a transform a human asked for; this decides whether the transform happens
    // without anyone asking. While auto-send is in its dry-run stage, turning it on records what
    // would have been sent and sends nothing.
    //
    // NULLABLE, and null/omitted = keep whatever is saved — the same rule as CredentialsJson and
    // CxmlCredentials above, and the same rule the recorded SSH host-key fingerprints get inside
    // UpsertAsync. It shipped as a non-nullable `bool` defaulting to false, which made "the client
    // has never heard of this property" indistinguishable from "the operator switched it off": no
    // caller sends it today, so every unrelated save — a changed timeout, a new URL — silently
    // turned auto-send off. Sending it explicitly, including as `false`, still wins and is the
    // deliberate way to switch it off.
    bool? AutoTransform = null);

/// <summary>
/// cXML network credentials sent to the supplier delivery-config upsert. Identities are cleartext;
/// <see cref="SenderSharedSecret"/> is WRITE-ONLY (never returned): null/omitted = keep the saved
/// secret, blank = clear it, non-blank = replace it — the same leave-blank-to-keep pattern as the
/// delivery transport credentials.
/// </summary>
public sealed record CxmlCredentialsInput(
    string? FromDomain,
    string? FromIdentity,
    string? ToDomain,
    string? ToIdentity,
    string? SenderDomain,
    string? SenderIdentity,
    string? SenderSharedSecret = null,
    // Optional configurable cXML DOCTYPE (T7). Free-text DTD URI; null/blank = no DOCTYPE.
    string? DtdSystemId = null,
    string? DtdPublicId = null);

/// <summary>
/// The non-secret cXML identity fields persisted as cleartext JSON in
/// <c>SupplierDeliveryConfig.CxmlConfigJson</c>. Serialized by the delivery-config service (write)
/// and the cXML credential resolver (read) — kept in one place so both sides agree on the keys.
/// </summary>
public sealed record CxmlIdentityFields(
    string? FromDomain,
    string? FromIdentity,
    string? ToDomain,
    string? ToIdentity,
    string? SenderDomain,
    string? SenderIdentity,
    // Configurable cXML DOCTYPE (T7), persisted as camelCase dtdSystemId / dtdPublicId. The FRONTEND
    // writes these keys. Additive: an older row without them deserializes to null = no DOCTYPE.
    string? DtdSystemId = null,
    string? DtdPublicId = null);

public sealed record DeliveryConfigResponse(
    Guid SupplierId,
    string Protocol,
    bool AutoDeliver,
    string ConfigJson,
    bool HasCredentials,
    string? CredentialsDisplay,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? OutputFormat,
    // Honest governance signal: when revision authority routes this supplier's delivery via a
    // published connection revision, the editor must tell the operator delivery follows that
    // versioned snapshot — not the raw row below. RevisionGoverned=false ⇒ the live row governs.
    bool RevisionGoverned = false,
    int? ActiveRevisionVersionNo = null,
    // Null unless RevisionGoverned. False ⇒ the live row below differs from the version that
    // currently governs delivery (save to publish a new revision and bring them in sync).
    bool? LiveMatchesActiveRevisionDelivery = null,
    // Configured cXML network identities (cleartext) + whether a Sender SharedSecret is stored.
    // Null when the supplier has no cXML credentials configured. The secret itself is never returned.
    CxmlCredentialsResponse? CxmlCredentials = null,
    // WP-33 — the per-supplier auto-send indicator. An operator must always be able to see which
    // suppliers are automatic, so this travels with every read of the delivery config.
    bool AutoTransform = false,
    // Set when the SAVED config carries a fault the write path now refuses: an endpoint the
    // transport policy rejects (written before TLS enforcement existed), a credential sitting in the
    // extra-headers map, or both. Delivery continues — refusing mid-flight would turn a security
    // weakness into an outage — so this is how the operator finds out. Null when fine. Never
    // contains the URL or a header value: those are precisely the strings that would copy the secret
    // into the editor.
    string? InsecureTransportWarning = null);

/// <summary>
/// cXML credentials returned to the editor: the cleartext identities plus a boolean flag for the
/// write-only Sender SharedSecret (so the UI can render "leave blank to keep"). The secret value
/// is NEVER returned.
/// </summary>
public sealed record CxmlCredentialsResponse(
    string? FromDomain,
    string? FromIdentity,
    string? ToDomain,
    string? ToIdentity,
    string? SenderDomain,
    string? SenderIdentity,
    bool HasSharedSecret,
    // Configurable cXML DOCTYPE (T7) — returned cleartext so the editor can pre-fill the DTD inputs.
    string? DtdSystemId = null,
    string? DtdPublicId = null);

public sealed record DeliveryTestResult(
    bool Success,
    string? ErrorMessage,
    int? ResponseCode);
