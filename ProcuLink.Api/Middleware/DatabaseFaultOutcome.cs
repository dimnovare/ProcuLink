using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ProcuLink.Api.Middleware;

/// <summary>
/// Says whether a request that hit a database fault SURVIVED it.
///
/// <para><b>The question this exists to answer.</b> Four Sentry issues report
/// <c>An error occurred using the connection to database 'neondb'</c> and
/// <c>An error occurred using a transaction</c>, logged by EF at Error. Their
/// trigger is precisely known — every one fired 40 to 49 seconds after a scheduled
/// production smoke run started, which is the first traffic to reach a database
/// that has scaled to zero. What is NOT known is whether those requests then
/// failed or recovered, and that single fact decides what to do about them:</para>
///
/// <list type="bullet">
///   <item>recovered — the fault is noise, and belongs with the two other handled
///     failures filtered out of Sentry this week;</item>
///   <item>failed — a customer's first page load after an idle period returns an
///     error, which is a pilot-blocking defect wearing a log line's clothes.</item>
/// </list>
///
/// <para>Sentry cannot answer it. The events carry no exception and no status code,
/// because EF logs them mid-request, before any status exists. The only place the
/// two facts meet is the end of the request, which is where this looks.</para>
///
/// <para><b>Why the smoke run passing is not the answer.</b> Those runs did pass
/// end to end — but a run asserts screens render, not that every request under them
/// returned 200. A retried or non-blocking call can fail without the run noticing.</para>
///
/// <para><b>THIS IS A TIME-BOXED DIAGNOSTIC.</b> It logs at Error because
/// <c>MinimumEventLevel = Error</c> and an answer nobody can see is not an answer.
/// It cannot fire on its own: something must already have logged a database fault,
/// which is itself already an Error event — so at worst this doubles a rare one.
/// Once a handful of occurrences have named the outcome, delete it or drop it to
/// Debug. Do not leave it running because it is harmless.</para>
/// </summary>
public static class DatabaseFaultOutcome
{
    private const string ItemKey = "proculink.db-fault";

    /// <summary>Records a fault against the in-flight request, if there is one.</summary>
    private static void Record(IHttpContextAccessor accessor, string kind)
    {
        // No ambient request: a Hangfire job, a hosted service, or startup. Those
        // have no status code to report, so there is nothing to correlate and this
        // deliberately drops it rather than inventing a request to blame.
        var items = accessor.HttpContext?.Items;
        if (items is null) return;
        items[ItemKey] ??= kind;
    }

    /// <summary>EF connection failures — the 'neondb' connection issues.</summary>
    public sealed class ConnectionObserver(IHttpContextAccessor accessor) : DbConnectionInterceptor
    {
        public override void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData)
            => Record(accessor, "connection");

        public override Task ConnectionFailedAsync(
            DbConnection connection, ConnectionErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Record(accessor, "connection");
            return Task.CompletedTask;
        }
    }

    /// <summary>EF transaction failures — the "An error occurred using a transaction" issue.</summary>
    public sealed class TransactionObserver(IHttpContextAccessor accessor) : DbTransactionInterceptor
    {
        public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
            => Record(accessor, $"transaction:{eventData.Action}");

        public override Task TransactionFailedAsync(
            DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Record(accessor, $"transaction:{eventData.Action}");
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Reports the outcome of any request that recorded a fault. Register EARLY, so
    /// the status code read below is the one actually sent.
    /// </summary>
    public static IApplicationBuilder UseDatabaseFaultOutcome(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            finally
            {
                if (context.Items.TryGetValue(ItemKey, out var kind) && kind is string faultKind)
                {
                    var status = context.Response.StatusCode;
                    // 5xx is the only unambiguous failure. A 4xx after a database
                    // fault is still the app answering deliberately, so it counts as
                    // survived — the distinction being measured is "did the fault
                    // reach the caller", not "was the caller happy".
                    var survived = status < 500;
                    var logger = context.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("ProcuLink.DatabaseFaultOutcome");

                    logger.LogError(
                        "Database fault ({FaultKind}) during {Method} {Path}: request completed {Status} — {Outcome}.",
                        faultKind, context.Request.Method, context.Request.Path.Value, status,
                        survived ? "SURVIVED" : "FAILED");
                }
            }
        });
}
