using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Launch batch 7 — <see cref="EffectiveConnectionConfigResolver"/> contract tests. The resolver
/// is the single gate for revision authority: flag ON + resolvable pin → the revision's immutable
/// bundle; EVERY other case (flag off, key absent, unpinned, orphan pin, cross-org pin) → the
/// <see cref="EffectiveConnectionConfig.Live"/> marker, never a throw.
/// </summary>
public class RevisionAuthorityResolverTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static EffectiveConnectionConfigResolver Resolver(ProcuLinkDbContext db, bool? enabled)
    {
        var values = new Dictionary<string, string?>();
        if (enabled is not null)
            values[EffectiveConnectionConfigResolver.FlagKey] = enabled.Value ? "true" : "false";
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new EffectiveConnectionConfigResolver(db, config);
    }

    private static async Task<SupplierConnectionRevision> SeedPublishedRevisionAsync(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId)
    {
        var now = DateTime.UtcNow;
        var connectionId = Guid.NewGuid();
        var revision = new SupplierConnectionRevision
        {
            Id           = Guid.NewGuid(),
            ConnectionId = connectionId,
            OrgId        = orgId,
            SupplierId   = supplierId,
            VersionNo    = 1,
            Status       = "published",
            CreatedAt    = now,
            PublishedAt  = now,
            InputMappingJson    = "{\"header\":{}}",
            OutputMappingJson   = "{\"lines\":{}}",
            OutputFormat        = "json",
            DeliveryProtocol    = "http",
            DeliveryConfigJson  = "{\"url\":\"https://rev.example\"}",
            DeliveryAutoDeliver = true,
            CredentialsRef      = "enc:payload",
            AcceptanceProfileId = Guid.NewGuid(),
            AcceptanceVersionNo = 3,
            ItemMappings =
            {
                new ConnectionRevisionItemMapping
                {
                    Id = Guid.NewGuid(), BuyerItemCode = "B-1", SupplierItemCode = "S-1",
                    Confidence = 1f, Source = "manual",
                },
            },
        };
        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = connectionId, OrgId = orgId, SupplierId = supplierId, Name = "conn",
            ActiveRevisionId = revision.Id, CreatedAt = now, UpdatedAt = now,
        });
        db.SupplierConnectionRevisions.Add(revision);
        await db.SaveChangesAsync();
        return revision;
    }

    [Fact]
    public async Task FlagOff_PinnedOrder_ReturnsLive()
    {
        await using var db = NewDb();
        var rev = await SeedPublishedRevisionAsync(db, Guid.NewGuid(), Guid.NewGuid());

        var effective = await Resolver(db, enabled: false)
            .ResolveAsync(rev.OrgId, rev.Id, CancellationToken.None);

        Assert.False(effective.IsRevision);
        Assert.Equal("live", effective.Source);
        Assert.Same(EffectiveConnectionConfig.Live, effective);
    }

    [Fact]
    public async Task FlagKeyAbsent_DefaultsOff_ReturnsLive()
    {
        await using var db = NewDb();
        var rev = await SeedPublishedRevisionAsync(db, Guid.NewGuid(), Guid.NewGuid());

        // No Connections:RevisionAuthority key at all — the safe production default.
        var effective = await Resolver(db, enabled: null)
            .ResolveAsync(rev.OrgId, rev.Id, CancellationToken.None);

        Assert.False(effective.IsRevision);
    }

    [Fact]
    public async Task FlagOn_UnpinnedOrder_ReturnsLive()
    {
        await using var db = NewDb();
        await SeedPublishedRevisionAsync(db, Guid.NewGuid(), Guid.NewGuid());

        var effective = await Resolver(db, enabled: true)
            .ResolveAsync(Guid.NewGuid(), connectionRevisionId: null, CancellationToken.None);

        Assert.False(effective.IsRevision);
    }

    [Fact]
    public async Task FlagOn_OrphanPin_ReturnsLive_NoThrow()
    {
        await using var db = NewDb();

        var effective = await Resolver(db, enabled: true)
            .ResolveAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.False(effective.IsRevision);
    }

    [Fact]
    public async Task FlagOn_CrossOrgPin_ReturnsLive()
    {
        await using var db = NewDb();
        var rev = await SeedPublishedRevisionAsync(db, orgId: Guid.NewGuid(), supplierId: Guid.NewGuid());

        // Another org asking for org B's revision id must MISS (org-scoped) → live.
        var effective = await Resolver(db, enabled: true)
            .ResolveAsync(Guid.NewGuid(), rev.Id, CancellationToken.None);

        Assert.False(effective.IsRevision);
    }

    [Fact]
    public async Task FlagOn_ValidPin_ReturnsFullRevisionBundle()
    {
        await using var db = NewDb();
        var rev = await SeedPublishedRevisionAsync(db, Guid.NewGuid(), Guid.NewGuid());

        var effective = await Resolver(db, enabled: true)
            .ResolveAsync(rev.OrgId, rev.Id, CancellationToken.None);

        Assert.True(effective.IsRevision);
        Assert.Equal(rev.Id, effective.RevisionId);
        Assert.Equal($"revision:{rev.Id}", effective.Source);
        Assert.Equal(rev.InputMappingJson,    effective.InputMappingJson);
        Assert.Equal(rev.OutputMappingJson,   effective.OutputMappingJson);
        Assert.Equal(rev.OutputFormat,        effective.OutputFormat);
        Assert.Equal(rev.DeliveryProtocol,    effective.DeliveryProtocol);
        Assert.Equal(rev.DeliveryConfigJson,  effective.DeliveryConfigJson);
        Assert.Equal(rev.DeliveryAutoDeliver, effective.DeliveryAutoDeliver);
        Assert.Equal(rev.CredentialsRef,      effective.CredentialsRef);
        Assert.Equal(rev.AcceptanceProfileId, effective.AcceptanceProfileId);
        Assert.Equal(rev.AcceptanceVersionNo, effective.AcceptanceVersionNo);
        var mapping = Assert.Single(effective.ItemMappings);
        Assert.Equal("B-1", mapping.BuyerItemCode);
        Assert.Equal("S-1", mapping.SupplierItemCode);
    }
}
