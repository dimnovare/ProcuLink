using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Storage;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// WP-34 "prove what was sent". An operator facing a supplier's "we never got it" has to be
/// able to (a) retrieve the exact bytes that were dispatched and (b) check them against the
/// fingerprint the system recorded at dispatch time. Both halves have to be TRUE, not merely
/// present:
/// <list type="bullet">
///   <item><description>the bytes the download path serves must HASH to the recorded
///   <c>ArtifactSha256</c> — asserting a hash merely exists proves nothing;</description></item>
///   <item><description>the download path must stay org-scoped — it serves customer
///   documents.</description></item>
/// </list>
/// The passport half (which artifact each attempt dispatched) retired with the passport endpoint
/// in plan 13b.
/// </summary>
public class ProveWhatWasSentTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// The real OrderService wired to a REAL <see cref="LocalFileStorageService"/> — blobs make an
    /// honest round trip through the filesystem, so a wrong key, a truncated upload or a hash taken
    /// over different bytes all fail the round-trip assertion. A storage mock that echoes whatever
    /// it was handed could not fail it.
    /// </summary>
    private static OrderService BuildOrderService(ProcuLinkDbContext db, IFileStorageService storage) =>
        new(
            db,
            storage,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser() }),
            new Mock<IItemMappingService>().Object,
            new OrderExceptionService(db),
            new Mock<IPoMappingService>().Object,
            new Mock<IAiMappingService>().Object,
            new ITransformService[] { new CsvTransformService(), new JsonTransformService() },
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());

    private static async Task<(Guid OrgId, Guid OrderId)> SeedResolvedOrderAsync(ProcuLinkDbContext db)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Nordmark", CreatedAt = DateTime.UtcNow });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = "PO-SENT-1",
            BuyerName  = "Acme",
            OrderDate  = new DateOnly(2026, 7, 30),
            Currency   = "EUR",
            Status     = "ready",
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

    /// <summary>
    /// Resolves the local dev "pre-signed" URL back to the storage key it points at. The URL —
    /// not the artifact row — is the thing under test: if the download path signed the WRONG key
    /// the bytes fetched here are the wrong bytes and the hash comparison fails.
    /// </summary>
    private static string KeyFromLocalSignedUrl(string url)
    {
        const string marker = "/api/dev/files/";
        var idx = url.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(idx >= 0, $"Unexpected signed-URL shape: {url}");
        return url[(idx + marker.Length)..];
    }

    // ── The proof: the served bytes HASH TO the recorded fingerprint ─────────────

    [Fact]
    public async Task DownloadedBytes_HashTo_TheRecordedArtifactFingerprint()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db);

        var storage = new LocalFileStorageService();
        var orders  = BuildOrderService(db, storage);

        // Real transform → real upload → real artifact row carrying the recorded fingerprint.
        var transform = await orders.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
        Assert.True(transform.IsSuccess, transform.Error);

        var artifact = await db.OutboundArtifacts.AsNoTracking().SingleAsync(a => a.OrderId == orderId);
        Assert.NotNull(artifact.ArtifactSha256);

        try
        {
            // Ask the download path — exactly what the UI button calls — for the URL.
            var query = new OrderQueryService(db, storage);
            var download = await query.GetDownloadUrlAsync(orgId, orderId, artifact.Id, CancellationToken.None);
            Assert.True(download.IsSuccess, download.Error);

            // Fetch the bytes the URL actually points at (the dev passthrough serves this key
            // verbatim; R2 serves the same object for a signed URL).
            var servedKey = KeyFromLocalSignedUrl(download.Value!.Url);
            await using var served = await storage.DownloadAsync(servedKey, CancellationToken.None);
            using var buffer = new MemoryStream();
            await served.CopyToAsync(buffer);
            var servedBytes = buffer.ToArray();

            Assert.NotEmpty(servedBytes);

            // THE assertion: hash what was served and compare to what was recorded. Not
            // "a hash exists" — the served bytes must reproduce the recorded fingerprint.
            var servedSha = Convert.ToHexString(SHA256.HashData(servedBytes)).ToLowerInvariant();
            Assert.Equal(artifact.ArtifactSha256, servedSha);
        }
        finally
        {
            await storage.DeleteAsync(artifact.FileKey, CancellationToken.None);
        }
    }

    [Fact]
    public async Task DownloadedBytes_FailTheHashCheck_WhenTheStoredBlobIsTamperedWith()
    {
        // Guards against a vacuous pass: if the round trip above could not FAIL, it proves nothing.
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db);

        var storage = new LocalFileStorageService();
        var orders  = BuildOrderService(db, storage);

        var transform = await orders.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
        Assert.True(transform.IsSuccess, transform.Error);
        var artifact = await db.OutboundArtifacts.AsNoTracking().SingleAsync(a => a.OrderId == orderId);

        // PRECONDITION, not decoration. Without it this test passes vacuously: Assert.NotEqual
        // succeeds for (null, "9f86d0…"), so a recorded hash of null satisfies the corruption
        // assertion below just as well as a correct one. Null is REACHABLE — provenance capture
        // in OrderTransformService sits inside a swallow-all try/catch and leaves ArtifactSha256
        // null on any failure — so a regression that stopped recording hashes entirely would
        // turn this guard-against-vacuity into the vacuum it exists to rule out.
        Assert.NotNull(artifact.ArtifactSha256);

        try
        {
            // Corrupt the stored blob behind the row's back.
            await storage.UploadAsync(
                new MemoryStream("tampered,bytes\r\n"u8.ToArray()), artifact.FileKey, "text/csv", CancellationToken.None);

            var query = new OrderQueryService(db, storage);
            var download = await query.GetDownloadUrlAsync(orgId, orderId, artifact.Id, CancellationToken.None);
            Assert.True(download.IsSuccess, download.Error);

            await using var served = await storage.DownloadAsync(
                KeyFromLocalSignedUrl(download.Value!.Url), CancellationToken.None);
            using var buffer = new MemoryStream();
            await served.CopyToAsync(buffer);

            var servedSha = Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
            Assert.NotEqual(artifact.ArtifactSha256, servedSha);
        }
        finally
        {
            await storage.DeleteAsync(artifact.FileKey, CancellationToken.None);
        }
    }

    // ── Org scope: the download path serves customer documents ──────────────────

    [Fact]
    public async Task Download_IsOrgScoped_ACrossTenantRequestGetsNothingAndNeverTouchesStorage()
    {
        var ownerOrgId  = Guid.NewGuid();
        var attackerOrg = Guid.NewGuid();
        var orderId     = Guid.NewGuid();
        var artifactId  = Guid.NewGuid();

        await using var db = NewDb();
        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id = artifactId, OrderId = orderId, OrgId = ownerOrgId, Format = "csv",
            FileKey = "owner/order/artifacts/a.csv", CreatedAt = DateTime.UtcNow,
            ArtifactSha256 = new string('a', 64),
        });
        await db.SaveChangesAsync();

        // Strict: ANY storage call throws — a cross-tenant request must not even reach signing.
        var storage = new Mock<IFileStorageService>(MockBehavior.Strict);
        var query   = new OrderQueryService(db, storage.Object);

        var stolen = await query.GetDownloadUrlAsync(attackerOrg, orderId, artifactId, CancellationToken.None);

        Assert.False(stolen.IsSuccess);
        Assert.Null(stolen.Value);
        storage.VerifyNoOtherCalls();
    }
}
