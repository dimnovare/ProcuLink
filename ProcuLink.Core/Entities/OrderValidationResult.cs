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

    /// <summary>
    /// <see cref="StatusPass"/> | <see cref="StatusFail"/> | <see cref="StatusNotEvaluated"/>.
    /// <para>The third value is not a shade of pass. It means the rule DID NOT RUN because the input
    /// it judges was absent from the order — and it exists because reporting those as "pass" told a
    /// customer a check had cleared when nothing had been examined. See
    /// <c>SupplierAcceptanceService.RuleOutcome</c>.</para>
    /// </summary>
    public string   Status     { get; set; } = StatusPass;

    /// <summary>The rule ran and the order satisfied it.</summary>
    public const string StatusPass = "pass";

    /// <summary>The rule ran and the order did not satisfy it.</summary>
    public const string StatusFail = "fail";

    /// <summary>
    /// The rule COULD NOT RUN: the value it judges was not present on the order, so there was
    /// nothing to check. Never blocks — <c>GetBlockingFailuresAsync</c> only collects
    /// <see cref="StatusFail"/> — but it must never be reported as a pass either.
    /// </summary>
    public const string StatusNotEvaluated = "not_evaluated";
    public string   Code       { get; set; } = string.Empty;
    public string   Message    { get; set; } = string.Empty;
    public DateTime DetectedAt { get; set; }

    public Organisation Organisation { get; set; } = null!;
}
