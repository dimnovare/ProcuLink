using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ProcuLink.Core.Documents;

namespace ProcuLink.Transform.Output.Ksef;

/// <summary>What a person checks before issuing a KSeF file, read from the file's own bytes, and the hash that binds their confirmation to them.</summary>
public static partial class Fa3Summary
{
    private static readonly (string Bucket, string Rate)[] Buckets = [("1", "23"), ("2", "8"), ("3", "5")];

    public static KsefSummary Read(byte[] xml)
    {
        var root = XDocument.Parse(Encoding.UTF8.GetString(xml)).Root!;
        XNamespace ns = Fa3Schema.Namespace;
        string? V(XElement? e, string name) => e?.Element(ns + name)?.Value;
        var seller = root.Element(ns + "Podmiot1")?.Element(ns + "DaneIdentyfikacyjne");
        var buyer = root.Element(ns + "Podmiot2")?.Element(ns + "DaneIdentyfikacyjne");
        var fa = root.Element(ns + "Fa");
        var rates = Buckets.Where(b => V(fa, "P_13_" + b.Bucket) is not null)
            .Select(b => new KsefRateTotal(b.Rate, V(fa, "P_13_" + b.Bucket)!, V(fa, "P_14_" + b.Bucket) ?? "")).ToList();
        return new KsefSummary(V(seller, "Nazwa") ?? "", V(seller, "NIP") ?? "", V(buyer, "Nazwa"),
            V(buyer, "NIP") ?? (V(buyer, "KodUE") + V(buyer, "NrVatUE")), V(fa, "P_2") ?? "", V(fa, "P_1") ?? "", V(fa, "P_6"),
            V(fa, "KodWaluty") ?? "", rates, V(fa, "P_15") ?? "", fa?.Elements(ns + "FaWiersz").Count() ?? 0, ConfirmHash(xml));
    }

    /// <summary>
    /// SHA-256 (lowercase hex) of the file with the text of Naglowek/DataWytworzeniaFa removed: the only part that changes between two
    /// renders of the same content, so a confirmation binds to exactly the content issued.
    /// </summary>
    public static string ConfirmHash(byte[] xml) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CreatedAt().Replace(Encoding.UTF8.GetString(xml), "$1")))).ToLowerInvariant();

    [GeneratedRegex(@"(<(?:\w+:)?DataWytworzeniaFa>)[^<]*")]
    private static partial Regex CreatedAt();
}
