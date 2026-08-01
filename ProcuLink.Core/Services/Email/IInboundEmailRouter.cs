namespace ProcuLink.Core.Services.Email;

/// <summary>
/// Routes an inbound email payload (e.g. from a Postmark Inbound webhook) into
/// the existing CreateStub + ParseOrderJob pipeline. The router is responsible
/// for resolving the tenant from the recipient address, validating the tenant
/// is allowed to ingest, filtering supported attachments, and creating one
/// order stub per supported attachment.
/// </summary>
/// <remarks>
/// The router mirrors the behaviour of <see cref="IClaimedOrderCreator.CreateClaimedStubAsync"/>
/// + <c>ParseOrderJob.Enqueue</c> used by both <c>OrdersController.Upload</c>
/// (browser upload) and <c>EmailPollingJob</c> (IMAP poll). It is the third
/// ingress channel on the same pipeline — only the ingress is new.
/// </remarks>
public interface IInboundEmailRouter
{
    /// <summary>
    /// Parses the inbound payload, extracts attachments, finds the tenant by
    /// recipient address, creates one order stub per CSV/XLSX/PDF/XML/EDI/TXT
    /// attachment, and enqueues parse jobs.
    /// </summary>
    /// <param name="payload">The inbound message envelope (from/to/subject + attachments).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A result describing whether the tenant could be resolved, which org received
    /// the message, and the list of created order ids (one per supported attachment).
    /// Unsupported attachments are skipped silently; the result is still
    /// <see cref="InboundEmailResult.Success"/> = <c>true</c> with possibly empty
    /// <see cref="InboundEmailResult.CreatedOrderIds"/>.
    /// </returns>
    Task<InboundEmailResult> RouteAsync(InboundEmailPayload payload, CancellationToken ct);
}

/// <summary>
/// Provider-neutral shape of an inbound email after the webhook has decoded it.
/// Mappers (e.g. Postmark Inbound JSON, SendGrid Inbound Parse) translate their
/// vendor payloads into this record before calling the router.
/// </summary>
/// <param name="FromEmail">The sender address — used for audit and future buyer-resolution.</param>
/// <param name="ToEmail">
/// The recipient address. The router parses the host portion as
/// <c>{tenant-slug}.proculink.app</c> to resolve the org.
/// </param>
/// <param name="Subject">Free-text subject line. Not used for routing today; kept for audit.</param>
/// <param name="Attachments">Decoded attachments — the router filters by extension.</param>
/// <param name="Body">
/// Plain-text body of the message. When no supported attachment yields an order,
/// the router falls back to <see cref="Ai.IEmailBodyOrderExtractor"/> on this
/// field. Webhook adapters should prefer the provider's text body and strip HTML
/// tags from the HTML body when only that is available.
/// </param>
/// <param name="ProviderMessageId">
/// The mail provider's own stable identifier for this message (Postmark's <c>MessageID</c>).
/// It is the dedupe key for the PUSH channel: a provider retries a non-200 many times over hours
/// — Postmark ten times over ~10.5 — and every retry carries this same id, so it is what tells a
/// re-delivery apart from a genuinely new message. Null/blank when a provider does not supply one;
/// the per-attachment content hash then carries the dedupe on its own.
/// </param>
public sealed record InboundEmailPayload(
    string FromEmail,
    string ToEmail,
    string Subject,
    IReadOnlyList<InboundAttachment> Attachments,
    string? Body = null,
    string? ProviderMessageId = null);

/// <summary>A single decoded attachment from an inbound email.</summary>
/// <param name="FileName">Original file name as supplied by the sender.</param>
/// <param name="ContentType">MIME content type as declared by the sender.</param>
/// <param name="Content">Decoded attachment bytes (already base64-decoded from the wire format).</param>
public sealed record InboundAttachment(
    string FileName,
    string ContentType,
    byte[] Content);

/// <summary>
/// Why a message was rejected — and therefore whether the sending provider should
/// try again. The webhook maps this to the HTTP status, because that status is the
/// only thing a provider's retry engine reads.
/// </summary>
/// <remarks>
/// Postmark retries any non-200 response 10 times over roughly 10.5 hours and then
/// files the message under <c>Failed</c>, where a human can re-fire it with
/// <c>PUT /messages/inbound/{id}/retry</c> for the 45 days it is retained. A 200
/// marks the message <c>Processed</c>: no retry, and nothing left to re-fire. So the
/// choice is not cosmetic — it decides whether a rejected message stays recoverable.
/// </remarks>
public enum InboundEmailRejectionKind
{
    /// <summary>
    /// Not stated. Treated as <see cref="Transient"/> by the webhook: an unlabelled
    /// rejection keeps its retries, so a branch added without thinking about this
    /// costs duplicate webhook calls rather than a silently dropped purchase order.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// The failure is on the sending side and no retry of this same message can fix
    /// it — the address is not one of ours, or names a tenant that does not exist.
    /// Re-sending the identical payload re-runs the identical decision, so the
    /// webhook stops the retries.
    /// </summary>
    Permanent,

    /// <summary>
    /// The message is fine; we are the ones who cannot take it right now (the
    /// organisation is missing from the database, or its account status blocks
    /// ingest). Retries are wanted: they give the operator a window in which fixing
    /// the cause lands the order with no further action, and the provider's failed
    /// bucket keeps the message re-fireable afterwards.
    /// </summary>
    Transient,
}

/// <summary>
/// Outcome of a router call. <see cref="Success"/> is <c>true</c> when the
/// tenant resolved and processing completed without infrastructure failure —
/// even if no order was created (e.g. all attachments were unsupported, or the
/// message carried no attachment at all).
/// <para>
/// <see cref="Success"/> is <c>false</c> only for a message the product cannot act
/// on: the recipient address does not parse, the tenant slug is unknown, the
/// resolved organisation does not exist, or its account status blocks ingest.
/// <see cref="RejectionKind"/> then says whether the sender should try again — see
/// <see cref="InboundEmailRejectionKind"/>. An organisation with NO supplier is
/// deliberately not in that set — its attachments are imported unrouted and held for
/// assignment, so the call succeeds and the webhook answers 200.
/// </para>
/// <para>
/// A returned result always means the decision is durable. To ask the provider to
/// deliver the message again after an infrastructure fault (database down, storage
/// unreachable), let the exception escape instead: the resulting 5xx is what the
/// retry engine reads.
/// </para>
/// </summary>
/// <param name="Success">True if routing completed; false if the message was rejected outright.</param>
/// <param name="OrgId">The resolved organisation id, or null if tenant resolution failed.</param>
/// <param name="CreatedOrderIds">Order stub ids created — one per successfully ingested attachment.</param>
/// <param name="Error">Human-readable failure reason when <see cref="Success"/> is false.</param>
/// <param name="RejectionKind">
/// Whether a rejected message is worth re-sending. Ignored when <see cref="Success"/>
/// is true.
/// </param>
public sealed record InboundEmailResult(
    bool Success,
    Guid? OrgId,
    IReadOnlyList<Guid> CreatedOrderIds,
    string? Error,
    InboundEmailRejectionKind RejectionKind = InboundEmailRejectionKind.Unspecified);

/// <summary>
/// Decouples the router (which lives in <c>ProcuLink.Infrastructure</c>) from
/// the Hangfire-bound <c>ParseOrderJob</c> (which lives in <c>ProcuLink.Api</c>).
/// The Api project supplies the concrete adapter that calls
/// <c>ParseOrderJob.Enqueue(IBackgroundJobClient, orderId, orgId)</c>; tests
/// supply a recording fake.
/// </summary>
public interface IParseJobEnqueuer
{
    /// <summary>Enqueues a parse job for the given order stub.</summary>
    Task EnqueueAsync(Guid orderId, Guid orgId, CancellationToken ct);
}
