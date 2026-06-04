using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Service-level tests for <see cref="PassportService"/> — exercised directly against an
/// EF InMemory DbContext (no WebApplicationFactory). Verifies org-scoping, that a failed
/// order still produces a passport, and that delivery data is surfaced for delivered orders.
/// </summary>
public class PassportServiceTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static AuditEvent Audit(Guid orgId, Guid orderId, string action, object? payload, DateTime at) =>
        new()
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            EntityType = "Order",
            EntityId   = orderId,
            Action     = action,
            Payload    = payload is null ? null : JsonDocument.Parse(JsonSerializer.Serialize(payload)),
            CreatedAt  = at,
        };

    /// <summary>
    /// Seeds one supplier + one order (with two lines: one resolved deterministically,
    /// one carrying an AI suggestion) and a "Created" + "Parsed" audit trail.
    /// </summary>
    private static async Task<(ProcuLinkDbContext db, Guid orgId, Guid orderId, Guid supplierId)> SeedAsync(
        ProcuLinkDbContext db,
        string status = OrderStatusConstants.Ready,
        string? sourceFileKey = "org/ord/po.csv")
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var t0         = new DateTime(2026, 5, 30, 10, 0, 0, DateTimeKind.Utc);

        db.Suppliers.Add(new Supplier
        {
            Id        = supplierId,
            OrgId     = orgId,
            Name      = "Northwind Trading OÜ",
            CreatedAt = t0,
        });

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id            = orderId,
            OrgId         = orgId,
            SupplierId    = supplierId,
            PoNumber      = "PO-PASSPORT-001",
            OrderDate     = DateOnly.FromDateTime(t0),
            Currency      = "EUR",
            Status        = status,
            SourceFileKey = sourceFileKey,
            CanonicalJson = JsonDocument.Parse("""{"buyerName":"Acme Procurement"}"""),
            CreatedAt     = t0,
            UpdatedAt     = t0.AddMinutes(5),
        });

        db.PurchaseOrderLines.AddRange(
            new PurchaseOrderLineEntity
            {
                Id               = Guid.NewGuid(),
                OrderId          = orderId,
                LineNumber       = 1,
                BuyerItemCode    = "BUY-1",
                SupplierItemCode = "SUP-1",
                Description      = "Widget",
                Quantity         = 10m,
                Unit             = "EA",
                UnitPrice        = 2.50m,
                Confidence       = 1.0f,
                NeedsReview      = false,
            },
            new PurchaseOrderLineEntity
            {
                Id                          = Guid.NewGuid(),
                OrderId                     = orderId,
                LineNumber                  = 2,
                BuyerItemCode               = "BUY-2",
                SupplierItemCode            = null,
                Description                 = "Gadget",
                Quantity                    = 4m,
                Unit                        = "EA",
                UnitPrice                   = 5.00m,
                Confidence                  = 0.0f,
                NeedsReview                 = true,
                AiSuggestedSupplierItemCode = "SUP-2-AI",
                AiSuggestionConfidence      = 0.91f,
                AiSuggestionReason          = "Description match",
                AiSuggestionProvenance      = "existing mappings",
            });

        db.AuditEvents.AddRange(
            Audit(orgId, orderId, "Created", new { sourceFileKey, lineCount = 2 }, t0),
            Audit(orgId, orderId, "Parsed",  new { lineCount = 2, unresolvedCount = 1 }, t0.AddMinutes(1)));

        await db.SaveChangesAsync();
        return (db, orgId, orderId, supplierId);
    }

    // ── Org-scoping ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_SameOrg_ReturnsPassport()
    {
        await using var db = NewDb();
        var (_, orgId, orderId, supplierId) = await SeedAsync(db);
        var svc = new PassportService(db);

        var result = await svc.GetAsync(orgId, orderId, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var p = result.Value!;
        Assert.Equal(orderId, p.Order.OrderId);
        Assert.Equal("PO-PASSPORT-001", p.Order.PoNumber);
        Assert.Equal(supplierId, p.Order.SupplierId);
        Assert.Equal("Northwind Trading OÜ", p.Order.SupplierName);
        Assert.Equal("Acme Procurement", p.Order.BuyerName);
        Assert.Equal("csv", p.SourceArtifact!.DetectedFormat);
        Assert.Equal("org/ord/po.csv", p.SourceArtifact.StorageKey);
        Assert.Equal(2, p.Canonical.LineCount);
        Assert.Equal(45.00m, p.Canonical.TotalValue);   // 10*2.5 + 4*5
        Assert.Equal(14m, p.Canonical.TotalQuantity);   // 10 + 4
    }

    [Fact]
    public async Task GetAsync_DifferentOrg_ReturnsNotFound()
    {
        await using var db = NewDb();
        var (_, _, orderId, _) = await SeedAsync(db);
        var svc = new PassportService(db);

        var otherOrg = Guid.NewGuid();
        var result = await svc.GetAsync(otherOrg, orderId, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Order not found.", result.Error);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task GetAsync_UnknownOrder_ReturnsNotFound()
    {
        await using var db = NewDb();
        var (_, orgId, _, _) = await SeedAsync(db);
        var svc = new PassportService(db);

        var result = await svc.GetAsync(orgId, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Order not found.", result.Error);
    }

    // ── Mapping decisions + AI suggestions ────────────────────────────────────────

    [Fact]
    public async Task GetAsync_SurfacesPerLineMappingDecisionsAndAiSuggestions()
    {
        await using var db = NewDb();
        var (_, orgId, orderId, _) = await SeedAsync(db);
        var svc = new PassportService(db);

        var p = (await svc.GetAsync(orgId, orderId, CancellationToken.None)).Value!;

        Assert.Equal(2, p.MappingDecisions.Count);

        var l1 = p.MappingDecisions.Single(m => m.LineNumber == 1);
        Assert.Equal("BUY-1", l1.BuyerItemCode);
        Assert.Equal("SUP-1", l1.SupplierItemCode);
        Assert.Equal("deterministic", l1.Source);

        var l2 = p.MappingDecisions.Single(m => m.LineNumber == 2);
        Assert.Null(l2.SupplierItemCode);
        Assert.Equal("ai", l2.Source);

        // Only the line with an attached suggestion appears in AiSuggestions.
        var ai = Assert.Single(p.AiSuggestions);
        Assert.Equal(2, ai.LineNumber);
        Assert.Equal("SUP-2-AI", ai.SuggestedSupplierItemCode);
        Assert.Equal(0.91f, ai.Confidence);
        Assert.Equal("pending", ai.Status);
    }

    // ── Failed order still produces a passport ────────────────────────────────────

    [Fact]
    public async Task GetAsync_FailedOrder_StillProducesPassportWithFailureInTimelineAndStatus()
    {
        await using var db = NewDb();
        var (_, orgId, orderId, _) = await SeedAsync(db, status: OrderStatusConstants.Failed);

        // Add the ParseFailed audit event that accompanies a failed parse.
        var tFail = new DateTime(2026, 5, 30, 10, 2, 0, DateTimeKind.Utc);
        db.AuditEvents.Add(Audit(orgId, orderId, "ParseFailed",
            new { error = "Unsupported file format", stage = "parse" }, tFail));
        await db.SaveChangesAsync();

        var svc = new PassportService(db);
        var result = await svc.GetAsync(orgId, orderId, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var p = result.Value!;

        // Final status reflects the failure.
        Assert.Equal(OrderStatusConstants.Failed, p.FinalStatus);
        Assert.Equal(OrderStatusConstants.Failed, p.Order.Status);

        // The failure is present in the timeline (ordered oldest → newest).
        Assert.Contains(p.Timeline, t => t.Action == "ParseFailed");
        var failEvent = p.Timeline.Single(t => t.Action == "ParseFailed");
        Assert.Contains("Unsupported file format", failEvent.Detail);

        // No delivery happened — delivery sections are empty, supplier response unknown/none.
        Assert.Empty(p.DeliveryAttempts);
        Assert.Null(p.OutputArtifact);
        Assert.Null(p.SupplierResponse);
    }

    // ── Delivered order surfaces delivery-attempt data ────────────────────────────

    [Fact]
    public async Task GetAsync_DeliveredOrder_IncludesDeliveryAttemptDataAndOutputArtifact()
    {
        await using var db = NewDb();
        var (_, orgId, orderId, _) = await SeedAsync(db, status: OrderStatusConstants.Delivered);

        var tTransform = new DateTime(2026, 5, 30, 10, 6, 0, DateTimeKind.Utc);
        var tDeliver   = new DateTime(2026, 5, 30, 10, 7, 0, DateTimeKind.Utc);

        var artifactId = Guid.NewGuid();
        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id        = artifactId,
            OrderId   = orderId,
            OrgId     = orgId,
            Format    = "xml",
            FileKey   = $"{orgId}/{orderId}/artifacts/{artifactId}.xml",
            CreatedAt = tTransform,
        });

        // Two attempts: a failed first try then a successful, acknowledged second.
        db.DeliveryAttempts.AddRange(
            new DeliveryAttempt
            {
                Id            = Guid.NewGuid(),
                OrderId       = orderId,
                OrgId         = orgId,
                Channel       = "http",
                Destination   = "https://supplier.example/orders",
                Status        = "failed",
                AttemptNumber = 1,
                AttemptedAt   = tDeliver,
                ResponseCode  = 503,
                ErrorMessage  = "503 Service Unavailable",
            },
            new DeliveryAttempt
            {
                Id             = Guid.NewGuid(),
                OrderId        = orderId,
                OrgId          = orgId,
                Channel        = "http",
                Destination    = "https://supplier.example/orders",
                Status         = "delivered",
                AttemptNumber  = 2,
                AttemptedAt    = tDeliver.AddMinutes(1),
                ResponseCode   = 200,
                AcknowledgedAt = tDeliver.AddMinutes(1),
                ResponseBody   = "OK",
            });

        db.AuditEvents.AddRange(
            Audit(orgId, orderId, "Transformed", new { format = "xml", artifactId }, tTransform),
            Audit(orgId, orderId, "delivered",   new { attempt = 2 }, tDeliver.AddMinutes(1)));

        await db.SaveChangesAsync();

        var svc = new PassportService(db);
        var p = (await svc.GetAsync(orgId, orderId, CancellationToken.None)).Value!;

        // Output artifact referenced (not embedded).
        Assert.NotNull(p.OutputArtifact);
        Assert.Equal(artifactId, p.OutputArtifact!.ArtifactId);
        Assert.Equal("xml", p.OutputArtifact.Format);

        // Delivery attempts surfaced, ordered by attempt time.
        Assert.Equal(2, p.DeliveryAttempts.Count);
        Assert.Equal(1, p.DeliveryAttempts[0].AttemptNumber);
        Assert.Equal("failed", p.DeliveryAttempts[0].Status);
        Assert.Equal(2, p.DeliveryAttempts[1].AttemptNumber);
        Assert.Equal("delivered", p.DeliveryAttempts[1].Status);
        Assert.Equal("https://supplier.example/orders", p.DeliveryAttempts[1].Destination);

        // Supplier response derived as acknowledged from delivered status + ACK timestamp.
        Assert.NotNull(p.SupplierResponse);
        Assert.Equal("acknowledged", p.SupplierResponse!.Outcome);
        Assert.Equal(tDeliver.AddMinutes(1), p.SupplierResponse.AcknowledgedAt);
        Assert.Equal(200, p.SupplierResponse.ResponseCode);

        Assert.Equal(OrderStatusConstants.Delivered, p.FinalStatus);
        Assert.Contains(p.Timeline, t => t.Action == "delivered");
    }

    // ── Supplier rejection surfaced via existing delivery data ────────────────────

    [Fact]
    public async Task GetAsync_RejectedOrder_SurfacesRejectionAsSupplierResponse()
    {
        await using var db = NewDb();
        var (_, orgId, orderId, _) = await SeedAsync(db, status: OrderStatusConstants.RejectedBySupplier);

        var tDeliver = new DateTime(2026, 5, 30, 10, 8, 0, DateTimeKind.Utc);
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id              = Guid.NewGuid(),
            OrderId         = orderId,
            OrgId           = orgId,
            Channel         = "http",
            Destination     = "https://supplier.example/orders",
            Status          = "failed",
            AttemptNumber   = 1,
            AttemptedAt     = tDeliver,
            ResponseCode    = 422,
            RejectionReason = "Item not in supplier catalog",
            ResponseBody    = "{\"error\":\"unknown sku\"}",
        });
        db.AuditEvents.Add(Audit(orgId, orderId, "MarkedRejected",
            new { reason = "Item not in supplier catalog" }, tDeliver.AddMinutes(1)));
        await db.SaveChangesAsync();

        var svc = new PassportService(db);
        var p = (await svc.GetAsync(orgId, orderId, CancellationToken.None)).Value!;

        Assert.NotNull(p.SupplierResponse);
        Assert.Equal("rejected", p.SupplierResponse!.Outcome);
        Assert.Equal("Item not in supplier catalog", p.SupplierResponse.RejectionReason);
        Assert.Equal(422, p.SupplierResponse.ResponseCode);

        // The manual rejection appears as a correction.
        Assert.Contains(p.ManualCorrections, c => c.Action == "MarkedRejected");
    }

    // ── Supplier acceptance profile (delivery config) version surrogate ───────────

    [Fact]
    public async Task GetAsync_WithDeliveryConfig_SurfacesProfileWithLastUpdatedAtAndNullVersion()
    {
        await using var db = NewDb();
        var (_, orgId, orderId, supplierId) = await SeedAsync(db);

        var cfgUpdated = new DateTime(2026, 5, 29, 9, 0, 0, DateTimeKind.Utc);
        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            SupplierId = supplierId,
            Protocol   = "http",
            ConfigJson = "{}",
            CreatedAt  = cfgUpdated.AddDays(-1),
            UpdatedAt  = cfgUpdated,
        });
        await db.SaveChangesAsync();

        var svc = new PassportService(db);
        var p = (await svc.GetAsync(orgId, orderId, CancellationToken.None)).Value!;

        Assert.NotNull(p.SupplierProfile);
        Assert.Equal("http", p.SupplierProfile!.Protocol);
        Assert.Null(p.SupplierProfile.Version);
        Assert.Equal(cfgUpdated, p.SupplierProfile.LastUpdatedAt);
    }

    // ── Order created from parsed payload (no source file) ────────────────────────

    [Fact]
    public async Task GetAsync_OrderWithoutSourceFile_ReturnsNullSourceKeyAndNote()
    {
        await using var db = NewDb();
        var (_, orgId, orderId, _) = await SeedAsync(db, sourceFileKey: null);
        var svc = new PassportService(db);

        var p = (await svc.GetAsync(orgId, orderId, CancellationToken.None)).Value!;

        Assert.Null(p.SourceArtifact!.StorageKey);
        Assert.Null(p.SourceArtifact.DetectedFormat);
        Assert.Contains(p.Notes, n => n.Contains("No stored source file"));
    }

    // ── BuyerName column preferred over CanonicalJson ─────────────────────────────

    /// <summary>
    /// An order where the async parse job has written the buyer name to the
    /// denormalized <c>BuyerName</c> column but <c>CanonicalJson</c> is null
    /// (the file-upload path) must still expose the buyer name in the passport.
    /// </summary>
    [Fact]
    public async Task GetAsync_BuyerNameColumnSet_CanonicalJsonNull_ReturnsBuyerNameFromColumn()
    {
        await using var db = NewDb();

        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var t0         = new DateTime(2026, 6, 4, 10, 0, 0, DateTimeKind.Utc);

        db.Suppliers.Add(new Supplier
        {
            Id        = supplierId,
            OrgId     = orgId,
            Name      = "Baltic Supplies OÜ",
            CreatedAt = t0,
        });

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id            = orderId,
            OrgId         = orgId,
            SupplierId    = supplierId,
            PoNumber      = "PO-BUYERNAME-001",
            OrderDate     = DateOnly.FromDateTime(t0),
            Currency      = "EUR",
            Status        = OrderStatusConstants.Ready,
            SourceFileKey = "org/ord/po.csv",
            // Denormalized column set by ParseStoredFileAsync; CanonicalJson left null.
            BuyerName     = "REDACTED-NAME",
            CanonicalJson = null,
            CreatedAt     = t0,
            UpdatedAt     = t0.AddMinutes(1),
        });

        await db.SaveChangesAsync();

        var svc = new PassportService(db);
        var result = await svc.GetAsync(orgId, orderId, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("REDACTED-NAME", result.Value!.Order.BuyerName);
    }
}
