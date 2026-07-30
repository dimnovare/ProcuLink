using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// WP-17 — the transform itself REFUSES a blocked order.
///
/// <para>This is the test that fails on <c>origin/main</c>. Before this work package
/// <c>ValidateOrderAsync</c> had exactly two production callers and both were HTTP controllers, so
/// the supplier's error-severity rules were enforced in the browser and nowhere else: the transform
/// never asked. An order the profile UI said would be blocked transformed, produced an artifact, and
/// was delivered.</para>
///
/// <para>The end-to-end proof across all four ingress channels needs real Postgres (the controller's
/// claim uses <c>ExecuteUpdateAsync</c>) and lives in <c>AcceptanceGateEntryPathsPostgresTests</c>.
/// These run the SAME door — <c>OrderService.TransformAsync</c>, which is what
/// <c>TransformOrderJob</c> calls — on the InMemory provider, so the refusal is covered even where
/// Docker is not available.</para>
/// </summary>
public sealed class AcceptanceGateBlocksTransformTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // ── The refusal ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AnOrderTheSupplierRefuses_doesNotTransform()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, currency: "USD", severity: "error");
        var (svc, uploads) = Build(db);

        var result = await svc.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.Csv, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Currency must be EUR", result.Error!);
        Assert.Contains("Set currency to EUR", result.Error!);

        // Nothing was produced, and nothing was uploaded — a refused order must not leave an
        // artifact behind that a later delivery sweep could pick up.
        Assert.Equal(0, uploads());
        Assert.Empty(await db.OutboundArtifacts.Where(a => a.OrderId == seed.OrderId).ToListAsync());

        // Visible, not silent: transform_failed feeds ops health, opens the exception row, and is
        // what the workshop reads to show the operator the reason.
        var status = await db.PurchaseOrders.AsNoTracking()
            .Where(o => o.Id == seed.OrderId).Select(o => o.Status).FirstAsync();
        Assert.Equal(OrderStatusConstants.TransformFailed, status);
    }

    /// <summary>
    /// NEGATIVE CONTROL. Same fixture, same rule, same code path; the ONLY difference is the
    /// currency the rule judges. The assertion is on the DIFFERENCE between the two runs, so this
    /// fails both if the gate stops enforcing AND if something unrelated starts refusing everything
    /// — which is the failure mode that would make every other assertion in this file worthless.
    /// </summary>
    [Fact]
    public async Task NegativeControl_theSameOrderSendsWhenItSatisfiesTheRule()
    {
        await using var blockedDb = NewDb();
        var blockedSeed = await SeedAsync(blockedDb, currency: "USD", severity: "error");
        var (blockedSvc, blockedUploads) = Build(blockedDb);
        var blocked = await blockedSvc.TransformAsync(blockedSeed.OrgId, blockedSeed.OrderId, OutputFormat.Csv, CancellationToken.None);

        await using var allowedDb = NewDb();
        var allowedSeed = await SeedAsync(allowedDb, currency: "EUR", severity: "error");   // ← the one difference
        var (allowedSvc, allowedUploads) = Build(allowedDb);
        var allowed = await allowedSvc.TransformAsync(allowedSeed.OrgId, allowedSeed.OrderId, OutputFormat.Csv, CancellationToken.None);

        Assert.NotEqual(blocked.IsSuccess, allowed.IsSuccess);

        Assert.False(blocked.IsSuccess);
        Assert.Equal(0, blockedUploads());

        Assert.True(allowed.IsSuccess, allowed.Error);
        Assert.Equal(1, allowedUploads());
        var status = await allowedDb.PurchaseOrders.AsNoTracking()
            .Where(o => o.Id == allowedSeed.OrderId).Select(o => o.Status).FirstAsync();
        Assert.Equal(OrderStatusConstants.ReadyToDeliver, status);
    }

    /// <summary>
    /// SECOND NEGATIVE CONTROL, on the other axis: identical non-conforming order and identical
    /// failing rule, differing only in SEVERITY. Warnings are advice. Without this, "the gate blocks"
    /// and "any validation finding blocks" are indistinguishable.
    /// </summary>
    [Fact]
    public async Task NegativeControl_aWarningRuleDoesNotStopTheTransform()
    {
        await using var errorDb = NewDb();
        var errorSeed = await SeedAsync(errorDb, currency: "USD", severity: "error");
        var (errorSvc, _) = Build(errorDb);
        var blocked = await errorSvc.TransformAsync(errorSeed.OrgId, errorSeed.OrderId, OutputFormat.Csv, CancellationToken.None);

        await using var warnDb = NewDb();
        var warnSeed = await SeedAsync(warnDb, currency: "USD", severity: "warning");       // ← the one difference
        var (warnSvc, _) = Build(warnDb);
        var allowed = await warnSvc.TransformAsync(warnSeed.OrgId, warnSeed.OrderId, OutputFormat.Csv, CancellationToken.None);

        Assert.NotEqual(blocked.IsSuccess, allowed.IsSuccess);
        Assert.False(blocked.IsSuccess);
        Assert.True(allowed.IsSuccess, allowed.Error);
    }

    /// <summary>
    /// THIRD NEGATIVE CONTROL: an order with no acceptance profile at all is the overwhelming
    /// majority of live traffic. It must be completely unaffected — this feature adds enforcement,
    /// not friction.
    ///
    /// <para>Like the two above, this asserts a DIFFERENCE rather than a success. Asserting only
    /// "the no-profile order transformed" made it not a control at all: deleting the gate outright
    /// would leave it green, so it could never have detected the thing it was named after. Both runs
    /// use the identical non-conforming order and the identical rule text; the ONE difference is
    /// whether that rule is bound to the supplier.</para>
    /// </summary>
    [Fact]
    public async Task NegativeControl_theProfileIsTheOnlyDifference()
    {
        await using var withDb = NewDb();
        var withSeed = await SeedAsync(withDb, currency: "USD", severity: "error", withProfile: true);
        var (withSvc, withUploads) = Build(withDb);
        var gated = await withSvc.TransformAsync(withSeed.OrgId, withSeed.OrderId, OutputFormat.Csv, CancellationToken.None);

        await using var withoutDb = NewDb();
        var withoutSeed = await SeedAsync(withoutDb, currency: "USD", severity: "error", withProfile: false); // ← the one difference
        var (withoutSvc, withoutUploads) = Build(withoutDb);
        var ungated = await withoutSvc.TransformAsync(withoutSeed.OrgId, withoutSeed.OrderId, OutputFormat.Csv, CancellationToken.None);

        Assert.NotEqual(gated.IsSuccess, ungated.IsSuccess);

        Assert.False(gated.IsSuccess);
        Assert.Equal(0, withUploads());

        Assert.True(ungated.IsSuccess, ungated.Error);
        Assert.Equal(1, withoutUploads());
        var status = await withoutDb.PurchaseOrders.AsNoTracking()
            .Where(o => o.Id == withoutSeed.OrderId).Select(o => o.Status).FirstAsync();
        Assert.Equal(OrderStatusConstants.ReadyToDeliver, status);
    }

    // ── The gate itself failing ───────────────────────────────────────────────

    /// <summary>
    /// A THROW inside the gate must not strand the order. The gate call runs AFTER the order has
    /// been claimed into <c>transforming</c> and OUTSIDE the try/catch that wraps document
    /// generation, so a profile-lookup failure (a DB blip, a malformed pin) unwound straight through
    /// the Hangfire job and left the row sitting in <c>transforming</c> — no artifact, no
    /// <c>transform_failed</c>, no sentence for the operator, and no sweep that looks for it.
    ///
    /// <para>The decision is REFUSE, visibly: a document we could not check against the supplier's
    /// rules is not one to send. It lands in <c>transform_failed</c> exactly like every other
    /// terminal failure — counted in ops health, carrying its own plain sentence, and re-claimable —
    /// so <see cref="AnOrderStrandedByAGateFailure_transformsOnceTheGateRecovers"/> proves the
    /// refusal is not a dead end either.</para>
    /// </summary>
    [Fact]
    public async Task WhenTheGateThrows_theOrderFailsVisibly_ratherThanStrandingInTransforming()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, currency: "EUR", severity: "error");   // the rule PASSES; only the gate breaks
        var (svc, uploads) = Build(db, gate: ThrowingGate());

        var result = await svc.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.Csv, CancellationToken.None);

        Assert.False(result.IsSuccess);

        var status = await db.PurchaseOrders.AsNoTracking()
            .Where(o => o.Id == seed.OrderId).Select(o => o.Status).FirstAsync();
        Assert.NotEqual(OrderStatusConstants.Transforming, status);
        Assert.Equal(OrderStatusConstants.TransformFailed, status);

        // Nothing was generated: refusing on an unreadable answer must not also leave an artifact
        // behind that a delivery sweep could pick up.
        Assert.Equal(0, uploads());
        Assert.Empty(await db.OutboundArtifacts.Where(a => a.OrderId == seed.OrderId).ToListAsync());

        // The operator gets a sentence, not a stack trace.
        var failure = await db.AuditEvents.AsNoTracking()
            .SingleAsync(a => a.OrgId == seed.OrgId && a.EntityId == seed.OrderId && a.Action == "TransformFailed");
        var error = failure.Payload!.RootElement.GetProperty("error").GetString()!;
        Assert.Contains("couldn't be checked", error);
        Assert.DoesNotContain("Exception", error);
    }

    /// <summary>The other half of "neither outcome is stuck": once the gate answers again, the same
    /// order sends. <c>transform_failed</c> is re-claimable, so the Hangfire retry that follows a
    /// transient lookup failure simply works.</summary>
    [Fact]
    public async Task AnOrderStrandedByAGateFailure_transformsOnceTheGateRecovers()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, currency: "EUR", severity: "error");

        var (broken, _) = Build(db, gate: ThrowingGate());
        Assert.False((await broken.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.Csv, CancellationToken.None)).IsSuccess);

        var (healthy, uploads) = Build(db);
        var result = await healthy.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(1, uploads());
    }

    private static IAcceptanceGate ThrowingGate()
    {
        var gate = new Mock<IAcceptanceGate>();
        gate.Setup(g => g.EvaluateAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("acceptance profile lookup failed"));
        return gate.Object;
    }

    // ── Audit ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheRefusal_isRecordedInTheAuditTrail()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, currency: "USD", severity: "error");
        var (svc, _) = Build(db);

        await svc.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.Csv, CancellationToken.None);

        var blockedEvent = await db.AuditEvents.AsNoTracking()
            .SingleAsync(a => a.OrgId == seed.OrgId
                           && a.EntityId == seed.OrderId
                           && a.Action == AcceptanceGateAudit.BlockedAction);

        var blockers = blockedEvent.Payload!.RootElement.GetProperty("blockers");
        Assert.Equal("currency.equals", blockers[0].GetProperty("code").GetString());
        Assert.Contains("currency must be EUR", blockers[0].GetProperty("message").GetString()!);

        // The failure the workshop shows the user carries the same sentence.
        var failure = await db.AuditEvents.AsNoTracking()
            .SingleAsync(a => a.OrgId == seed.OrgId && a.EntityId == seed.OrderId && a.Action == "TransformFailed");
        Assert.Contains("Currency must be EUR", failure.Payload!.RootElement.GetProperty("error").GetString()!);
    }

    // ── Override ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithARecordedOverride_theSameOrderTransforms_andTheUseIsAudited()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, currency: "USD", severity: "error");
        var (svc, uploads) = Build(db);

        Assert.False((await svc.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.Csv, CancellationToken.None)).IsSuccess);

        var recorded = await new AcceptanceGate(db, new SupplierAcceptanceService(db))
            .RecordOverrideAsync(seed.OrgId, seed.OrderId, "user_2opsLead", "Supplier accepts USD on this PO.", CancellationToken.None);
        Assert.True(recorded.Recorded, recorded.Error);

        var result = await svc.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(1, uploads());

        var used = await db.AuditEvents.AsNoTracking()
            .SingleAsync(a => a.OrgId == seed.OrgId
                           && a.EntityId == seed.OrderId
                           && a.Action == AcceptanceGateAudit.OverrideUsedAction);
        Assert.Equal("user_2opsLead", used.Payload!.RootElement.GetProperty("by").GetString());
        Assert.Equal("Supplier accepts USD on this PO.", used.Payload.RootElement.GetProperty("reason").GetString());
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private sealed record Seed(Guid OrgId, Guid OrderId);

    /// <summary>
    /// OrderService with the real CSV transformer and an upload counter. <paramref name="gate"/>
    /// null means "let OrderService build the real gate", which is what production does.
    /// </summary>
    private static (OrderService Svc, Func<int> Uploads) Build(
        ProcuLinkDbContext db, IAcceptanceGate? gate = null)
    {
        var uploads = 0;
        var fileStorage = new Mock<IFileStorageService>();
        fileStorage
            .Setup(s => s.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => uploads++)
            .ReturnsAsync("artifact-key");

        var svc = new OrderService(
            db,
            fileStorage.Object,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser() }),
            new Mock<IItemMappingService>().Object,
            new OrderExceptionService(db),
            new Mock<IPoMappingService>().Object,
            new Mock<IAiMappingService>().Object,
            new ITransformService[] { new CsvTransformService() },
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService(),
            acceptanceGate: gate);

        return (svc, () => uploads);
    }

    /// <summary>
    /// A fully-resolved <c>ready</c> order that passes every invariant, so the acceptance rule is
    /// the only thing that can refuse it.
    /// </summary>
    private static async Task<Seed> SeedAsync(
        ProcuLinkDbContext db, string currency, string severity, bool withProfile = true)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_{orgId:N}", Name = "Gate Org", Slug = $"gate-{orgId:N}", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme GmbH", CreatedAt = now });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "PO-GATE-1", BuyerName = "Buyer Ltd", Currency = currency,
            OrderDate = new DateOnly(2026, 7, 30), Status = OrderStatusConstants.Ready,
            CreatedAt = now, UpdatedAt = now,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "B-1", SupplierItemCode = "SUP-1", Description = "Widget",
                    Quantity = 3m, Unit = "EA", UnitPrice = 10m, NeedsReview = false, Confidence = 1.0f,
                },
            },
        });

        if (withProfile)
        {
            db.SupplierAcceptanceProfiles.Add(new SupplierAcceptanceProfile
            {
                Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
                VersionNo = 1, Status = "active", CreatedAt = now, EffectiveFrom = now,
                Rules =
                {
                    new SupplierAcceptanceRule
                    {
                        Id = Guid.NewGuid(), Scope = "order", FieldPath = "currency", Operator = "equals",
                        ExpectedValue = "EUR", Severity = severity, BlockOnFail = false,
                    },
                },
            });
        }

        await db.SaveChangesAsync();
        return new Seed(orgId, orderId);
    }
}
