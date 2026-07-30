using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
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
/// Finds a REAL Postgres to test against, preferring the long-running local dev server on :5435
/// (the one <c>appsettings.Development.json</c> already points at) over spinning up yet another
/// Testcontainers instance, and falling back to Testcontainers when there is no local server.
/// Probed ONCE, statically, because xUnit v2 has no dynamic <c>Assert.Skip</c> — see
/// <see cref="RealPostgresRequiredFactAttribute"/>.
/// </summary>
internal static class RealPostgres
{
    private const string Host = "localhost";
    private const int    Port = 5435;
    private const string User = "postgres";
    private const string Pass = "postgres";

    // Pooling=false keeps a throwaway database droppable; the generous Timeout is deliberate — a
    // developer box running a pile of other containers makes Docker Desktop's port proxy slow to
    // answer, and a short timeout turns that into a spurious red instead of a slow green.
    public static string LocalConnectionString(string database) =>
        $"Host={Host};Port={Port};Database={database};Username={User};Password={Pass};Pooling=false;Timeout=60;Command Timeout=120";

    public static readonly bool LocalAvailable = ProbeLocal();

    /// <summary>Null when SOME real Postgres is reachable (local or Docker); the reason otherwise.</summary>
    public static readonly string? UnavailableReason =
        LocalAvailable
            ? null
            : DockerProbe.UnavailableReason is { } dockerReason
                ? $"no local Postgres on :{Port} and no Docker — {dockerReason}"
                : null;

    /// <summary>Runs one statement against the local server's <c>postgres</c> maintenance database.</summary>
    public static async Task ExecuteOnLocalAsync(string sql)
    {
        await using var conn = new Npgsql.NpgsqlConnection(LocalConnectionString("postgres"));
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Retried on purpose: a single dropped connection through a loaded Docker Desktop port proxy
    /// must not permanently route the whole run onto the (slower, and on that same loaded box less
    /// reliable) Testcontainers path.
    /// </summary>
    private static bool ProbeLocal()
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var conn = new Npgsql.NpgsqlConnection(LocalConnectionString("postgres"));
                conn.Open();
                return true;
            }
            catch when (attempt < 3)
            {
                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
            catch
            {
                return false;
            }
        }
        return false;
    }
}

/// <summary>A <see cref="FactAttribute"/> that statically skips when no real Postgres is reachable.</summary>
public sealed class RealPostgresRequiredFactAttribute : FactAttribute
{
    public RealPostgresRequiredFactAttribute()
    {
        if (RealPostgres.UnavailableReason is { } reason)
            Skip = $"Requires a real Postgres — {reason}";
    }
}

/// <summary>
/// WP-12 on REAL Postgres: the promoted output TREE has to survive an actual JSONB round-trip
/// through <c>supplier_po_mappings.config_json</c> — the additive-column claim ("no EF migration
/// needed") is only true if Npgsql stores and returns the nested tree unchanged. EF InMemory keeps
/// the string in a dictionary and would happily hide a jsonb normalisation problem.
///
/// The flow proven here is the founder's actual one, end-to-end and across DbContexts:
/// design a tree on order A → transform A → "Save mappings for this supplier" → upload an identical
/// order B → transform B with ZERO designer interaction → BYTE-IDENTICAL bytes.
///
/// Docker-gated; skips cleanly where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class PromotedOutputTreePostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private string? _throwawayDb;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (RealPostgres.UnavailableReason is not null) return;

        string connectionString;
        if (RealPostgres.LocalAvailable)
        {
            // Prefer the long-running local dev server on :5435 with a THROWAWAY database. A fresh
            // Testcontainers instance per test class is the nicer isolation story right up until a
            // developer box is already running a dozen of them, at which point container start
            // reliably times out and this proof silently stops running.
            _throwawayDb = $"proculink_wp12_{Guid.NewGuid():N}";
            await RealPostgres.ExecuteOnLocalAsync($"CREATE DATABASE \"{_throwawayDb}\"");
            connectionString = RealPostgres.LocalConnectionString(_throwawayDb);
        }
        else
        {
            _pg = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase($"proculink_wp12_{Guid.NewGuid():N}")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _pg.StartAsync();
            connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
            { Pooling = false }.ConnectionString;
        }

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(connectionString).Options;

        // EnsureCreated, not Migrate: what this class proves is that Npgsql/jsonb round-trips the
        // promoted OutputNode tree unchanged and that the real relational transform path works on
        // it — neither depends on migration history or on the migration-defined triggers. Replaying
        // the whole migration chain would open hundreds of short-lived connections through Docker
        // Desktop's port proxy, which is exactly what makes these tests flaky on a loaded box.
        //
        // Even so, the FIRST connection to a just-created database can be dropped by that proxy when
        // the host is running many containers, so retry a few times rather than reporting a red that
        // says nothing about the code under test.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var schemaDb = new ProcuLinkDbContext(_options);
                await schemaDb.Database.EnsureCreatedAsync();
                break;
            }
            catch (Exception ex) when (attempt < 5 && ex is Npgsql.NpgsqlException or TimeoutException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }

    public async Task DisposeAsync()
    {
        if (_throwawayDb is not null)
        {
            // Drop with FORCE: a leaked pooled connection would otherwise leave the throwaway
            // database (and its disk) behind on the shared dev server, run after run.
            try { await RealPostgres.ExecuteOnLocalAsync($"DROP DATABASE IF EXISTS \"{_throwawayDb}\" WITH (FORCE)"); }
            catch { /* best effort — never fail a green test on cleanup */ }
        }
        if (_pg is not null) await _pg.DisposeAsync();
    }

    private ProcuLinkDbContext NewContext() => new(_options!);

    private static (OrderService Svc, Func<byte[]?> Bytes) Build(ProcuLinkDbContext db)
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

    private async Task<(Guid OrgId, Guid SupplierId, Guid OrderA, Guid OrderB)> SeedAsync()
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_wp12_{orgId:N}", Name = "WP12 Org",
            Slug = $"wp12-{orgId:N}", Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Tree Supplier", CreatedAt = now });
        await db.SaveChangesAsync();

        var orderA = AddOrder(db, orgId, supplierId, now);
        var orderB = AddOrder(db, orgId, supplierId, now);
        await db.SaveChangesAsync();

        return (orgId, supplierId, orderA, orderB);
    }

    /// <summary>Two byte-for-byte identical uploads from the same supplier.</summary>
    private static Guid AddOrder(ProcuLinkDbContext db, Guid orgId, Guid supplierId, DateTime now)
    {
        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "PO-WP12-PG", BuyerName = "WP12 Buyer",
            OrderDate = new DateOnly(2026, 7, 1), Currency = "EUR",
            Status = "ready", CreatedAt = now, UpdatedAt = now,
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
        return orderId;
    }

    /// <summary>
    /// The supplier's exact required document: renamed root key, nested object, repeating array,
    /// an XML-namespaced node and an IncludeWhen predicate — every part of the AST that a naive
    /// "copy the leaf rules" promotion would drop on the floor.
    /// </summary>
    private static OutputNodeTemplate DesignedTree() => new()
    {
        Format = OutputFormat.Json,
        Namespaces = new Dictionary<string, string> { ["cbc"] = "urn:oasis:names:cbc-2" },
        Root = OutputNode.Obj("root",
            OutputNode.FieldOf("supplierOrderRef",
                new OutputFieldRule { OutputPath = "supplierOrderRef", CanonicalField = "PoNumber" }),
            OutputNode.Arr("items",
                new OutputNode
                {
                    Name        = "item",
                    NodeType    = OutputNodeType.Object,
                    IncludeWhen = "line.Quantity > 0",
                    Children =
                    {
                        OutputNode.FieldOf("sku",
                            new OutputFieldRule { OutputPath = "sku", CanonicalField = "SupplierItemCode" }),
                        OutputNode.FieldOf("qty",
                            new OutputFieldRule { OutputPath = "qty", CanonicalField = "Quantity" }),
                    },
                })),
    };

    [RealPostgresRequiredFact]
    public async Task PromoteThenTransform_OnRealPostgres_RendersOrderBByteIdenticallyToOrderA()
    {
        var (orgId, supplierId, orderA, orderB) = await SeedAsync();

        // ── Order A: design + deliver ────────────────────────────────────────
        byte[] designedBytes;
        await using (var db = NewContext())
        {
            await new OrderMappingOverrideService(db).UpsertAsync(
                orgId, orderA, new OrderMappingOverride { OutputTree = DesignedTree() }, CancellationToken.None);

            var (svc, bytes) = Build(db);
            var result = await svc.TransformAsync(orgId, orderA, OutputFormat.Json, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error);
            designedBytes = bytes()!;
        }

        // ── "Save mappings for this supplier" ────────────────────────────────
        await using (var db = NewContext())
        {
            var promote = await new PromoteMappingService(db, new PoMappingService(db))
                .PromoteAsync(orgId, orderA, CancellationToken.None);
            Assert.NotNull(promote);
            Assert.False(promote!.NothingToPromote);
        }

        // ── The jsonb round-trip actually preserved the AST ──────────────────
        await using (var db = NewContext())
        {
            var stored = await new PoMappingService(db).GetAsync(orgId, supplierId, CancellationToken.None);
            Assert.NotNull(stored);
            var tree = stored!.OutputTree;
            Assert.NotNull(tree);
            Assert.Equal(OutputFormat.Json, tree!.Format);
            Assert.Equal("urn:oasis:names:cbc-2", tree.Namespaces!["cbc"]);      // namespaces survived
            var itemTemplate = tree.Root.Children.Single(c => c.Name == "items").Children.Single();
            Assert.Equal("line.Quantity > 0", itemTemplate.IncludeWhen);          // conditionals survived
        }

        // ── Order B: an identical upload, ZERO designer interaction ──────────
        await using (var db = NewContext())
        {
            var (svc, bytes) = Build(db);
            var result = await svc.TransformAsync(orgId, orderB, OutputFormat.Json, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error);

            Assert.Equal(designedBytes, bytes());

            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bytes()!));
            Assert.Equal("PO-WP12-PG", doc.RootElement.GetProperty("supplierOrderRef").GetString());
            Assert.Equal(2, doc.RootElement.GetProperty("items").GetArrayLength());
        }
    }
}
