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
///   <item><description>the passport must say WHICH artifact each attempt dispatched (an order
///   can hold several of each), and must say nothing when no dispatch evidence proves the
///   link;</description></item>
///   <item><description>the download path must stay org-scoped — it serves customer
///   documents.</description></item>
/// </list>
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

    // ── The passport surfaces the fingerprint on the artifact ───────────────────

    [Fact]
    public async Task Passport_SurfacesTheFingerprint_OnTheOutputArtifact()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db);

        var artifactId = Guid.NewGuid();
        var sha        = new string('b', 64);
        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id = artifactId, OrderId = orderId, OrgId = orgId, Format = "csv",
            FileKey = $"{orgId}/{orderId}/artifacts/{artifactId}.csv", CreatedAt = DateTime.UtcNow,
            ArtifactSha256 = sha,
        });
        await db.SaveChangesAsync();

        var passport = await new PassportService(db).GetAsync(orgId, orderId, CancellationToken.None);

        Assert.True(passport.IsSuccess, passport.Error);
        Assert.Equal(artifactId, passport.Value!.OutputArtifact!.ArtifactId);
        Assert.Equal(sha, passport.Value.OutputArtifact.ArtifactSha256);
    }

    // ── The passport says WHICH artifact each attempt dispatched ────────────────

    [Fact]
    public async Task Passport_LinksEachAttempt_ToTheArtifactItDispatched_AndCarriesTheSentFingerprint()
    {
        // Two artifacts, two attempts — the shape the UI must not flatten into "the" artifact.
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db);

        var firstArtifact  = Guid.NewGuid();
        var secondArtifact = Guid.NewGuid();
        var firstSha       = new string('1', 64);
        var secondSha      = new string('2', 64);
        var now            = DateTime.UtcNow;

        db.OutboundArtifacts.AddRange(
            new OutboundArtifact
            {
                Id = firstArtifact, OrderId = orderId, OrgId = orgId, Format = "csv",
                FileKey = $"{orgId}/{orderId}/artifacts/{firstArtifact}.csv",
                CreatedAt = now.AddMinutes(-10), ArtifactSha256 = firstSha,
            },
            new OutboundArtifact
            {
                Id = secondArtifact, OrderId = orderId, OrgId = orgId, Format = "csv",
                FileKey = $"{orgId}/{orderId}/artifacts/{secondArtifact}.csv",
                CreatedAt = now, ArtifactSha256 = secondSha,
            });

        // The dispatch evidence that ties an attempt to its artifact: the deterministic
        // idempotency key, written verbatim in the on-the-wire format (independent of the
        // helper under test — the symmetry assertion below pins the two together).
        db.DeliveryAttempts.AddRange(
            new DeliveryAttempt
            {
                Id = Guid.NewGuid(), OrderId = orderId, OrgId = orgId, AttemptNumber = 1,
                Channel = "http", Destination = "https://nordmark.example/po",
                Status = DeliveryAttempt.StatusFailed, AttemptedAt = now.AddMinutes(-9),
                IdempotencyKey = $"plk-dlv-{orderId:N}-{firstArtifact:N}",
                ArtifactSha256 = firstSha,
            },
            new DeliveryAttempt
            {
                Id = Guid.NewGuid(), OrderId = orderId, OrgId = orgId, AttemptNumber = 2,
                Channel = "http", Destination = "https://nordmark.example/po",
                Status = DeliveryAttempt.StatusSuccess, AttemptedAt = now.AddMinutes(-1),
                IdempotencyKey = $"plk-dlv-{orderId:N}-{secondArtifact:N}",
                ArtifactSha256 = secondSha,
            });
        await db.SaveChangesAsync();

        var passport = await new PassportService(db).GetAsync(orgId, orderId, CancellationToken.None);
        Assert.True(passport.IsSuccess, passport.Error);

        var attempts = passport.Value!.DeliveryAttempts.OrderBy(a => a.AttemptNumber).ToList();
        Assert.Equal(2, attempts.Count);

        // Each attempt points at the artifact IT sent — not at "the latest".
        Assert.Equal(firstArtifact,  attempts[0].ArtifactId);
        Assert.Equal(firstSha,       attempts[0].ArtifactSha256);
        Assert.Equal(secondArtifact, attempts[1].ArtifactId);
        Assert.Equal(secondSha,      attempts[1].ArtifactSha256);
        Assert.NotEqual(attempts[0].ArtifactId, attempts[1].ArtifactId);
    }

    [Fact]
    public void TheIdempotencyKey_BuiltByDelivery_ParsesBackToTheSameArtifactId()
    {
        // Build↔parse symmetry against the literal wire format used above, so the passport's
        // attempt→artifact link cannot silently drift from what DeliveryService actually writes.
        var orderId    = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        var built = DeliveryService.BuildIdempotencyKey(orderId, artifactId);
        Assert.Equal($"plk-dlv-{orderId:N}-{artifactId:N}", built);

        Assert.True(DeliveryIdempotencyKey.TryParseArtifactId(built, out var parsed));
        Assert.Equal(artifactId, parsed);
    }

    [Fact]
    public async Task Passport_LeavesTheArtifactLinkNull_WhenNoDispatchEvidenceProvesIt()
    {
        // A legacy / test-fire attempt carries no idempotency key and no dispatched-bytes hash.
        // We must not guess: no link means the UI offers no "download what we sent".
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db);

        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), OrderId = orderId, OrgId = orgId, AttemptNumber = 1,
            Channel = "http", Destination = "https://nordmark.example/po",
            Status = DeliveryAttempt.StatusFailed, AttemptedAt = DateTime.UtcNow,
            IdempotencyKey = null, ArtifactSha256 = null,
        });
        await db.SaveChangesAsync();

        var passport = await new PassportService(db).GetAsync(orgId, orderId, CancellationToken.None);
        Assert.True(passport.IsSuccess, passport.Error);

        var attempt = Assert.Single(passport.Value!.DeliveryAttempts);
        Assert.Null(attempt.ArtifactId);
        Assert.Null(attempt.ArtifactSha256);
    }

    [Fact]
    public async Task Passport_DoesNotLinkAnAttempt_ToAnArtifactBelongingToAnotherOrder()
    {
        // A malformed / stale key that names an artifact this order does not own must not
        // produce a download link — the passport would be pointing the operator at bytes the
        // download endpoint will (correctly) refuse.
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db);

        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), OrderId = orderId, OrgId = orgId, AttemptNumber = 1,
            Channel = "http", Destination = "https://nordmark.example/po",
            Status = DeliveryAttempt.StatusSuccess, AttemptedAt = DateTime.UtcNow,
            IdempotencyKey = $"plk-dlv-{orderId:N}-{Guid.NewGuid():N}", // artifact row does not exist
            ArtifactSha256 = new string('c', 64),
        });
        await db.SaveChangesAsync();

        var passport = await new PassportService(db).GetAsync(orgId, orderId, CancellationToken.None);
        Assert.True(passport.IsSuccess, passport.Error);

        var attempt = Assert.Single(passport.Value!.DeliveryAttempts);
        Assert.Null(attempt.ArtifactId);
        // The dispatched-bytes fingerprint is still real evidence and stays.
        Assert.Equal(new string('c', 64), attempt.ArtifactSha256);
    }
}
