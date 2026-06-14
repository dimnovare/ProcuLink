using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Phase 1 — proves on REAL Postgres (not EF InMemory) that the lossless-capture additions are
/// REAL persisted schema: the new header columns (incoterms / contact_email / …) on
/// <c>purchase_orders</c>, the new line columns (manufacturer_part_number / recipient / …) on
/// <c>purchase_order_lines</c>, the <c>order_parties</c> child table, and the one-per-order
/// <c>source_captures</c> raw-bag table (with its jsonb tokens_json) all survive an INSERT →
/// fresh-context reload. The migration <c>AddLosslessCanonicalCapture</c> creates them; an
/// EF-Ignored property or a forgotten migration column would silently vanish here.
/// Docker-gated; skips cleanly where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class LosslessCapturePersistencePostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_lossless_{Guid.NewGuid():N}")
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

    [DockerRequiredFact]
    public async Task Parties_line_columns_and_source_capture_survive_reload()
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        // ── Seed parents first (FKs enforced on Postgres; mirrors ProvenancePersistencePostgresTests) ──
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            db.Organisations.Add(new Organisation
            {
                Id            = orgId,
                ClerkOrgId    = $"org_lossless_{orgId:N}",
                Name          = "Lossless Org",
                Slug          = $"lossless-{orgId:N}",
                Plan          = "operations",
                AccountStatus = "active",
                CreatedAt     = now,
            });
            db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Lossless Supplier", CreatedAt = now });
            await db.SaveChangesAsync();

            // Header lossless column + one party + one line with lossless columns + the raw-bag capture.
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id        = orderId,
                OrgId     = orgId,
                SupplierId = supplierId,
                PoNumber  = "4730154181",
                Currency  = "EUR",
                Status    = "pending_review",
                OrderDate = new DateOnly(2026, 6, 12),
                CreatedAt = now,
                UpdatedAt = now,
                // Phase 1 header lossless columns.
                Incoterms    = "DDP",
                ContactEmail = "redacted@example.invalid",
                Parties =
                {
                    new OrderParty
                    {
                        Id = Guid.NewGuid(), OrgId = orgId, Role = "shipTo",
                        Name = "REDACTED-PARTY", City = "Linz", Vat = "REDACTED-TAXID",
                    },
                },
                Lines =
                {
                    new PurchaseOrderLineEntity
                    {
                        Id = Guid.NewGuid(), LineNumber = 1, BuyerItemCode = "00010",
                        Quantity = 1, UnitPrice = 306.28m,
                        // Phase 1 line lossless columns.
                        ManufacturerPartNumber = "SCPMX94EGK",
                        Recipient = "redacted@example.invalid",
                    },
                },
                SourceCapture = new SourceCapture
                {
                    Id = Guid.NewGuid(), OrgId = orgId, Format = "pdf", CapturedAt = now,
                    TokensJson = System.Text.Json.JsonDocument.Parse(
                        """[{"label":"EDI id","value":"REDACTED-TAXID"}]"""),
                },
            });
            await db.SaveChangesAsync();
        }

        // ── Reload through a FRESH context: values must come back from Postgres itself ──
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var o = await db.PurchaseOrders
                .AsNoTracking()
                .Include(x => x.Parties)
                .Include(x => x.Lines)
                .Include(x => x.SourceCapture)
                .SingleAsync(x => x.Id == orderId);

            // Header lossless column.
            Assert.Equal("DDP", o.Incoterms);
            Assert.Equal("redacted@example.invalid", o.ContactEmail);

            // order_parties child row.
            var party = Assert.Single(o.Parties);
            Assert.Equal("shipTo", party.Role);
            Assert.Equal("REDACTED-TAXID", party.Vat);
            Assert.Equal("Linz", party.City);

            // Line lossless columns.
            var line = Assert.Single(o.Lines);
            Assert.Equal("SCPMX94EGK", line.ManufacturerPartNumber);
            Assert.Equal("redacted@example.invalid", line.Recipient);

            // source_captures one-per-order raw bag (jsonb round-trip — parse, don't substring).
            Assert.NotNull(o.SourceCapture);
            Assert.Equal("pdf", o.SourceCapture!.Format);
            Assert.NotNull(o.SourceCapture.TokensJson);
            using var bag = System.Text.Json.JsonDocument.Parse(o.SourceCapture.TokensJson!.RootElement.GetRawText());
            var first = bag.RootElement[0];
            Assert.Equal("EDI id", first.GetProperty("label").GetString());
            Assert.Equal("REDACTED-TAXID", first.GetProperty("value").GetString());
        }
    }
}
