namespace ProcuLink.Core.Entities;

/// <summary>
/// An accountability record that must outlive the organisation it is about — today, the receipt
/// of an organisation erase (<c>admin.organisation_erased</c>).
///
/// <para><c>audit_events</c> cannot hold that receipt: its <c>org_id</c> is a required FK that
/// cascades on organisation delete, so the receipt would be erased with the organisation it
/// records. <see cref="SubjectOrgId"/> is deliberately NOT named <c>OrgId</c> and carries no FK, so
/// the tenancy tooling (query filters, the org-FK guard, the eraser's own table walk) never treats
/// these rows as tenant data.</para>
/// </summary>
public class PlatformAuditEvent
{
    public Guid Id { get; set; }
    public string Action { get; set; } = string.Empty;
    public Guid SubjectOrgId { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    /// <summary>jsonb — the full <c>OrgEraseReceipt</c>.</summary>
    public string Payload { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
}
