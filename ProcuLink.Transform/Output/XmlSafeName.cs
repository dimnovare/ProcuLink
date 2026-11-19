using System.Text;
using System.Xml;

namespace ProcuLink.Transform.Output;

/// <summary>
/// An element or attribute name the XML writer will accept. A name that is already valid is returned
/// unchanged, so existing output stays byte-identical. Any other name (usually a column label such as
/// "Invoice Number") is split on each run of characters a name cannot hold, each part after the first
/// gets a capital first letter, and the parts are joined: "Total (EUR)" → "TotalEUR". A result that
/// cannot start a name gets a leading "_"; an empty one becomes "Field".
/// </summary>
internal static class XmlSafeName
{
    internal static string For(string? name)
    {
        if (IsValid(name)) return name!;

        var sb = new StringBuilder();
        var newPart = false;
        foreach (var c in name ?? string.Empty)
        {
            if (!XmlConvert.IsNCNameChar(c)) { newPart = sb.Length > 0; continue; }
            sb.Append(newPart ? char.ToUpperInvariant(c) : c);
            newPart = false;
        }
        if (sb.Length == 0) return "Field";
        if (!XmlConvert.IsStartNCNameChar(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    /// <summary>
    /// <paramref name="value"/> without the characters XML 1.0 cannot carry (a form feed or another control
    /// character from a PDF's text, a lone surrogate). Valid surrogate pairs stay; a clean value is returned unchanged.
    /// </summary>
    internal static string Text(string value)
    {
        StringBuilder? sb = null;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var pair = i + 1 < value.Length && XmlConvert.IsXmlSurrogatePair(value[i + 1], c);
            if (pair || XmlConvert.IsXmlChar(c))
            {
                sb?.Append(c);
                if (pair) { sb?.Append(value[++i]); }
                continue;
            }
            sb ??= new StringBuilder(value, 0, i, value.Length);
        }
        return sb?.ToString() ?? value;
    }

    private static bool IsValid(string? name) =>
        name is { Length: > 0 } && XmlConvert.IsStartNCNameChar(name[0]) && name.All(XmlConvert.IsNCNameChar);
}
