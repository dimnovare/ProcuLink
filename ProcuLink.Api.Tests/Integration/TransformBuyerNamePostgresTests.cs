using System.Text.Json;
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
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Regression for the BuyerName denormalisation split (prod order 14c340a1, 2026-06-13):
/// the async parse path writes <c>purchase_orders.buyer_name</c> (the COLUMN) but leaves
/// <c>CanonicalJson</c> WITHOUT a buyerName. The fixed JSON transform used to read CanonicalJson
/// only, so a correctly-extracted buyer ("REDACTED-PARTY") was delivered to the supplier
/// as <c>"buyerName": ""</c>.
///
/// <para>This drives the REAL <see cref="OrderService.TransformAsync"/> end-to-end on REAL
/// Postgres — the order is persisted (column set, CanonicalJson missing buyerName), reloaded by
/// the service, transformed to JSON, and the EXACT uploaded artifact bytes are captured and
/// parsed. Asserting on the delivered bytes (not just the in-memory entity) is what closes the
/// gap: InMemory round-trips would not catch a jsonb-vs-column divergence. Docker-gated.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class TransformBuyerNamePostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_buyer_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
        {
            Pooling = false,
        }.ConnectionString;

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var migrateDb = new ProcuLinkDbContext(_options);
        await migrateDb.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    private ProcuLinkDbContext NewContext() => new(_options!);

    /// <summary>Records the exact bytes handed to <c>UploadAsync</c> so the delivered artifact can be inspected.</summary>
    private sealed class CapturingFileStorage : IFileStorageService
    {
        public byte[]? LastUploadedBytes { get; private set; }

        public async Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            LastUploadedBytes = buffer.ToArray();
            return key;
        }

        public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) =>
            Task.FromResult($"https://example.test/{key}");

        public Task<Stream> DownloadAsync(string key, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream(LastUploadedBytes ?? Array.Empty<byte>()));

        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }

    private static OrderService Build(ProcuLinkDbContext db, IFileStorageService fileStorage) =>
        new(
            db,
            fileStorage,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser() }),
            new Mock<IItemMappingService>().Object,
            new OrderExceptionService(db),
            new Mock<IPoMappingService>().Object,
            new Mock<IAiMappingService>().Object,
            new ITransformService[] { new JsonTransformService() },
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());

    /// <summary>
    /// Seeds a fully-resolved 'ready' order EXACTLY as the async parse path leaves it: the
    /// <c>buyer_name</c> COLUMN carries the extracted buyer, while CanonicalJson has NO buyerName.
    /// </summary>
    private async Task<(Guid OrgId, Guid OrderId)> SeedSplitBuyerOrderAsync(string buyerName)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id            = orgId,
            ClerkOrgId    = $"org_buyer_{orgId:N}",
            Name          = "Buyer Split Org",
            Slug          = $"buyer-{orgId:N}",
            Plan          = "operations",
            AccountStatus = "active",
            CreatedAt     = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Voest Supplier", CreatedAt = now });
        await db.SaveChangesAsync();

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = "PO-VOEST-1",
            BuyerName  = buyerName, // the column — written by the async parse path
            // CanonicalJson WITHOUT buyerName — the parse path does NOT backfill it (the bug).
            CanonicalJson = JsonDocument.Parse("""{"poNumber":"PO-VOEST-1","currency":"EUR"}"""),
            OrderDate  = new DateOnly(2026, 6, 13),
            Currency   = "EUR",
            Status     = OrderStatusConstants.Ready,
            CreatedAt  = now,
            UpdatedAt  = now,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "B-1", SupplierItemCode = "SUP-1", Description = "Steel coil",
                    Quantity = 2m, Unit = "EA", UnitPrice = 100m, NeedsReview = false, Confidence = 1.0f,
                },
            },
        });
        await db.SaveChangesAsync();

        return (orgId, orderId);
    }

    [DockerRequiredFact]
    public async Task DeliveredJsonArtifact_BuyerName_EqualsExtractedBuyer_NotEmpty()
    {
        const string buyer = "REDACTED-PARTY";
        var (orgId, orderId) = await SeedSplitBuyerOrderAsync(buyer);

        var storage = new CapturingFileStorage();
        await using (var db = NewContext())
        {
            var result = await Build(db, storage).TransformAsync(orgId, orderId, OutputFormat.Json, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error);
            Assert.False(result.Value!.Skipped);
        }

        Assert.NotNull(storage.LastUploadedBytes);
        using var doc = JsonDocument.Parse(storage.LastUploadedBytes!);
        var delivered = doc.RootElement.GetProperty("buyerName").GetString();

        // The buyer the order extracted (the column) — NOT the empty CanonicalJson buyer.
        Assert.Equal(buyer, delivered);
    }
}
