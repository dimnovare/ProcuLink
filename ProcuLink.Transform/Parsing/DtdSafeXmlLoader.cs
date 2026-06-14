using System.Xml;
using System.Xml.Linq;

namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Loads XML that may carry a <c>&lt;!DOCTYPE&gt;</c> declaration — which genuine
/// cXML (Coupa/Ariba/Jaggaer), UBL and SAP IDoc documents routinely include — WITHOUT
/// throwing, while staying safe from XML External Entity (XXE) attacks.
///
/// The default <see cref="XDocument"/> reader uses <see cref="DtdProcessing.Prohibit"/>
/// and throws "For security reasons DTD is prohibited in this XML document" on the very
/// first line of every real cXML PO. We instead use <see cref="DtdProcessing.Ignore"/>
/// (skip the DOCTYPE; never fetch or expand it) plus <c>XmlResolver = null</c> (no
/// external entity resolution), which accepts the DOCTYPE these documents require while
/// resolving no external entities — the XXE risk is eliminated by Ignore + null resolver.
///
/// Loading is SYNCHRONOUS on purpose: the asynchronous reader has a framework bug
/// (ParsePIAsync) when skipping a DOCTYPE's internal subset, which threw on Coupa's
/// <c>&lt;!DOCTYPE cXML SYSTEM "…"[]&gt;</c>. Purchase-order documents are small, so a
/// synchronous parse is not a throughput concern.
/// </summary>
internal static class DtdSafeXmlLoader
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver   = null,
    };

    public static XDocument Load(Stream stream)
    {
        using var reader = XmlReader.Create(stream, Settings);
        return XDocument.Load(reader);
    }
}
