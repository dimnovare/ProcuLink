using Hangfire;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Downstream honesty for retention-purged blobs: a purged file is a deliberate,
/// explainable state — endpoints must answer with a clear policy message (410 for the
/// artifact download, a graceful empty result for source tokens), NEVER a 500 and never
/// a signed URL pointing at a deleted object.
/// </summary>
public class OrdersControllerPurgedBlobTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static OrdersController Build(
        Mock<IOrderService> ordersSvc,
        Guid orgId,
        ProcuLinkDbContext db,
        Mock<IFileStorageService>? fileStorage = null)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        return new OrdersController(
            ordersSvc.Object,
            tenant.Object,
            new Mock<IBackgroundJobClient>().Object,
            db,
            NullLogger<OrdersController>.Instance,
            new Mock<IBillingService>().Object,
            new Mock<IIdempotencyService>().Object,
            new Mock<IOrderExceptionService>().Object,
            new Mock<ISupplierAcceptanceService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IOrderMappingOverrideService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IPromoteMappingService>().Object,
            (fileStorage ?? new Mock<IFileStorageService>()).Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            Array.Empty<ITransformService>());
    }

    // ── GET /api/orders/{id}/artifacts/{artifactId}/download ─────────────────

    [Fact]
    public async Task Download_PurgedArtifact_Returns410WithPolicyMessage_Not500()
    {
        var orgId = Guid.NewGuid();
        var ordersSvc = new Mock<IOrderService>();
        ordersSvc
            .Setup(s => s.GetDownloadUrlAsync(orgId, It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<DownloadUrl>.Fail(RetentionConstants.BlobPurgedError));

        await using var db = NewDb();
        var ctrl = Build(ordersSvc, orgId, db);

        var result = await ctrl.Download(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        var gone = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status410Gone, gone.StatusCode);
        Assert.Contains("purged per", gone.Value!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retention", gone.Value!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Download_UnknownArtifact_StillReturns404()
    {
        var orgId = Guid.NewGuid();
        var ordersSvc = new Mock<IOrderService>();
        ordersSvc
            .Setup(s => s.GetDownloadUrlAsync(orgId, It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<DownloadUrl>.Fail("Artifact not found."));

        await using var db = NewDb();
        var ctrl = Build(ordersSvc, orgId, db);

        var result = await ctrl.Download(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    // ── OrderQueryService — the layer that detects the purge ─────────────────

    [Fact]
    public async Task GetDownloadUrl_PurgedArtifactRow_ReturnsTheExactPolicyMarker()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var purgedId   = Guid.NewGuid();
        var healthyId  = Guid.NewGuid();

        await using var db = NewDb();
        db.OutboundArtifacts.AddRange(
            new OutboundArtifact
            {
                Id = purgedId, OrderId = orderId, OrgId = orgId, Format = "csv",
                FileKey = "k/purged.csv", CreatedAt = DateTime.UtcNow,
                ArtifactSha256 = "sha-survives", BlobPurgedAt = DateTime.UtcNow, // blob gone, row + hash stay
            },
            new OutboundArtifact
            {
                Id = healthyId, OrderId = orderId, OrgId = orgId, Format = "csv",
                FileKey = "k/healthy.csv", CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var storage = new Mock<IFileStorageService>();
        storage.Setup(s => s.GetSignedDownloadUrlAsync("k/healthy.csv", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://signed.example/healthy");
        var query = new OrderQueryService(db, storage.Object);

        // Purged → the exact marker error (the controller maps it to 410), and NO signed URL.
        var purged = await query.GetDownloadUrlAsync(orgId, orderId, purgedId, CancellationToken.None);
        Assert.False(purged.IsSuccess);
        Assert.Equal(RetentionConstants.BlobPurgedError, purged.Error);
        storage.Verify(s => s.GetSignedDownloadUrlAsync("k/purged.csv", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);

        // A sibling unpurged artifact still downloads normally.
        var healthy = await query.GetDownloadUrlAsync(orgId, orderId, healthyId, CancellationToken.None);
        Assert.True(healthy.IsSuccess);
        Assert.Equal("https://signed.example/healthy", healthy.Value!.Url);
    }

    // ── GET /api/orders/{id}/source-tokens ───────────────────────────────────

    [Fact]
    public async Task GetSourceTokens_PurgedSourceBlob_ReturnsEmptyList_WithoutTouchingStorage()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using var db = NewDb();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = Guid.NewGuid(),
            PoNumber = "PO-PURGED", OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency = "EUR", Status = "delivered",
            SourceFileKey = $"{orgId}/{orderId}/file.csv",
            SourceFilePurgedAt = DateTime.UtcNow, // purged per retention policy
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var fileStorage = new Mock<IFileStorageService>(MockBehavior.Strict); // ANY storage call would throw
        var ctrl = Build(new Mock<IOrderService>(), orgId, db, fileStorage);

        var result = await ctrl.GetSourceTokens(orderId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var tokens = Assert.IsAssignableFrom<IEnumerable<ProcuLink.Transform.Tokenizing.SourceToken>>(ok.Value);
        Assert.Empty(tokens);
    }
}
