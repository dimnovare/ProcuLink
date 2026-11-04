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

    private static Organisation Org(string slug, int ageHours = 24, string? subscriptionStatus = null) => new()
    {
        Id = Guid.NewGuid(), ClerkOrgId = $"org_{Guid.NewGuid():N}", Name = slug, Slug = slug,
        CreatedAt = DateTime.UtcNow.AddHours(-ageHours), TrialStartedAt = DateTime.UtcNow,
        StripeSubscriptionId = subscriptionStatus is null ? null : "sub_test", StripeSubscriptionStatus = subscriptionStatus,
    };

    [Theory]
    [InlineData("active", false, OrgEraseOutcome.RefusedPaidSubscription)]
    [InlineData("past_due", false, OrgEraseOutcome.RefusedPaidSubscription)]
    [InlineData(null, true, OrgEraseOutcome.RefusedBillingEvidence)]
    public async Task Erase_RefusesAPayingOrBilledOrg_AndDeletesNothing(
        string? subscriptionStatus, bool billedOverage, OrgEraseOutcome expected)
    {
        await using var db = NewDb();
        var org = Org("erase-refusal-test", subscriptionStatus: subscriptionStatus);
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
    [InlineData("proculink-ci-smoke-run1", 7, false, null, true)]
    [InlineData("smoke-20260815-run1", 7, false, null, true)]
    [InlineData("acme-workspace", 7, false, null, false)]          // empty, but not disposable
    [InlineData("smoke-team", 7, false, null, false)]              // smoke- without the run date
    [InlineData("proculink-ci-smoke-run1", 1, false, null, false)] // a smoke run may still be in flight
    [InlineData("proculink-ci-smoke-run1", 7, true, null, false)]  // has an order
    [InlineData("proculink-ci-smoke-run1", 7, false, "canceled", false)] // has a Stripe subscription
    public async Task Sweep_ErasesOnlyOldEmptyUnsubscribedSmokeOrgs(
        string slug, int ageHours, bool hasOrder, string? subscriptionStatus, bool erased)
    {
        await using var db = NewDb();
        var org = Org(slug, ageHours, subscriptionStatus);
        db.Organisations.Add(org);
        if (hasOrder) db.PurchaseOrders.Add(new PurchaseOrderEntity { Id = Guid.NewGuid(), OrgId = org.Id });
        await db.SaveChangesAsync();

        var eraser = new Mock<IOrganisationEraser>();
        eraser.Setup(e => e.EraseAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgEraseReceipt(OrgEraseOutcome.Erased, org.Id, slug, new Dictionary<string, int>(), 0, "", "", DateTime.UtcNow));

        await new DisposableOrgSweepJob(db, eraser.Object, NullLogger<DisposableOrgSweepJob>.Instance)
            .ExecuteAsync(CancellationToken.None);

        eraser.Verify(e => e.EraseAsync(org.Id, DisposableOrgSweepJob.Actor, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            erased ? Times.Once() : Times.Never());
    }
}
