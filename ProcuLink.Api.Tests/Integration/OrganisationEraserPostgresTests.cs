using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Tenancy;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// The guard that keeps <see cref="OrganisationEraser"/> complete. Its corpus is the EF MODEL, not
/// a list: every entity type with an org column is seeded for two organisations, one is erased,
/// and every table must hold zero rows for it and every row of the other. A table added tomorrow
/// is seeded and checked without editing this file — and if the generic seeder cannot build it,
/// seeding throws naming the table, so a skipped table fails rather than passing silently.
/// </summary>
[Collection("postgres-container")]
public sealed class OrganisationEraserPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    /// <summary>
    /// Anti-vacuity floor: entity types carrying OrgId/OrganisationId, measured 2026-11-04 (an
    /// older audit said 9, a later note 15 — both were partial). Raise as the schema grows; never
    /// lower it to make a red build green.
    /// </summary>
    private const int OrgScopedTables = 48;

    /// <summary>Mapped types that are not tenant data and are never seeded.</summary>
    private static readonly Type[] NotTenantData =
        [typeof(Organisation), typeof(DataProtectionKey), typeof(WorkerHealthAlertCooldown), typeof(PlatformAuditEvent)];

    private DbContextOptions<ProcuLinkDbContext>? _options;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;
        _connectionString = await postgres.CreateDatabaseAsync("proculink_orgerase");
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(_connectionString) { Pooling = false }.ConnectionString)
            .Options;
    }

    public async Task DisposeAsync() => await postgres.DropDatabaseAsync(_connectionString);

    private ProcuLinkDbContext NewContext() => new(_options!);

    [DockerRequiredFact]
    public async Task Erase_RemovesEveryOrgOwnedRow_AndLeavesTheOtherOrgIntact()
    {
        Guid target = Guid.NewGuid(), other = Guid.NewGuid();
        await SeedEverythingAsync(target, 'a');
        await SeedEverythingAsync(other, 'b');

        await using var check = NewContext();
        var orgTables = check.Model.GetEntityTypes().Where(t => OrgQueryFilters.FindOrgPropertyName(t) is not null).ToList();
        orgTables.Count.Should().BeGreaterThanOrEqualTo(OrgScopedTables);

        var otherBefore = new Dictionary<string, int>();
        foreach (var t in orgTables) otherBefore[t.GetTableName()!] = await CountAsync(check, t, other);
        var unseeded = new List<string>();
        foreach (var t in orgTables)
            if (await CountAsync(check, t, target) == 0) unseeded.Add(t.GetTableName()!);
        unseeded.Should().BeEmpty("a table the guard never seeded is a table it proves nothing about");

        // Billed overage refuses the erase (RefusedBillingEvidence, pinned in OrganisationEraseTests),
        // so — having proved it seeds — the guard clears that one table for the target.
        await check.OverageBillingRecords.Where(r => r.OrgId == target).ExecuteDeleteAsync();

        var storage = new Mock<IFileStorageService>();
        await using (var db = NewContext())
        {
            var receipt = await new OrganisationEraser(db, storage.Object, NullLogger<OrganisationEraser>.Instance)
                .EraseAsync(target, "test:guard", "guard", CancellationToken.None);
            receipt.Outcome.Should().Be(OrgEraseOutcome.Erased);
            receipt.RowsDeleted.Keys.Should().Contain(orgTables.Select(t => t.GetTableName()!));
            receipt.BlobsDeleted.Should().BePositive();
        }

        var leftovers = new List<string>();
        var lost = new List<string>();
        foreach (var t in orgTables)
        {
            if (await CountAsync(check, t, target) > 0) leftovers.Add(t.GetTableName()!);
            if (await CountAsync(check, t, other) != otherBefore[t.GetTableName()!]) lost.Add(t.GetTableName()!);
        }
        leftovers.Should().BeEmpty("the erased org must own nothing afterwards");
        lost.Should().BeEmpty("the erase must never touch another org");
        (await check.Organisations.AnyAsync(o => o.Id == target)).Should().BeFalse();
        (await check.PlatformAuditEvents.AnyAsync(e => e.SubjectOrgId == target && e.Action == OrganisationEraser.AuditAction))
            .Should().BeTrue("the receipt must survive the erase");
        storage.Verify(s => s.DeleteAsync(It.Is<string>(k => !k.StartsWith('a')), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Task<int> CountAsync(ProcuLinkDbContext db, IEntityType type, Guid orgId) =>
        (Task<int>)typeof(OrganisationEraserPostgresTests)
            .GetMethod(nameof(CountOf), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(type.ClrType)
            .Invoke(null, [db, OrgQueryFilters.FindOrgPropertyName(type)!, orgId])!;

    private static Task<int> CountOf<T>(ProcuLinkDbContext db, string orgProperty, Guid orgId) where T : class =>
        db.Set<T>().CountAsync(e => EF.Property<Guid>(e, orgProperty) == orgId);

    /// <summary>
    /// One row of every mapped tenant type for <paramref name="orgId"/>, built from the model:
    /// principals first, required FKs pointed at this org's own parents, then a second pass fills
    /// the optional FKs (active revision, delivery attempt → order, confirmation line → order line)
    /// so the RESTRICT / NO ACTION edges the eraser has to order around are really populated.
    /// Every filled string starts with <paramref name="tag"/>, which is how the blob assertion
    /// tells the two orgs' keys apart.
    /// </summary>
    private async Task SeedEverythingAsync(Guid orgId, char tag)
    {
        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_{tag}{orgId:N}", Name = "erase-guard", Slug = $"erase-guard-{tag}{orgId:N}",
            CreatedAt = DateTime.UtcNow, TrialStartedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var seeded = new Dictionary<IEntityType, object>();
        object? KeyOf(IForeignKey fk, IProperty p) =>
            seeded.TryGetValue(fk.PrincipalEntityType, out var parent)
                ? db.Entry(parent).Property(fk.PrincipalKey.Properties[fk.Properties.ToList().IndexOf(p)].Name).CurrentValue
                : null;

        foreach (var type in db.Model.GetEntityTypes().Where(t => !NotTenantData.Contains(t.ClrType)).OrderBy(SeedDepth))
        {
            var entity = Activator.CreateInstance(type.ClrType)!;
            var entry = db.Entry(entity);
            foreach (var p in type.GetProperties())
            {
                var current = entry.Property(p.Name).CurrentValue;
                var fk = p.GetContainingForeignKeys().FirstOrDefault(f => f.IsRequired);
                var value = p.Name == OrgQueryFilters.FindOrgPropertyName(type) ? orgId
                    : fk is not null ? KeyOf(fk, p)
                    : p.IsForeignKey() ? current
                    : Fill(p, current, tag);
                if (!Equals(value, current)) entry.Property(p.Name).CurrentValue = value;
            }
            db.Add(entity);
            try { await db.SaveChangesAsync(); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not seed {type.GetTableName()} from the model — give it a bespoke seeder in this file.", ex);
            }
            seeded[type] = entity;
        }

        foreach (var (type, entity) in seeded)
            foreach (var fk in type.GetForeignKeys().Where(f => !f.IsRequired && seeded.ContainsKey(f.PrincipalEntityType)))
                foreach (var p in fk.Properties)
                    db.Entry(entity).Property(p.Name).CurrentValue = KeyOf(fk, p);
        await db.SaveChangesAsync();
    }

    private static int SeedDepth(IEntityType type) =>
        type.GetForeignKeys().Where(fk => fk.IsRequired && fk.PrincipalEntityType != type)
            .Select(fk => 1 + SeedDepth(fk.PrincipalEntityType)).DefaultIfEmpty(0).Max();

    private static object? Fill(IProperty p, object? current, char tag)
    {
        if (p.IsPrimaryKey() && p.ClrType == typeof(Guid)) return Guid.NewGuid();
        if (p.ClrType == typeof(string) && string.IsNullOrEmpty((string?)current))
        {
            if (p.GetColumnType() == "jsonb") return "{}";
            var token = $"{tag}{Guid.NewGuid():N}";
            return token[..Math.Min(token.Length, p.GetMaxLength() ?? token.Length)];
        }
        if (p.ClrType == typeof(DateTime) && (DateTime)current! == default) return DateTime.UtcNow;
        if (p.ClrType == typeof(JsonDocument) && current is null) return JsonDocument.Parse("{}");
        if (p.ClrType == typeof(byte[]) && current is null) return new byte[] { 1 };
        return current;
    }
}
