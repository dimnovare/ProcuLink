using System.Text.RegularExpressions;

namespace ProcuLink.Core.Documents;

/// <summary>
/// Whether an emailed document's From address is confirmed by the receiving side's signature check. A From line is free text,
/// so an unconfirmed sender waits for a person on a flow that sends by itself. Absent evidence is unconfirmed.
/// </summary>
public static class SenderAuthentication
{
    /// <summary>Postmark's spam check stamps <c>X-Spam-Tests</c>; <c>DKIM_VALID_AU</c> is a valid DKIM signature from the From domain.
    /// Every copy must pass: a sender can add one but cannot remove Postmark's. Residual: if Postmark ever stamped none, a copy the
    /// sender wrote would pass; its inbound JSON carries no spam verdict outside this forgeable list.
    /// Postmark adds no <c>Authentication-Results</c>, so one in the message was written by the sender and is never read here.</summary>
    public static bool Postmark(IEnumerable<(string Name, string Value)> headers)
    {
        var values = headers.Where(h => string.Equals(h.Name, "X-Spam-Tests", StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).ToList();
        return values.Count > 0 && values.All(v => v.Split(',', StringSplitOptions.TrimEntries).Contains("DKIM_VALID_AU", StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The mailbox provider's verdict (RFC 8601): a <c>dmarc=pass</c> result for the From domain, in the topmost
    /// <c>Authentication-Results</c> only. A receiver prepends its own, so copies a sender wrote sit below it. DMARC pass is DKIM or
    /// SPF passing aligned with the From domain.</summary>
    public static bool Mailbox(IEnumerable<(string Name, string Value)> headers, string? fromAddress)
    {
        var top = headers.FirstOrDefault(h => string.Equals(h.Name, "Authentication-Results", StringComparison.OrdinalIgnoreCase)).Value;
        var at = fromAddress?.LastIndexOf('@') ?? -1;
        if (top is null || at < 0) return false;
        var domain = fromAddress![(at + 1)..].Trim().TrimEnd('>');

        // Quoted strings and (nested) comments are free text, never results.
        var text = Regex.Replace(top, @"""(?:[^""\\]|\\.)*""", " ");
        for (var was = ""; was != text;) (was, text) = (text, Regex.Replace(text, @"\([^()]*\)", " "));
        text = Regex.Replace(text, @"\s*=\s*", "=");

        // The first element is the authserv-id; each later one is "method=result property=value ...".
        return text.Split(';').Skip(1).Select(r => r.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Any(t =>
            t.Length > 0 && t[0].Equals("dmarc=pass", StringComparison.OrdinalIgnoreCase)
            && t.Skip(1).Any(p => p.Equals($"header.from={domain}", StringComparison.OrdinalIgnoreCase)));
    }
}
