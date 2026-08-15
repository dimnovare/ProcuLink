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

/// <summary>
/// What a check's verdict is worth as evidence — which is not the same question as whether it
/// passed.
///
/// <para>The report is downloadable and leaves the product, so this distinction is the difference
/// between "a standards body's own machine-readable artifact accepted this document" and
/// "ProcuLink agreed with ProcuLink". Both are worth printing; only one is worth forwarding as
/// proof.</para>
///
/// <para><b><see cref="SelfCheck"/> is the default deliberately.</b> A new check is the weaker
/// claim until someone states otherwise, because the failure this codebase keeps repeating is an
/// unclassified value falling through to the favourable reading. Opting a check up to
/// <see cref="ExternalArtifact"/> should require naming the artifact.</para>
/// </summary>
public enum ConformanceEvidence
{
    /// <summary>
    /// Checked against ProcuLink's own reading of the named profile.
    ///
    /// <para>On ProcuLink's own output a number of these are near-tautological: the value asserted
    /// is a constant this codebase's emitter just wrote (<c>GS01</c>=<c>PO</c>, <c>ST01</c>=<c>850</c>,
    /// <c>cbc:UBLVersionID</c>=<c>2.1</c>), or an emitter fallback substitutes a placeholder
    /// precisely so the element is never empty, which is exactly the condition the check tests. A
    /// PASS therefore carries no information about the document. A FAILURE still does, and these
    /// checks are kept for that, and because they name a fault in procurement terms a grammar
    /// error cannot.</para>
    /// </summary>
    SelfCheck,

    /// <summary>
    /// Validated against a third-party artifact vendored into this repo unmodified — today only the
    /// OASIS UBL 2.1 Order-2 XSD (<c>Conformance/Schemas/ubl-2.1/</c>, provenance and SHA-256 in
    /// <c>PROVENANCE.md</c>), via <see cref="UblSchemaValidator"/>. A verdict here was reached by
    /// something ProcuLink did not author, so it is the only kind of row in this report that is
    /// evidence about the standard rather than about ProcuLink.
    /// </summary>
    ExternalArtifact,
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
/// <param name="Evidence">What this row's verdict is worth — see <see cref="ConformanceEvidence"/>. Defaults to the weaker claim.</param>
public sealed record ConformanceCheck(
    string Code,
    ConformanceSeverity Severity,
    bool Passed,
    string Message,
    string ProfileRef,
    ConformanceEvidence Evidence = ConformanceEvidence.SelfCheck);

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

    /// <summary>Count of rows validated against a third-party artifact rather than against our own reading of a spec.</summary>
    public int ExternalArtifactCount => Checks.Count(c => c.Evidence == ConformanceEvidence.ExternalArtifact);

    /// <summary>
    /// Renders the report as a downloadable Markdown document. Deterministic — the
    /// same report always renders byte-identically (no timestamps inside).
    ///
    /// <para>This file leaves the product: a customer can forward it to a supplier or an access
    /// point as evidence. So it states its own scope — and, since 2026-08-14, states it PER ROW
    /// rather than in one blanket sentence.</para>
    ///
    /// <para><b>Why the blanket sentence had to go.</b> It read "Not a schema validation and not a
    /// certification", which was false in both directions at once. Too generous, because most rows
    /// are self-checks that on ProcuLink's own output cannot fail — the emitter writes the constant
    /// the checker then asserts, so a PASS restated our own output back to us and was rendered
    /// under a Download button as evidence. Too harsh, because <c>ubl.xsd</c> really does validate
    /// against the vendored OASIS UBL 2.1 schema, and denying that gave away the one verdict here
    /// that a third party actually produced. Under-claiming a real check is its own false
    /// statement. The two kinds are now counted in the header, explained once, and labelled on
    /// every row.</para>
    /// </summary>
    public string ToMarkdown()
    {
        var external = ExternalArtifactCount;
        var self     = Checks.Count - external;

        var sb = new System.Text.StringBuilder();
        sb.Append("# Standards conformance report\n\n");
        sb.Append($"- **Profile:** {ProfileName}\n");
        sb.Append($"- **Version:** {ProfileVersion}\n");
        sb.Append($"- **Result:** {Verdict()}\n");
        sb.Append($"- **Checks:** {Checks.Count} total · {ErrorCount} error(s) · {WarningCount} warning(s)\n");
        sb.Append($"- **Evidence:** {external} published-schema check(s) · {self} self-check(s)\n\n");

        sb.Append("## What this result is worth\n\n");

        sb.Append(external > 0
            ? "**Published schema.** Validated against a standards-body artifact vendored into " +
              "ProcuLink unmodified — element order, cardinality and datatypes. That verdict was " +
              "reached by the published grammar itself, not by ProcuLink's summary of it, and it " +
              "catches faults a presence check structurally cannot: the content models are ordered " +
              "sequences, so a document with every mandatory element present but two of them " +
              "transposed passes every self-check below and fails this one.\n\n"
            : "**No row in this report was validated against a published schema.** Every check " +
              "below is a self-check in the sense that follows.\n\n");

        sb.Append("**Self-check.** Presence and cardinality, checked against ProcuLink's own reading " +
                  "of the named profile. Some of these assert a value ProcuLink's own transformer " +
                  "writes as a fixed constant, or an element it fills with a placeholder when the " +
                  "source order has none — which is the very condition the check tests. On " +
                  "ProcuLink's own output those cannot fail, so passing one is not independent " +
                  "evidence about the document. A self-check that FAILS is still meaningful, and " +
                  "these rows name a fault in procurement terms a grammar error cannot.\n\n");

        sb.Append("This report is not a certification against any profile, and not a statement that " +
                  "the receiving party will accept the document. Validate with them before relying " +
                  "on this result.\n\n");

        sb.Append("| Result | Evidence | Severity | Check | Profile reference | Detail |\n");
        sb.Append("|---|---|---|---|---|---|\n");
        foreach (var c in Checks)
        {
            var mark = c.Passed ? "PASS" : "FAIL";
            sb.Append($"| {mark} | {EvidenceLabel(c.Evidence)} | {c.Severity} | `{c.Code}` | `{MdCell(c.ProfileRef)}` | {MdCell(c.Message)} |\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// The headline verdict. Three-valued on purpose: a report with no checks has examined nothing,
    /// and saying FAIL of it would be as false as saying PASS — nothing failed, because nothing
    /// ran. Same reasoning as the publish gate's <c>not_exercised</c> outcome.
    /// </summary>
    private string Verdict() =>
        Checks.Count == 0
            ? "NOT CHECKED — this report contains no checks, so it states nothing about the document"
            : OverallPass ? "PASS" : "FAIL";

    private static string EvidenceLabel(ConformanceEvidence evidence) =>
        evidence == ConformanceEvidence.ExternalArtifact ? "Published schema" : "Self-check";

    /// <summary>Escapes pipe / newline characters so a value cannot break the Markdown table layout.</summary>
    private static string MdCell(string value) =>
        (value ?? string.Empty).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
