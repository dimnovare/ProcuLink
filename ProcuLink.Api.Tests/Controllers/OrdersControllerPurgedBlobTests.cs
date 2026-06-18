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

    // ── T1b: persisted SourceCapture is preferred over re-tokenising the live file ──────────────

    [Fact]
    public async Task GetSourceTokens_PrefersPersistedSourceCapture_ForPdfRawFields_WithoutTouchingStorage()
    {
        // A PDF order: the rendered file can't be re-tokenised (would return empty), so the only way
        // to expose its extracted fields is the persisted SourceCapture (LLM raw_fields). T1b serves
        // it — and must NOT hit storage at all (strict mock throws on any call).
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using var db = NewDb();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = Guid.NewGuid(),
            PoNumber = "PO-PDF", OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency = "EUR", Status = "delivered",
            SourceFileKey = $"{orgId}/{orderId}/scan.pdf",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SourceCaptures.Add(new SourceCapture
        {
            Id = Guid.NewGuid(), OrderId = orderId, OrgId = orgId, Format = "pdf", CapturedAt = DateTime.UtcNow,
            TokensJson = System.Text.Json.JsonDocument.Parse(
                """[{"label":"Buyer name","value":"Acme"},{"label":"PO ref","value":"123"}]"""),
        });
        await db.SaveChangesAsync();

        var fileStorage = new Mock<IFileStorageService>(MockBehavior.Strict); // ANY storage call would throw
        var ctrl = Build(new Mock<IOrderService>(), orgId, db, fileStorage);

        var result = await ctrl.GetSourceTokens(orderId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var tokens = Assert.IsAssignableFrom<IEnumerable<ProcuLink.Transform.Tokenizing.SourceToken>>(ok.Value).ToList();
        Assert.Equal(2, tokens.Count);
        Assert.Contains(tokens, t => t.Id == "raw:Buyer name" && t.Value == "Acme");
        Assert.Contains(tokens, t => t.Id == "raw:PO ref" && t.Value == "123");
    }

    // ── Workshop P0 (Task 4): persisted structured capture survives a PURGED source blob ──

    [Fact]
    public async Task GetSourceTokens_PrefersPersistedStructuredCapture_AfterSourceBlobPurged_WithoutTouchingStorage()
    {
        // The Task-4 scenario: a CSV order whose source FILE was purged per retention policy, but
        // whose persisted SourceCapture retains the full structured token set. Without the
        // capture-first precedence the endpoint would re-tokenise the (gone) blob and return empty.
        // It must instead return every persisted token — and must NOT hit storage (strict mock
        // throws on any call).
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using var db = NewDb();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = Guid.NewGuid(),
            PoNumber = "PO-CSV-PURGED", OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency = "EUR", Status = "delivered",
            SourceFileKey = $"{orgId}/{orderId}/order.csv",
            SourceFilePurgedAt = DateTime.UtcNow, // blob gone — re-tokenisation impossible
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SourceCaptures.Add(new SourceCapture
        {
            Id = Guid.NewGuid(), OrderId = orderId, OrgId = orgId, Format = "csv", CapturedAt = DateTime.UtcNow,
            // The full structured token bag (id/label/value/group) the ingest path persists.
            TokensJson = System.Text.Json.JsonDocument.Parse(
                "[{\"id\":\"cell:r1c1\",\"label\":\"po_number\",\"value\":\"po_number\",\"group\":\"header\"}," +
                "{\"id\":\"cell:r2c1\",\"label\":\"po_number row 2\",\"value\":\"PO-CSV-PURGED\",\"group\":\"line\"}," +
                "{\"id\":\"cell:r2c2\",\"label\":\"buyer_code row 2\",\"value\":\"ACM-BOLT-001\",\"group\":\"line\"}]"),
        });
        await db.SaveChangesAsync();

        var fileStorage = new Mock<IFileStorageService>(MockBehavior.Strict); // any storage call would throw
        var ctrl = Build(new Mock<IOrderService>(), orgId, db, fileStorage);

        var result = await ctrl.GetSourceTokens(orderId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var tokens = Assert.IsAssignableFrom<IEnumerable<ProcuLink.Transform.Tokenizing.SourceToken>>(ok.Value).ToList();
        Assert.Equal(3, tokens.Count);
        // The full structured token set — including the stable cell ids — survives the purge.
        Assert.Contains(tokens, t => t.Id == "cell:r2c2" && t.Value == "ACM-BOLT-001");
        Assert.Contains(tokens, t => t.Id == "cell:r2c1" && t.Value == "PO-CSV-PURGED");
    }
}
