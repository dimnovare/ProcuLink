using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Phase 2 (B-slice, Task 4) — proves on REAL Postgres that <c>SourceMapReDerive</c> resolves a
/// <c>SourceFieldRule.SourceToken</c> from the persisted <c>SourceCapture.TokensJson</c> bag even
/// after the source FILE has been purged (<c>SourceFilePurgedAt</c> set, no re-tokenisation
/// possible). This is the whole point of the B-slice: the durable token bag — not the ephemeral
/// blob — drives re-derive at transform/preview time.
///
/// It exercises the exact PRODUCTION path the two threaded seams use:
///   reload order WITH SourceCapture → <see cref="SourceTokenSerialization.FromTokensJson"/> →
///   <c>MappedTransformService.Build(..., sourceTokens:)</c>.
/// Docker-gated; skips cleanly where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class SourceTokenReDerivePostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_rederive_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

        var cs = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
        {
            Pooling = false,
        }.ConnectionString;

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options;

        await using var migrate = new ProcuLinkDbContext(_options);
        await migrate.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    [DockerRequiredFact]
    public async Task ReDerive_uses_persisted_capture_tokens_after_source_blob_is_purged()
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        // ── Seed: an order whose source FILE is purged, but whose SourceCapture retains the token
        //         set. The "Customer ref" raw_field (no id → raw:{label}) is the ONLY place the
        //         value CR-42 lives — the parsed PoNumber is the stale "PO-OLD". ──
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            db.Organisations.Add(new Organisation
            {
                Id            = orgId,
                ClerkOrgId    = $"org_rederive_{orgId:N}",
                Name          = "ReDerive Org",
                Slug          = $"rederive-{orgId:N}",
                Plan          = "operations",
                AccountStatus = "active",
                CreatedAt     = now,
            });
            db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "ReDerive Supplier", CreatedAt = now });
            await db.SaveChangesAsync();

            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id                 = orderId,
                OrgId              = orgId,
                SupplierId         = supplierId,
                PoNumber           = "PO-OLD",        // stale parsed value — re-derive must override it
                Currency           = "EUR",
                Status             = "ready",
                OrderDate          = new DateOnly(2026, 6, 14),
                SourceFileKey      = "orders/rederive.csv",
                SourceFilePurgedAt = now,             // ← blob is GONE; only the token bag survives
                CreatedAt          = now,
                UpdatedAt          = now,
                Lines =
                {
                    new PurchaseOrderLineEntity
                    {
                        Id = Guid.NewGuid(), LineNumber = 1, SupplierItemCode = "S1",
                        Quantity = 1, UnitPrice = 1m,
                    },
                },
                SourceCapture = new SourceCapture
                {
                    Id = Guid.NewGuid(), OrgId = orgId, Format = "csv", CapturedAt = now,
                    TokensJson = System.Text.Json.JsonDocument.Parse(
                        """[{"label":"Customer ref","value":"CR-42"}]"""),
                },
            });
            await db.SaveChangesAsync();
        }

        // ── Reload through a FRESH context exactly like the transform seam: order + SourceCapture.
        //    Then rebuild tokens via the production serializer and run the override transform. ──
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var order = await db.PurchaseOrders
                .AsNoTracking()
                .Include(x => x.Lines)
                .Include(x => x.SourceCapture)
                .SingleAsync(x => x.Id == orderId && x.OrgId == orgId);

            Assert.NotNull(order.SourceFilePurgedAt); // sanity: the blob really is purged
            Assert.NotNull(order.SourceCapture);

            // PRODUCTION path: deserialize the persisted bag (no re-tokenisation of any file).
            var tokens = SourceTokenSerialization.FromTokensJson(order.SourceCapture!.TokensJson);
            Assert.Equal("CR-42", Assert.Single(tokens).Value);

            // A SourceMap rule that addresses the raw_field by its synthesised id "raw:Customer ref".
            var @override = new OrderMappingOverride
            {
                SourceMap = new Dictionary<string, SourceFieldRule>
                {
                    ["PoNumber"] = new SourceFieldRule { SourceToken = "raw:Customer ref" },
                },
                Output = new OutputMappingConfig
                {
                    Header = new() { ["po"]  = new OutputFieldRule { OutputPath = "po",  CanonicalField = "PoNumber" } },
                    Lines  = new() { ["sku"] = new OutputFieldRule { OutputPath = "sku", CanonicalField = "SupplierItemCode" } },
                },
            };

            var result = new MappedTransformService().Build(order, @override, OutputFormat.Csv, sourceTokens: tokens);
            using var reader = new StreamReader(result.Content);
            var csv = await reader.ReadToEndAsync();

            // PoNumber was re-derived from the persisted token, NOT the stale parsed "PO-OLD".
            Assert.Contains("CR-42", csv);
            Assert.DoesNotContain("PO-OLD", csv);
        }
    }
}
