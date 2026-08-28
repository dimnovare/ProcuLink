using Sentry;

namespace ProcuLink.Api.Telemetry;

/// <summary>
/// Drops Sentry events that report a condition the code already handles correctly.
///
/// <para><b>The one case, and how it was found.</b> Reading production Sentry on 2026-08-28 to
/// confirm that <c>TenantNotResolvedException</c> was being filtered (it was — zero events) turned
/// up the issue sitting next to it: <b>exactly four error events every time a new organisation
/// signs up</b>, open since 28 May, 22 events, 5 people, still ongoing. Every one is
/// <c>Failed executing DbCommand … INSERT INTO organisations …</c>, on the first four requests a
/// fresh session makes — orders, billing status, suppliers, dashboard topology — all at the same
/// second.</para>
///
/// <para><b>Nothing is broken.</b> Those four requests arrive together and each tries to create the
/// organisation row; one wins the unique index and the losers catch the violation, detach, and
/// adopt the winner's row (<c>TenantResolutionMiddleware</c>, the <c>IsUniqueViolation</c> catch).
/// That is anticipated, correct, and invisible to the customer — which is exactly why it must not
/// be filed as an error.</para>
///
/// <para><b>Why the existing mechanisms could not catch it.</b> The
/// <c>AddExceptionFilterForType&lt;TenantNotResolvedException&gt;()</c> filter in
/// <c>Program.cs</c> matches an event's EXCEPTION, and this event has none: EF logs the failed
/// command at <c>Error</c> level <i>before</i> the exception reaches the catch, and
/// <c>MinimumEventLevel = Error</c> means Sentry files the log line on its own. One door was
/// closed; this walks in through the other.</para>
///
/// <para><b>Why not fix it at the log level instead.</b> The tempting one-liner is
/// <c>ConfigureWarnings(w =&gt; w.Log(RelationalEventId.CommandError, LogLevel.Information))</c>,
/// which is smaller and needs no string matching. It was rejected after counting the blast radius:
/// this solution has <b>eight</b> non-test <c>catch (DbUpdateException)</c> sites and several of
/// them swallow unconditionally (<c>StripeBillingService</c>, <c>AiUsageTracker</c>,
/// <c>SchemaFingerprintService</c>). Downgrading the event id would silence those too, and a
/// swallowed Stripe write failing quietly is worth more than this tidy-up is.</para>
///
/// <para><b>Why it is safe to match on text.</b> Three conjuncts have to hold, and each one is a
/// door this filter cannot walk through by accident: the event must come from EF's command logger,
/// it must carry <b>no exception</b> (so anything diagnosable keeps its event and its stack), and
/// its message must name the organisations INSERT specifically. The table name is a constant here
/// and pinned to the EF model by <c>ExpectedLogNoiseTests</c>, so renaming the table fails the
/// build rather than quietly voiding the filter.</para>
/// </summary>
public static class ExpectedLogNoise
{
    /// <summary>EF Core's logging category for "Failed executing DbCommand".</summary>
    public const string EfCommandLogger = "Microsoft.EntityFrameworkCore.Database.Command";

    /// <summary>
    /// The organisations table as EF maps it. A constant rather than a literal at the match site
    /// so one test can pin it to the model — see <c>ExpectedLogNoiseTests</c>.
    /// </summary>
    public const string OrganisationsTable = "organisations";

    /// <summary>
    /// True when the event is the handled signup race described on this class, and nothing else.
    /// </summary>
    public static bool ShouldDrop(SentryEvent? e)
    {
        if (e is null) return false;

        // 1. EF's command logger. `Logger` is what the logging integration sets; the tag is
        //    checked too because it is what the Sentry UI groups on, and an event carrying one
        //    without the other is not a shape worth trusting either way.
        var logger = e.Logger;
        if (string.IsNullOrEmpty(logger) && e.Tags.TryGetValue("logger", out var tagged)) logger = tagged;
        if (!string.Equals(logger, EfCommandLogger, StringComparison.Ordinal)) return false;

        // 2. NO exception attached. A command error that actually matters propagates, and Sentry
        //    captures that event with a type and a stack. This filter refuses to touch anything
        //    carrying one, so it can never remove the diagnosable half of a real failure.
        if (e.SentryExceptions?.Any() == true) return false;

        // 3. The organisations INSERT specifically. Any other failed command — a different table,
        //    a SELECT, a migration — keeps its event.
        var text = e.Message?.Formatted ?? e.Message?.Message ?? string.Empty;
        return text.Contains($"INSERT INTO {OrganisationsTable}", StringComparison.OrdinalIgnoreCase);
    }
}
