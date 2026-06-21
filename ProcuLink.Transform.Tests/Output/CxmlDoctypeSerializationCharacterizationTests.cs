using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Xunit.Abstractions;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// CHARACTERIZATION (T7): before wiring a configurable cXML <c>&lt;!DOCTYPE&gt;</c> we must prove
/// EMPIRICALLY how <see cref="CxmlTransformService"/>'s serialization path renders an
/// <see cref="XDocumentType"/>. The transform builds bytes with
/// <c>doc.Declaration + Environment.NewLine + doc.ToString()</c> (CxmlTransformService.cs:161), so
/// the open question is: does <see cref="XDocument.ToString()"/> emit the <c>&lt;!DOCTYPE cXML …&gt;</c>
/// line at all, and WHERE (after the <c>&lt;?xml?&gt;</c> declaration, before the <c>&lt;cXML&gt;</c>
/// root)? These tests answer that against the live BCL before any production code changes.
/// </summary>
public class CxmlDoctypeSerializationCharacterizationTests
{
    private readonly ITestOutputHelper _out;

    public CxmlDoctypeSerializationCharacterizationTests(ITestOutputHelper output) => _out = output;

    /// <summary>Mirror EXACTLY the transform's serialization path (CxmlTransformService.cs:161).</summary>
    private static string Serialize(XDocument doc) =>
        doc.Declaration + Environment.NewLine + doc.ToString();

    [Fact]
    public void XDocumentToString_WithDocumentType_System_EmitsDoctypeLine_BeforeRoot()
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("cXML", null, "http://xml.cxml.org/schemas/cXML/1.2.024/cXML.dtd", null),
            new XElement("cXML", new XAttribute("payloadID", "x")));

        var text = Serialize(doc);
        _out.WriteLine(text);

        // The whole point: ToString() DOES render the DOCTYPE node.
        text.Should().Contain("<!DOCTYPE cXML SYSTEM \"http://xml.cxml.org/schemas/cXML/1.2.024/cXML.dtd\">");

        // Positioned AFTER the <?xml?> declaration and BEFORE the <cXML> root.
        var declIdx    = text.IndexOf("<?xml", StringComparison.Ordinal);
        var doctypeIdx = text.IndexOf("<!DOCTYPE", StringComparison.Ordinal);
        var rootIdx    = text.IndexOf("<cXML", StringComparison.Ordinal);

        declIdx.Should().BeGreaterThanOrEqualTo(0);
        doctypeIdx.Should().BeGreaterThan(declIdx);
        rootIdx.Should().BeGreaterThan(doctypeIdx);
    }

    [Fact]
    public void XDocumentToString_WithDocumentType_Public_EmitsPublicForm()
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType(
                "cXML",
                "-//cXML//DTD cXML 1.2.024//EN",
                "http://xml.cxml.org/schemas/cXML/1.2.024/cXML.dtd",
                null),
            new XElement("cXML"));

        var text = Serialize(doc);
        _out.WriteLine(text);

        text.Should().Contain(
            "<!DOCTYPE cXML PUBLIC \"-//cXML//DTD cXML 1.2.024//EN\" \"http://xml.cxml.org/schemas/cXML/1.2.024/cXML.dtd\">");
    }

    [Fact]
    public void XDocumentToString_NoDocumentType_EmitsNoDoctypeLine()
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("cXML"));

        var text = Serialize(doc);

        text.Should().NotContain("<!DOCTYPE");
    }

    [Fact]
    public void Bytes_AreUtf8_AndDoctypePresent()
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("cXML", null, "http://example.test/x.dtd", null),
            new XElement("cXML"));

        var bytes = Encoding.UTF8.GetBytes(Serialize(doc));
        Encoding.UTF8.GetString(bytes).Should().Contain("<!DOCTYPE cXML SYSTEM \"http://example.test/x.dtd\">");
    }
}
