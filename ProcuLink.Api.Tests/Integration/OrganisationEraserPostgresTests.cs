using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using ProcuLink.Core.Documents;
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
    private const int OrgScopedTables = 49;

    /// <summary>Mapped types that are not tenant data and are never seeded.</summary>
    private static readonly Type[] NotTenantData =
        [typeof(Organisation), typeof(DataProtectionKey), typeof(WorkerHealthAlertCooldown), typeof(PlatformAuditEvent)];

    /// <summary>
    /// String columns named *Key that are not stored-file keys (ObjectKey names an object in the
    /// CUSTOMER's S3 bucket). A new *Key column fails the blob assertion until the eraser deletes
    /// its files or it is listed here — the definition is this file's, not the eraser's.
    /// </summary>
    private static readonly string[] NotFileKeys = ["Key", "IdempotencyKey", "ObjectKey", "BillingKey", "EncryptedSecretKey", "DedupKey", "TableKey", "ItemKey", "SenderKey"];

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
        var targetFiles = await SeedEverythingAsync(target, 'a');
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

        // Each org's own member, plus the other org's member joining the target: only the first may go.
        var targetUser = await check.Memberships.Where(m => m.OrgId == target).Select(m => m.UserId).SingleAsync();
        var otherUser = await check.Memberships.Where(m => m.OrgId == other).Select(m => m.UserId).SingleAsync();
        check.Memberships.Add(new Membership { Id = Guid.NewGuid(), OrgId = target, UserId = otherUser, Role = "member", CreatedAt = DateTime.UtcNow });
        await check.SaveChangesAsync();

        // Billed overage refuses the erase (RefusedBillingEvidence, pinned in OrganisationEraseTests),
        // so — having proved it seeds — the guard clears that one table for the target.
        await check.OverageBillingRecords.Where(r => r.OrgId == target).ExecuteDeleteAsync();

        var storage = new Mock<IFileStorageService>();
        storage.Setup(x => x.ListKeysAsync($"{target}/documents/", It.IsAny<CancellationToken>())).ReturnsAsync(new List<string>());
        await using (var db = NewContext())
        {
            var receipt = await new OrganisationEraser(db, storage.Object, NullLogger<OrganisationEraser>.Instance)
                .EraseAsync(target, "test:guard", "guard", CancellationToken.None);
            receipt.Outcome.Should().Be(OrgEraseOutcome.Erased);
            receipt.RowsDeleted.Keys.Should().Contain(orgTables.Select(t => t.GetTableName()!));
            receipt.FailedBlobKeys.Should().BeEmpty();
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
        (await check.PlatformAuditEvents.SingleAsync(e => e.SubjectOrgId == target && e.Action == OrganisationEraser.AuditAction))
            .SubjectClerkOrgId.Should().Be($"org_a{target:N}", "the receipt survives, keyed so the workspace is not re-created");
        (await check.Users.AnyAsync(u => u.Id == targetUser)).Should().BeFalse("a member no organisation holds any more is erased");
        (await check.Users.AnyAsync(u => u.Id == otherUser)).Should().BeTrue("a member of another organisation stays");
        storage.Invocations.Where(i => i.Method.Name == nameof(IFileStorageService.DeleteAsync))
            .Select(i => (string)i.Arguments[0]).Should().BeEquivalentTo(targetFiles,
            "every stored file the target owns, children included, and nothing of the other org's");
        storage.Invocations.Where(i => i.Method.Name != nameof(IFileStorageService.DeleteAsync))
            .Select(i => (i.Method.Name, (string)i.Arguments[0])).Should()
            .Equal([(nameof(IFileStorageService.ListKeysAsync), $"{target}/documents/")], "the one other storage call is the documents listing");
    }

    [DockerRequiredFact]
    public async Task EraseOnRequest_ErasesAnEndedPaidWorkspace_WithItsBilledOverage_ThenItsSignInAccounts()
    {
        Guid orgId = Guid.NewGuid(), manualId = Guid.NewGuid();
        await using (var seed = NewContext())
        {
            Organisation Growth(Guid id, string? customer) => new()
            {
                Id = id, ClerkOrgId = $"org_test{id:N}", Name = "erase-on-request", Slug = $"erase-on-request-{id:N}", Plan = "growth",
                CreatedAt = DateTime.UtcNow, TrialStartedAt = DateTime.UtcNow, StripeCustomerId = customer,
            };
            seed.Organisations.AddRange(Growth(orgId, "cus_test"), Growth(manualId, null));
            seed.OverageBillingRecords.Add(new OverageBillingRecord
            {
                Id = Guid.NewGuid(), OrgId = orgId, BillingKey = "test", StripeInvoiceItemId = "ii_test", CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }
        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.HasRunningSubscriptionAsync("cus_test", null, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        billing.Setup(b => b.HasRunningSubscriptionAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync((bool?)null);
        var remover = new Mock<ISignInAccountRemover>();
        remover.Setup(r => r.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new SignInRemoval("done", 2, []));

        async Task<OrgEraseReceipt> Erase(Guid id)
        {
            await using var db = NewContext();
            var storage = Mock.Of<IFileStorageService>(s =>
                s.ListKeysAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()) == Task.FromResult<IReadOnlyList<string>>(new List<string>()));
            return await new OrganisationEraser(db, storage, NullLogger<OrganisationEraser>.Instance, billing.Object, remover.Object)
                .EraseOnRequestAsync(id, "test:request", "request", agreementEnded: true, CancellationToken.None);
        }

        (await Erase(orgId)).Outcome.Should().Be(OrgEraseOutcome.Erased);
        await using var check = NewContext();
        (await check.Organisations.AnyAsync(o => o.Id == orgId)).Should().BeFalse();
        (await check.OverageBillingRecords.AnyAsync(r => r.OrgId == orgId)).Should().BeFalse();
        remover.Verify(r => r.RemoveAsync($"org_test{orgId:N}", It.IsAny<CancellationToken>()), Times.Once());
        var stored = JsonDocument.Parse((await check.PlatformAuditEvents
            .SingleAsync(e => e.SubjectOrgId == orgId && e.Action == OrganisationEraser.AuditAction)).Payload!).RootElement;
        stored.GetProperty("signIn").GetString().Should().Be("done");
        stored.GetProperty("signInAccountsRemoved").GetInt32().Should().Be(2);

        // A manual contract carries no Stripe ids: the operator's word is the authority, Stripe is not asked.
        (await Erase(manualId)).Outcome.Should().Be(OrgEraseOutcome.Erased);
        billing.Verify(b => b.HasRunningSubscriptionAsync(null, null, It.IsAny<CancellationToken>()), Times.Never());
    }

    [DockerRequiredFact]
    public async Task Erase_AlsoDeletesStoredFilesNoRowNames()
    {
        async Task<(Guid Org, string Named, string Unnamed)> Seed()
        {
            await using var db = NewContext();
            Guid org = Guid.NewGuid(), flow = Guid.NewGuid(), doc = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.Organisations.Add(new Organisation
            {
                Id = org, ClerkOrgId = $"org_test{org:N}", Name = "erase-listing", Slug = $"erase-listing-{org:N}", CreatedAt = now, TrialStartedAt = now,
            });
            db.Flows.Add(new Flow { Id = flow, OrgId = org, Name = "Orders", CreatedAt = now, UpdatedAt = now });
            db.FlowDocuments.Add(new FlowDocument
            {
                Id = doc, OrgId = org, FlowId = flow, FileName = "synthetic.pdf", SourceFileKey = $"{org}/documents/{doc}/source.pdf",
                Sha256 = Guid.NewGuid().ToString("N"), Status = DocumentStatus.Sent, CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
            return (org, $"{org}/documents/{doc}/source.pdf", $"{org}/documents/{doc}/outputs/old.pdf");
        }
        var (listed, named, old) = await Seed();
        var (unlistable, _, _) = await Seed();
        var deleted = new List<string>();
        var storage = new Mock<IFileStorageService>();
        storage.Setup(x => x.ListKeysAsync($"{listed}/documents/", It.IsAny<CancellationToken>())).ReturnsAsync(new List<string> { named, old });
        storage.Setup(x => x.ListKeysAsync($"{unlistable}/documents/", It.IsAny<CancellationToken>())).ThrowsAsync(new NotSupportedException());
        storage.Setup(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((k, _) => deleted.Add(k)).Returns(Task.CompletedTask);
        async Task<OrgEraseReceipt> Erase(Guid id)
        {
            await using var db = NewContext();
            return await new OrganisationEraser(db, storage.Object, NullLogger<OrganisationEraser>.Instance)
                .EraseAsync(id, "test", "listing", CancellationToken.None);
        }

        var first = await Erase(listed);
        (first.Outcome, first.BlobsDeleted, first.FailedBlobKeys.Count).Should().Be((OrgEraseOutcome.Erased, 2, 0));
        deleted.Where(k => k.StartsWith($"{listed}/")).Should().BeEquivalentTo([named, old], "each key once, listed or named");

        // A folder that cannot be listed does not stop the erase: it is named as what may remain.
        var second = await Erase(unlistable);
        second.Outcome.Should().Be(OrgEraseOutcome.Erased);
        second.FailedBlobKeys.Should().Equal($"{unlistable}/documents/");
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
    /// Every filled string starts with <paramref name="tag"/>. Returns the stored-file keys it wrote.
    /// </summary>
    private async Task<HashSet<string>> SeedEverythingAsync(Guid orgId, char tag)
    {
        await using var db = NewContext();
        var fileKeys = new HashSet<string>();
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
                if (p.ClrType == typeof(string) && p.Name.EndsWith("Key") && !NotFileKeys.Contains(p.Name) && value is string { Length: > 0 } key)
                    fileKeys.Add(key);
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
        return fileKeys;
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
