namespace ProcuLink.Transform.Conformance;

/// <summary>
/// Validates a single OUTBOUND document string against ONE named
/// <see cref="StandardsProfile"/>. Each implementation runs a fixed, ordered set
/// of pragmatic structural + required-element/segment + key-cardinality checks
/// (NOT a full XSD / EDI grammar engine) and returns a
/// <see cref="ConformanceReport"/>.
///
/// <para>
/// A checker NEVER throws on malformed input: a parse / well-formedness failure
/// is itself reported as a failed <see cref="ConformanceSeverity.Error"/> check
/// (and the dependent structural checks are reported as failed too), so a report
/// is always produced.
/// </para>
/// </summary>
internal interface IProfileChecker
{
    /// <summary>The profile this checker validates against.</summary>
    StandardsProfile Profile { get; }

    /// <summary>Builds the full report for <paramref name="documentText"/>.</summary>
    ConformanceReport Check(string documentText);
}
