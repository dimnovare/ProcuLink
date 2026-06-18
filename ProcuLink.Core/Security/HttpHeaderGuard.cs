using System.Net.Http.Headers;

namespace ProcuLink.Core.Security;

/// <summary>
/// Shared validator for tenant-supplied outbound HTTP header names and values, defending
/// against CR/LF (header / response-splitting) injection. Tenants control supplier delivery
/// header maps, API-key auth header names/values, ERP client codes, and webhook event types;
/// without validation a value containing <c>\r\n</c> could smuggle extra headers
/// (e.g. <c>Host:</c>, <c>X-Forwarded-For:</c>) into the outbound request.
///
/// <para>
/// This is the HTTP-header twin of <c>MailKitEmailSender.SanitizeHeaderValue</c> (which guards
/// SMTP subject headers). Whereas the SMTP path can safely strip CR/LF to a space, an HTTP
/// header NAME containing illegal characters has no safe rewrite, so the policy here is to
/// <strong>reject</strong> (skip) the offending header rather than mangle it.
/// </para>
///
/// <para>
/// Use <see cref="TryAdd"/> at every site that adds a tenant-supplied header — it validates the
/// name and value and only then calls <c>TryAddWithoutValidation</c>, returning <c>false</c>
/// (without adding) for anything that fails. Callers should log a warning on a <c>false</c>
/// result so blocked injection attempts are observable.
/// </para>
/// </summary>
public static class HttpHeaderGuard
{
    /// <summary>
    /// Validates an HTTP header field-name against the RFC 7230 <c>token</c> grammar — i.e. it
    /// contains only visible ASCII token characters and none of the separator / control
    /// characters (including CR, LF, NUL, space, and the <c>tspecials</c>). Empty / whitespace
    /// names are rejected.
    /// </summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        foreach (var c in name)
        {
            // RFC 7230 token = 1*tchar; tchar excludes controls, space, and separators.
            if (c <= 0x20 || c >= 0x7F)
                return false;
            if (IsSeparator(c))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Validates an HTTP header field-value. Rejects any value containing CR, LF, or NUL, or any
    /// other ASCII control character except horizontal tab (HTAB, the one control char RFC 7230
    /// permits inside <c>field-content</c> via <c>obs-fold</c> / optional whitespace). A
    /// <c>null</c> value is treated as valid (it serializes to an empty value).
    /// </summary>
    public static bool IsValidValue(string? value)
    {
        if (value is null)
            return true;

        foreach (var c in value)
        {
            if (c == '\t')
                continue; // HTAB is the single permitted control char.
            if (c < 0x20 || c == 0x7F)
                return false; // controls (incl. CR \r, LF \n, NUL \0) and DEL.
        }

        return true;
    }

    /// <summary>
    /// Validates <paramref name="name"/> and <paramref name="value"/> and, only when both pass,
    /// adds the header via <c>TryAddWithoutValidation</c>. Returns <c>true</c> when the header
    /// was added; <c>false</c> when it was rejected (and therefore NOT added) because the name
    /// or value contained CR/LF/NUL/control characters or the name was not a valid token.
    /// Callers should log a warning on <c>false</c>.
    /// </summary>
    public static bool TryAdd(HttpRequestHeaders headers, string? name, string? value)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (!IsValidName(name) || !IsValidValue(value))
            return false;

        // Name validated above; TryAddWithoutValidation still returns false if the header is a
        // restricted/known header that cannot be set this way — surface that to the caller too.
        return headers.TryAddWithoutValidation(name!, value);
    }

    private static bool IsSeparator(char c) =>
        c is '(' or ')' or '<' or '>' or '@'
          or ',' or ';' or ':' or '\\' or '"'
          or '/' or '[' or ']' or '?' or '='
          or '{' or '}';
}
