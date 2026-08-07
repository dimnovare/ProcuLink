using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Task 8 — the regression guard for this plan's central finding. The credential-binding backfill
/// (<see cref="CredentialBindingBackfillService"/>) must NEVER touch
/// <c>SupplierDeliveryConfig.EncryptedCredentials</c> or <c>SupplierConnectionRevision.CredentialsRef</c>:
///
/// <list type="bullet">
/// <item><c>CredentialsRef</c> is a verbatim byte-copy of the live row, compared by ORDINAL BYTE
/// EQUALITY in <c>DeliverySnapshotMatches</c> (<c>SupplierConnectionService.cs:554</c>). AES-GCM's
/// random nonce means re-encrypting either side changes the bytes, and the live config then reads
/// as permanently drifted from its active revision.</item>
/// <item>A published revision's <c>credentials_ref</c> is frozen by the Postgres trigger
/// <c>proculink_block_published_revision_content_update</c> (migration
/// <c>AddReviewReasonAndPublishedRevisionImmutability</c>), which raises <c>P0001</c> on any change
/// while the revision's status is <c>published</c>.</item>
/// </list>
///
/// Needs REAL Postgres — the trigger does not exist on the EF InMemory provider. Docker-gated via
/// <see cref="DockerRequiredFactAttribute"/>, which statically reports these tests as Skipped (not
/// Passed) when Docker is unreachable, so a missing Docker daemon can never read as a green run.
/// </summary>
[Collection("postgres-container")]
public sealed class CredentialBindingBackfillPostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_credbind_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

        // Pooling=false so each context opens its own physical connection — matches the convention
        // in DeliveryConcurrencyPostgresTests.cs; not load-bearing for these tests (no concurrency
        // here) but keeps the fixture identical to its model.
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

    [DockerRequiredFact]
    public async Task Rebind_DoesNotTripThePublishedRevisionImmutabilityTrigger()
    {
        var legacy = LegacyBlob("delivery-credentials");
        await SeedPublishedRevisionWithCopiedCredentialsAsync(legacy);

        await using var db = NewContext();
        var act = async () => await new CredentialBindingBackfillService(
            db, Encryption(), NullLogger<CredentialBindingBackfillService>.Instance)
            .RebindLegacyCredentialsAsync(default);

        // A PostgresException with SqlState 'P0001' from
        // proculink_block_published_revision_content_update means the backfill reached
        // credentials_ref, which it must never do.
        await act.Should().NotThrowAsync();
    }

    [DockerRequiredFact]
    public async Task Rebind_LeavesTheLiveAndRevisionCredentialBytesEqual()
    {
        var legacy = LegacyBlob("delivery-credentials");
        var supplierId = await SeedPublishedRevisionWithCopiedCredentialsAsync(legacy);

        await using (var db = NewContext())
        {
            await new CredentialBindingBackfillService(
                db, Encryption(), NullLogger<CredentialBindingBackfillService>.Instance)
                .RebindLegacyCredentialsAsync(default);
        }

        await using var check = NewContext();
        var live = await check.SupplierDeliveryConfigs.AsNoTracking()
            .SingleAsync(x => x.SupplierId == supplierId);
        var revision = await check.SupplierConnectionRevisions.AsNoTracking()
            .SingleAsync(x => x.SupplierId == supplierId && x.Status == "published");

        // This ordinal equality IS what DeliverySnapshotMatches (SupplierConnectionService.cs:554)
        // compares. Re-encrypting either side changes the bytes — a random nonce guarantees it —
        // and the live config would then read as permanently drifted from its active revision,
        // spawning a redundant revision on every subsequent save.
        revision.CredentialsRef.Should().Be(live.EncryptedCredentials);
        live.EncryptedCredentials.Should().Be(legacy, "neither side may be rewritten");
    }

    // ── seeding ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds an org + supplier + SupplierConnection + PUBLISHED revision whose CredentialsRef is the
    /// SAME bytes as the live SupplierDeliveryConfig.EncryptedCredentials — the verbatim copy
    /// production creates at SupplierConnectionService.cs:483. Modeled on
    /// PinnedOrderDoesNotRerouteAfterConfigEditPostgresTests.SeedGovernedSupplierAsync (this test
    /// class does not need the order/dispatch machinery that file also seeds). Returns the supplier
    /// id.
    /// </summary>
    private async Task<Guid> SeedPublishedRevisionWithCopiedCredentialsAsync(string legacyBlob)
    {
        var orgId        = Guid.NewGuid();
        var supplierId   = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var revisionId   = Guid.NewGuid();
        var now          = DateTime.UtcNow;

        await using var db = NewContext();

        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_credbind_{orgId:N}",
            Name = "Credential Rebind Org",
            Slug = $"credbind-{orgId:N}",
            Plan = "operations",
            AccountStatus = "active",
            CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier
        {
            Id = supplierId,
            OrgId = orgId,
            Name = "Governed Supplier",
            CreatedAt = now,
        });
        await db.SaveChangesAsync();

        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = connectionId,
            OrgId = orgId,
            SupplierId = supplierId,
            Name = "Governed Supplier",
            ActiveRevisionId = null,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        // Published revision — CredentialsRef is the SAME legacy blob bytes as the live row below,
        // exactly as production's verbatim copy (SupplierConnectionService.cs:483) produces.
        db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
        {
            Id = revisionId,
            ConnectionId = connectionId,
            OrgId = orgId,
            SupplierId = supplierId,
            VersionNo = 1,
            Status = "published",
            PublishedAt = now,
            EffectiveFrom = now,
            CreatedAt = now,
            CatalogMode = "live",
            OutputFormat = "csv",
            DeliveryProtocol = "http",
            DeliveryConfigJson = """{"url":"https://supplier.test/endpoint","method":"POST"}""",
            DeliveryAutoDeliver = true,
            CredentialsRef = legacyBlob,
        });
        await db.SaveChangesAsync();

        var conn = await db.SupplierConnections.SingleAsync(c => c.Id == connectionId);
        conn.ActiveRevisionId = revisionId;
        await db.SaveChangesAsync();

        // The live row starts in sync with the published revision — same legacy blob, byte for byte.
        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            Protocol = "http",
            AutoDeliver = true,
            ConfigJson = """{"url":"https://supplier.test/endpoint","method":"POST"}""",
            EncryptedCredentials = legacyBlob,
            OutputFormat = "csv",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        return supplierId;
    }

    // Builds a version-1 envelope directly, the way rows were written before binding existed.
    // Deliberately does NOT go through Encrypt, which now always writes version 2.
    private static string LegacyBlob(string plaintext)
    {
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[16];

        using var aes = new System.Security.Cryptography.AesGcm(new byte[32], 16);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag); // no associated data — that is the point

        var combined = new byte[29 + ciphertext.Length];
        combined[0] = 1;
        nonce.CopyTo(combined, 1);
        tag.CopyTo(combined, 13);
        ciphertext.CopyTo(combined, 29);
        return Convert.ToBase64String(combined);
    }

    private static DeliveryEncryptionService Encryption()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();
        return new DeliveryEncryptionService(cfg);
    }
}
