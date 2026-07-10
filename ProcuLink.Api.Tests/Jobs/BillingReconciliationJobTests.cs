using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Worker.Jobs;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// Tests the <see cref="BillingReconciliationJob"/> sweep: it reconciles only orgs that carry a
/// Stripe subscription id, and a per-org failure never aborts the remaining orgs (try/catch
/// isolation). The per-org reconciliation logic itself is covered by
/// StripeSubscriptionReconciliationServiceTests.
/// </summary>
public class BillingReconciliationJobTests
{
    private sealed class RecordingReconciler : IBillingReconciliationService
    {
        public List<Guid> Reconciled { get; } = new();
        public HashSet<Guid> Throw { get; } = new();
        public Task ReconcileOrgAsync(Guid orgId, CancellationToken ct = default)
        {
            Reconciled.Add(orgId);
            if (Throw.Contains(orgId)) throw new InvalidOperationException("boom");
            return Task.CompletedTask;
        }
    }

    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Organisation Org(string? subId)
    {
        var id = Guid.NewGuid();
        return new Organisation
        {
            Id = id, ClerkOrgId = $"org_{id:N}", Name = "n", Slug = $"s-{id:N}",
            Plan = PlanConstants.Growth, AccountStatus = AccountStatusConstants.Active,
            StripeSubscriptionId = subId, CreatedAt = DateTime.UtcNow, TrialStartedAt = DateTime.UtcNow,
        };
    }

    [Fact]
    public async Task Sweep_OnlyReconcilesOrgsWithASubscriptionId()
    {
        var db = MakeDb();
        var withSub = Org("sub_a");
        var blankSub = Org("");
        var noSub = Org(null);
        db.Organisations.AddRange(withSub, blankSub, noSub);
        await db.SaveChangesAsync();
        var reconciler = new RecordingReconciler();
        var job = new BillingReconciliationJob(db, reconciler, NullLogger<BillingReconciliationJob>.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        reconciler.Reconciled.Should().ContainSingle().Which.Should().Be(withSub.Id);
    }

    [Fact]
    public async Task Sweep_OneOrgThrowing_DoesNotAbortTheRest()
    {
        var db = MakeDb();
        var a = Org("sub_a");
        var b = Org("sub_b");
        var c = Org("sub_c");
        db.Organisations.AddRange(a, b, c);
        await db.SaveChangesAsync();
        var reconciler = new RecordingReconciler();
        reconciler.Throw.Add(b.Id);
        var job = new BillingReconciliationJob(db, reconciler, NullLogger<BillingReconciliationJob>.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        reconciler.Reconciled.Should().BeEquivalentTo(new[] { a.Id, b.Id, c.Id },
            "every org is attempted even though one threw (per-org try/catch isolation)");
    }
}
