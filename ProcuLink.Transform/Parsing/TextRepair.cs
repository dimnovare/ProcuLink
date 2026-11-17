using System.Text;
using System.Text.RegularExpressions;

namespace ProcuLink.Transform.Parsing;

/// <summary>Repairs UTF-8 text that was stored as Windows-1252 ("fÃ¼r" → "für"), leaving real non-ASCII text alone.</summary>
public static class TextRepair
{
    private static readonly Regex Suspect = new(
        "[Â-ô][\u0080-¿ŒœŠšŸŽžƒˆ˜–—‘-„†-•…‰‹›€™]{1,3}",
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
