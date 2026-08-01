using System.ComponentModel.DataAnnotations.Schema;

namespace ProcuLink.Core.Entities;

/// <summary>
/// Idempotency ledger for email attachment ingestion — BOTH email channels. One row per
/// (OrgId, ImapMessageId, AttachmentHash) piece of content already imported into an order.
///
/// <para><b>IMAP pull.</b> The poller flags a message SEEN only AFTER all its attachments are
/// queued, so a crash between "create order stub" and "set SEEN" re-presents the same unseen
/// message on the next poll — without this ledger the same attachment is re-imported as a
/// brand-new order. A unique index on (OrgId, ImapMessageId, AttachmentHash) makes re-import a
/// no-op, and the content hash means a re-sent attachment under a new Message-Id is still
/// deduplicated.</para>
///
/// <para><b>Postmark push.</b> The inbound webhook claims rows here too. A mail provider re-POSTs
/// any non-200 many times over hours (Postmark: ten attempts over ~10.5), so a transient failure
/// after the order was created would otherwise produce one duplicate order — and one duplicate
/// supplier delivery — per retry. <c>InboundEmailRouter</c> stores the provider's own message id
/// under a <c>postmark:</c> prefix so the two channels' identifier namespaces cannot alias each
/// other on the unique index, and hashes the email BODY under a <c>body:</c> prefix for the
/// body-NLP path, which has no attachment to hash.</para>
///
/// <para>Both channels use the same claim-first ordering: this row is committed BEFORE the order
/// is created, carrying the order's pre-generated id, so a crash in between is resumable rather
/// than a silently lost order. See
/// <c>ProcuLink.Infrastructure.Services.Ingress.IngressDedupe</c> for the full contract.</para>
///
/// EF table: <c>email_import_records</c>.
/// </summary>
[Table("email_import_records")]
public class EmailImportRecord
{
    public Guid Id { get; set; }

    /// <summary>Owning organisation.</summary>
    public Guid OrgId { get; set; }

    /// <summary>
    /// The source email's message identifier. IMAP pull stores the RFC-822 <c>Message-Id</c> header
    /// verbatim (empty when the server omits it); the Postmark push channel stores its provider
    /// <c>MessageID</c> behind a <c>postmark:</c> prefix. The column name is historical — it predates
    /// the push channel, and renaming it is a cosmetic migration not worth bundling with a
    /// correctness fix.
    /// </summary>
    public string ImapMessageId { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 (hex) of the decoded attachment bytes — dedupes a re-sent attachment even under a new
    /// message id. The body-NLP path stores a <c>body:</c>-prefixed hash of the email text instead,
    /// so a body order and an attachment can never collide on one key.
    /// </summary>
    public string AttachmentHash { get; set; } = string.Empty;

    /// <summary>
    /// The order this content was (or will be) imported into. PRE-GENERATED and written atomically
    /// with the claim, so it is populated before the order exists: that is what lets a retry tell an
    /// abandoned claim (order missing ⇒ RESUME under this id) from a real duplicate (order present ⇒
    /// SKIP). <c>Guid.Empty</c> is the terminal sentinel — a legacy row predating this column, or a
    /// claim deliberately bounded after a PERMANENT content failure so a bad file is not retried
    /// forever.
    /// </summary>
    public Guid OrderId { get; set; }

    /// <summary>Original attachment file name, for operator diagnostics.</summary>
    public string? FileName { get; set; }

    /// <summary>UTC timestamp when the attachment was imported.</summary>
    public DateTime ImportedAt { get; set; }
}
