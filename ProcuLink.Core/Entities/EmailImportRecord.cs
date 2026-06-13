using System.ComponentModel.DataAnnotations.Schema;

namespace ProcuLink.Core.Entities;

/// <summary>
/// Idempotency ledger for IMAP attachment ingestion. One row per
/// (OrgId, ImapMessageId, AttachmentHash) attachment already imported into an order.
///
/// <para>The IMAP poller flags a message SEEN only AFTER all its attachments are queued, so a
/// crash between "create order stub" and "set SEEN" re-presents the same unseen message on the
/// next poll — without this ledger the same attachment is re-imported as a brand-new order. A
/// unique index on (OrgId, ImapMessageId, AttachmentHash) makes re-import a no-op, and the
/// content hash means a re-sent attachment under a new Message-Id is still deduplicated.</para>
///
/// EF table: <c>email_import_records</c>.
/// </summary>
[Table("email_import_records")]
public class EmailImportRecord
{
    public Guid Id { get; set; }

    /// <summary>Owning organisation.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The IMAP Message-Id header of the source email (may be empty if the server omits it).</summary>
    public string ImapMessageId { get; set; } = string.Empty;

    /// <summary>SHA-256 (hex) of the decoded attachment bytes — dedupes a re-sent attachment.</summary>
    public string AttachmentHash { get; set; } = string.Empty;

    /// <summary>The order stub this attachment was imported into (for traceability).</summary>
    public Guid OrderId { get; set; }

    /// <summary>Original attachment file name, for operator diagnostics.</summary>
    public string? FileName { get; set; }

    /// <summary>UTC timestamp when the attachment was imported.</summary>
    public DateTime ImportedAt { get; set; }
}
