using Microsoft.EntityFrameworkCore;
using Moq;
using Npgsql;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// ONE postgres:16 for the whole class. xUnit builds a fresh test-class instance per test, so an
/// <c>IAsyncLifetime</c> on the test class itself would start a container per test — twenty-plus of
/// them, which is the load <see cref="PostgresContainerCollection"/> already documents as making a
/// container time out on its first connection. Every test seeds its own organisation, so sharing
/// the database costs nothing as long as assertions stay org-scoped, which they are.
/// </summary>
public sealed class AutoSendDryRunPostgresFixture(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;

    public DbContextOptions<ProcuLinkDbContext>? Options { get; private set; }

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_autosend");

        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_databaseConnectionString)
        {
            Pooling = false,
        }.ConnectionString;

        Options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(connectionString)
            .Options;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }
}

/// <summary>
/// WP-33 stage 1 — <b>auto-send in dry run</b>, proven on real Postgres.
///
/// <para>The packet's own words: "the negative test is the most important one you will write — an
/// order with any blocking issue must never be considered clean." So most of what follows is an
/// attack on <see cref="AutoSendDryRunEvaluator"/>, trying to construct an order that is called
/// clean while carrying a real problem. The subtlest attempt — and the one that would have slipped
/// through a naive <c>!decision.Blocked</c> check — is
/// <see cref="An_order_whose_blockers_an_operator_excused_is_never_clean"/>: WP-17's gate answers
/// <c>Blocked: false</c> for an order whose supplier rules refuse it, when a human has signed an
/// override. That human authorised ONE send. It is not consent to send unattended, forever.</para>
///
/// <para>Real Postgres and not EF InMemory for two reasons the repo has already paid for: the
/// unique index that makes a Hangfire refetch harmless does not exist InMemory, and a forgotten
/// migration column vanishes silently there.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class AutoSendDryRunPostgresTests : IClassFixture<AutoSendDryRunPostgresFixture>
{
    private readonly DbContextOptions<ProcuLinkDbContext>? _options;

    public AutoSendDryRunPostgresTests(AutoSendDryRunPostgresFixture fixture) => _options = fixture.Options;

    // ── The happy path ────────────────────────────────────────────────────────

    [DockerRequiredFact]
    public async Task A_clean_opted_in_order_records_one_would_have_sent_row()
    {
        var ids = await SeedAsync(autoTransform: true, status: OrderStatusConstants.Ready);

        await using var db = new ProcuLinkDbContext(_options!);
        var outcome = await EvaluatorFor(db).EvaluateAsync(ids.OrgId, ids.OrderId, CancellationToken.None);

        Assert.True(outcome.Recorded);
        Assert.True(outcome.WouldHaveSent);
        Assert.Equal(AutoSendDecision.Clean, outcome.Decision);
        Assert.Equal("http", outcome.Channel);
        Assert.Equal("csv", outcome.OutputFormat);

        await using var read = new ProcuLinkDbContext(_options!);
        var row = await read.AutoSendDryRuns.AsNoTracking().SingleAsync(r => r.OrgId == ids.OrgId);

        Assert.Equal(ids.OrderId, row.OrderId);
        Assert.Equal(ids.SupplierId, row.SupplierId);
        Assert.True(row.WouldHaveSent);
        Assert.Equal(AutoSendDecision.Clean, row.Decision);
        Assert.Equal("http", row.Channel);
        Assert.Equal("csv", row.OutputFormat);
        Assert.Equal(0, row.BlockerCount);

        // The five fields the founder reads. A digest is what makes two rows comparable, so a null
        // one is a row that cannot answer "was this the same document twice?".
        Assert.False(string.IsNullOrWhiteSpace(row.DecisionDigest));
        Assert.Equal(64, row.DecisionDigest!.Length);
        Assert.NotNull(row.Evidence);
        Assert.NotEqual(default, row.EvaluatedAt);
    }

    /// <summary>
    /// The whole ruling in one assertion: evaluating an order must leave it exactly where it was.
    /// No transform claim, no artifact, no delivery attempt, no status movement — because every one
    /// of those is a step on the path to a real supplier receiving a real purchase order.
    /// </summary>
    [DockerRequiredFact]
    public async Task Evaluating_an_order_moves_nothing()
    {
        var ids = await SeedAsync(autoTransform: true, status: OrderStatusConstants.Ready);

        await using (var db = new ProcuLinkDbContext(_options!))
            await EvaluatorFor(db).EvaluateAsync(ids.OrgId, ids.OrderId, CancellationToken.None);

        await using var read = new ProcuLinkDbContext(_options!);

        var order = await read.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == ids.OrderId);
        Assert.Equal(OrderStatusConstants.Ready, order.Status);

        Assert.False(await read.OutboundArtifacts.AnyAsync(a => a.OrderId == ids.OrderId));
        Assert.False(await read.DeliveryAttempts.AnyAsync(a => a.OrderId == ids.OrderId));
    }

    // ── Not opted in: nothing is recorded ─────────────────────────────────────

    [DockerRequiredFact]
    public async Task The_switch_off_records_nothing()
    {
        var ids = await SeedAsync(autoTransform: false, status: OrderStatusConstants.Ready);

        await using var db = new ProcuLinkDbContext(_options!);
        var outcome = await EvaluatorFor(db).EvaluateAsync(ids.OrgId, ids.OrderId, CancellationToken.None);

        Assert.False(outcome.Recorded);
        Assert.False(outcome.WouldHaveSent);
        Assert.Equal(AutoSendDecision.AutoTransformOff, outcome.Decision);

        await using var read = new ProcuLinkDbContext(_options!);
        Assert.False(await read.AutoSendDryRuns.AnyAsync(r => r.OrgId == ids.OrgId));
    }

    /// <summary>
    /// The opt-in lives ON the delivery config, so a supplier with nowhere to send cannot be opted
    /// in at all. This pins that structural property: delete the delivery config and the order goes
    /// quiet rather than becoming sendable.
    /// </summary>
    [DockerRequiredFact]
    public async Task A_supplier_with_no_delivery_config_records_nothing()
    {
        var ids = await SeedAsync(autoTransform: true, status: OrderStatusConstants.Ready);

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var cfg = await db.SupplierDeliveryConfigs.SingleAsync(c => c.SupplierId == ids.SupplierId);
            db.SupplierDeliveryConfigs.Remove(cfg);
            await db.SaveChangesAsync();
        }

        await using var db2 = new ProcuLinkDbContext(_options!);
        var outcome = await EvaluatorFor(db2).EvaluateAsync(ids.OrgId, ids.OrderId, CancellationToken.None);

        Assert.False(outcome.Recorded);
        Assert.False(outcome.WouldHaveSent);
        Assert.Equal(AutoSendDecision.NoDeliveryConfig, outcome.Decision);
    }

    // ── The negative tests: opted in, but never clean ─────────────────────────

    /// <summary>
    /// Every status that is not <c>ready</c>. An order still parsing, one a human was asked to
    /// review, one that already failed, and — the one worth naming — one that has ALREADY moved on
    /// to <c>delivered</c>, which must never be re-sent by an automatic path.
    /// </summary>
    [DockerRequiredTheory]
    [InlineData(OrderStatusConstants.Parsing)]
    [InlineData(OrderStatusConstants.PendingReview)]
    [InlineData(OrderStatusConstants.Unrouted)]
    [InlineData(OrderStatusConstants.Failed)]
    [InlineData(OrderStatusConstants.ReadyToDeliver)]
    [InlineData(OrderStatusConstants.Delivering)]
    [InlineData(OrderStatusConstants.Delivered)]
    [InlineData(OrderStatusConstants.DeliveryFailed)]
    [InlineData(OrderStatusConstants.RejectedBySupplier)]
    [InlineData(OrderStatusConstants.TransformFailed)]
    public async Task An_order_that_is_not_ready_is_never_clean(string status)
    {
        var ids = await SeedAsync(autoTransform: true, status: status);

        await using var db = new ProcuLinkDbContext(_options!);
        var outcome = await EvaluatorFor(db).EvaluateAsync(ids.OrgId, ids.OrderId, CancellationToken.None);

        Assert.False(outcome.WouldHaveSent);
        Assert.Equal(AutoSendDecision.StatusNotReady, outcome.Decision);

        await using var read = new ProcuLinkDbContext(_options!);
        var row = await read.AutoSendDryRuns.AsNoTracking().SingleAsync(r => r.OrgId == ids.OrgId);
        Assert.False(row.WouldHaveSent);
    }

    /// <summary>
    /// A line still needing a human, on an order whose header says <c>ready</c>. The status and the
    /// lines are checked INDEPENDENTLY on purpose: inferring "all lines resolved" from the status
    /// would mean trusting one write to stay in step with another, and the real transform does not
    /// make that assumption either (it re-checks <c>NeedsReview</c> at
    /// <c>OrderTransformService.TransformAsync</c> before doing anything).
    /// </summary>
    [DockerRequiredFact]
    public async Task An_order_with_an_unresolved_line_is_never_clean_even_when_its_status_says_ready()
    {
        var ids = await SeedAsync(autoTransform: true, status: OrderStatusConstants.Ready);

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var line = await db.PurchaseOrderLines.SingleAsync(l => l.OrderId == ids.OrderId);
            line.NeedsReview      = true;
            line.ReviewReason     = "no supplier item code";
            line.SupplierItemCode = null;
            await db.SaveChangesAsync();
        }

        await using var db2 = new ProcuLinkDbContext(_options!);
        var outcome = await EvaluatorFor(db2).EvaluateAsync(ids.OrgId, ids.OrderId, CancellationToken.None);

        Assert.False(outcome.WouldHaveSent);
        Assert.Equal(AutoSendDecision.UnresolvedLines, outcome.Decision);
    }

    /// <summary>An order the supplier's own acceptance rules refuse.</summary>
    [DockerRequiredFact]
    public async Task An_order_the_supplier_rules_refuse_is_never_clean()
    {
        var ids = await SeedAsync(autoTransform: true, status: OrderStatusConstants.Ready);

        await using var db = new ProcuLinkDbContext(_options!);
        var evaluator = EvaluatorFor(db, blockers: [Blocker("line.qty.max", 1)]);

        var outcome = await evaluator.EvaluateAsync(ids.OrgId, ids.OrderId, CancellationToken.None);

        Assert.False(outcome.WouldHaveSent);
        Assert.Equal(AutoSendDecision.AcceptanceBlocked, outcome.Decision);

        await using var read = new ProcuLinkDbContext(_options!);
        var row = await read.AutoSendDryRuns.AsNoTracking().SingleAsync(r => r.OrgId == ids.OrgId);
        Assert.False(row.WouldHaveSent);
        Assert.Equal(1, row.BlockerCount);
    }

    /// <summary>
    /// <b>The attack.</b> WP-17's gate returns <c>Blocked: false</c> for an order whose supplier
    /// rules refuse it, once an operator has recorded an override covering every blocker. An
    /// evaluator that asked only "is it blocked?" would call this order clean and, in stage 2, send
    /// a document the supplier's own rules reject — on the strength of a human decision that was
    /// about one send, made once, possibly weeks earlier.
    ///
    /// <para>Note this test grants the override through the REAL
    /// <see cref="AcceptanceGate.RecordOverrideAsync"/>, so the audit row it reads is the genuine
    /// article and the gate genuinely answers <c>Blocked: false</c> — asserted below, so the test
    /// cannot quietly stop exercising the case it exists for.</para>
    /// </summary>
    [DockerRequiredFact]
    public async Task An_order_whose_blockers_an_operator_excused_is_never_clean()
    {
        var ids = await SeedAsync(autoTransform: true, status: OrderStatusConstants.Ready);
        var blockers = new[] { Blocker("line.qty.max", 1) };

        await using var db = new ProcuLinkDbContext(_options!);
        var gate = GateFor(db, blockers);

        var recorded = await gate.RecordOverrideAsync(
            ids.OrgId, ids.OrderId, "user_operator", "Customer confirmed by phone.", CancellationToken.None);
        Assert.True(recorded.Recorded);

        // Anti-vacuity: the gate really does wave this order through now. If this ever stopped
        // being true, the test below would pass for the wrong reason.
        var decision = await gate.EvaluateAsync(ids.OrgId, ids.OrderId, CancellationToken.None);
        Assert.False(decision!.Blocked);
        Assert.True(decision.Overridden);
        Assert.NotEmpty(decision.Blockers);

        var outcome = await new AutoSendDryRunEvaluator(db, gate)
            .EvaluateAsync(ids.OrgId, ids.OrderId, CancellationToken.None);

        Assert.False(outcome.WouldHaveSent);
        Assert.Equal(AutoSendDecision.AcceptanceOverridden, outcome.Decision);

        await using var read = new ProcuLinkDbContext(_options!);
        var row = await read.AutoSendDryRuns.AsNoTracking().SingleAsync(r => r.OrgId == ids.OrgId);
        Assert.False(row.WouldHaveSent);
        Assert.Equal(1, row.BlockerCount);
    }

    /// <summary>
    /// The gate itself failing is not permission to send. An order nobody could check against the
    /// supplier's rules is exactly the order not to send unattended — and it is recorded under its
    /// own decision code, so the trail never claims the supplier refused it.
    /// </summary>
    [DockerRequiredFact]
    public async Task A_gate_that_cannot_answer_is_never_clean()
    {
        var ids = await SeedAsync(autoTransform: true, status: OrderStatusConstants.Ready);

        await using var db = new ProcuLinkDbContext(_options!);
        var evaluator = new AutoSendDryRunEvaluator(db, new ThrowingGate());

        var outcome = await evaluator.EvaluateAsync(ids.OrgId, ids.OrderId, CancellationToken.None);

        Assert.False(outcome.WouldHaveSent);
        Assert.Equal(AutoSendDecision.AcceptanceGateUnavailable, outcome.Decision);
    }

    // ── The duplicate signal ──────────────────────────────────────────────────

    /// <summary>
    /// An order carrying an OPEN <c>duplicate_po_number</c> warning is a human decision, not an
    /// automatic send.
    ///
    /// <para>Duplicate detection is advisory on purpose — it opens a warning and blocks nothing,
    /// because suppliers legitimately reuse PO numbers. That is sound while a person reads the
    /// warning before clicking Send, which is what the order review screen now puts in front of
    /// them. It stops being sound the moment nobody is clicking: the thing waved through would be
    /// a second copy of a purchase order the supplier already has, and no amount of audit trail
    /// un-sends it.</para>
    /// </summary>
    [DockerRequiredFact]
    public async Task An_order_flagged_as_a_possible_duplicate_is_never_clean()
    {
        var ids = await SeedAsync(autoTransform: true, status: OrderStatusConstants.Ready);
        await AddExceptionAsync(ids.OrgId, ids.OrderId, OrderExceptionService.DuplicatePoNumberCode, "open");

        await using var db = new ProcuLinkDbContext(_options!);
        var outcome = await EvaluatorFor(db).EvaluateAsync(ids.OrgId, ids.OrderId, CancellationToken.None);

        Assert.False(outcome.WouldHaveSent);
        Assert.Equal(AutoSendDecision.PossibleDuplicate, outcome.Decision);

        await using var read = new ProcuLinkDbContext(_options!);
        var row = await read.AutoSendDryRuns.AsNoTracking().SingleAsync(r => r.OrgId == ids.OrgId);
        Assert.False(row.WouldHaveSent);
        Assert.Equal(AutoSendDecision.PossibleDuplicate, row.Decision);
    }

    /// <summary>
    /// The other arm. A duplicate warning an operator has already dealt with must NOT hold the
    /// order forever — otherwise the flag becomes impossible to clear and the supplier's opt-in is
    /// worth nothing. <c>resolved</c> and <c>ignored</c> are both "a human looked and decided".
    /// </summary>
    [DockerRequiredTheory]
    [InlineData("resolved")]
    [InlineData("ignored")]
    public async Task A_duplicate_warning_a_human_has_already_settled_does_not_hold_the_order(string state)
    {
        var ids = await SeedAsync(autoTransform: true, status: OrderStatusConstants.Ready);
        await AddExceptionAsync(ids.OrgId, ids.OrderId, OrderExceptionService.DuplicatePoNumberCode, state);

        await using var db = new ProcuLinkDbContext(_options!);
        var outcome = await EvaluatorFor(db).EvaluateAsync(ids.OrgId, ids.OrderId, CancellationToken.None);

        Assert.True(outcome.WouldHaveSent);
        Assert.Equal(AutoSendDecision.Clean, outcome.Decision);
    }

    /// <summary>
    /// Anti-vacuity for the two tests above: the check keys on the duplicate CODE, not on "this
    /// order has an open exception". An order can legitimately carry other open exceptions —
    /// refusing every one of them would quietly make auto-send unreachable for reasons nobody
    /// intended, under a decision code that names duplicates.
    /// </summary>
    [DockerRequiredFact]
    public async Task An_unrelated_open_exception_does_not_make_an_order_a_possible_duplicate()
    {
        var ids = await SeedAsync(autoTransform: true, status: OrderStatusConstants.Ready);
        await AddExceptionAsync(ids.OrgId, ids.OrderId, "delivery_unconfirmed", "open");

        await using var db = new ProcuLinkDbContext(_options!);
        var outcome = await EvaluatorFor(db).EvaluateAsync(ids.OrgId, ids.OrderId, CancellationToken.None);

        Assert.True(outcome.WouldHaveSent);
        Assert.Equal(AutoSendDecision.Clean, outcome.Decision);
    }

    /// <summary>
    /// Org scope, on the new read as on every other. A duplicate warning belonging to a DIFFERENT
    /// organisation — same order id, another tenant's row — must not reach this order's decision.
    /// </summary>
    [DockerRequiredFact]
    public async Task Another_organisations_duplicate_warning_does_not_hold_this_order()
    {
        var ids      = await SeedAsync(autoTransform: true, status: OrderStatusConstants.Ready);
        var otherOrg = await SeedOrganisationAsync();

        await AddExceptionAsync(otherOrg, ids.OrderId, OrderExceptionService.DuplicatePoNumberCode, "open");

        await using var db = new ProcuLinkDbContext(_options!);
        var outcome = await EvaluatorFor(db).EvaluateAsync(ids.OrgId, ids.OrderId, CancellationToken.None);

        Assert.True(outcome.WouldHaveSent);
        Assert.Equal(AutoSendDecision.Clean, outcome.Decision);
    }

    /// <summary>An opted-in supplier whose configured channel is blank has nowhere to send.</summary>
    [DockerRequiredFact]
    public async Task An_opted_in_supplier_with_no_channel_is_never_clean()
    {
        var ids = await SeedAsync(autoTransform: true, status: OrderStatusConstants.Ready);

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var cfg = await db.SupplierDeliveryConfigs.SingleAsync(c => c.SupplierId == ids.SupplierId);
            cfg.Protocol = "";
            await db.SaveChangesAsync();
        }

        await using var db2 = new ProcuLinkDbContext(_options!);
        var outcome = await EvaluatorFor(db2).EvaluateAsync(ids.OrgId, ids.OrderId, CancellationToken.None);

        Assert.False(outcome.WouldHaveSent);
        Assert.Equal(AutoSendDecision.NoDeliveryChannel, outcome.Decision);
    }

    /// <summary>Cross-tenant: another org's id must never reach this org's order.</summary>
    [DockerRequiredFact]
    public async Task An_order_is_invisible_to_another_organisation()
    {
        var ids = await SeedAsync(autoTransform: true, status: OrderStatusConstants.Ready);

        await using var db = new ProcuLinkDbContext(_options!);
        var outcome = await EvaluatorFor(db).EvaluateAsync(Guid.NewGuid(), ids.OrderId, CancellationToken.None);

        Assert.False(outcome.Recorded);
        Assert.Equal(AutoSendDecision.OrderNotFound, outcome.Decision);

        await using var read = new ProcuLinkDbContext(_options!);
        Assert.False(await read.AutoSendDryRuns.AnyAsync(r => r.OrderId == ids.OrderId));
    }

    // ── Idempotency: a Hangfire refetch must not double-count ─────────────────

    /// <summary>
    /// The Hangfire refetch: a worker executes the job, dies before acknowledging it, and the job
    /// runs again. In stage 1 a second run must not double-count a would-be send; in stage 2 the
    /// same boundary is what stops the PO going out twice.
    /// </summary>
    [DockerRequiredFact]
    public async Task A_repeated_evaluation_records_exactly_one_row()
    {
        var ids = await SeedAsync(autoTransform: true, status: OrderStatusConstants.Ready);

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var first = await EvaluatorFor(db).EvaluateAsync(ids.OrgId, ids.OrderId, CancellationToken.None);
            Assert.True(first.Recorded);
            Assert.False(first.AlreadyEvaluated);
        }

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var second = await EvaluatorFor(db).EvaluateAsync(ids.OrgId, ids.OrderId, CancellationToken.None);
            Assert.True(second.AlreadyEvaluated);
        }

        await using var read = new ProcuLinkDbContext(_options!);
        Assert.Equal(1, await read.AutoSendDryRuns.CountAsync(r => r.OrderId == ids.OrderId));
    }

    /// <summary>
    /// <b>The index is the guarantee, so the index is what gets tested.</b> This bypasses the
    /// evaluator entirely and asks the database directly: two rows, one order. If the unique index
    /// were ever dropped from the migration, every idempotency claim in this file would rest on
    /// nothing — and only this test would notice, because the evaluator's own duplicate handling
    /// would simply stop being reached.
    ///
    /// <para>It exists because the version of this test that went through the evaluator twice
    /// proved nothing: an "already evaluated?" read absorbed the duplicate before the index was
    /// consulted, and BOTH deleting the index and disabling the violation handler left it
    /// green.</para>
    /// </summary>
    [DockerRequiredFact]
    public async Task The_database_itself_refuses_a_second_row_for_the_same_order()
    {
        var ids = await SeedAsync(autoTransform: true, status: OrderStatusConstants.Ready);

        await using var db = new ProcuLinkDbContext(_options!);

        db.AutoSendDryRuns.Add(NewRow(ids));
        await db.SaveChangesAsync();

        db.AutoSendDryRuns.Add(NewRow(ids));   // same (org, order), different id
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.Equal(
            PostgresErrorCodes.UniqueViolation,
            Assert.IsType<PostgresException>(ex.InnerException).SqlState);

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.AutoSendDryRuns.CountAsync(r => r.OrderId == ids.OrderId));
    }

    private static AutoSendDryRun NewRow(Ids ids) => new()
    {
        Id            = Guid.NewGuid(),
        OrgId         = ids.OrgId,
        OrderId       = ids.OrderId,
        SupplierId    = ids.SupplierId,
        WouldHaveSent = true,
        Decision      = AutoSendDecision.Clean,
        EvaluatedAt   = DateTime.UtcNow,
    };

    // ── Seeding + doubles ─────────────────────────────────────────────────────

    private sealed record Ids(Guid OrgId, Guid SupplierId, Guid OrderId);

    private static AutoSendDryRunEvaluator EvaluatorFor(
        ProcuLinkDbContext db, IReadOnlyList<AcceptanceBlocker>? blockers = null) =>
        new(db, GateFor(db, blockers));

    /// <summary>
    /// The REAL <see cref="AcceptanceGate"/> over a real database — only the supplier's rule
    /// evaluation is stubbed, so the override reading (which is the interesting half) is genuine.
    /// </summary>
    private static AcceptanceGate GateFor(
        ProcuLinkDbContext db, IReadOnlyList<AcceptanceBlocker>? blockers = null)
    {
        var acceptance = new Mock<ISupplierAcceptanceService>();
        acceptance
            .Setup(a => a.GetBlockingFailuresAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(blockers ?? Array.Empty<AcceptanceBlocker>());
        return new AcceptanceGate(db, acceptance.Object);
    }

    private static AcceptanceBlocker Blocker(string code, int? line) =>
        new(code, line, $"{code} refused line {line}.", RuleId: Guid.NewGuid(), ProfileVersion: 1,
            ExpectedValue: "10", ActualValue: "99");

    /// <summary>A gate that cannot answer — a DB blip, a malformed pin.</summary>
    private sealed class ThrowingGate : IAcceptanceGate
    {
        public Task<AcceptanceGateDecision?> EvaluateAsync(Guid orgId, Guid orderId, CancellationToken ct) =>
            throw new InvalidOperationException("acceptance profile lookup failed");

        public Task<AcceptanceOverrideResult> RecordOverrideAsync(
            Guid orgId, Guid orderId, string actor, string reason, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private async Task<Ids> SeedAsync(bool autoTransform, string status)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        await using var db = new ProcuLinkDbContext(_options!);

        db.Organisations.Add(new Organisation
        {
            Id              = orgId,
            ClerkOrgId      = $"org_autosend_{orgId:N}",
            Name            = "Auto Send Org",
            Slug            = $"autosend-{orgId:N}",
            Plan            = "operations",
            AccountStatus   = "active",
            CreatedAt       = now,
            TrialStartedAt  = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Recurring Supplier", CreatedAt = now });
        await db.SaveChangesAsync();

        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id            = Guid.NewGuid(),
            OrgId         = orgId,
            SupplierId    = supplierId,
            Protocol      = DeliveryProtocolConstants.Http,
            AutoDeliver   = true,
            AutoTransform = autoTransform,
            ConfigJson    = """{"url":"https://supplier.example/po"}""",
            OutputFormat  = "csv",
            CreatedAt     = now,
            UpdatedAt     = now,
        });

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = "PO-AUTOSEND-1",
            Currency   = "EUR",
            Status     = status,
            OrderDate  = new DateOnly(2026, 8, 1),
            CreatedAt  = now,
            UpdatedAt  = now,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id               = Guid.NewGuid(),
                    OrderId          = orderId,
                    LineNumber       = 1,
                    BuyerItemCode    = "B-1",
                    SupplierItemCode = "S-1",
                    Quantity         = 4m,
                    UnitPrice        = 12.50m,
                    NeedsReview      = false,
                },
            },
        });

        await db.SaveChangesAsync();
        return new Ids(orgId, supplierId, orderId);
    }

    /// <summary>An organisation on its own, for the cross-tenant test.</summary>
    private async Task<Guid> SeedOrganisationAsync()
    {
        var orgId = Guid.NewGuid();
        var now   = DateTime.UtcNow;

        await using var db = new ProcuLinkDbContext(_options!);
        db.Organisations.Add(new Organisation
        {
            Id             = orgId,
            ClerkOrgId     = $"org_autosend_{orgId:N}",
            Name           = "Another Org",
            Slug           = $"autosend-{orgId:N}",
            Plan           = "operations",
            AccountStatus  = "active",
            CreatedAt      = now,
            TrialStartedAt = now,
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    /// <summary>
    /// One exception row against an order, in a named state. Written directly rather than through
    /// <c>OrderExceptionService.ReconcileAsync</c> so a test can construct the <c>resolved</c> and
    /// <c>ignored</c> states the reconciler would not produce on demand.
    /// </summary>
    private async Task AddExceptionAsync(Guid orgId, Guid orderId, string code, string state)
    {
        await using var db = new ProcuLinkDbContext(_options!);
        db.OrderExceptions.Add(new OrderException
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            OrderId    = orderId,
            Stage      = "Parse",
            Code       = code,
            Severity   = "warning",
            State      = state,
            Message    = $"{code} raised for the test in state {state}.",
            CreatedAt  = DateTime.UtcNow,
            ResolvedAt = state == "resolved" ? DateTime.UtcNow : null,
        });
        await db.SaveChangesAsync();
    }
}
