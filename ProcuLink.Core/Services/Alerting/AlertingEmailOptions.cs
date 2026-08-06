namespace ProcuLink.Core.Services.Alerting;

/// <summary>
/// Where operator alerts are emailed. Bound from configuration section <c>Alerting:Email</c>
/// (env: <c>Alerting__Email__To</c>). Unset is the safe default and means "do not email" — the sink
/// becomes a silent no-op rather than throwing or crashing the Worker.
/// </summary>
public sealed class AlertingEmailOptions
{
    public const string SectionName = "Alerting:Email";

    /// <summary>
    /// Recipient address for operator alerts. Blank disables email alerting entirely.
    /// Multiple recipients may be given comma- or semicolon-separated.
    /// </summary>
    public string? To { get; set; }

    /// <summary>
    /// Subject prefix, so alerts are trivially filterable in a mail client. Default
    /// <c>[ProcuLink alert]</c>; a blank configured value falls back to the default.
    /// </summary>
    public string? SubjectPrefix { get; set; }

    /// <summary>Effective subject prefix (never blank).</summary>
    public string EffectiveSubjectPrefix =>
        string.IsNullOrWhiteSpace(SubjectPrefix) ? "[ProcuLink alert]" : SubjectPrefix.Trim();

    /// <summary>
    /// Parsed recipient list — trimmed, empties dropped. Empty when alert email is not configured.
    /// </summary>
    public IReadOnlyList<string> Recipients =>
        string.IsNullOrWhiteSpace(To)
            ? Array.Empty<string>()
            : To.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
