namespace ProcuLink.Core.Services.Email;

/// <summary>
/// Routes an inbound email payload (e.g. from a Postmark Inbound webhook) into
/// the existing CreateStub + ParseOrderJob pipeline. The router is responsible
/// for resolving the tenant from the recipient address, validating the tenant
/// is allowed to ingest, filtering supported attachments, and creating one
/// order stub per supported attachment.
/// </summary>
/// <remarks>
/// The router mirrors the behaviour of <see cref="IOrderService.CreateStubAsync"/>
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
public sealed record InboundEmailPayload(
    string FromEmail,
    string ToEmail,
    string Subject,
    IReadOnlyList<InboundAttachment> Attachments,
    string? Body = null);

/// <summary>A single decoded attachment from an inbound email.</summary>
/// <param name="FileName">Original file name as supplied by the sender.</param>
/// <param name="ContentType">MIME content type as declared by the sender.</param>
/// <param name="Content">Decoded attachment bytes (already base64-decoded from the wire format).</param>
public sealed record InboundAttachment(
    string FileName,
    string ContentType,
    byte[] Content);

/// <summary>
/// Outcome of a router call. <see cref="Success"/> is <c>true</c> when the
/// tenant resolved and processing completed without infrastructure failure —
/// even if no order was created (e.g. all attachments were unsupported).
/// <see cref="Success"/> is <c>false</c> when the tenant could not be resolved,
/// the tenant is in a non-ingest account status, or no attachments were present
/// on the message.
/// </summary>
/// <param name="Success">True if routing completed; false if the message was rejected outright.</param>
/// <param name="OrgId">The resolved organisation id, or null if tenant resolution failed.</param>
/// <param name="CreatedOrderIds">Order stub ids created — one per successfully ingested attachment.</param>
/// <param name="Error">Human-readable failure reason when <see cref="Success"/> is false.</param>
public sealed record InboundEmailResult(
    bool Success,
    Guid? OrgId,
    IReadOnlyList<Guid> CreatedOrderIds,
    string? Error);

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
