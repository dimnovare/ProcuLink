using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
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
/// One postgres:16 for the whole class rather than one per test — xUnit constructs a test-class
/// instance per <c>[Fact]</c>, so the repo's per-test <c>IAsyncLifetime</c> convention would start
/// and migrate a container each time. Mirrors <see cref="SupplierSuggestionPostgresFixture"/>,
/// including the TCP readiness loop and the IPv4 pin.
/// </summary>
public sealed class PromotedOutputTreePostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    public string? ConnectionString { get; private set; }

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_output_tree_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await _pg.StartAsync();

        var csb = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
        {
            Pooling = false,
            Timeout = 10,
        };
        if (string.Equals(csb.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            csb.Host = "127.0.0.1";

        ConnectionString = csb.ConnectionString;
        await WaitUntilAcceptingTcpAsync();

        await using var migrateDb = new ProcuLinkDbContext(Options());
        await migrateDb.Database.MigrateAsync();
    }

    private async Task WaitUntilAcceptingTcpAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        Exception? last = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var probe = new Npgsql.NpgsqlConnection(ConnectionString);
                await probe.OpenAsync();
                await using var cmd = new Npgsql.NpgsqlCommand("SELECT 1", probe);
                await cmd.ExecuteScalarAsync();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        throw new InvalidOperationException(
            "Postgres testcontainer never accepted a TCP connection within 90s.", last);
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null) await _pg.DisposeAsync();
    }

    public DbContextOptions<ProcuLinkDbContext> Options() =>
        new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(ConnectionString!).Options;
}

/// <summary>
/// WP-12 on real Postgres. <c>SupplierPoMapping.ConfigJson</c> is a <c>jsonb</c> column, and jsonb
/// is not a string: Postgres reparses the document, drops insignificant whitespace, discards
/// duplicate keys and **reorders object members**. InMemory stores the string verbatim, so it cannot
/// see any of that.
///
/// <para>Two things therefore need a real database to mean anything: that a promoted
/// <c>OutputNodeTemplate</c> still deserialises into the same tree after the round trip, and that
/// the promote → transform chain still renders byte-identically when the bytes come back through
/// jsonb rather than out of a dictionary.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class PromotedOutputTreePostgresTests : IClassFixture<PromotedOutputTreePostgresFixture>
{
    private readonly PromotedOutputTreePostgresFixture _fx;

    public PromotedOutputTreePostgresTests(PromotedOutputTreePostgresFixture fx) => _fx = fx;

    private DbContextOptions<ProcuLinkDbContext> Options() => _fx.Options();

    // ── Seeding ───────────────────────────────────────────────────────────────

    private static async Task<Guid> SeedOrgAsync(ProcuLinkDbContext db, string tag)
    {
        var orgId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            // Unique per org — the container is shared across this class, so a fixed ClerkOrgId
            // would collide on IX_organisations_clerk_org_id from the second seed on.
            ClerkOrgId = $"org_{tag}_{orgId:N}",
            Name       = $"Org {tag}",
            Slug       = $"org-{tag}-{orgId:N}",
            CreatedAt  = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    private static async Task<Guid> SeedSupplierAsync(ProcuLinkDbContext db, Guid orgId)
    {
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier
        {
            Id = supplierId, OrgId = orgId, Name = "WP12 PG Supplier", CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return supplierId;
    }

    private static async Task<Guid> SeedReadyOrderAsync(ProcuLinkDbContext db, Guid orgId, Guid supplierId)
    {
        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = "PO-WP12-PG",
            BuyerName  = "WP12 PG Buyer",
            OrderDate  = new DateOnly(2026, 7, 27),
            Currency   = "EUR",
            Status     = OrderStatusConstants.Ready,
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
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 2,
                    BuyerItemCode = "B-2", SupplierItemCode = "SUP-2", Description = "Gadget",
                    Quantity = 2m, Unit = "EA", UnitPrice = 5.5m, NeedsReview = false, Confidence = 1.0f,
                },
            },
        });
        await db.SaveChangesAsync();
        return orderId;
    }

    /// <summary>
    /// Deliberately awkward for jsonb: many members on one node (so member order is very unlikely to
    /// survive by luck), a namespace map, and an IncludeWhen predicate containing quotes.
    /// </summary>
    private static OutputNodeTemplate DesignedTree() => new()
    {
        Format     = OutputFormat.Json,
        Namespaces = new Dictionary<string, string>
        {
            ["cbc"] = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2",
            ["cac"] = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2",
        },
        Root = OutputNode.Obj("root",
            OutputNode.FieldOf("orderNumber",
                new OutputFieldRule { OutputPath = "orderNumber", CanonicalField = "PoNumber" }),
            OutputNode.FieldOf("buyer",
                new OutputFieldRule { OutputPath = "buyer", CanonicalField = "BuyerName" }),
            OutputNode.FieldOf("currency",
                new OutputFieldRule { OutputPath = "currency", CanonicalField = "Currency" }),
            OutputNode.Arr("items",
                new OutputNode
                {
                    Name        = "item",
                    NodeType    = OutputNodeType.Object,
                    IncludeWhen = "line.Quantity > 0",
                    Children =
                    {
                        OutputNode.FieldOf("code",
                            new OutputFieldRule { OutputPath = "code", CanonicalField = "SupplierItemCode" }),
                        OutputNode.FieldOf("qty",
                            new OutputFieldRule { OutputPath = "qty", CanonicalField = "Quantity" }),
                    },
                })),
    };

    private static (OrderService Svc, Func<byte[]?> CapturedBytes) Build(ProcuLinkDbContext db)
    {
        byte[]? captured = null;

        var fileStorage = new Mock<IFileStorageService>();
        fileStorage
            .Setup(s => s.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, string, string, CancellationToken>((stream, _, _, _) =>
            {
                using var ms = new MemoryStream();
                stream.Position = 0;
                stream.CopyTo(ms);
                captured = ms.ToArray();
            })
            .ReturnsAsync("artifact-key");

        var svc = new OrderService(
            db,
            fileStorage.Object,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser() }),
            new Mock<IItemMappingService>().Object,
            new OrderExceptionService(db),
            new PoMappingService(db),
            new Mock<IAiMappingService>().Object,
            new ITransformService[] { new CsvTransformService(), new JsonTransformService(), new XmlTransformService() },
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());

        return (svc, () => captured);
    }

    // ── The tests ─────────────────────────────────────────────────────────────

    [DockerRequiredFact]
    public async Task PromotedTree_SurvivesTheJsonbRoundTrip_WithNamespacesAndPredicateIntact()
    {
        await using var db = new ProcuLinkDbContext(Options());
        var orgId      = await SeedOrgAsync(db, "tree_roundtrip");
        var supplierId = await SeedSupplierAsync(db, orgId);

        await new PoMappingService(db).UpsertAsync(
            orgId, supplierId, new PoMappingConfig { OutputTree = DesignedTree() }, CancellationToken.None);

        // A separate context, so nothing is served from the change tracker — the value genuinely
        // comes back out of the jsonb column.
        await using var reader = new ProcuLinkDbContext(Options());
        var reread = await new PoMappingService(reader).GetAsync(orgId, supplierId, CancellationToken.None);

        Assert.NotNull(reread?.OutputTree);
        var tree = reread!.OutputTree!;
        Assert.Equal(OutputFormat.Json, tree.Format);
        Assert.Equal(2, tree.Namespaces!.Count);
        Assert.Equal(
            "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2",
            tree.Namespaces["cbc"]);

        // Child ORDER is what jsonb reordering would break, and child order is column order.
        Assert.Equal(
            new[] { "orderNumber", "buyer", "currency", "items" },
            tree.Root.Children.Select(c => c.Name).ToArray());

        var itemTemplate = tree.Root.Children.Single(c => c.Name == "items").Children.Single();
        Assert.Equal("line.Quantity > 0", itemTemplate.IncludeWhen);
        Assert.Equal(new[] { "code", "qty" }, itemTemplate.Children.Select(c => c.Name).ToArray());
    }

    [DockerRequiredFact]
    public async Task DesignOnOrderA_Promote_OrderBRendersByteIdentically_ThroughRealPostgres()
    {
        await using var db = new ProcuLinkDbContext(Options());
        var orgId      = await SeedOrgAsync(db, "tree_parity");
        var supplierId = await SeedSupplierAsync(db, orgId);

        // Order A — the operator designs the layout here.
        var orderA = await SeedReadyOrderAsync(db, orgId, supplierId);
        await new OrderMappingOverrideService(db).UpsertAsync(
            orgId, orderA, new OrderMappingOverride { OutputTree = DesignedTree() }, CancellationToken.None);

        var (svcA, capturedA) = Build(db);
        var resultA = await svcA.TransformAsync(orgId, orderA, OutputFormat.Json, CancellationToken.None);
        Assert.True(resultA.IsSuccess, resultA.Error);
        var bytesA = capturedA();
        Assert.NotNull(bytesA);

        // "Save this layout for the supplier" — through the real jsonb column.
        var promoted = await new PromoteMappingService(db, new PoMappingService(db))
            .PromoteAsync(orgId, orderA, CancellationToken.None);
        Assert.NotNull(promoted);
        Assert.True(promoted!.OutputTreePromoted);
        Assert.False(promoted.NothingToPromote);

        // Order B — same supplier sends the same file again. No per-order override at all, and a
        // fresh context so the promoted tree is read back out of Postgres rather than from memory.
        await using var second = new ProcuLinkDbContext(Options());
        var orderB = await SeedReadyOrderAsync(second, orgId, supplierId);

        var (svcB, capturedB) = Build(second);
        var resultB = await svcB.TransformAsync(orgId, orderB, OutputFormat.Json, CancellationToken.None);
        Assert.True(resultB.IsSuccess, resultB.Error);

        Assert.Equal(
            Encoding.UTF8.GetString(bytesA!),
            Encoding.UTF8.GetString(capturedB()!)); // readable diff on failure
        Assert.Equal(bytesA, capturedB());          // byte-for-byte
    }

    [DockerRequiredFact]
    public async Task PromotedTree_ProducesAStableConfigDigestAcrossOrders()
    {
        // Provenance has to identify the config that produced the bytes. Two orders rendered from
        // ONE promoted layout must therefore carry the SAME digest — which is only true because the
        // digest is computed from the deserialised tree rather than from the raw column text, whose
        // member order jsonb is free to change.
        await using var db = new ProcuLinkDbContext(Options());
        var orgId      = await SeedOrgAsync(db, "tree_digest");
        var supplierId = await SeedSupplierAsync(db, orgId);

        await new PoMappingService(db).UpsertAsync(
            orgId, supplierId, new PoMappingConfig { OutputTree = DesignedTree() }, CancellationToken.None);

        var orderA = await SeedReadyOrderAsync(db, orgId, supplierId);
        var orderB = await SeedReadyOrderAsync(db, orgId, supplierId);

        var (svc, _) = Build(db);
        var a = await svc.TransformAsync(orgId, orderA, OutputFormat.Json, CancellationToken.None);
        var b = await svc.TransformAsync(orgId, orderB, OutputFormat.Json, CancellationToken.None);
        Assert.True(a.IsSuccess, a.Error);
        Assert.True(b.IsSuccess, b.Error);

        await using var check = new ProcuLinkDbContext(Options());
        var digestA = (await check.OutboundArtifacts.AsNoTracking().SingleAsync(x => x.Id == a.Value!.ArtifactId)).ConfigDigest;
        var digestB = (await check.OutboundArtifacts.AsNoTracking().SingleAsync(x => x.Id == b.Value!.ArtifactId)).ConfigDigest;

        Assert.NotNull(digestA);
        Assert.Equal(digestA, digestB);
        Assert.NotEqual(ProvenanceHash.TrySha256HexUtf8("fixed:json"), digestA);
    }
}
