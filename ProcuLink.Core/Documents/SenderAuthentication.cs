using System.Text.RegularExpressions;

namespace ProcuLink.Core.Documents;

/// <summary>
/// Whether an emailed document's From address is confirmed by the receiving side's signature check. A From line is free text,
/// so an unconfirmed sender waits for a person on a flow that sends by itself. Absent evidence is unconfirmed.
/// </summary>
public static class SenderAuthentication
{
    /// <summary>Postmark's spam check stamps <c>X-Spam-Tests</c>; <c>DKIM_VALID_AU</c> is a valid DKIM signature from the From domain.
    /// Postmark adds no <c>Authentication-Results</c>, so one in the message was written by the sender and is never read here.</summary>
    public static bool Postmark(IEnumerable<(string Name, string Value)> headers) =>
        Every(headers, "X-Spam-Tests", v => v.Split(',', StringSplitOptions.TrimEntries).Contains("DKIM_VALID_AU", StringComparer.OrdinalIgnoreCase));

    /// <summary>The mailbox provider's <c>Authentication-Results</c>: DMARC pass is DKIM or SPF passing aligned with the From domain.</summary>
    public static bool Mailbox(IEnumerable<(string Name, string Value)> headers) =>
        Every(headers, "Authentication-Results", v => Regex.IsMatch(v, @"\bdmarc\s*=\s*pass\b", RegexOptions.IgnoreCase));

    // A sender can add a header but cannot remove the one the receiving side stamped, so every copy must pass.
    private static bool Every(IEnumerable<(string Name, string Value)> headers, string name, Func<string, bool> pass)
    {
        var values = headers.Where(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).ToList();
        return values.Count > 0 && values.All(pass);
    }
}
