using System.ComponentModel.DataAnnotations.Schema;

namespace ProcuLink.Core.Entities;

/// <summary>Persisted outcome of evaluating one rule against one order (or line).</summary>
public class OrderValidationResult
{
    /// <summary>
    /// The value the rule actually judged — TRANSIENT, never persisted (<see cref="NotMappedAttribute"/>:
    /// no column, no migration, no change to the model snapshot).
    ///
    /// <para>It exists because the acceptance gate's override key must be able to tell "line 3 is
    /// 50,001 against a 50,000 cap" apart from "line 3 is 900,000 against the same cap". Without the
    /// judged value in that identity, one operator sign-off silently excused every later re-parse of
    /// the same line. <see cref="Message"/> usually embeds the value, but not for every operator
    /// (<c>not_equals</c> and <c>max_length</c> never mention it), so the key reads this field
    /// instead of digesting prose.</para>
    ///
    /// <para>Populated only on freshly evaluated rows. A row read back from the database has it
    /// null, which is correct: the value it judged belonged to the order as it was then.</para>
    /// </summary>
    [NotMapped]
    public string?  ActualValue { get; set; }

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
