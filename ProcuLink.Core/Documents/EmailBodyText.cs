using System.Text.RegularExpressions;

namespace ProcuLink.Core.Documents;

/// <summary>The plain-text body of an email: the text part, else the HTML part stripped of markup.</summary>
public static class EmailBodyText
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private static readonly Regex ScriptRegex = new("<script[^>]*>.*?</script>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline, Timeout);
    private static readonly Regex StyleRegex = new("<style[^>]*>.*?</style>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline, Timeout);
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled, Timeout);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled, Timeout);

    /// <summary>The text part, else the stripped HTML part; null when both are blank or the HTML will not strip in time.</summary>
    public static string? Resolve(string? text, string? html)
    {
        if (!string.IsNullOrWhiteSpace(text)) return text;
        if (string.IsNullOrWhiteSpace(html)) return null;
        try
        {
            var stripped = StripHtml(html);
            return string.IsNullOrWhiteSpace(stripped) ? null : stripped;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    // Drops <script> and <style> blocks (content, not just tags) so their text doesn't bleed into the body.
    private static string StripHtml(string html)
    {
        var stripped = HtmlTagRegex.Replace(StyleRegex.Replace(ScriptRegex.Replace(html, string.Empty), string.Empty), " ");
        return WhitespaceRegex.Replace(System.Net.WebUtility.HtmlDecode(stripped), " ").Trim();
    }
}
