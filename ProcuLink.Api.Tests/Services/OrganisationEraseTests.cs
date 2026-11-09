using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Worker.Jobs;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// The two decisions around <see cref="OrganisationEraser"/> that the Postgres model-walk guard
/// does not exercise: when it refuses, and which orgs the daily sweep hands it.
/// </summary>
public sealed class OrganisationEraseTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>().UseInMemoryDatabase($"org-erase-{Guid.NewGuid()}").Options);

    private static Organisation Org(string slug, int ageHours = 24, string? customer = null, string? subscription = null, string plan = "pilot") => new()
    {
        Id = Guid.NewGuid(), ClerkOrgId = $"org_{Guid.NewGuid():N}", Name = slug, Slug = slug, Plan = plan,
        CreatedAt = DateTime.UtcNow.AddHours(-ageHours), TrialStartedAt = DateTime.UtcNow,
        StripeCustomerId = customer, StripeSubscriptionId = subscription, StripeSubscriptionStatus = subscription is null ? null : "canceled",
    };

    [Theory]
    [InlineData("cus_test", null, "pilot", false, OrgEraseOutcome.RefusedPaidSubscription)]       // customer, webhook never landed
    [InlineData(null, "sub_test", "pilot", false, OrgEraseOutcome.RefusedPaidSubscription)]       // even a "canceled" one
    [InlineData(null, null, "enterprise", false, OrgEraseOutcome.RefusedPaidSubscription)]        // manual contract, no Stripe ids
    [InlineData(null, null, "pilot", true, OrgEraseOutcome.RefusedBillingEvidence)]
    public async Task Erase_RefusesAPayingOrBilledOrg_AndDeletesNothing(
        string? customer, string? subscription, string plan, bool billedOverage, OrgEraseOutcome expected)
    {
        await using var db = NewDb();
        var org = Org("erase-refusal-test", customer: customer, subscription: subscription, plan: plan);
        db.Organisations.Add(org);
        if (billedOverage)
            db.OverageBillingRecords.Add(new OverageBillingRecord { Id = Guid.NewGuid(), OrgId = org.Id, BillingKey = "test" });
        await db.SaveChangesAsync();

        var receipt = await new OrganisationEraser(db, Mock.Of<IFileStorageService>(), NullLogger<OrganisationEraser>.Instance)
            .EraseAsync(org.Id, "test", "refusal", CancellationToken.None);

        receipt.Outcome.Should().Be(expected);
        (await db.Organisations.AnyAsync(o => o.Id == org.Id)).Should().BeTrue();
    }

    [Theory]
    [InlineData("proculink-ci-smoke-run1", 7, null, null, true)]
    [InlineData("proculink-ci-smoke-run1", 7, "membership", null, true)]   // signed into, never used
    [InlineData("proculink-ci-smoke-run1", 7, "inbound", null, true)]     // a backfill mints one per org
    [InlineData("proculink-ci-smoke-run1", 7, "order", null, false)]       // used
    [InlineData("proculink-ci-smoke-run1", 7, "buyer", null, false)]       // used, in a table nobody hand-listed
    [InlineData("proculink-ci-smoke-run1", 1, null, null, false)]          // a smoke run may still be in flight
    [InlineData("proculink-ci-smoke-run1", 7, null, "cus_test", false)]    // a Stripe customer
    [InlineData("smoke-20260815-run1", 7, null, null, false)]              // no generator emits this; erased by hand
    [InlineData("acme-workspace", 7, null, null, false)]
    // The shapes of real workspaces (invented values — the repo is public) must never match.
    [InlineData("my-organization-1700000000000000001", 7, null, null, false)]
    [InlineData("personal-workspace-0a1b", 7, null, null, false)]
    public async Task Sweep_ErasesOnlyOldUnusedUnpaidSmokeOrgs(
        string slug, int ageHours, string? row, string? customer, bool erased)
    {
        await using var db = NewDb();
        var org = Org(slug, ageHours, customer);
        db.Organisations.Add(org);
        if (row == "membership") db.Memberships.Add(new Membership { Id = Guid.NewGuid(), OrgId = org.Id, UserId = Guid.NewGuid(), Role = "admin" });
        if (row == "inbound") db.Set<OrgInboundAddress>().Add(new OrgInboundAddress { Id = Guid.NewGuid(), OrganisationId = org.Id });
        if (row == "order") db.PurchaseOrders.Add(new PurchaseOrderEntity { Id = Guid.NewGuid(), OrgId = org.Id });
        if (row == "buyer") db.Buyers.Add(new Buyer { Id = Guid.NewGuid(), OrgId = org.Id });
        await db.SaveChangesAsync();

        var eraser = new Mock<IOrganisationEraser>();
        eraser.Setup(e => e.EraseAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgEraseReceipt(OrgEraseOutcome.Erased, org.Id, slug, null, new Dictionary<string, int>(), 0, [], "", "", DateTime.UtcNow));

        await new DisposableOrgSweepJob(db, eraser.Object, NullLogger<DisposableOrgSweepJob>.Instance)
            .ExecuteAsync(CancellationToken.None);

        eraser.Verify(e => e.EraseAsync(org.Id, DisposableOrgSweepJob.Actor, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            erased ? Times.Once() : Times.Never());
    }
}
