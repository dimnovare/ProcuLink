using ProcuLink.Core.Services;

namespace ProcuLink.Transform.Conformance;

/// <summary>
/// The named standard profiles ProcuLink validates its OUTBOUND documents against.
///
/// <para>
/// Group V8 makes "supported" mean <em>validated against a named profile</em>:
/// for each output format there is exactly one profile whose mandatory
/// elements / segments and key cardinalities are checked explicitly. The
/// profiles are the same structural guarantees the deterministic 8×7 in×out
/// format matrix asserts, made explicit and named here. They are pragmatic
/// structural + required-field + segment checks — NOT a full XSD / EDI grammar
/// engine.
/// </para>
/// </summary>
public enum StandardsProfile
{
    /// <summary>cXML 1.2 — Request/OrderRequest envelope (<see cref="OutputFormat.CXml"/>).</summary>
    CXml12OrderRequest,

    /// <summary>
    /// OASIS UBL 2.1 Order-2 (<see cref="OutputFormat.Ubl"/> / <see cref="OutputFormat.UblOrder"/>).
    /// Base UBL only — no Peppol BIS profile is declared, emitted or checked. See
    /// <c>UblProfileChecker</c>.
    /// </summary>
    Ubl21Order,

    /// <summary>ANSI ASC X12 850 Purchase Order, version 004010 (<see cref="OutputFormat.X12"/> / <see cref="OutputFormat.X12_850"/>).</summary>
    X12_850,

    /// <summary>UN/EDIFACT ORDERS message, directory D.96A (<see cref="OutputFormat.EdifactOrders"/>).</summary>
    EdifactOrdersD96A,

    /// <summary>SAP IDoc ORDERS05 purchase order (inbound canonical shape; validated for round-trip conformance).</summary>
    SapIDocOrders05,
}

/// <summary>Severity of a single conformance check. <see cref="Error"/> failures fail the overall report.</summary>
public enum ConformanceSeverity
{
    /// <summary>A mandatory structural / required-element failure — fails the overall report.</summary>
    Error,

    /// <summary>A recommended-but-not-mandatory finding — does NOT fail the overall report.</summary>
    Warning,

    /// <summary>Informational only — never affects the overall result.</summary>
    Info,
}

/// <summary>
/// One named, profile-referenced conformance check result.
/// </summary>
/// <param name="Code">Stable machine code for the check, e.g. <c>cxml.orderRequestHeader.orderID</c>.</param>
/// <param name="Severity">Whether a failure of this check is fatal (<see cref="ConformanceSeverity.Error"/>) or advisory.</param>
/// <param name="Passed">True when the document satisfied the check.</param>
/// <param name="Message">Human-readable description of what was checked and the outcome.</param>
/// <param name="ProfileRef">The exact element / segment / cardinality reference in the named profile, e.g. <c>OrderRequestHeader/@orderID</c> or <c>EDIFACT BGM 1004</c>.</param>
public sealed record ConformanceCheck(
    string Code,
    ConformanceSeverity Severity,
    bool Passed,
    string Message,
    string ProfileRef);

/// <summary>
/// A complete conformance report for one document against one named profile.
/// </summary>
/// <param name="Profile">The profile enum that was checked.</param>
/// <param name="ProfileName">Human-readable profile name, e.g. "cXML 1.2 OrderRequest".</param>
/// <param name="ProfileVersion">Profile version string, e.g. "1.2.024".</param>
/// <param name="OverallPass">True when no <see cref="ConformanceSeverity.Error"/> check failed.</param>
/// <param name="Checks">The ordered list of named checks that were run.</param>
public sealed record ConformanceReport(
    StandardsProfile Profile,
    string ProfileName,
    string ProfileVersion,
    bool OverallPass,
    IReadOnlyList<ConformanceCheck> Checks)
{
    /// <summary>Count of failed checks at <see cref="ConformanceSeverity.Error"/>.</summary>
    public int ErrorCount => Checks.Count(c => !c.Passed && c.Severity == ConformanceSeverity.Error);

    /// <summary>Count of failed checks at <see cref="ConformanceSeverity.Warning"/>.</summary>
    public int WarningCount => Checks.Count(c => !c.Passed && c.Severity == ConformanceSeverity.Warning);

    /// <summary>
    /// Renders the report as a downloadable Markdown document. Deterministic — the
    /// same report always renders byte-identically (no timestamps inside).
    ///
    /// <para>This file leaves the product: a customer can forward it to a supplier or an access
    /// point as evidence. So it states its own scope. The checkers are presence + cardinality
    /// checks against a named profile's mandatory elements — not schema validation, and not
    /// certification — and a PASS that does not say so reads as a conformance guarantee ProcuLink
    /// cannot give.</para>
    /// </summary>
    public string ToMarkdown()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("# Standards conformance report\n\n");
        sb.Append($"- **Profile:** {ProfileName}\n");
        sb.Append($"- **Version:** {ProfileVersion}\n");
        sb.Append($"- **Result:** {(OverallPass ? "PASS" : "FAIL")}\n");
        sb.Append($"- **Checks:** {Checks.Count} total · {ErrorCount} error(s) · {WarningCount} warning(s)\n");
        sb.Append("- **Scope:** presence and cardinality of this profile's mandatory elements. " +
                  "Not a schema validation and not a certification — validate with the receiving " +
                  "party before relying on this result.\n\n");

        sb.Append("| Result | Severity | Check | Profile reference | Detail |\n");
        sb.Append("|---|---|---|---|---|\n");
        foreach (var c in Checks)
        {
            var mark = c.Passed ? "PASS" : "FAIL";
            sb.Append($"| {mark} | {c.Severity} | `{c.Code}` | `{MdCell(c.ProfileRef)}` | {MdCell(c.Message)} |\n");
        }

        return sb.ToString();
    }

    /// <summary>Escapes pipe / newline characters so a value cannot break the Markdown table layout.</summary>
    private static string MdCell(string value) =>
        (value ?? string.Empty).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
