using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Jobs;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Tests.Jobs;

/// <summary>
/// The webhook signing secret is bound to its subscription id. A secret that will not decrypt used
/// to leave the signature header null and the payload was POSTed anyway — unsigned, unlogged, and
/// with no failure recorded. These tests pin that it now sends nothing instead.
/// </summary>
public class FireIntegrationTriggerJobSecretBindingTests
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

    // Deterministic 32-zero-byte key, so a blob made here is readable by the job's own service.
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

    /// <summary>Records every outbound request so a test can assert none was made.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Sends;
        public HttpRequestMessage? Last;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Sends);
            Last = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok"),
            });
        }
    }

    private sealed class TestableJob : FireIntegrationTriggerJob
    {
        private readonly CountingHandler _handler;

        public TestableJob(ProcuLinkDbContext db, CountingHandler handler)
            : base(db, new Moq.Mock<IHttpClientFactory>().Object, Enc(), PermissiveGuard(),
                   NullLogger<FireIntegrationTriggerJob>.Instance)
            => _handler = handler;

        internal override HttpClient CreateSendClient() => new(_handler);
    }

    /// <summary>
    /// Seeds an org + subscription. <paramref name="secretBoundTo"/> chooses which subscription id
    /// the secret is encrypted against: pass the subscription's own id for the healthy case, or a
    /// different id to simulate a blob that does not belong to this subscription.
    /// </summary>
    private static async Task<Guid> SeedAsync(
        ProcuLinkDbContext db, string? secret, Func<Guid, Guid>? secretBoundTo = null)
    {
        var orgId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_{orgId:N}",
            Name = "Binding Org",
            Slug = $"binding-{orgId:N}",
            Plan = "operations",
            AccountStatus = "active",
            CreatedAt = DateTime.UtcNow,
        });

        var subId = Guid.NewGuid();
        string? encrypted = null;
        if (secret is not null)
        {
            var boundTo = secretBoundTo?.Invoke(subId) ?? subId;
            encrypted = Enc().Encrypt(secret, CredentialScope.ForSupplier(
                orgId, CredentialPurpose.OrgIntegrationWebhookSecret, boundTo));
        }

        db.IntegrationSubscriptions.Add(new IntegrationSubscription
        {
            Id = subId,
            OrganisationId = orgId,
            Platform = "custom",
            EventType = "order.delivered",
            TargetUrl = "https://hooks.example.com/webhook",
            EncryptedSecret = encrypted,
            IsActive = true,
            FailureCount = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return subId;
    }

    [Fact]
    public async Task Fire_SecretBoundToThisSubscription_SendsSigned()
    {
        await using var db = NewDb();
        var subId = await SeedAsync(db, "signing-secret");
        var handler = new CountingHandler();
        var job = new TestableJob(db, handler);

        await job.ExecuteCoreAsync(subId, "{}", isFinalAttempt: true, default);

        handler.Sends.Should().Be(1);
        handler.Last!.Headers.Contains("X-ProcuLink-Signature")
            .Should().BeTrue("a subscription with a secret must get a signed delivery");
    }

    // The fail-open fix.
    [Fact]
    public async Task Fire_SecretBoundToADifferentSubscription_SendsNothingAndRecordsFailure()
    {
        await using var db = NewDb();
        var subId = await SeedAsync(db, "signing-secret", secretBoundTo: _ => Guid.NewGuid());
        var handler = new CountingHandler();
        var job = new TestableJob(db, handler);

        var act = async () => await job.ExecuteCoreAsync(subId, "{}", isFinalAttempt: true, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Sends.Should().Be(0, "an unreadable signing secret must never fall through to an unsigned POST");

        var sub = await db.IntegrationSubscriptions.AsNoTracking().SingleAsync(s => s.Id == subId);
        sub.FailureCount.Should().Be(1);
    }

    [Fact]
    public async Task Fire_NoSecretConfigured_StillSendsUnsigned()
    {
        await using var db = NewDb();
        var subId = await SeedAsync(db, secret: null);
        var handler = new CountingHandler();
        var job = new TestableJob(db, handler);

        await job.ExecuteCoreAsync(subId, "{}", isFinalAttempt: true, default);

        handler.Sends.Should().Be(1,
            "a subscription that never configured a secret is not asking for signatures");
        handler.Last!.Headers.Contains("X-ProcuLink-Signature").Should().BeFalse();
    }
}
