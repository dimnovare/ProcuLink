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
/// An unexpected failure anywhere in <c>TransformAsync</c> must be VISIBLE.
///
/// <para>Before this fix, <c>TransformAsync</c> had no exception handling at all between its first
/// line and the acceptance gate's try at <c>:455</c>, and none again between the artifact generation
/// handler at <c>:647</c> and the end of the method. Anything thrown in either region unwound
/// through <c>TransformOrderJob</c> into Hangfire, which retried and then permanently failed the
/// job, leaving the order at <c>transforming</c>. <c>StuckOrderDetectionService</c> then recovered
/// that strand to <c>ready</c> — deliberately, and explicitly never marking it failed, because its
/// premise was that a job which actually RAN and failed had already written its own status.</para>
///
/// <para>The combined effect was that a real, repeatable error produced no <c>transform_failed</c>
/// status, no error message, no ops-health count, and no exception row. It looked like nothing had
/// happened. These tests fail on the pre-fix code.</para>
/// </summary>
public sealed class TransformStrandNeverSilentTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>A cXML credential resolver that throws — a real seam on the pre-claim path
    /// (<c>OrderTransformService.cs:154</c>), reached only when the effective format is cXML.</summary>
    private sealed class ThrowingCxmlResolver : ICxmlCredentialResolver
    {
        private readonly Exception _toThrow;
        public ThrowingCxmlResolver(Exception toThrow) => _toThrow = toThrow;
        public Task<CxmlCredentialConfig?> ResolveAsync(Guid organisationId, Guid supplierId, CancellationToken ct)
            => throw _toThrow;
    }

    /// <summary>
    /// Throws from <c>SaveChangesAsync</c> on the ONE call that is persisting an Added
    /// <see cref="OutboundArtifact"/> — which happens only on the FINAL commit inside
    /// <c>TransformCoreAsync</c> (artifact + <c>ready_to_deliver</c> status + the "Transformed" audit
    /// event, all in the one <c>SaveChangesAsync</c> at <c>OrderTransformService.cs:819</c>).
    ///
    /// <para>This is the discriminator, not a call counter, because a counter would also have to
    /// account for <see cref="SeedAsync"/>'s own <c>SaveChangesAsync</c> — made through this SAME
    /// context before <c>TransformAsync</c> is even invoked — and for the InMemory claim commit inside
    /// <c>TransformCoreAsync</c> itself. Neither of those, nor anything any collaborator (mapping,
    /// exception service) might save, ever has an <see cref="OutboundArtifact"/> on the tracker: the
    /// ONLY <c>_db.OutboundArtifacts.Add(...)</c> in the whole call graph reached from
    /// <c>OrderService.TransformAsync</c> is the one immediately before the final commit
    /// (<c>OrderTransformService.cs:807</c>). Checking for it identifies the final commit exactly,
    /// regardless of how many other calls happen first.</para>
    /// </summary>
    private sealed class ThrowsOnFinalArtifactCommitDbContext : ProcuLinkDbContext
    {
        private readonly Exception _toThrow;

        public ThrowsOnFinalArtifactCommitDbContext(DbContextOptions<ProcuLinkDbContext> options, Exception toThrow)
            : base(options) => _toThrow = toThrow;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var committingTheArtifact = ChangeTracker.Entries<OutboundArtifact>()
                .Any(e => e.State == EntityState.Added);

            return committingTheArtifact
                ? throw _toThrow
                : base.SaveChangesAsync(cancellationToken);
        }
    }

    private static ThrowsOnFinalArtifactCommitDbContext NewDbThrowingOnFinalCommit(Exception toThrow) =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options, toThrow);

    /// <summary>
    /// OrderService wired for cXML, with an injectable cXML resolver and an injectable upload
    /// behaviour. <paramref name="uploadThrows"/> covers the far side of the method (the R2 call at
    /// <c>OrderTransformService.cs:664</c>), which was unguarded for the same reason.
    /// </summary>
    private static OrderService Build(
        ProcuLinkDbContext db,
        ICxmlCredentialResolver? cxmlResolver = null,
        Exception? uploadThrows = null,
        bool registerTransformers = true)
    {
        var fileStorage = new Mock<IFileStorageService>();
        var upload = fileStorage.Setup(s => s.UploadAsync(
            It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()));

        if (uploadThrows is not null) upload.ThrowsAsync(uploadThrows);
        else                          upload.ReturnsAsync("artifact-key");

        var transformers = registerTransformers
            ? new ITransformService[] { new CxmlTransformService(), new XmlTransformService() }
            : Array.Empty<ITransformService>();

        return new OrderService(
            db,
            fileStorage.Object,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser() }),
            new Mock<IItemMappingService>().Object,
            new OrderExceptionService(db),
            new PoMappingService(db),
            new Mock<IAiMappingService>().Object,
            transformers,
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService(),
            cxmlResolver: cxmlResolver);
    }

    private static async Task<(Guid OrgId, Guid SupplierId, Guid OrderId)> SeedAsync(
        ProcuLinkDbContext db, string status = OrderStatusConstants.Transforming, bool resolved = true)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Supplier", CreatedAt = DateTime.UtcNow });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "PO-STRAND-1", BuyerName = "Buyer", OrderDate = new DateOnly(2026, 8, 7),
            Currency = "EUR", Status = status, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "B-1", SupplierItemCode = "SUP-1", Description = "Widget",
                    Quantity = 3m, Unit = "EA", UnitPrice = 10m,
                    NeedsReview = !resolved, Confidence = 1.0f,
                },
            },
        });
        await db.SaveChangesAsync();
        return (orgId, supplierId, orderId);
    }

    private static Task<List<AuditEvent>> TransformFailedEventsAsync(ProcuLinkDbContext db, Guid orderId) =>
        db.AuditEvents.AsNoTracking()
            .Where(a => a.EntityId == orderId && a.Action == "TransformFailed")
            .ToListAsync();

    private static Task<string> StatusOfAsync(ProcuLinkDbContext db, Guid orderId) =>
        db.PurchaseOrders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.Status).FirstAsync();

    // ── 1. A pre-claim throw is recorded, not swallowed ───────────────────────

    [Fact]
    public async Task APreClaimThrow_isRecordedAsTransformFailed_notLeftInTransforming()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db);
        var svc = Build(db, cxmlResolver: new ThrowingCxmlResolver(new InvalidOperationException("resolver exploded")));

        var result = await svc.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.CXml, CancellationToken.None);

        // EVIDENCE FIRST. The audit row is what OrdersController reads to populate errorMessage and
        // what OrderExceptionService reconciles into the operator-workable exception. Asserting the
        // status string ahead of it would let a mutation that writes the status but drops the trail
        // pass — and a mutation run reports only the FIRST failure, so an assertion behind a passing
        // one is never reached.
        var events = await TransformFailedEventsAsync(db, seed.OrderId);
        Assert.Single(events);

        var error = events[0].Payload.RootElement.GetProperty("error").GetString();
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.DoesNotContain("resolver exploded", error);   // raw exception text never reaches the operator

        Assert.Equal(OrderStatusConstants.TransformFailed, await StatusOfAsync(db, seed.OrderId));
        Assert.False(result.IsSuccess);
    }

    // ── 2. The guard: a row we could not have claimed is not touched ──────────

    [Fact]
    public async Task AThrowOnAnOrderThatIsNotClaimable_leavesTheStatusAlone()
    {
        await using var db = NewDb();
        // ready_to_deliver is NOT in OrderStatusMachine.ClaimableForTransformFrom: this order has a
        // completed transform and possibly an in-flight delivery. Failing it here would overwrite a
        // good result with a false failure.
        var seed = await SeedAsync(db, status: OrderStatusConstants.ReadyToDeliver);
        var svc = Build(db, cxmlResolver: new ThrowingCxmlResolver(new InvalidOperationException("resolver exploded")));

        var result = await svc.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.CXml, CancellationToken.None);

        Assert.Empty(await TransformFailedEventsAsync(db, seed.OrderId));
        Assert.Equal(OrderStatusConstants.ReadyToDeliver, await StatusOfAsync(db, seed.OrderId));
        Assert.False(result.IsSuccess);
    }

    // ── 3. Cancellation is not a failure ──────────────────────────────────────

    [Fact]
    public async Task ACancelledTransform_propagates_andIsNotRecordedAsAFailure()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db);
        var svc = Build(db, cxmlResolver: new ThrowingCxmlResolver(new OperationCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.CXml, CancellationToken.None));

        Assert.Empty(await TransformFailedEventsAsync(db, seed.OrderId));
        Assert.Equal(OrderStatusConstants.Transforming, await StatusOfAsync(db, seed.OrderId));
    }

    // ── 4. The far side of the method: the artifact upload ────────────────────

    [Fact]
    public async Task AFailedArtifactUpload_isRecordedAsTransformFailed_notLeftInTransforming()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db);
        // The R2 call at OrderTransformService.cs:664 sat outside every try for the same reason the
        // pre-claim region did, and a storage blip is the likelier of the two in production.
        var svc = Build(db, uploadThrows: new IOException("R2 unavailable"));

        var result = await svc.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.CXml, CancellationToken.None);

        var events = await TransformFailedEventsAsync(db, seed.OrderId);
        Assert.Single(events);
        Assert.DoesNotContain("R2 unavailable", events[0].Payload.RootElement.GetProperty("error").GetString());

        Assert.Equal(OrderStatusConstants.TransformFailed, await StatusOfAsync(db, seed.OrderId));
        Assert.False(result.IsSuccess);

        // No OutboundArtifact assertion here: the upload throws at OrderTransformService.cs:736-737,
        // well before the artifact row is even constructed (:794) or Added (:807), so "no artifact
        // exists" would hold whether or not the guarded write worked — a tautology. That check is only
        // meaningful where the row was genuinely pending when the failure hit; see
        // AThrowFromTheFinalCommit_isRecordedAsTransformFailed_noOrphanedArtifact below.
    }

    // ── 5. The line no other test reaches: the FINAL SaveChanges itself ──────

    /// <summary>
    /// Finding 1 (task-1-review.md): every test above throws BEFORE the artifact is ever added to the
    /// tracker, so none of them exercise <c>_db.ChangeTracker.Clear()</c> at
    /// <c>OrderTransformService.cs:150</c> — delete that line and all five original tests still pass.
    /// This test throws from the FINAL <c>SaveChangesAsync</c> instead
    /// (<c>OrderTransformService.cs:819</c>), the one moment the tracker holds an Added
    /// <c>OutboundArtifact</c> and a modified <c>Status = ready_to_deliver</c> together — exactly the
    /// state <c>Clear()</c> exists to discard before <c>FailTransformFromClaimableAsync</c> writes
    /// <c>transform_failed</c>.
    /// </summary>
    [Fact]
    public async Task AThrowFromTheFinalCommit_isRecordedAsTransformFailed_noOrphanedArtifact()
    {
        await using var db = NewDbThrowingOnFinalCommit(new InvalidOperationException("final commit exploded"));
        var seed = await SeedAsync(db);
        var svc = Build(db);   // nothing else throws — only the final SaveChangesAsync does, via the DbContext above

        var result = await svc.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.CXml, CancellationToken.None);

        // Same ordering rule as test 1: the audit row is asserted before the status, because a mutation
        // run reports only the first failing assertion, and a status check ahead of it could hide a
        // mutation that writes the status but drops the trail.
        var events = await TransformFailedEventsAsync(db, seed.OrderId);
        Assert.Single(events);

        Assert.Equal(OrderStatusConstants.TransformFailed, await StatusOfAsync(db, seed.OrderId));
        Assert.False(result.IsSuccess);

        // Unlike test 4's version of this check, the artifact row genuinely WAS pending (Added, inside
        // the very commit that failed) at the moment the failure hit — so finding it empty here actually
        // proves the abandoned Add was discarded, not silently re-committed later by the guarded write.
        Assert.Empty(await db.OutboundArtifacts.AsNoTracking().Where(a => a.OrderId == seed.OrderId).ToListAsync());
    }

    // ── 6. Negative control ───────────────────────────────────────────────────

    /// <summary>
    /// Identical fixture, identical code path; the ONLY difference is that nothing throws. Without
    /// this, "the guard records real failures" and "something now fails every transform" are
    /// indistinguishable — which would make every assertion above worthless.
    /// </summary>
    [Fact]
    public async Task NegativeControl_theSameOrderTransformsCleanlyWhenNothingThrows()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db);
        var svc = Build(db);   // ← the one difference: no throwing resolver, no throwing upload

        var result = await svc.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.CXml, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(await TransformFailedEventsAsync(db, seed.OrderId));
        Assert.Equal(OrderStatusConstants.ReadyToDeliver, await StatusOfAsync(db, seed.OrderId));

        // The positive contrast the failure tests' Assert.Empty(OutboundArtifacts...) needs: without
        // this, "the guard records real failures" and "something now fails every transform" would both
        // leave the same empty-artifacts result, and the control would not actually distinguish them.
        Assert.Single(await db.OutboundArtifacts.AsNoTracking().Where(a => a.OrderId == seed.OrderId).ToListAsync());
    }
}
