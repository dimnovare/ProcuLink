using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// The second caller-supplied value in the same request body: <c>credentialsRef</c>.
///
/// <para><b>What it is.</b> <see cref="SupplierConnectionRevision.CredentialsRef"/> is the AES-GCM
/// ciphertext of a supplier's delivery credentials, copied verbatim onto the detached config the
/// dispatchers use (<c>DeliveryService</c> sets <c>EncryptedCredentials = effective.CredentialsRef</c>)
/// and decrypted there to authenticate the outbound request.</para>
///
/// <para><b>Why accepting it from a request body was wrong.</b>
/// <c>DeliveryEncryptionService.Encrypt</c> calls <c>AesGcm.Encrypt(nonce, plaintext, ciphertext,
/// tag)</c> — the overload with NO associated data — so a blob is bound to the deployment key and to
/// nothing else: not to an org, not to a supplier. Any ciphertext that decrypts under the key
/// decrypts for everyone. So a tenant holding another tenant's blob could paste it into their own
/// draft revision, publish, and authenticate outbound AS the victim without ever learning the
/// plaintext. Read-back masking (the revision DTO returns <c>HasCredentials</c>, a bool, never the
/// ref) means no client can obtain a blob through this API today, which is why this needed a second
/// flaw to fire — and also why refusing the field removes no capability any legitimate client has.
/// A caller cannot read the value back, so it can only ever be echoing one it got elsewhere.</para>
///
/// <para><b>Why refusing, not ignoring.</b> Silently dropping a field a caller sent hides the
/// integration bug that made them send it. The supported route to put credentials on a revision is
/// unchanged and is exercised below: save them on the supplier delivery config, where the server
/// encrypts them, then let clone-from-active / rollback / republish-from-live carry the ciphertext
/// forward internally. Those three are direct entity copies that never touch this input, so all
/// three are pinned here as still working.</para>
///
/// <para><b>Not fixed here.</b> The missing AAD binding itself. Adding org+supplier as associated
/// data would make a stolen blob cryptographically useless rather than merely unreachable, but it is
/// a ciphertext FORMAT change: every existing credential was encrypted without associated data and
/// would stop decrypting, so it needs the version byte at <c>DeliveryEncryptionService</c> and a
/// dual-read migration. That is its own packet.</para>
/// </summary>
public class ConnectionRevisionCredentialsRefTests
{
    private const string SecureUrlJson  = """{"url":"https://supplier.example/orders"}""";
    private const string VictimCiphertext = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=";

    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static SupplierConnectionService MakeSvc(ProcuLinkDbContext db, bool revisionAuthority = false) =>
        new(db,
            new ReplayService(db, Array.Empty<ITransformService>()),
            new ProcuLink.Transform.Conformance.ConformanceService(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [EffectiveConnectionConfigResolver.FlagKey] = revisionAuthority ? "true" : "false",
                })
                .Build());

    private static ConnectionRevisionDraftInput Bundle(string? credentialsRef = null, string format = "xml") =>
        new(
            InputMappingJson: "{}",
            OutputMappingJson: null,
            OutputFormat: format,
            DeliveryProtocol: DeliveryProtocolConstants.Http,
            DeliveryConfigJson: SecureUrlJson,
            DeliveryAutoDeliver: false,
            CredentialsRef: credentialsRef,
            AcceptanceProfileId: null,
            AcceptanceVersionNo: null,
            CatalogMode: "live",
            ItemMappings: new List<ConnectionItemMappingInput>());

    private static async Task<(Guid OrgId, Guid SupplierId, SupplierConnection Connection)> SeedAsync(
        ProcuLinkDbContext db, SupplierConnectionService svc)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme OÜ", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var connection = await svc.EnsureConnectionAsync(orgId, supplierId, "user", CancellationToken.None);
        return (orgId, supplierId, connection!);
    }

    /// <summary>A revision written straight to the database — the internal copy paths' starting state.</summary>
    private static async Task<SupplierConnectionRevision> SeedRevisionAsync(
        ProcuLinkDbContext db, SupplierConnection connection, string status, string? credentialsRef,
        int versionNo = 1, DateTime? publishedAt = null)
    {
        var rev = new SupplierConnectionRevision
        {
            Id                 = Guid.NewGuid(),
            ConnectionId       = connection.Id,
            OrgId              = connection.OrgId,
            SupplierId         = connection.SupplierId,
            VersionNo          = versionNo,
            Status             = status,
            CreatedAt          = DateTime.UtcNow.AddDays(-3),
            PublishedAt        = publishedAt,
            EffectiveFrom      = publishedAt,
            CatalogMode        = "live",
            OutputFormat       = "xml",
            DeliveryProtocol   = DeliveryProtocolConstants.Http,
            DeliveryConfigJson = SecureUrlJson,
            CredentialsRef     = credentialsRef,
        };
        db.SupplierConnectionRevisions.Add(rev);
        await db.SaveChangesAsync();
        return rev;
    }

    // ── Refusal ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateDraft_WithACallerSuppliedCredentialsRef_IsRefusedAndPersistsNothing()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);

        var act = () => svc.CreateDraftAsync(
            orgId, conn.Id, Bundle(VictimCiphertext), cloneFromActive: false, "user", CancellationToken.None);

        await act.Should().ThrowAsync<ClientSuppliedCredentialsRefException>();
        db.SupplierConnectionRevisions.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateDraft_WithACallerSuppliedCredentialsRef_IsRefusedAndLeavesTheStoredReference()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);
        var draft = await SeedRevisionAsync(db, conn, "draft", "the-orgs-own-blob");

        var act = () => svc.UpdateDraftAsync(
            orgId, conn.Id, draft.Id, Bundle(VictimCiphertext), CancellationToken.None);

        await act.Should().ThrowAsync<ClientSuppliedCredentialsRefException>();

        var stored = await db.SupplierConnectionRevisions.AsNoTracking().FirstAsync(r => r.Id == draft.Id);
        stored.CredentialsRef.Should().Be(
            "the-orgs-own-blob", "a refused write must not swap the credential it was refused for");
    }

    /// <summary>
    /// The message has to tell an operator what to do instead, and must not echo the blob back —
    /// a refusal that quotes the value copies it into the log and the screen.
    /// </summary>
    [Fact]
    public async Task TheRefusal_ExplainsTheSupportedRoute_AndNeverEchoesTheBlob()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);

        var act = () => svc.CreateDraftAsync(
            orgId, conn.Id, Bundle(VictimCiphertext), cloneFromActive: false, "user", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<ClientSuppliedCredentialsRefException>();
        thrown.And.PolicyMessage.Should().NotBeNullOrWhiteSpace();
        thrown.And.PolicyMessage.Should().NotContain(VictimCiphertext);
        thrown.And.PolicyMessage.Should().Contain("delivery");
    }

    // ── The half a refuse-everything rule would break ─────────────────────────

    [Fact]
    public async Task CreateDraft_WithNoCredentialsRef_Saves()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);

        var draft = await svc.CreateDraftAsync(
            orgId, conn.Id, Bundle(credentialsRef: null), cloneFromActive: false, "user", CancellationToken.None);

        draft.Should().NotBeNull();
        draft!.CredentialsRef.Should().BeNull();
    }

    // The other half of the null contract — omitted must mean "no change", not "wipe" — is the
    // pre-existing Fix A data-loss guard, and it stays where it has always lived:
    // SupplierConnectionServiceTests.UpdateDraft_OmittedCredentialsRef_KeepsExistingReference.

    // ── Internal copy paths keep carrying the ciphertext forward ──────────────

    [Fact]
    public async Task CreateDraft_CloneFromActive_StillCarriesTheStoredCredentialReference()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);
        var active = await SeedRevisionAsync(
            db, conn, "published", "cred-from-live", publishedAt: DateTime.UtcNow.AddDays(-2));
        conn.ActiveRevisionId = active.Id;
        await db.SaveChangesAsync();

        var clone = await svc.CreateDraftAsync(
            orgId, conn.Id, input: null, cloneFromActive: true, "user", CancellationToken.None);

        clone!.CredentialsRef.Should().Be("cred-from-live",
            "this is how a revision legitimately gets credentials, and it must not be collateral damage");
    }

    [Fact]
    public async Task Rollback_StillCarriesTheStoredCredentialReference()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);
        var archived = await SeedRevisionAsync(
            db, conn, "archived", "cred-v1", publishedAt: DateTime.UtcNow.AddDays(-2));
        var live = await SeedRevisionAsync(
            db, conn, "published", "cred-v2", versionNo: 2, publishedAt: DateTime.UtcNow.AddDays(-1));
        conn.ActiveRevisionId = live.Id;
        await db.SaveChangesAsync();

        var outcome = await svc.RollbackAsync(orgId, conn.Id, archived.Id, "user", CancellationToken.None);

        outcome.Status.Should().Be(ConnectionRollbackStatus.Completed);
        outcome.NewRevision!.CredentialsRef.Should().Be("cred-v1");
    }

    [Fact]
    public async Task RepublishLiveDelivery_StillCarriesTheLiveEncryptedCredentials()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db, revisionAuthority: true);
        var (orgId, supplierId, conn) = await SeedAsync(db, svc);
        var active = await SeedRevisionAsync(
            db, conn, "published", null, publishedAt: DateTime.UtcNow.AddDays(-1));
        conn.ActiveRevisionId = active.Id;
        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id                   = Guid.NewGuid(),
            OrgId                = orgId,
            SupplierId           = supplierId,
            Protocol             = DeliveryProtocolConstants.Http,
            ConfigJson           = SecureUrlJson,
            EncryptedCredentials = "cred-encrypted-server-side",
            CreatedAt            = DateTime.UtcNow.AddDays(-5),
            UpdatedAt            = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var outcome = await svc.RepublishLiveDeliveryAsync(orgId, supplierId, "api", CancellationToken.None);

        outcome.Status.Should().Be(DeliveryRepublishStatus.Republished);
        var published = await db.SupplierConnectionRevisions.AsNoTracking()
            .FirstAsync(r => r.ConnectionId == conn.Id && r.Status == "published");
        published.CredentialsRef.Should().Be("cred-encrypted-server-side",
            "the server-encrypted live credential is the supported way onto a revision");
    }
}
