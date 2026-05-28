namespace ProcuLink.Core.Entities;

/// <summary>
/// Tracks total OpenAI tokens consumed per organisation per calendar month.
/// Composite primary key (OrgId, Year, Month). Updated after every successful
/// OpenAI call. Used by the per-org monthly cost cap in <c>OpenAiMappingService</c>.
/// </summary>
public class AiUsageMonthly
{
    public Guid OrgId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public long TokensUsed { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
