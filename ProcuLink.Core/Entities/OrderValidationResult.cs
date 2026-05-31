namespace ProcuLink.Core.Entities;

/// <summary>Persisted outcome of evaluating one rule against one order (or line).</summary>
public class OrderValidationResult
{
    public Guid     Id         { get; set; }
    public Guid     OrgId      { get; set; }
    public Guid     OrderId    { get; set; }
    public Guid?    ProfileId  { get; set; }
    public Guid?    RuleId     { get; set; }
    public int?     LineNumber { get; set; }
    /// <summary>info | warning | error</summary>
    public string   Severity   { get; set; } = "error";
    /// <summary>pass | fail</summary>
    public string   Status     { get; set; } = "pass";
    public string   Code       { get; set; } = string.Empty;
    public string   Message    { get; set; } = string.Empty;
    public DateTime DetectedAt { get; set; }

    public Organisation Organisation { get; set; } = null!;
}
