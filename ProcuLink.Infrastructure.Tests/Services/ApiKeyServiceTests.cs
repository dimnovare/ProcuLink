using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services;

public class ApiKeyServiceTests
{
    private static ProcuLinkDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ProcuLinkDbContext(opts);
    }

    [Fact]
    public async Task CreateAsync_ReturnsRawKeyAndEntityWithPrefix()
    {
        var db  = MakeDb();
        var svc = new ApiKeyService(db);
        var orgId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = "c_test", Name = "Test", Slug = "test-0001"
        });
        await db.SaveChangesAsync();

        var (entity, rawKey) = await svc.CreateAsync(orgId, "Zapier prod", null, default);

        rawKey.Should().StartWith("plk_");
        entity.KeyPrefix.Should().Be(rawKey[..8]);
        entity.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task RevokeAsync_SetsIsActiveFalse()
    {
        var db  = MakeDb();
        var svc = new ApiKeyService(db);
        var orgId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = "c2", Name = "T2", Slug = "t2-0002"
        });
        await db.SaveChangesAsync();

        var (entity, _) = await svc.CreateAsync(orgId, "temp", null, default);
        var ok = await svc.RevokeAsync(orgId, entity.Id, default);

        ok.Should().BeTrue();
        var loaded = await db.TenantApiKeys.FindAsync(entity.Id);
        loaded!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task RevokeAsync_WrongOrg_ReturnsFalse()
    {
        var db = MakeDb();
        var svc = new ApiKeyService(db);
        var orgId1 = Guid.NewGuid();
        var orgId2 = Guid.NewGuid();
        db.Organisations.Add(new Organisation { Id = orgId1, ClerkOrgId = "a1", Name = "A1", Slug = "a1-aaaa" });
        db.Organisations.Add(new Organisation { Id = orgId2, ClerkOrgId = "b2", Name = "B2", Slug = "b2-bbbb" });
        await db.SaveChangesAsync();

        var (entity, _) = await svc.CreateAsync(orgId1, "key", null, default);
        var ok = await svc.RevokeAsync(orgId2, entity.Id, default);

        ok.Should().BeFalse();
        var loaded = await db.TenantApiKeys.FindAsync(entity.Id);
        loaded!.IsActive.Should().BeTrue();
    }
}
