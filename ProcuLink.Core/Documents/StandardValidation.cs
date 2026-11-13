namespace ProcuLink.Core.Documents;

public enum ValidationStatus { Passed, Failed, Unavailable }

/// <summary>One broken rule. Severity is "error", "warning" or "info"; only "error" fails a document.</summary>
public sealed record ValidationIssue(string RuleId, string Severity, string Message);

public sealed record ValidationOutcome(ValidationStatus Status, IReadOnlyList<ValidationIssue> Issues);

/// <summary>Checks a rendered document against the published rules of a standard.</summary>
public interface IStandardValidator
{
    /// <summary>Never throws for an outage: an unreachable or unreadable checker answers <see cref="ValidationStatus.Unavailable"/>.</summary>
    Task<ValidationOutcome> ValidateAsync(string vesid, byte[] xml, CancellationToken ct);
}
