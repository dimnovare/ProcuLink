using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Jobs;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Tests.Jobs;

/// <summary>
/// Defer-list #5 — proves the persisted consecutive-failure count is DECOUPLED from Hangfire's
/// per-job retry count. Hangfire re-runs <see cref="FireIntegrationTriggerJob.ExecuteAsync"/> on
/// each AutomaticRetry; the old code bumped <c>FailureCount</c> on every run, so ONE failed webhook
/// counted up to <c>Attempts + 1</c> times and auto-deactivated the subscription after a single
/// real failure. The fix advances the count (and decides deactivation) only on the FINAL Hangfire
/// attempt, so three genuinely separate failed deliveries are required to deactivate.
/// </summary>
public class FireIntegrationTriggerJobFailureCountTests
{
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

    private static async Task<Guid> SeedSubscriptionAsync(ProcuLinkDbContext db, int failureCount = 0)
    {
        var orgId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_{orgId:N}",
            Name = "Trigger Org",
            Slug = $"trigger-{orgId:N}",
            Plan = "operations",
            AccountStatus = "active",
            CreatedAt = DateTime.UtcNow,
        });

        var subId = Guid.NewGuid();
        db.IntegrationSubscriptions.Add(new IntegrationSubscription
        {
            Id = subId,
            OrganisationId = orgId,
            Platform = "custom",
            EventType = "order.delivered",
            TargetUrl = "https://hooks.example.com/webhook",
            EncryptedSecret = null,
            IsActive = true,
            FailureCount = failureCount,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return subId;
    }

    /// <summary>A subclass whose send path returns a fixed HTTP status without a real socket.</summary>
    private sealed class TestableJob : FireIntegrationTriggerJob
    {
        private readonly HttpStatusCode _status;
        public TestableJob(ProcuLinkDbContext db, HttpStatusCode status)
            : base(db, new Moq.Mock<IHttpClientFactory>().Object, Enc(), PermissiveGuard(),
                   NullLogger<FireIntegrationTriggerJob>.Instance)
            => _status = status;

        internal override HttpClient CreateSendClient() =>
            new(new FixedStatusHandler(_status));
    }

    private sealed class FixedStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("nope") });
    }

    [Fact]
    public async Task NonFinalAttempt_FailedDelivery_DoesNotBumpFailureCount()
    {
        await using var db = NewDb();
        var subId = await SeedSubscriptionAsync(db);
        var job = new TestableJob(db, HttpStatusCode.InternalServerError);

        // A non-final Hangfire attempt: the delivery failed and is being retried, so the persisted
        // count must NOT advance (Hangfire's retry, not a new logical failure).
        var act = async () => await job.ExecuteCoreAsync(subId, "{}", isFinalAttempt: false, default);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var sub = await db.IntegrationSubscriptions.AsNoTracking().SingleAsync(s => s.Id == subId);
        sub.FailureCount.Should().Be(0, "a retried attempt of the same delivery must not bump the count");
        sub.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task FinalAttempt_FailedDelivery_BumpsFailureCountExactlyOnce()
    {
        await using var db = NewDb();
        var subId = await SeedSubscriptionAsync(db);
        var job = new TestableJob(db, HttpStatusCode.InternalServerError);

        var act = async () => await job.ExecuteCoreAsync(subId, "{}", isFinalAttempt: true, default);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var sub = await db.IntegrationSubscriptions.AsNoTracking().SingleAsync(s => s.Id == subId);
        sub.FailureCount.Should().Be(1, "one logical failure bumps the count once on the final attempt");
        sub.IsActive.Should().BeTrue("one failure is below the 3-failure deactivation threshold");
    }

    [Fact]
    public async Task OneLogicalFailure_AcrossFourHangfireRuns_BumpsCountOnce()
    {
        // Simulate Hangfire running the job 4 times for a SINGLE failing webhook
        // (initial + 3 AutomaticRetry attempts). Only the last run is final.
        await using var db = NewDb();
        var subId = await SeedSubscriptionAsync(db);
        var job = new TestableJob(db, HttpStatusCode.InternalServerError);

        for (var attempt = 0; attempt < FireIntegrationTriggerJob.MaxRetryAttempts + 1; attempt++)
        {
            var isFinal = attempt == FireIntegrationTriggerJob.MaxRetryAttempts;
            var act = async () => await job.ExecuteCoreAsync(subId, "{}", isFinal, default);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        var sub = await db.IntegrationSubscriptions.AsNoTracking().SingleAsync(s => s.Id == subId);
        sub.FailureCount.Should().Be(1, "four Hangfire runs of ONE failed webhook = one logical failure");
        sub.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task ThirdConsecutiveFinalFailure_DeactivatesSubscription()
    {
        // Start one short of the threshold; the next FINAL failure deactivates.
        await using var db = NewDb();
        var subId = await SeedSubscriptionAsync(db, failureCount: FireIntegrationTriggerJob.DeactivateAfterFailures - 1);
        var job = new TestableJob(db, HttpStatusCode.InternalServerError);

        var act = async () => await job.ExecuteCoreAsync(subId, "{}", isFinalAttempt: true, default);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var sub = await db.IntegrationSubscriptions.AsNoTracking().SingleAsync(s => s.Id == subId);
        sub.FailureCount.Should().Be(FireIntegrationTriggerJob.DeactivateAfterFailures);
        sub.IsActive.Should().BeFalse("the third consecutive logical failure deactivates the subscription");
    }

    [Fact]
    public async Task SuccessfulDelivery_ResetsFailureCountToZero()
    {
        await using var db = NewDb();
        var subId = await SeedSubscriptionAsync(db, failureCount: 2);
        var job = new TestableJob(db, HttpStatusCode.OK);

        await job.ExecuteCoreAsync(subId, "{}", isFinalAttempt: false, default);

        var sub = await db.IntegrationSubscriptions.AsNoTracking().SingleAsync(s => s.Id == subId);
        sub.FailureCount.Should().Be(0, "a success clears the consecutive-failure streak regardless of attempt");
        sub.IsActive.Should().BeTrue();
    }
}
