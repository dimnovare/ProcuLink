using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace ProcuLink.Transform.Output.Ksef;

/// <summary>
/// The official FA(3) schema (wersjaSchemy="1-0E"), embedded byte-exact from crd.gov.pl; sources, fetch date and SHA-256 of each
/// file are in Schema/README.md:
///   http://crd.gov.pl/wzor/2025/06/25/13775/schemat.xsd (schemat_FA(3)_v1-0E.xsd)
///   http://crd.gov.pl/xml/schematy/dziedzinowe/mf/2022/01/05/eD/DefinicjeTypy/StrukturyDanych_v10-0E.xsd
///   .../ElementarneTypyDanych_v10-0E.xsd and .../KodyKrajow_v10-0E.xsd
/// It is XSD 1.0, so XmlSchemaSet compiles it. The resolver is null and every file is added under its own namespace,
/// so no schemaLocation is ever fetched.
/// </summary>
public static class Fa3Schema
{
    public const string Namespace = "http://crd.gov.pl/wzor/2025/06/25/13775/";
    private const string Types = "http://crd.gov.pl/xml/schematy/dziedzinowe/mf/2022/01/05/eD/DefinicjeTypy/";

    private static readonly Lazy<XmlSchemaSet> Set = new(() =>
    {
        var set = new XmlSchemaSet { XmlResolver = null };
        foreach (var (file, ns) in new[] { ("KodyKrajow_v10-0E.xsd", Types), ("ElementarneTypyDanych_v10-0E.xsd", Types), ("StrukturyDanych_v10-0E.xsd", Types), ("schemat_FA(3)_v1-0E.xsd", Namespace) })
        {
            using var stream = typeof(Fa3Schema).Assembly.GetManifestResourceStream("ksef-fa3/" + file)
                ?? throw new InvalidOperationException($"Embedded FA(3) schema {file} is missing.");
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { XmlResolver = null, DtdProcessing = DtdProcessing.Prohibit });
            set.Add(ns, reader);
        }
        set.Compile();
        return set;
    });

    /// <summary>Null when the document is valid FA(3); otherwise the local name of the first element that fails.</summary>
    public static string? FirstError(XDocument doc)
    {
        string? first = null;
        doc.Validate(Set.Value, (sender, e) =>
        {
            if (first is not null || e.Severity != XmlSeverityType.Error) return;
            first = sender switch { XElement el => el.Name.LocalName, XAttribute a => a.Parent?.Name.LocalName ?? a.Name.LocalName, _ => "Faktura" };
        });
        return first;
    }
}
