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
/// A TERMINAL transform failure must be VISIBLE.
///
/// <para><b>The bug these pin.</b> <c>transform_failed</c> was a DEAD status: both terminal
/// failure paths in <c>OrderTransformService</c> (broken template → <c>TransformTemplateException</c>,
/// unusable mapping → <c>TransformValidationException</c>) reverted the order to <c>ready</c>, which is
/// indistinguishable from "never transformed". Everything downstream keyed on the status was therefore
/// structurally dead: <c>OpsHealthService.TransformFailed</c> could only ever count 0, so it contributed
/// 0 to <c>TotalProblemOrders</c> and /operations/health rendered a green "All clear" while POs were
/// stranded; <c>OrderExceptionService</c> never emitted its <c>transform_failed</c> row; the
/// <c>"TransformFailed"</c> audit action <c>OrdersController</c> reads for <c>errorMessage</c> was never
/// written. A broken supplier template silently stranded EVERY order for that supplier, invisibly.</para>
///
/// <para><b>The recovery half.</b> Making the status real is only safe if the order can get OUT of it:
/// these also pin that a <c>transform_failed</c> order is re-claimable, so fixing the template and
/// re-transforming succeeds. Without that the fix would trade a silent strand for a permanent one.</para>
/// </summary>
public class TransformFailureVisibilityTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// A transformer that always throws <see cref="TransformValidationException"/> — the OTHER terminal
    /// failure path (a configured output mapping that cannot be applied). Driving it through a stub keeps
    /// the test about the FAILURE HANDLING, not about which mapping shape happens to be unusable today.
    /// </summary>
    private sealed class ValidationFailingTransformService : ITransformService
    {
        public const string Message = "The published output mapping for this connection could not be applied.";

        public bool CanTransform(OutputFormat format) => format == OutputFormat.Csv;

        public Task<TransformResult> TransformAsync(
            PurchaseOrderEntity order, OutputFormat format, CancellationToken ct,
            CxmlCredentialConfig? cxmlCredentials = null)
            => throw new TransformValidationException(Message);
    }

    private static OrderService Build(ProcuLinkDbContext db, ITransformService? transformer = null)
    {
        var fileStorage = new Mock<IFileStorageService>();
        fileStorage
            .Setup(s => s.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("artifact-key");

        return new OrderService(
            db,
            fileStorage.Object,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser() }),
            new Mock<IItemMappingService>().Object,
            new OrderExceptionService(db),
            new Mock<IPoMappingService>().Object,
            new Mock<IAiMappingService>().Object,
            new[] { transformer ?? new CsvTransformService() },
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());
    }

    private static async Task<(Guid orgId, Guid orderId)> SeedResolvedOrderAsync(
        ProcuLinkDbContext db, string status = OrderStatusConstants.Ready)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme Supplier", CreatedAt = DateTime.UtcNow });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = "PO-TF-1",
            BuyerName  = "Acme Buyer",
            OrderDate  = new DateOnly(2026, 6, 1),
            Currency   = "EUR",
            Status     = status,
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
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

        await db.SaveChangesAsync();
        return (orgId, orderId);
    }

    /// <summary>Stores a whole-document Scriban template that cannot compile → TransformTemplateException.</summary>
    private static async Task StoreBrokenTemplateAsync(ProcuLinkDbContext db, Guid orgId, Guid orderId)
    {
        var stored = await new OrderMappingOverrideService(db).UpsertAsync(
            orgId, orderId, new OrderMappingOverride { OutputTemplate = "{{ for }}" }, CancellationToken.None);
        Assert.True(stored);
    }

    /// <summary>
    /// Replaces the broken template with one that renders — the user's "I fixed it" edit.
    /// <c>PoNumber</c> is a top-level global of the ScribanOrderModel namespace.
    /// </summary>
    private static async Task StoreWorkingTemplateAsync(ProcuLinkDbContext db, Guid orgId, Guid orderId)
    {
        var stored = await new OrderMappingOverrideService(db).UpsertAsync(
            orgId, orderId,
            new OrderMappingOverride { OutputTemplate = "PO {{ PoNumber }}", OutputTemplateContentType = "text/plain" },
            CancellationToken.None);
        Assert.True(stored);
    }

    // ── The automatic pipeline: a terminal failure becomes VISIBLE ─────────────

    [Fact]
    public async Task TransformAsync_BrokenTemplate_EndsInTransformFailed_NotReady()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db);
        await StoreBrokenTemplateAsync(db, orgId, orderId);

        var result = await Build(db).TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        // The synchronous caller's contract is unchanged: the error still comes back.
        Assert.False(result.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatusConstants.TransformFailed, order.Status);
        Assert.Equal(0, await db.OutboundArtifacts.CountAsync(a => a.OrderId == orderId));
    }

    [Fact]
    public async Task TransformAsync_ValidationFailure_EndsInTransformFailed_LikeATemplateFailure()
    {
        // The second terminal path: a CONFIGURED output mapping that cannot be applied. Both are
        // failures a Hangfire retry cannot fix, so both must land in the same visible status.
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db);

        var result = await Build(db, new ValidationFailingTransformService())
            .TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ValidationFailingTransformService.Message, result.Error);

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatusConstants.TransformFailed, order.Status);
        Assert.Equal(0, await db.OutboundArtifacts.CountAsync(a => a.OrderId == orderId));
    }

    [Fact]
    public async Task TransformAsync_BrokenTemplate_WritesTransformFailedAuditEvent_CarryingTheError()
    {
        // OrdersController reads the LATEST "TransformFailed" audit event's `error` payload key to
        // populate the order's errorMessage — the FE workshop shows exactly this string. The action
        // was read but never written.
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db);
        await StoreBrokenTemplateAsync(db, orgId, orderId);

        var result = await Build(db).TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
        Assert.False(result.IsSuccess);

        var audit = await db.AuditEvents.SingleAsync(
            e => e.EntityId == orderId && e.OrgId == orgId && e.Action == "TransformFailed");

        Assert.Equal("Order", audit.EntityType);
        Assert.True(audit.Payload!.RootElement.TryGetProperty("error", out var error));
        Assert.False(string.IsNullOrWhiteSpace(error.GetString()));
    }

    [Fact]
    public async Task TransformAsync_BrokenTemplate_OpensTheTransformFailedException()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db);
        await StoreBrokenTemplateAsync(db, orgId, orderId);

        var result = await Build(db).TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
        Assert.False(result.IsSuccess);

        var ex = await db.OrderExceptions.SingleAsync(e => e.OrderId == orderId && e.OrgId == orgId);
        Assert.Equal("transform_failed", ex.Code);
        Assert.Equal("Transform", ex.Stage);
        Assert.Equal("error", ex.Severity);
        Assert.Equal("open", ex.State);
    }

    [Fact]
    public async Task TransformAsync_BrokenTemplate_IsCountedByOpsHealth_AndBreaksAllClear()
    {
        // The headline consequence: the /operations/health tile read zero (and TotalProblemOrders
        // summed a structural 0) while transforms failed → a green "All clear" over stranded POs.
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db);
        await StoreBrokenTemplateAsync(db, orgId, orderId);

        var before = await new OpsHealthService(db).GetHealthAsync(orgId, CancellationToken.None);
        Assert.Equal(0, before.TransformFailed);
        Assert.Equal(0, before.TotalProblemOrders); // all clear — correctly, nothing has failed yet

        var result = await Build(db).TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
        Assert.False(result.IsSuccess);

        var after = await new OpsHealthService(db).GetHealthAsync(orgId, CancellationToken.None);
        Assert.Equal(1, after.TransformFailed);
        // transform_failed is a GENUINE fault (unlike delivery_held, a deliberate self-releasing
        // pause that is excluded on purpose), so it must raise the problem total that gates All-clear.
        Assert.Equal(1, after.TotalProblemOrders);
    }

    // ── Recovery: a transform_failed order must never be STUCK ─────────────────

    [Fact]
    public async Task TransformAsync_TransformFailedOrder_IsReclaimable_AndRecoversAfterTheTemplateIsFixed()
    {
        // The full user loop: the automatic transform fails on a broken template, the user fixes the
        // template, and the re-transform succeeds. If the transform claim did not accept
        // transform_failed the order would be stuck forever — a WORSE strand than the one being fixed.
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db);
        await StoreBrokenTemplateAsync(db, orgId, orderId);

        var failed = await Build(db).TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
        Assert.False(failed.IsSuccess);
        Assert.Equal(
            OrderStatusConstants.TransformFailed,
            (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status);

        // The user fixes the template. A transform_failed order has NO artifact, so the MV-1
        // "mapping edit invalidates the artifact" reset does not apply — it stays transform_failed,
        // which is exactly why the claim itself must accept the status.
        await StoreWorkingTemplateAsync(db, orgId, orderId);
        Assert.Equal(
            OrderStatusConstants.TransformFailed,
            (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status);

        var recovered = await Build(db).TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(recovered.IsSuccess, recovered.Error);
        Assert.False(recovered.Value!.Skipped);
        Assert.NotEqual(Guid.Empty, recovered.Value.ArtifactId);

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatusConstants.ReadyToDeliver, order.Status);
        Assert.Equal(1, await db.OutboundArtifacts.CountAsync(a => a.OrderId == orderId));
    }

    [Fact]
    public async Task TransformAsync_TransformFailedOrder_ThatStillFails_StaysTransformFailed()
    {
        // A Hangfire retry of a still-broken order re-claims and fails again — it must not decay into
        // a benign 'Skipped' no-op that reports success while the order is broken.
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db, status: OrderStatusConstants.TransformFailed);
        await StoreBrokenTemplateAsync(db, orgId, orderId);

        var result = await Build(db).TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.False(result.IsSuccess);
        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatusConstants.TransformFailed, order.Status);
    }
}
