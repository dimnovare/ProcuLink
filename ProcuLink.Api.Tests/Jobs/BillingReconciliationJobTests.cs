using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
/// Stripe subscription id, a per-org failure never aborts the rest (try/catch isolation), and the
/// mass-downgrade circuit breaker aborts the whole sweep when an abnormal number of orgs are past
/// the missing-grace window. The per-org reconciliation logic itself is covered by
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

    private static IConfiguration Config(
        int? massDowngradeThreshold = null,
        int? floor = null,
        double? fraction = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Billing:ReconciliationMassDowngradeThreshold"] = massDowngradeThreshold?.ToString(),
            ["Billing:ReconciliationMassDowngradeFloor"]     = floor?.ToString(),
            ["Billing:ReconciliationMassDowngradeFraction"]  =
                fraction?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Build();

    private static Organisation Org(string? subId, DateTime? missingSince = null)
    {
        var id = Guid.NewGuid();
        return new Organisation
        {
            Id = id, ClerkOrgId = $"org_{id:N}", Name = "n", Slug = $"s-{id:N}",
            Plan = PlanConstants.Growth, AccountStatus = AccountStatusConstants.Active,
            StripeSubscriptionId = subId, StripeReconciliationMissingSince = missingSince,
            CreatedAt = DateTime.UtcNow, TrialStartedAt = DateTime.UtcNow,
        };
    }

    private static BillingReconciliationJob Job(ProcuLinkDbContext db, RecordingReconciler r, IConfiguration config) =>
        new(db, r, config, NullLogger<BillingReconciliationJob>.Instance);

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

        await Job(db, reconciler, Config()).ExecuteAsync(CancellationToken.None);

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

        await Job(db, reconciler, Config()).ExecuteAsync(CancellationToken.None);

        reconciler.Reconciled.Should().BeEquivalentTo(new[] { a.Id, b.Id, c.Id },
            "every org is attempted even though one threw (per-org try/catch isolation)");
    }

    [Fact]
    public async Task CircuitBreaker_AbortsWhenTooManyOrgsPastGrace()
    {
        var db = MakeDb();
        var pastGrace = DateTime.UtcNow.AddDays(-4); // beyond the 3-day grace
        // 3 orgs simultaneously past grace, threshold 2 → systemic-fault suspicion → abort.
        db.Organisations.AddRange(
            Org("sub_a", pastGrace), Org("sub_b", pastGrace), Org("sub_c", pastGrace));
        await db.SaveChangesAsync();
        var reconciler = new RecordingReconciler();

        await Job(db, reconciler, Config(massDowngradeThreshold: 2)).ExecuteAsync(CancellationToken.None);

        reconciler.Reconciled.Should().BeEmpty("the mass-downgrade circuit breaker must abort the whole sweep");
    }

    [Fact]
    public async Task CircuitBreaker_ProceedsWhenPastGraceAtOrUnderThreshold()
    {
        var db = MakeDb();
        var pastGrace = DateTime.UtcNow.AddDays(-4);
        // 2 orgs past grace, threshold 2 → 2 is NOT > 2 → proceed normally.
        var a = Org("sub_a", pastGrace);
        var b = Org("sub_b", pastGrace);
        db.Organisations.AddRange(a, b);
        await db.SaveChangesAsync();
        var reconciler = new RecordingReconciler();

        await Job(db, reconciler, Config(massDowngradeThreshold: 2)).ExecuteAsync(CancellationToken.None);

        reconciler.Reconciled.Should().BeEquivalentTo(new[] { a.Id, b.Id });
    }

    // ── Relative circuit breaker (Finding 1) ─────────────────────────────────
    // The absolute-only breaker (fixed 10) is inert for a small paying base: a
    // wrong-mode/wrong-account Stripe key 404s every live subscription, so ~3 days
    // later the ENTIRE base of ≤10 paying orgs is past grace yet 10 is never exceeded,
    // and the whole base is frozen in one 02:00 run. The relative breaker trips when
    // past-grace orgs are both ≥ a small absolute floor (default 3) AND ≥ a fraction
    // (default 25%) of the subscribed orgs reconciled this run.

    [Fact]
    public async Task CircuitBreaker_Relative_SmallPayingBaseAllPastGrace_Aborts()
    {
        var db = MakeDb();
        var pastGrace = DateTime.UtcNow.AddDays(-4);
        // 8 paying orgs, ALL past grace. Absolute threshold defaults to 10 → 8 is NOT
        // > 10, so the absolute breaker never fires. The relative breaker (8 ≥ floor 3
        // AND 8 ≥ 25% of 8 = 2) MUST abort — otherwise the whole small base freezes.
        for (var i = 0; i < 8; i++)
            db.Organisations.Add(Org($"sub_{i}", pastGrace));
        await db.SaveChangesAsync();
        var reconciler = new RecordingReconciler();

        // Defaults: absolute 10, floor 3, fraction 0.25.
        await Job(db, reconciler, Config()).ExecuteAsync(CancellationToken.None);

        reconciler.Reconciled.Should().BeEmpty(
            "the whole small paying base is past grace — the relative breaker must abort even though past-grace (8) ≤ absolute threshold (10)");
    }

    [Fact]
    public async Task CircuitBreaker_Relative_OneLegitCancellationInLargeBase_ProceedsAndDowngradesThatOne()
    {
        var db = MakeDb();
        var pastGrace = DateTime.UtcNow.AddDays(-4);
        // 40 subscribed orgs, exactly ONE genuinely past grace (a lone real cancellation).
        // 1 of 40 = 2.5% and below the floor of 3 → must NOT trip either breaker; the sweep
        // proceeds and that single org is reconciled (→ downgraded by the real service).
        var lone = Org("sub_lone", pastGrace);
        db.Organisations.Add(lone);
        for (var i = 0; i < 39; i++)
            db.Organisations.Add(Org($"sub_ok_{i}")); // healthy, not past grace
        await db.SaveChangesAsync();
        var reconciler = new RecordingReconciler();

        await Job(db, reconciler, Config()).ExecuteAsync(CancellationToken.None);

        reconciler.Reconciled.Should().HaveCount(40, "a lone genuine cancellation must never abort the sweep");
        reconciler.Reconciled.Should().Contain(lone.Id,
            "the single genuinely-cancelled org must still be reconciled (and thus downgraded)");
    }

    [Fact]
    public async Task CircuitBreaker_Absolute_LargeBaseAllPastGrace_StillAborts()
    {
        var db = MakeDb();
        var pastGrace = DateTime.UtcNow.AddDays(-4);
        // 12 orgs past grace with the default absolute threshold 10 → the absolute breaker
        // still trips independently (12 > 10), preserving the original safety net.
        for (var i = 0; i < 12; i++)
            db.Organisations.Add(Org($"sub_{i}", pastGrace));
        await db.SaveChangesAsync();
        var reconciler = new RecordingReconciler();

        await Job(db, reconciler, Config()).ExecuteAsync(CancellationToken.None);

        reconciler.Reconciled.Should().BeEmpty(
            "the absolute-10 breaker must still trip for a large all-past-grace base");
    }
}
