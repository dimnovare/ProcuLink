namespace ProcuLink.Core.Entities;

public class ValidationRule
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    /// <summary>error | warning | info</summary>
    public string Severity { get; set; } = "error";
    /// <summary>Line item | Header | Supplier | Buyer | Amount</summary>
    public string Entity { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool AutoBlock { get; set; } = false;
    public int TriggerCount { get; set; } = 0;
    public DateTime? LastTriggeredAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
}
