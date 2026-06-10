using ProcuLink.Core.Services;

namespace ProcuLink.Transform.Conformance;

/// <summary>
/// Validates a generated OUTBOUND document against the NAMED standard profile for
/// its format, producing a <see cref="ConformanceReport"/> (overall pass + named,
/// profile-referenced per-check rows). This is the Group V8 "supported means
/// validated against a named profile" capability.
///
/// <para>
/// The service is pure / stateless and has no I/O — callers hand it the document
/// text + format, so it is trivially unit-testable and reusable from the API
/// controller, a future Worker job, or the format-matrix test suite.
/// </para>
/// </summary>
public interface IConformanceService
{
    /// <summary>
    /// True when an <see cref="OutputFormat"/> has a named conformance profile.
    /// Formats without a standards profile (e.g. the generic CSV / XML / JSON
    /// supplier templates) return false.
    /// </summary>
    bool SupportsFormat(OutputFormat format);

    /// <summary>
    /// Returns the <see cref="StandardsProfile"/> a given output format is validated
    /// against, or <c>null</c> when the format has no named profile.
    /// </summary>
    StandardsProfile? ProfileForFormat(OutputFormat format);

    /// <summary>
    /// Validates <paramref name="documentText"/> against the named profile for
    /// <paramref name="format"/>.
    /// </summary>
    /// <exception cref="System.NotSupportedException">
    /// Thrown when <paramref name="format"/> has no named conformance profile
    /// (guard with <see cref="SupportsFormat"/> first).
    /// </exception>
    ConformanceReport Check(string documentText, OutputFormat format);

    /// <summary>
    /// Validates <paramref name="documentText"/> against an explicitly-named
    /// <paramref name="profile"/> (used by tests and any caller that already knows
    /// the profile, e.g. the IDoc round-trip profile that has no outbound transform).
    /// </summary>
    ConformanceReport Check(string documentText, StandardsProfile profile);
}
