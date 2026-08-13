using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace ProcuLink.Transform.Conformance;

/// <summary>One finding from XSD validation, with the position that produced it.</summary>
/// <param name="Severity">As reported by <see cref="XmlSchemaValidationFlags"/>.</param>
/// <param name="Message">The validator's own message, verbatim.</param>
/// <param name="LineNumber">1-based line in the validated document, or 0 when unknown.</param>
internal sealed record UblSchemaFinding(XmlSeverityType Severity, string Message, int LineNumber)
{
    public override string ToString() =>
        LineNumber > 0 ? $"line {LineNumber}: {Message}" : Message;
}

/// <summary>
/// The outcome of validating one document against the vendored OASIS UBL 2.1 Order schema.
/// </summary>
/// <param name="Valid">True only when validation actually RAN and produced no finding.</param>
/// <param name="Findings">Schema errors and warnings, capped at <see cref="UblSchemaValidator.MaxFindings"/>.</param>
/// <param name="Truncated">True when more findings existed than were kept.</param>
internal sealed record UblSchemaResult(bool Valid, IReadOnlyList<UblSchemaFinding> Findings, bool Truncated);

/// <summary>
/// Validates an emitted UBL order against the vendored OASIS UBL 2.1 Order-2 schema, using the in-box
/// <see cref="System.Xml.Schema"/> validator. No package, no network, no Schematron.
///
/// <para><b>What a pass here proves, exactly.</b> That the document is well-formed XML and satisfies
/// the OASIS UBL 2.1 Order-2 grammar: the elements OASIS declares, in the order OASIS declares them,
/// with the cardinalities and datatypes OASIS declares. That is a stronger claim than anything else in
/// <c>ProcuLink.Transform/Conformance/</c>, because the rule set is OASIS's machine-readable schema and
/// not a summary of it that ProcuLink wrote — the other checkers validate our output against our own
/// reading of a standard, which cannot be evidence about the standard.</para>
///
/// <para><b>What it does not prove.</b> Not that a supplier will accept the document: acceptance
/// depends on the receiver's own profile, its master data, and whether the item codes and party
/// identifiers mean anything on their side. Not Peppol BIS conformance: Peppol's rules are Schematron
/// business rules layered on top of this schema, and there is no Schematron in this repository. Not
/// that the values are right — a schema cannot tell a correct price from a wrong one. It is a grammar
/// check, and it is worth exactly one sentence:
/// <em>"Valid against the OASIS UBL 2.1 Order schema."</em></para>
///
/// <para><b>Two silent-pass traps are closed deliberately.</b> XSD validation in .NET reports both as
/// warnings, not errors, and a caller that only counts errors reads either as a clean pass:</para>
/// <list type="number">
///   <item><description><b>No schema for the root's namespace.</b> If the root element is not in the
///   UBL Order-2 namespace, .NET has nothing to validate against and validates nothing — reporting
///   success for a document it never examined. So the namespace is asserted first, and
///   <see cref="XmlSchemaValidationFlags.ReportValidationWarnings"/> is on so the underlying
///   "could not find schema information" warning is a finding rather than silence.</description></item>
///   <item><description><b>An unresolvable import.</b> Handled one layer down, in
///   <see cref="UblSchemaCatalog"/> — a schema set with missing components would report a missing
///   file as a malformed document.</description></item>
/// </list>
/// </summary>
internal static class UblSchemaValidator
{
    /// <summary>
    /// Findings are capped because the report is rendered in a panel and downloaded as Markdown, and
    /// one misplaced element in an ordered UBL sequence cascades into a finding per following sibling.
    /// The first is the informative one; the rest are echoes.
    /// </summary>
    internal const int MaxFindings = 25;

    internal static UblSchemaResult Validate(string documentText)
    {
        var findings = new List<UblSchemaFinding>();
        var truncated = false;

        void Record(XmlSeverityType severity, string message, int line)
        {
            if (findings.Count >= MaxFindings)
            {
                truncated = true;
                return;
            }

            findings.Add(new UblSchemaFinding(severity, message, line));
        }

        // The root namespace is checked BEFORE validating, because a document outside the UBL
        // namespace validates "clean" against a schema set that has nothing to say about it.
        var namespaceProblem = RootNamespaceProblem(documentText);
        if (namespaceProblem is not null)
        {
            Record(XmlSeverityType.Error, namespaceProblem, 0);
            return new UblSchemaResult(false, findings, truncated);
        }

        var settings = new XmlReaderSettings
        {
            ValidationType = ValidationType.Schema,
            Schemas = UblSchemaCatalog.Compiled,

            // The document is data from a transform, not a trusted source. No DTD (XXE), and no
            // resolver, so neither a DOCTYPE nor an xsi:schemaLocation can cause a fetch.
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };

        // ReportValidationWarnings: ON — see the class summary; a warning here is the shape a silent
        // pass takes. ProcessSchemaLocation / ProcessInlineSchema: OFF (the default, set explicitly)
        // so a document cannot introduce its own schema, which would both defeat the check and mutate
        // the process-wide compiled set.
        settings.ValidationFlags = XmlSchemaValidationFlags.ReportValidationWarnings;
        settings.ValidationEventHandler += (_, e) =>
            Record(e.Severity, e.Message, e.Exception?.LineNumber ?? 0);

        try
        {
            using var reader = XmlReader.Create(new StringReader(documentText), settings);
            while (reader.Read())
            {
                // Reading to the end is what drives validation; the events do the reporting.
            }
        }
        catch (XmlException ex)
        {
            // Well-formedness. UblProfileChecker checks that first and does not call this method on a
            // malformed document, so reaching here means the two disagree — reported, never swallowed.
            Record(XmlSeverityType.Error, $"Document could not be read as XML: {ex.Message}", ex.LineNumber);
        }

        return new UblSchemaResult(findings.Count == 0, findings, truncated);
    }

    /// <summary>
    /// A message when the root element is not the UBL 2.1 Order-2 <c>Order</c>, or null when it is.
    /// </summary>
    private static string? RootNamespaceProblem(string documentText)
    {
        XElement? root;
        try
        {
            root = XDocument.Parse(documentText).Root;
        }
        catch (XmlException)
        {
            return null; // let the validating read below report it, with its own line numbers
        }

        if (root is null)
        {
            return "Document has no root element.";
        }

        if (!string.Equals(root.Name.NamespaceName, UblSchemaCatalog.OrderNamespace, StringComparison.Ordinal))
        {
            return
                $"Root element <{root.Name.LocalName}> is in namespace " +
                $"'{(root.Name.NamespaceName.Length == 0 ? "(none)" : root.Name.NamespaceName)}', not the UBL 2.1 " +
                $"Order namespace '{UblSchemaCatalog.OrderNamespace}'. Nothing was validated: the schema " +
                "has no rules for that namespace, so a validator would report success without reading the document.";
        }

        return null;
    }
}
