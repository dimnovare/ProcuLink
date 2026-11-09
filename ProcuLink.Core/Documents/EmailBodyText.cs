using System.Text.RegularExpressions;

namespace ProcuLink.Core.Documents;

/// <summary>The plain-text body of an email: the text part, else the HTML part stripped of markup.</summary>
public static class EmailBodyText
{
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public static string? Resolve(string? text, string? html)
    {
        if (!string.IsNullOrWhiteSpace(text)) return text;
        return string.IsNullOrWhiteSpace(html) ? null : StripHtml(html);
    }

    // Drops <script> and <style> blocks (content, not just tags) so their text doesn't bleed into the body.
    private static string StripHtml(string html)
    {
        var noScripts = Regex.Replace(html, "<script[^>]*>.*?</script>",
            string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var noStyles = Regex.Replace(noScripts, "<style[^>]*>.*?</style>",
            string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var stripped = HtmlTagRegex.Replace(noStyles, " ");
        var decoded = System.Net.WebUtility.HtmlDecode(stripped);
        return WhitespaceRegex.Replace(decoded, " ").Trim();
    }
}
