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
/// line and the acceptance gate's <c>try</c> around <c>_acceptanceGate.EvaluateAsync</c>, and none
/// again between the artifact-generation <c>try</c> (the one whose catches match
/// <c>TransformTemplateException</c>/<c>TransformValidationException</c>) and the end of the method.
/// Anything thrown in either region unwound through <c>TransformOrderJob</c> into Hangfire, which
/// retried and then permanently failed the job, leaving the order at <c>transforming</c>.
/// <c>StuckOrderDetectionService</c> then recovered that strand to <c>ready</c> — deliberately, and
/// explicitly never marking it failed, because its premise was that a job which actually RAN and
/// failed had already written its own status.</para>
///
/// <para>The combined effect was that a real, repeatable error produced no <c>transform_failed</c>
/// status, no error message, no ops-health count, and no exception row. It looked like nothing had
/// happened.</para>
///
/// <para><b>What proves what — because "these tests fail on the pre-fix code" is not true of all of
/// them, and a blanket claim hides which ones carry the weight.</b></para>
/// <list type="bullet">
///   <item><b>Red-then-green against the pre-fix code</b> (they were run against it and failed):
///     <c>APreClaimThrow_…</c>, <c>AThrowOnAnOrderThatIsNotClaimable_…</c> and
///     <c>AFailedArtifactUpload_…</c>. These are the proof that the strand existed and is
///     closed.</item>
///   <item><b>Controls and regression pins, green before and after — by design:</b>
///     <c>NegativeControl_…</c> (identical fixture, nothing throws; without it "the guard records
///     real failures" and "something now fails every transform" would be indistinguishable) and
///     <c>ACancelledTransform_…</c> (pre-fix nothing caught cancellation either — it pins the
///     <c>catch (OperationCanceledException) { throw; }</c> clause, and deleting that clause does
///     turn it red).</item>
///   <item><b>Added after the fix landed, and verified by MUTATION rather than by a pre-fix run:</b>
///     <c>AThrowFromTheFinalCommit_…</c> (covers <c>ChangeTracker.Clear()</c>, which no earlier test
///     reached), <c>NoRegisteredTransformer_…</c> and <c>UnresolvedLines_…</c> (the two
///     <c>Result.Fail</c> returns, which strand an order exactly as a throw did),
///     <c>ATransformFailureOnARejectedOrder_…</c> (the guard set is narrower than the claim set) and
///     <c>ATransientFailureThatLaterSucceeds_…</c> (the success path closes the exception row it
///     opened). Each names the mutation that kills it.</item>
/// </list>
/// </summary>
public sealed class TransformStrandNeverSilentTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>A cXML credential resolver that throws — a real seam on the pre-claim path
    /// (<c>OrderTransformService</c>'s <c>_cxmlResolver.ResolveAsync</c> call, which sits well above
    /// the atomic claim), reached only when the effective format is cXML.</summary>
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
    /// event, all in <c>TransformCoreAsync</c>'s FINAL <c>SaveChangesAsync</c>).
    ///
    /// <para>This is the discriminator, not a call counter, because a counter would also have to
    /// account for <see cref="SeedAsync"/>'s own <c>SaveChangesAsync</c> — made through this SAME
    /// context before <c>TransformAsync</c> is even invoked — and for the InMemory claim commit inside
    /// <c>TransformCoreAsync</c> itself. Neither of those, nor anything any collaborator (mapping,
    /// exception service) might save, ever has an <see cref="OutboundArtifact"/> on the tracker: the
    /// ONLY <c>_db.OutboundArtifacts.Add(...)</c> in the whole call graph reached from
    /// <c>OrderService.TransformAsync</c> is the one immediately before that final commit. Checking
    /// for it identifies the final commit exactly, regardless of how many other calls happen
    /// first.</para>
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
    /// The same discriminator as <see cref="ThrowsOnFinalArtifactCommitDbContext"/>, but ARMED ONCE:
    /// the first artifact commit throws and every later one is allowed through. That is the
    /// TRANSIENT-fault shape — a storage or DB blip that Hangfire's +10s retry cures — and it is the
    /// only way to drive one order through a failure and then a success on a single context.
    /// </summary>
    private sealed class ThrowsOnceOnFinalArtifactCommitDbContext : ProcuLinkDbContext
    {
        private readonly Exception _toThrow;
        private bool _armed = true;

        public ThrowsOnceOnFinalArtifactCommitDbContext(DbContextOptions<ProcuLinkDbContext> options, Exception toThrow)
            : base(options) => _toThrow = toThrow;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (_armed && ChangeTracker.Entries<OutboundArtifact>().Any(e => e.State == EntityState.Added))
            {
                _armed = false;
                throw _toThrow;
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }

    private static ThrowsOnceOnFinalArtifactCommitDbContext NewDbThrowingOnceOnFinalCommit(Exception toThrow) =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options, toThrow);

    /// <summary>
    /// OrderService wired for cXML, with an injectable cXML resolver and an injectable upload
    /// behaviour. <paramref name="uploadThrows"/> covers the far side of the method (the R2 call —
    /// <c>_fileStorage.UploadAsync</c> in <c>TransformCoreAsync</c>), which was unguarded for the
    /// same reason.
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

    /// <summary>
    /// The OPEN <c>transform_failed</c> exception rows for an order. Open-set rather than absence on
    /// purpose: <c>OrderExceptionService.ReconcileAsync</c> CLEARS a row by flipping
    /// <c>State</c> to <c>"resolved"</c> and stamping <c>ResolvedAt</c> — it never deletes it, and a
    /// resolved row is deliberately never resurrected. Asserting "no rows at all" would therefore
    /// assert the wrong model and fail even on correct behaviour.
    /// </summary>
    private static Task<List<OrderException>> OpenTransformFailedExceptionsAsync(
        ProcuLinkDbContext db, Guid orgId, Guid orderId) =>
        db.OrderExceptions.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.OrderId == orderId
                     && e.Code == "transform_failed" && e.State == "open")
            .ToListAsync();

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

    // ── 2. The guard: a row we may not fail is not touched ────────────────────

    [Fact]
    public async Task AThrowOnAnOrderThatIsNotClaimable_leavesTheStatusAlone()
    {
        await using var db = NewDb();
        // ready_to_deliver is NOT in OrderStatusMachine.TransformFailableFrom: this order has a
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
        // The R2 call (_fileStorage.UploadAsync in TransformCoreAsync) sat outside every try for the
        // same reason the pre-claim region did, and a storage blip is the likelier of the two in
        // production.
        var svc = Build(db, uploadThrows: new IOException("R2 unavailable"));

        var result = await svc.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.CXml, CancellationToken.None);

        var events = await TransformFailedEventsAsync(db, seed.OrderId);
        Assert.Single(events);
        Assert.DoesNotContain("R2 unavailable", events[0].Payload.RootElement.GetProperty("error").GetString());

        Assert.Equal(OrderStatusConstants.TransformFailed, await StatusOfAsync(db, seed.OrderId));
        Assert.False(result.IsSuccess);

        // No OutboundArtifact assertion here: _fileStorage.UploadAsync throws well before the
        // artifact row is even constructed (`new OutboundArtifact { … }`) or Added
        // (`_db.OutboundArtifacts.Add`), so "no artifact exists" would hold whether or not the guarded
        // write worked — a tautology. That check is only meaningful where the row was genuinely
        // pending when the failure hit; see
        // AThrowFromTheFinalCommit_isRecordedAsTransformFailed_noOrphanedArtifact below.
    }

    // ── 5. The line no other test reaches: the FINAL SaveChanges itself ──────

    /// <summary>
    /// Finding 1 (task-1-review.md): every test above throws BEFORE the artifact is ever added to the
    /// tracker, so none of them exercise the <c>_db.ChangeTracker.Clear()</c> in
    /// <c>TransformAsync</c>'s catch — delete that line and all five original tests still pass.
    /// This test throws from <c>TransformCoreAsync</c>'s FINAL <c>SaveChangesAsync</c> instead, the
    /// one moment the tracker holds an Added
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

    // ── 7. A Fail return is as invisible as a throw ───────────────────────────

    /// <summary>
    /// TransformOrderJob turns a Fail into a throw (its <c>if (!result.IsSuccess) throw new
    /// InvalidOperationException($"Transform failed: {result.Error}")</c>), so a Fail that writes no
    /// status strands the order in exactly the same way an unhandled exception did.
    /// </summary>
    [Fact]
    public async Task NoRegisteredTransformer_isRecordedAsTransformFailed_notASilentFail()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db);
        var svc = Build(db, registerTransformers: false);

        var result = await svc.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.CXml, CancellationToken.None);

        var events = await TransformFailedEventsAsync(db, seed.OrderId);
        Assert.Single(events);
        Assert.Contains("No transform service registered",
            events[0].Payload.RootElement.GetProperty("error").GetString());

        Assert.Equal(OrderStatusConstants.TransformFailed, await StatusOfAsync(db, seed.OrderId));
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task UnresolvedLines_areRecordedAsTransformFailed_notASilentFail()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, resolved: false);
        var svc = Build(db);

        var result = await svc.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.CXml, CancellationToken.None);

        var events = await TransformFailedEventsAsync(db, seed.OrderId);
        Assert.Single(events);

        // The existing sentence is already written for a user and names the exact lines, so it is
        // passed through unaltered rather than replaced with the generic one.
        Assert.Equal("Resolve all lines before transforming. Unresolved: 1.",
            events[0].Payload.RootElement.GetProperty("error").GetString());

        Assert.Equal(OrderStatusConstants.TransformFailed, await StatusOfAsync(db, seed.OrderId));
        Assert.False(result.IsSuccess);
    }

    // ── 8. An operator's verdict outranks a machine failure ───────────────────

    /// <summary>
    /// The guard set is NARROWER than the claim set, by exactly <c>rejected_by_supplier</c>.
    ///
    /// <para>The interleaving this pins: a transform is in flight (the order is
    /// <c>transforming</c>); an operator records a supplier rejection, because the supplier told
    /// them out of band that the PO is refused; the in-flight transform then throws. Guarding the
    /// failure write on <c>ClaimableForTransformFrom</c> — which admits <c>rejected_by_supplier</c>,
    /// so that a CORRECTED document can be produced — would stamp <c>transform_failed</c> over that
    /// verdict, replacing a human's finding with "something went wrong preparing this order to
    /// send". The verdict is load-bearing: it is the one status no delivery claim set admits, and it
    /// feeds the supplier acceptance-rate figures.</para>
    ///
    /// <para><b>Mutation that kills this test:</b> guard
    /// <c>OrderTransformService.FailTransformFromClaimableAsync</c> on
    /// <c>OrderStatusMachine.ClaimableForTransformFrom</c> again (both the relational and the
    /// InMemory branch) — the status is overwritten and the audit row appears.</para>
    /// </summary>
    [Fact]
    public async Task ATransformFailureOnARejectedOrder_leavesTheOperatorsVerdictStanding()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, status: OrderStatusConstants.RejectedBySupplier);
        var svc = Build(db, cxmlResolver: new ThrowingCxmlResolver(new InvalidOperationException("resolver exploded")));

        var result = await svc.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.CXml, CancellationToken.None);

        // EVIDENCE FIRST, same rule as test 1: a mutation run reports only the first failing
        // assertion, so the trail is asserted ahead of the status it explains.
        Assert.Empty(await TransformFailedEventsAsync(db, seed.OrderId));
        Assert.Equal(OrderStatusConstants.RejectedBySupplier, await StatusOfAsync(db, seed.OrderId));

        // Still a failure to the caller — refusing to OVERWRITE the status is not the same as
        // pretending the transform worked.
        Assert.False(result.IsSuccess);
    }

    // ── 9. A failure that self-heals closes its own exception row ─────────────

    /// <summary>
    /// A transient fault opens a <c>transform_failed</c> exception row; the retry succeeds; the row
    /// must not stay open on a now-healthy order.
    ///
    /// <para>Why it used to: the only production callers of <c>ReconcileAsync</c> sat on the
    /// delivery side, and the documented escape ("a successful re-transform enqueues delivery, and
    /// DeliveryService reconciles on every successful attempt") holds only when a delivery attempt
    /// is actually PERSISTED. With AutoDeliver off the automatic dispatch claim matches 0 rows,
    /// writes no attempt and reconciles nothing — and the exceptions UI derives
    /// <c>transform_failed</c> from status, so no operator could clear it by hand either. The
    /// wrapper made that routine rather than rare: a DB or storage blip now opens a row and then
    /// self-heals on the +10s Hangfire retry.</para>
    ///
    /// <para><b>Mutation that kills this test:</b> delete the
    /// <c>SafeReconcileExceptionsAsync</c> call on <c>TransformCoreAsync</c>'s success path — the
    /// row is still open after the successful re-transform.</para>
    /// </summary>
    [Fact]
    public async Task ATransientFailureThatLaterSucceeds_leavesNoOpenTransformFailedException()
    {
        await using var db = NewDbThrowingOnceOnFinalCommit(new InvalidOperationException("storage blip"));
        var seed = await SeedAsync(db);
        var svc = Build(db);

        var firstAttempt = await svc.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.CXml, CancellationToken.None);
        Assert.False(firstAttempt.IsSuccess);

        // NON-VACUITY: the row must genuinely be open at this point, or the final assertion proves
        // nothing at all. transform_failed is also the status here, so the exception really is derived.
        Assert.Single(await OpenTransformFailedExceptionsAsync(db, seed.OrgId, seed.OrderId));
        Assert.Equal(OrderStatusConstants.TransformFailed, await StatusOfAsync(db, seed.OrderId));

        // The Hangfire retry, in effect: transform_failed is re-claimable, and nothing throws now.
        var retry = await svc.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.CXml, CancellationToken.None);
        Assert.True(retry.IsSuccess, retry.Error);
        Assert.Equal(OrderStatusConstants.ReadyToDeliver, await StatusOfAsync(db, seed.OrderId));

        Assert.Empty(await OpenTransformFailedExceptionsAsync(db, seed.OrgId, seed.OrderId));
    }
}
