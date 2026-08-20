using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Behavioural half of the two-integrity-gaps fix (model pins live in
/// <c>ProcuLink.Infrastructure.Tests/DeliveryIdempotencyAndSuggestionIntegrityModelTests</c>):
///
/// <list type="number">
/// <item>The partial unique index on <c>delivery_attempts (org_id, idempotency_key)</c> really
/// rejects a second in-flight (<c>dispatching</c>) row for the same key — the invariant
/// <c>OpenDispatchAttemptAsync</c>'s read-then-insert has been holding in application code alone —
/// while terminal retries with the same deterministic key still insert freely (the retry ladder
/// must not break).</item>
/// <item>The new FK from <c>order_supplier_suggestions.order_id</c> to <c>purchase_orders(id)</c>
/// really cascades on a RAW SQL delete — the exact path (direct SQL bypassing
/// <c>DataErasureService</c>) that produced this table's GDPR orphan the first time.</item>
/// </list>
///
/// Needs REAL Postgres: EF InMemory neither honours index filters nor executes raw SQL.
/// Docker-gated via <see cref="DockerRequiredFactAttribute"/>.
/// </summary>
[Collection("postgres-container")]
public sealed class DeliveryIdempotencyAndSuggestionIntegrityPostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_integrity_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

        var connectionString = new NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
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

    // ── seeding ──────────────────────────────────────────────────────────────

    private async Task<(Guid OrgId, Guid OrderId, Guid SupplierId)> SeedOrderAsync()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_integrity_{orgId:N}", Name = "integrity",
            Slug = $"integrity-{orgId:N}", Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme", CreatedAt = now });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "PO-INTEGRITY-1", BuyerName = "Buyer", OrderDate = new DateOnly(2026, 8, 1),
            Currency = "EUR", Status = "ready_to_deliver", CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return (orgId, orderId, supplierId);
    }

    private static DeliveryAttempt Attempt(Guid orgId, Guid orderId, string status, string key, int number) => new()
    {
        Id = Guid.NewGuid(),
        OrderId = orderId,
        OrgId = orgId,
        Channel = "http",
        Destination = "https://supplier.example/orders",
        Status = status,
        AttemptNumber = number,
        AttemptedAt = DateTime.UtcNow,
        IdempotencyKey = key,
    };

    // ── 1. the in-flight uniqueness is now the database's, not the application's ──

    [DockerRequiredFact]
    public async Task SecondDispatchingRow_WithTheSameOrgAndKey_IsRejectedByTheDatabase()
    {
        var (orgId, orderId, _) = await SeedOrderAsync();
        var key = $"po:{orderId:N}:art:{Guid.NewGuid():N}";

        await using (var db = NewContext())
        {
            db.DeliveryAttempts.Add(Attempt(orgId, orderId, DeliveryAttempt.StatusDispatching, key, 1));
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext())
        {
            db.DeliveryAttempts.Add(Attempt(orgId, orderId, DeliveryAttempt.StatusDispatching, key, 2));
            var act = async () => await db.SaveChangesAsync();
            (await act.Should().ThrowAsync<DbUpdateException>())
                .WithInnerException<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        }
    }

    [DockerRequiredFact]
    public async Task TerminalRetries_LegitimatelyReuseTheDeterministicKey_AndStillInsert()
    {
        var (orgId, orderId, _) = await SeedOrderAsync();
        var key = $"po:{orderId:N}:art:{Guid.NewGuid():N}";

        await using var db = NewContext();
        // The key is deterministic per (order, artifact): a failed attempt, a second failed
        // attempt, and the current in-flight retry ALL carry the same key. The index must accept
        // this — it guards concurrent in-flight rows, not the retry ladder's history.
        db.DeliveryAttempts.Add(Attempt(orgId, orderId, DeliveryAttempt.StatusFailed, key, 1));
        db.DeliveryAttempts.Add(Attempt(orgId, orderId, DeliveryAttempt.StatusFailed, key, 2));
        db.DeliveryAttempts.Add(Attempt(orgId, orderId, DeliveryAttempt.StatusDispatching, key, 3));
        var act = async () => await db.SaveChangesAsync();
        await act.Should().NotThrowAsync();
    }

    // ── 2. a raw order delete can no longer orphan suggestion rows ────────────

    [DockerRequiredFact]
    public async Task RawSqlDeleteOfAnOrder_CascadesToItsSupplierSuggestions()
    {
        var (orgId, orderId, supplierId) = await SeedOrderAsync();

        await using (var db = NewContext())
        {
            db.OrderSupplierSuggestions.Add(new OrderSupplierSuggestion
            {
                Id = Guid.NewGuid(), OrgId = orgId, OrderId = orderId, SupplierId = supplierId,
                Rank = 1, Score = 0.9, SignalsJson = """{"why":"sender domain"}""",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext())
        {
            // Raw SQL on purpose: this is the DataErasureService-bypassing path that orphaned
            // this table the first time. The FK, not application code, must clean up.
            var deleted = await db.Database.ExecuteSqlAsync(
                $"DELETE FROM purchase_orders WHERE id = {orderId}");
            deleted.Should().Be(1);

            var remaining = await db.OrderSupplierSuggestions
                .Where(x => x.OrderId == orderId).CountAsync();
            remaining.Should().Be(0, "the FK is ON DELETE CASCADE — nothing may survive the order");
        }
    }
}
