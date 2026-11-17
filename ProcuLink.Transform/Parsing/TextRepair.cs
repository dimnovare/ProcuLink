using System.Text;
using System.Text.RegularExpressions;

namespace ProcuLink.Transform.Parsing;

/// <summary>Repairs UTF-8 text that was stored as Windows-1252 ("fÃ¼r" → "für"), leaving real non-ASCII text alone.</summary>
public static class TextRepair
{
    private static readonly Regex Suspect = new(
        "[\u00C2-\u00C5\u00E2][\u0080-\u00BF\u0152\u0153\u0160\u0161\u0178\u017D\u017E\u0192\u02C6\u02DC\u2013\u2014\u2018-\u201E\u2020-\u2022\u2026\u2030\u2039\u203A\u20AC\u2122]{1,3}",
        RegexOptions.Compiled);

    private static readonly Encoding Cp1252 =
        CodePagesEncodingProvider.Instance.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    /// <summary>Each suspect run is replaced only when it re-encodes to exactly one valid UTF-8 character.</summary>
    public static string RepairMojibake(string text) => !Suspect.IsMatch(text) ? text : Suspect.Replace(text, m =>
    {
        try
        {
            var decoded = StrictUtf8.GetString(Cp1252.GetBytes(m.Value));
            return decoded.EnumerateRunes().Count() == 1 ? decoded : m.Value;
        }
        catch (ArgumentException)
        {
            return m.Value;
        }
    });
}
