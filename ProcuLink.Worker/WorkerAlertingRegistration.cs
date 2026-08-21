using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Alerting;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Alerting;
using ProcuLink.Worker.Jobs;

namespace ProcuLink.Worker;

/// <summary>
/// The Worker's operator-alerting object graph, in ONE place that both the host and its guard use.
///
/// <para><b>Why this is a method and not inline in <c>Program.cs</c>.</b> The wiring guard used to
/// assert on <c>Program.cs</c> SOURCE with a regex, and the end-to-end test built its own private
/// copy of the same graph. Both stayed green when a transport was deleted from the real composite:
/// the regex stopped at the constructor name, and the test's copy could never disagree with a host
/// it never read. Extracting the registrations means the guard resolves the SAME graph production
/// resolves, so removing a sink here fails a test that actually exercises delivery.</para>
///
/// <para><b>What is deliberately NOT here.</b> <c>IOpsHealthService</c>, <c>IEmailApiClient</c>,
/// <c>IAiUsageTracker</c> and the <c>DbContext</c> are inputs the alerting graph reads, not parts
/// of it. Leaving them to the caller is what lets the guard substitute a recording transport and a
/// scripted health snapshot while every alerting type stays the production one.</para>
/// </summary>
public static class WorkerAlertingRegistration
{
    /// <summary>
    /// Registers the recurring alert sweep, its probe, its tunables and its transports.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">
    /// Source for the <c>WorkerHealthAlert</c> and <c>Alerting:Email</c> sections. Both bind to
    /// safe defaults when absent.
    /// </param>
    public static IServiceCollection AddWorkerAlerting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Singleton: per-condition rate-limit state, which must survive across the scoped sweeps.
        // It captures no scoped graph of its own — the store below opens its own scope per call,
        // which is what lets a singleton reach a scoped DbContext safely.
        //
        // The state is DURABLE because a singleton alone was not enough: it survives sweeps inside
        // one process and nothing else, so every Worker restart re-armed every healthy→bad
        // transition and restarted every cooldown. A crash-looping Worker emailed on the raw
        // 5-minute sweep interval (observed live 2026-08-20: 14:50 / 14:55 / 15:00 / 15:05 across
        // a run of Railway redeploys, against 30-minute spacing either side of it).
        services.AddSingleton<IWorkerHealthAlertStateStore>(sp =>
            new DbWorkerHealthAlertStateStore(sp.GetRequiredService<IServiceScopeFactory>()));
        services.AddSingleton(sp =>
            new WorkerHealthAlertState(sp.GetRequiredService<IWorkerHealthAlertStateStore>()));

        services.AddSingleton(_ =>
        {
            var opts = new WorkerHealthAlertOptions();
            configuration.GetSection(WorkerHealthAlertOptions.SectionName).Bind(opts);
            return opts;
        });

        services.AddSingleton(_ =>
        {
            var opts = new AlertingEmailOptions();
            configuration.GetSection(AlertingEmailOptions.SectionName).Bind(opts);
            return opts;
        });

        // The recurring-job source is Hangfire's own scheduler record, which is why SFTP/S3 pull
        // freshness needs no new column. It takes an OPTIONAL JobStorage, so it still resolves in a
        // container that has none.
        services.AddSingleton<IRecurringJobLastExecutionSource, HangfireRecurringJobLastExecutionSource>();
        services.AddScoped<IOperationalAlertProbe, OperationalAlertProbe>();

        // Sinks are SCOPED because the email sink resolves the scoped IEmailApiClient — a singleton
        // sink would capture it. The composite fans out and isolates each transport, so a dead
        // Sentry cannot suppress the email and vice versa. Both are safe no-ops when unconfigured,
        // and both report honestly whether they actually delivered.
        services.AddScoped<SentryWorkerAlertSink>();
        services.AddScoped<EmailWorkerAlertSink>();
        services.AddScoped<IWorkerAlertSink>(sp => new CompositeWorkerAlertSink(
            new IWorkerAlertSink[]
            {
                sp.GetRequiredService<SentryWorkerAlertSink>(),
                sp.GetRequiredService<EmailWorkerAlertSink>(),
            },
            sp.GetRequiredService<ILogger<CompositeWorkerAlertSink>>()));

        services.AddScoped<IWorkerHealthAlertService, WorkerHealthAlertService>();
        services.AddScoped<WorkerHealthAlertJob>();

        return services;
    }
}
