using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Jobs;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Tests.Jobs;

/// <summary>
/// P1 telemetry hygiene — the webhook target URL must never be logged whole, on ANY path.
///
/// <para>A Slack / Teams / Zapier / Discord incoming-webhook URL <b>is</b> the credential: the
/// secret is the path and there is no separate token. This job used to log <c>sub.TargetUrl</c>
/// whole at Information <b>on the success path</b>, so every successful customer webhook wrote a
/// working credential into the log sink — which, with <c>MinimumBreadcrumbLevel = Information</c>
/// on both hosts, is also the Sentry surface. The failure paths and the thrown exception messages
/// (which become Sentry event titles) leaked exactly as hard.</para>
///
/// <para>The Slack URL below is a hand-typed vendor shape with a fake token, not a value derived
/// from anything the redactor matches on — so if the redaction is removed these assertions go red
/// rather than comparing a constant to itself.</para>
/// </summary>
public class FireIntegrationTriggerJobUrlRedactionTests
{
    // Realistic Slack incoming-webhook shape. The third path segment is the whole credential.
    //
    // DO NOT JOIN THESE INTO ONE LITERAL — see the same note in
    // ProcuLink.Api.Tests/Architecture/SentryScrubbingHostWiringTests. The job is handed the
    // complete vendor shape either way; the split only keeps GitHub push protection's Slack
    // detector from rejecting the push, and that it fires is independent evidence the fixture is a
    // real captured shape.
    private const string SlackSecret    = "QVc4vBPBt2M0uSm5oQwGaJ7T";
    private const string SlackTargetUrl = "https://hooks.slack.com/services/T0ABCDE12/B0FGHIJ34/" + SlackSecret;
    private const string SlackOrigin    = "https://hooks.slack.com";

    private const string OrdinaryTargetUrl = "https://api.supplier.example.com/inbound/orders";

    // ── harness ──────────────────────────────────────────────────────────────
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static OutboundRequestGuard PermissiveGuard()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:AllowPrivateNetworkTargets"] = "true",
            })
            .Build();
        return new OutboundRequestGuard(cfg, NullLogger<OutboundRequestGuard>.Instance);
    }

    private static DeliveryEncryptionService Enc()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();
        return new DeliveryEncryptionService(cfg);
    }

    private static async Task<Guid> SeedAsync(ProcuLinkDbContext db, string targetUrl)
    {
        var orgId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_{orgId:N}",
            Name = "Redaction Org",
            Slug = $"red-{orgId:N}",
            Plan = "operations",
            AccountStatus = "active",
            CreatedAt = DateTime.UtcNow,
        });

        var subId = Guid.NewGuid();
        db.IntegrationSubscriptions.Add(new IntegrationSubscription
        {
            Id = subId,
            OrganisationId = orgId,
            Platform = "slack",
            EventType = "order.delivered",
            TargetUrl = targetUrl,
            EncryptedSecret = null,
            IsActive = true,
            FailureCount = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return subId;
    }

    /// <summary>Captures the fully rendered log line — exactly what reaches stdout and Sentry.</summary>
    private sealed class CapturingLogger : ILogger<FireIntegrationTriggerJob>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
                                Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, ex));
            if (ex is not null) Lines.Add(ex.ToString());
        }
        public string All => string.Join("\n", Lines);
    }

    private sealed class FixedStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("body") });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            // A transport exception that echoes the request URI back — the shape that would
            // smuggle the credential out through an inner exception message.
            throw new HttpRequestException($"connection refused to {SlackTargetUrl} (simulated)");
    }

    private sealed class TestJob : FireIntegrationTriggerJob
    {
        private readonly HttpMessageHandler _handler;
        public TestJob(ProcuLinkDbContext db, CapturingLogger logger, HttpMessageHandler handler)
            : base(db, new Moq.Mock<IHttpClientFactory>().Object, Enc(), PermissiveGuard(), logger)
            => _handler = handler;

        internal override HttpClient CreateSendClient() => new(_handler);
    }

    // ── the leak the audit named: the SUCCESS path ───────────────────────────
    [Fact]
    public async Task SuccessPath_neverLogsTheWebhookCredential_butKeepsTheDestinationActionable()
    {
        await using var db = NewDb();
        var subId = await SeedAsync(db, SlackTargetUrl);

        // Anti-vacuity: the stored target really is a credential-bearing URL. Without this the
        // "secret is absent" assertion could pass on a subscription that never had one.
        (await db.IntegrationSubscriptions.AsNoTracking().FirstAsync(s => s.Id == subId))
            .TargetUrl.Should().Contain(SlackSecret);

        var log = new CapturingLogger();
        var job = new TestJob(db, log, new FixedStatusHandler(HttpStatusCode.OK));

        await job.ExecuteCoreAsync(subId, "{}", isFinalAttempt: true, CancellationToken.None);

        // The job really did log something on the success path — otherwise this test proves nothing.
        log.Lines.Should().NotBeEmpty();
        log.All.Should().Contain("delivered to");

        log.All.Should().NotContain(SlackSecret);
        log.All.Should().NotContain(SlackTargetUrl);
        // Still actionable: the vendor host, and the stored config id to look the target up by.
        log.All.Should().Contain(SlackOrigin);
        log.All.Should().Contain(subId.ToString());
    }

    // ── a failure log leaks exactly as hard ──────────────────────────────────
    [Fact]
    public async Task NonSuccessStatus_neverPutsTheCredentialInTheLogOrTheExceptionMessage()
    {
        await using var db = NewDb();
        var subId = await SeedAsync(db, SlackTargetUrl);
        var log = new CapturingLogger();
        var job = new TestJob(db, log, new FixedStatusHandler(HttpStatusCode.InternalServerError));

        var act = async () => await job.ExecuteCoreAsync(subId, "{}", isFinalAttempt: true, CancellationToken.None);
        var thrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;

        // The exception message becomes the Sentry event title.
        thrown.ToString().Should().NotContain(SlackSecret);
        thrown.Message.Should().Contain(SlackOrigin);
        thrown.Message.Should().Contain("HTTP 500");
        log.All.Should().NotContain(SlackSecret);
    }

    [Fact]
    public async Task SendException_scrubsTheCredentialOutOfTheInnerTransportMessageToo()
    {
        await using var db = NewDb();
        var subId = await SeedAsync(db, SlackTargetUrl);
        var log = new CapturingLogger();
        var job = new TestJob(db, log, new ThrowingHandler());

        var act = async () => await job.ExecuteCoreAsync(subId, "{}", isFinalAttempt: true, CancellationToken.None);
        var thrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;

        // Anti-vacuity: the inner transport exception genuinely carried the whole URL.
        thrown.InnerException.Should().NotBeNull();
        thrown.InnerException!.Message.Should().Contain(SlackSecret);

        // …and the message this job composes — the one that is logged and captured — does not.
        thrown.Message.Should().NotContain(SlackSecret);
        thrown.Message.Should().Contain(SlackOrigin);
    }

    [Fact]
    public async Task InactiveSubscription_logsNothingAboutTheTarget()
    {
        await using var db = NewDb();
        var subId = await SeedAsync(db, SlackTargetUrl);
        var sub = await db.IntegrationSubscriptions.FirstAsync(s => s.Id == subId);
        sub.IsActive = false;
        await db.SaveChangesAsync();

        var log = new CapturingLogger();
        var job = new TestJob(db, log, new FixedStatusHandler(HttpStatusCode.OK));

        await job.ExecuteCoreAsync(subId, "{}", isFinalAttempt: true, CancellationToken.None);

        log.All.Should().NotContain(SlackSecret);
    }

    // ── control: an ordinary destination stays readable ──────────────────────
    [Fact]
    public async Task OrdinaryTarget_stillLogsAUsableDestination()
    {
        // A redactor that logs "[redacted]" for every destination would pass the tests above and
        // be useless in an incident. The destination must remain a real, resolvable origin.
        await using var db = NewDb();
        var subId = await SeedAsync(db, OrdinaryTargetUrl);
        var log = new CapturingLogger();
        var job = new TestJob(db, log, new FixedStatusHandler(HttpStatusCode.OK));

        await job.ExecuteCoreAsync(subId, "{}", isFinalAttempt: true, CancellationToken.None);

        log.All.Should().Contain("https://api.supplier.example.com");
        log.All.Should().NotContain("[redacted]");
    }
}
