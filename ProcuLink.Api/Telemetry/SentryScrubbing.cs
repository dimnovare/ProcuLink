using ProcuLink.Core.Security;
using Sentry;

namespace ProcuLink.Api.Telemetry;

/// <summary>
/// The single Sentry redaction wiring shared by BOTH hosts.
///
/// <para><b>Why one place.</b> Before this existed, <c>ProcuLink.Api/Program.cs</c> scrubbed only
/// <see cref="SentryRequest"/> (the inbound-email <c>?token=</c>) and <c>ProcuLink.Worker/Program.cs</c>
/// scrubbed <b>nothing at all</b> — it had no <c>BeforeSend</c> callback of any kind. The Worker is
/// the host that fires customer webhooks, so it was the host that most needed one. The two
/// <c>Program.cs</c> files drifting apart is a recurring defect class in this repo; the fix is a
/// shared registration both hosts call, pinned by a cross-host guard
/// (<c>ProcuLink.Api.Tests/Architecture/SentryScrubbingHostWiringTests</c>).</para>
///
/// <para><b>Why redaction and not a lower breadcrumb level.</b> Both hosts keep
/// <c>MinimumBreadcrumbLevel = Information</c>. Raising it to Warning would drop the request/job
/// trail that makes a captured exception diagnosable, and it would not fix the leak: a secret
/// logged at Error leaves just as fast. The targeted fix is to remove the secret, which is what
/// this does — on breadcrumbs (at add time, before they are attached to any event), on the event
/// message and its structured parameters, on exception messages, on <c>Extra</c>, and on the
/// captured request URL/query for both events and transactions.</para>
///
/// <para><b>Scope note.</b> This is a last line of defence. Code that knowingly handles a
/// secret-bearing URL must log <see cref="TelemetryRedactor.SafeDestination"/> plus a stored config
/// id rather than relying on this to clean up afterwards — this layer protects Sentry, not the
/// stdout log sink that Railway retains.</para>
/// </summary>
public static class SentryScrubbing
{
    /// <summary>
    /// Installs ProcuLink's redaction callbacks on any <see cref="SentryOptions"/> — the ASP.NET
    /// Core options in the API and the logging options in the Worker both derive from it.
    /// </summary>
    public static void UseProcuLinkScrubbing(this SentryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Breadcrumbs are scrubbed at ADD time, before they are attached to any event. This is the
        // only hook that works: SentryEvent.Breadcrumbs is read-only, so BeforeSend cannot rewrite
        // breadcrumbs that are already on the event.
        options.SetBeforeBreadcrumb((breadcrumb, _) => Scrub(breadcrumb));
        options.SetBeforeSend((e, _) => Scrub(e));
        options.SetBeforeSendTransaction((t, _) => Scrub(t));
    }

    /// <summary>Redacts a breadcrumb's message and every string in its data bag.</summary>
    public static Breadcrumb? Scrub(Breadcrumb? breadcrumb)
    {
        if (breadcrumb is null) return null;

        IReadOnlyDictionary<string, string>? data = null;
        if (breadcrumb.Data is { Count: > 0 })
        {
            var scrubbed = new Dictionary<string, string>(breadcrumb.Data.Count);
            foreach (var kv in breadcrumb.Data)
                scrubbed[kv.Key] = TelemetryRedactor.Redact(kv.Value) ?? string.Empty;
            data = scrubbed;
        }

        // Breadcrumb is immutable and its timestamp-carrying constructor is internal, so the
        // replacement is stamped at scrub time. That is the same instant the original was created:
        // BeforeBreadcrumb runs synchronously inside AddBreadcrumb.
        // The `!`s preserve nulls exactly: Breadcrumb's ctor annotates message/type as non-nullable
        // but stores whatever it is given, and coercing a null Type to "" would change what is
        // serialised. Redact(null) is null, so a null message stays a null message.
        return new Breadcrumb(
            TelemetryRedactor.Redact(breadcrumb.Message)!,
            breadcrumb.Type!,
            data,
            breadcrumb.Category,
            breadcrumb.Level);
    }

    /// <summary>
    /// Redacts everything on an event that can carry a secret copied out of a log line: the
    /// message template, its rendered form, its structured parameters, exception values, the
    /// transaction name, the <c>Extra</c> bag and the captured request.
    /// </summary>
    public static SentryEvent Scrub(SentryEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Message is { } message)
        {
            message.Message   = TelemetryRedactor.Redact(message.Message);
            message.Formatted = TelemetryRedactor.Redact(message.Formatted);
            // The rendered "Formatted" is built from these; a webhook URL passed to ILogger as a
            // structured argument lives here as well as in the rendered string.
            if (message.Params is { } parameters)
                message.Params = parameters
                    .Select(p => p is string s ? (object)(TelemetryRedactor.Redact(s) ?? string.Empty) : p)
                    .ToList();
        }

        if (e.SentryExceptions is { } exceptions)
        {
            var list = exceptions.ToList();
            foreach (var ex in list)
                ex.Value = TelemetryRedactor.Redact(ex.Value);
            e.SentryExceptions = list;
        }

        e.TransactionName = TelemetryRedactor.Redact(e.TransactionName);

        // Extra is exposed read-only but SetExtra writes through to the same store.
        foreach (var kv in e.Extra.ToList())
            if (kv.Value is string s)
                e.SetExtra(kv.Key, TelemetryRedactor.Redact(s));

        ScrubRequest(e.Request);
        return e;
    }

    /// <summary>Redacts the parts of a transaction that can carry a URL secret.</summary>
    public static SentryTransaction Scrub(SentryTransaction t)
    {
        ArgumentNullException.ThrowIfNull(t);
        t.Description = TelemetryRedactor.Redact(t.Description);
        ScrubRequest(t.Request);
        return t;
    }

    /// <summary>
    /// Scrubs the captured HTTP request. This subsumes the API's original inbound-email
    /// <c>?token=</c> rule: Postmark can only pass its shared secret in the webhook URL, so a
    /// sampled transaction would otherwise persist it to Sentry.
    /// </summary>
    private static void ScrubRequest(SentryRequest? request)
    {
        if (request is null) return;
        if (!string.IsNullOrEmpty(request.Url)) request.Url = TelemetryRedactor.Redact(request.Url);
        if (!string.IsNullOrEmpty(request.QueryString)) request.QueryString = TelemetryRedactor.Redact(request.QueryString);
    }
}
