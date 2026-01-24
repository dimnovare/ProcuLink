namespace ProcuLink.Core.Canonical;

/// <summary>
/// Record of a webhook delivery attempt
/// </summary>
public class DeliveryRecord
{
    public Guid OrderId { get; set; }
    public DateTime AttemptedAtUtc { get; set; }
    public string WebhookUrl { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty; // "Sent" or "Failed"
    public int? HttpStatusCode { get; set; }
    public string? ResponseSnippet { get; set; } // max 2k chars
    public string? ErrorMessage { get; set; }
}
