using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Catalog;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Catalog;

namespace ProcuLink.Infrastructure.Tests.Services.Catalog;

/// <summary>
/// Task 4 — catalog source credentials (file-channel password, http/vendor auth-config envelope)
/// bound to <c>SupplierCatalogSource.Id</c>. A catalog source is never snapshotted anywhere, so the
/// row id is the tightest available binding (unlike delivery credentials, which must scope on
/// supplier id because a pinned revision holds a verbatim byte-copy — see
/// <see cref="CredentialScope"/>'s doc comment).
/// </summary>
public class CatalogCredentialBindingTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SourceOne = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid SourceTwo = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid SupplierId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private static DeliveryEncryptionService Encryption()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();
        return new DeliveryEncryptionService(config);
    }

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase($"catalog-cred-binding-{Guid.NewGuid()}")
            .Options);

    [Fact]
    public void CatalogPassword_DoesNotDecryptForAnotherSource()
    {
        var enc = Encryption();
        var blob = enc.Encrypt("sftp-password", CredentialScope.ForSupplier(
            OrgA, CredentialPurpose.SupplierCatalogPassword, SourceOne));

        var act = () => enc.Decrypt(blob, CredentialScope.ForSupplier(
            OrgA, CredentialPurpose.SupplierCatalogPassword, SourceTwo));

        act.Should().Throw<CredentialUnbindableException>();
    }

    [Fact]
    public void CatalogPassword_DoesNotDecryptAsAuthConfig()
    {
        var enc = Encryption();
        var blob = enc.Encrypt("sftp-password", CredentialScope.ForSupplier(
            OrgA, CredentialPurpose.SupplierCatalogPassword, SourceOne));

        var act = () => enc.Decrypt(blob, CredentialScope.ForSupplier(
            OrgA, CredentialPurpose.SupplierCatalogAuthConfig, SourceOne));

        act.Should().Throw<CredentialUnbindableException>();
    }

    // Writes through the REAL production write path, then decrypts with the exact tuple
    // CatalogPullService.cs:312 constructs. A wrong purpose or a wrong scope id on either side
    // shows up here as an unreadable credential rather than as a broken catalog sync in production.
    [Fact]
    public async Task Upsert_ThenDecryptWithTheReadSideScope_RoundTrips()
    {
        await using var db = NewDb();
        db.Suppliers.Add(new Supplier
        {
            Id = SupplierId, OrgId = OrgA, Name = "Catalog supplier", CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var enc = Encryption();
        var service = new CatalogSourceSettingsService(db, enc, new Mock<IBackgroundJobClient>().Object);

        await service.UpsertAsync(OrgA, SupplierId, new UpsertCatalogSourceRequest(
            Protocol: "sftp",
            Host: "files.example.com",
            Port: 22,
            Username: "catalog",
            Password: "catalog-password",
            RemotePath: "/exports/catalog.csv",
            FileFormat: "auto",
            SyncIntervalHours: 24,
            IsEnabled: true), CancellationToken.None);

        var source = await db.SupplierCatalogSources.AsNoTracking()
            .SingleAsync(s => s.OrgId == OrgA && s.SupplierId == SupplierId);

        enc.Decrypt(source.EncryptedPassword!, CredentialScope.ForSupplier(
            source.OrgId, CredentialPurpose.SupplierCatalogPassword, source.Id))
            .Should().Be("catalog-password");
    }

    // The positive round-trip above cannot, by itself, prove the write side binds anything: a
    // legacy version-1 envelope (what CatalogSourceSettingsService wrote before this task) carries
    // no associated data and decrypts successfully under ANY scope, so "does the plaintext come
    // back" passes whether or not source.Id is actually bound in. This test supplies the missing
    // negative case — same stored ciphertext, decrypted under a DIFFERENT source id — which is
    // red before Step 3 (legacy blob, scope-blind, no throw) and green after (bound blob, wrong
    // AAD, throws).
    [Fact]
    public async Task Upsert_ThenDecryptUnderAWrongSourceId_Throws()
    {
        await using var db = NewDb();
        db.Suppliers.Add(new Supplier
        {
            Id = SupplierId, OrgId = OrgA, Name = "Catalog supplier", CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var enc = Encryption();
        var service = new CatalogSourceSettingsService(db, enc, new Mock<IBackgroundJobClient>().Object);

        await service.UpsertAsync(OrgA, SupplierId, new UpsertCatalogSourceRequest(
            Protocol: "sftp",
            Host: "files.example.com",
            Port: 22,
            Username: "catalog",
            Password: "catalog-password",
            RemotePath: "/exports/catalog.csv",
            FileFormat: "auto",
            SyncIntervalHours: 24,
            IsEnabled: true), CancellationToken.None);

        var source = await db.SupplierCatalogSources.AsNoTracking()
            .SingleAsync(s => s.OrgId == OrgA && s.SupplierId == SupplierId);

        var wrongSourceId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var act = () => enc.Decrypt(source.EncryptedPassword!, CredentialScope.ForSupplier(
            source.OrgId, CredentialPurpose.SupplierCatalogPassword, wrongSourceId));

        act.Should().Throw<CredentialUnbindableException>();
    }
}
