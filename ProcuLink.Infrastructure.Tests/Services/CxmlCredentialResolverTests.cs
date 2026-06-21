using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// The transform-side read path: <see cref="CxmlCredentialResolver"/> loads a supplier's saved
/// cXML credentials (decrypting the Sender SharedSecret) so the cXML transform can stamp them into
/// the Header. Seeds via the real <see cref="DeliveryConfigService"/> so the write→read round-trip
/// is exercised end-to-end.
/// </summary>
public class CxmlCredentialResolverTests
{
    private static ProcuLinkDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DeliveryEncryptionService CreateEncryption()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();
        return new DeliveryEncryptionService(config);
    }

    private static UpsertDeliveryConfigRequest CxmlReq(CxmlCredentialsInput cxml) =>
        new("http", false, "{\"url\":\"https://a.example\"}", null, "cxml", cxml);

    [Fact]
    public async Task Resolve_ReturnsDecryptedConfig_WhenCxmlConfigured()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await new DeliveryConfigService(db, encryption).UpsertAsync(orgId, supplierId, CxmlReq(
            new CxmlCredentialsInput(
                "NetworkId", "REDACTED-NETWORK-ID", "NetworkId", "REDACTED-NETWORK-ID", "NetworkId", "REDACTED-NETWORK-ID", "shared-secret")),
            default);

        var resolved = await new CxmlCredentialResolver(db, encryption).ResolveAsync(orgId, supplierId, default);

        resolved.Should().NotBeNull();
        resolved!.FromDomain.Should().Be("NetworkId");
        resolved.FromIdentity.Should().Be("REDACTED-NETWORK-ID");
        resolved.ToIdentity.Should().Be("REDACTED-NETWORK-ID");
        resolved.SenderIdentity.Should().Be("REDACTED-NETWORK-ID");
        resolved.SenderSharedSecret.Should().Be("shared-secret"); // decrypted
    }

    [Fact]
    public async Task Resolve_ReturnsNull_WhenNoDeliveryConfigRow()
    {
        await using var db = CreateDb();
        var resolved = await new CxmlCredentialResolver(db, CreateEncryption())
            .ResolveAsync(Guid.NewGuid(), Guid.NewGuid(), default);
        resolved.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_ReturnsNull_WhenRowHasNoCxmlCredentials()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        // A plain HTTP delivery config with NO cXML block.
        await new DeliveryConfigService(db, encryption).UpsertAsync(orgId, supplierId,
            new UpsertDeliveryConfigRequest("http", false, "{\"url\":\"https://a.example\"}", null, "xml"),
            default);

        var resolved = await new CxmlCredentialResolver(db, encryption).ResolveAsync(orgId, supplierId, default);
        resolved.Should().BeNull("no cXML identities or secret were configured → legacy GUID identities");
    }

    [Fact]
    public async Task Resolve_IsOrgScoped()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var supplierId = Guid.NewGuid();

        await new DeliveryConfigService(db, encryption).UpsertAsync(Guid.NewGuid(), supplierId, CxmlReq(
            new CxmlCredentialsInput("NetworkId", "REDACTED-NETWORK-ID", null, null, null, null, null)), default);

        var resolved = await new CxmlCredentialResolver(db, encryption)
            .ResolveAsync(Guid.NewGuid(), supplierId, default); // different org
        resolved.Should().BeNull();
    }

    // ── Configurable cXML DOCTYPE (T7) — write→read round-trip ─────────────────

    [Fact]
    public async Task Resolve_CarriesDtd_WhenConfigured()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await new DeliveryConfigService(db, encryption).UpsertAsync(orgId, supplierId, CxmlReq(
            new CxmlCredentialsInput(
                "NetworkId", "REDACTED-NETWORK-ID", "NetworkId", "REDACTED-NETWORK-ID", "NetworkId", "REDACTED-NETWORK-ID",
                SenderSharedSecret: null,
                DtdSystemId: "http://xml.cxml.org/schemas/cXML/1.2.024/cXML.dtd",
                DtdPublicId: "-//cXML//DTD cXML 1.2.024//EN")),
            default);

        var resolved = await new CxmlCredentialResolver(db, encryption).ResolveAsync(orgId, supplierId, default);

        resolved.Should().NotBeNull();
        resolved!.DtdSystemId.Should().Be("http://xml.cxml.org/schemas/cXML/1.2.024/cXML.dtd");
        resolved.DtdPublicId.Should().Be("-//cXML//DTD cXML 1.2.024//EN");
    }

    [Fact]
    public async Task Resolve_CarriesDtd_EvenWhenNoNetworkIdentityOrSecret()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        // A supplier that ONLY needs a DOCTYPE — no network identities, no secret.
        await new DeliveryConfigService(db, encryption).UpsertAsync(orgId, supplierId, CxmlReq(
            new CxmlCredentialsInput(
                null, null, null, null, null, null,
                SenderSharedSecret: null,
                DtdSystemId: "http://example.test/cXML.dtd")),
            default);

        var resolved = await new CxmlCredentialResolver(db, encryption).ResolveAsync(orgId, supplierId, default);

        resolved.Should().NotBeNull("a configured DTD alone is enough to need a non-null config");
        resolved!.DtdSystemId.Should().Be("http://example.test/cXML.dtd");
    }

    [Fact]
    public async Task PersistedDtd_UsesCamelCaseJsonKeys_TheFrontendWrites()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await new DeliveryConfigService(db, encryption).UpsertAsync(orgId, supplierId, CxmlReq(
            new CxmlCredentialsInput(
                "NetworkId", "REDACTED-NETWORK-ID", null, null, null, null,
                SenderSharedSecret: null,
                DtdSystemId: "http://example.test/cXML.dtd",
                DtdPublicId: "-//X//Y//EN")),
            default);

        var row = await db.SupplierDeliveryConfigs
            .AsNoTracking()
            .FirstAsync(x => x.OrgId == orgId && x.SupplierId == supplierId);

        // The FRONTEND writes camelCase dtdSystemId / dtdPublicId — pin those exact keys.
        row.CxmlConfigJson.Should().Contain("\"dtdSystemId\":\"http://example.test/cXML.dtd\"");
        row.CxmlConfigJson.Should().Contain("\"dtdPublicId\":\"-//X//Y//EN\"");
    }

    [Fact]
    public async Task Resolve_NoDtd_LeavesDtdNull()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await new DeliveryConfigService(db, encryption).UpsertAsync(orgId, supplierId, CxmlReq(
            new CxmlCredentialsInput("NetworkId", "REDACTED-NETWORK-ID", null, null, null, null, null)), default);

        var resolved = await new CxmlCredentialResolver(db, encryption).ResolveAsync(orgId, supplierId, default);

        resolved.Should().NotBeNull();
        resolved!.DtdSystemId.Should().BeNull();
        resolved.DtdPublicId.Should().BeNull();
    }
}
