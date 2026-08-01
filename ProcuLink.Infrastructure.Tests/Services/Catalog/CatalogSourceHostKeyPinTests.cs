using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Catalog;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Catalog;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services.Catalog;

/// <summary>
/// The lifecycle of a catalog source's SSH host-key pin: what a save keeps, what clears it, and
/// what invalidates it (WP-38).
///
/// <para>
/// The pin only protects anything if it survives an ordinary save, and only stays usable if there
/// is a way to clear it. Both halves are settings-service behaviour rather than transport
/// behaviour, and neither is visible to the pull tests.
/// </para>
/// </summary>
public class CatalogSourceHostKeyPinTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DeliveryEncryptionService Encryption() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build());

    private sealed record Fixture(
        ProcuLinkDbContext Db, CatalogSourceSettingsService Service, Guid OrgId, Guid SupplierId);

    private static async Task<Fixture> BuildAsync()
    {
        var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.Suppliers.Add(new Supplier
        {
            Id = supplierId, OrgId = orgId, Name = "Catalog supplier", CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var service = new CatalogSourceSettingsService(db, Encryption(), new Mock<IBackgroundJobClient>().Object);
        return new Fixture(db, service, orgId, supplierId);
    }

    private static UpsertCatalogSourceRequest Request(
        string protocol = "sftp",
        string host = "files.example.com",
        int port = 22,
        string? hostKeyFingerprints = null,
        string remotePath = "/exports/catalog.csv") =>
        new(protocol, host, port, "catalog", "secret", remotePath, "auto", 24, IsEnabled: false,
            HostKeyFingerprints: hostKeyFingerprints);

    private static Task<SupplierCatalogSource> SourceAsync(Fixture f) =>
        f.Db.SupplierCatalogSources.SingleAsync(s => s.OrgId == f.OrgId && s.SupplierId == f.SupplierId);

    [Fact]
    public async Task NullFingerprints_KeepsWhatTheLastSyncRecorded()
    {
        var f = await BuildAsync();
        await f.Service.UpsertAsync(f.OrgId, f.SupplierId, Request(hostKeyFingerprints: "SHA256:pinned"), default);

        await f.Service.UpsertAsync(f.OrgId, f.SupplierId, Request(), default);

        (await SourceAsync(f)).HostKeyFingerprints.Should().Be("SHA256:pinned",
            "a client that has never heard of host keys must not silently un-pin the source");
    }

    [Fact]
    public async Task EmptyFingerprints_ClearsThePin_TheReTrustPath()
    {
        var f = await BuildAsync();
        await f.Service.UpsertAsync(f.OrgId, f.SupplierId, Request(hostKeyFingerprints: "SHA256:pinned"), default);

        await f.Service.UpsertAsync(f.OrgId, f.SupplierId, Request(hostKeyFingerprints: ""), default);

        (await SourceAsync(f)).HostKeyFingerprints.Should().BeNull();
    }

    /// <summary>
    /// A pin names a SERVER. Repointing the source at a different host leaves a fingerprint that
    /// describes something it no longer talks to, and every sync would then be refused with a
    /// message about a rebuild that never happened.
    /// </summary>
    [Fact]
    public async Task ChangingTheHost_DropsThePin()
    {
        var f = await BuildAsync();
        await f.Service.UpsertAsync(f.OrgId, f.SupplierId, Request(hostKeyFingerprints: "SHA256:pinned"), default);

        await f.Service.UpsertAsync(f.OrgId, f.SupplierId, Request(host: "files.somewhere-else.com"), default);

        (await SourceAsync(f)).HostKeyFingerprints.Should().BeNull();
    }

    [Fact]
    public async Task ChangingThePort_DropsThePin()
    {
        var f = await BuildAsync();
        await f.Service.UpsertAsync(f.OrgId, f.SupplierId, Request(hostKeyFingerprints: "SHA256:pinned"), default);

        await f.Service.UpsertAsync(f.OrgId, f.SupplierId, Request(port: 2223), default);

        (await SourceAsync(f)).HostKeyFingerprints.Should().BeNull();
    }

    [Fact]
    public async Task SwitchingOffSftp_DropsThePin()
    {
        // ftp/ftps authenticate the server with TLS, not a host key; http/https and the vendor
        // fetchers have no SSH at all. A leftover pin would describe a protocol this source no
        // longer speaks.
        var f = await BuildAsync();
        await f.Service.UpsertAsync(f.OrgId, f.SupplierId, Request(hostKeyFingerprints: "SHA256:pinned"), default);

        await f.Service.UpsertAsync(f.OrgId, f.SupplierId, Request(protocol: "ftps", port: 21), default);

        (await SourceAsync(f)).HostKeyFingerprints.Should().BeNull();
    }

    [Fact]
    public async Task EditingSomethingElse_KeepsThePin()
    {
        // The other half of the rule: only a change of SERVER drops the pin. Re-saving the same host
        // and port — the ordinary case — must leave it alone, or the feature un-pins itself on every
        // settings save.
        var f = await BuildAsync();
        await f.Service.UpsertAsync(f.OrgId, f.SupplierId, Request(hostKeyFingerprints: "SHA256:pinned"), default);

        await f.Service.UpsertAsync(f.OrgId, f.SupplierId, Request(remotePath: "/exports/other.csv"), default);

        (await SourceAsync(f)).HostKeyFingerprints.Should().Be("SHA256:pinned");
    }

    [Fact]
    public async Task TheResponseReturnsTheFingerprintInFull()
    {
        // Not masked like the password: an operator cannot judge a changed host key without seeing
        // the value it is being compared against.
        var f = await BuildAsync();

        var result = await f.Service.UpsertAsync(
            f.OrgId, f.SupplierId, Request(hostKeyFingerprints: "SHA256:pinned"), default);

        result.Source.HostKeyFingerprints.Should().Be("SHA256:pinned");
    }
}
