using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Transport security at the connection-revision WRITE path — the second way to choose where a
/// purchase order and its credentials are sent.
///
/// <para><b>The gap this closes.</b> The delivery-config endpoint refuses a cleartext or
/// credential-bearing endpoint at save time. The revision path did not look at the URL at all:
/// <c>ApplyScalars</c> assigned <c>rev.DeliveryConfigJson</c> straight from the request body of
/// <c>POST|PUT /api/connections/{id}/revisions…</c>, and a published revision is what a pinned
/// order actually delivers through. So <c>http://supplier.example/orders</c> — or
/// <c>https://user:pass@supplier.example/orders</c> — could be saved on a revision and shipped
/// from, routing around the refusal on the sibling endpoint.</para>
///
/// <para><b>One policy, not two.</b> These assertions deliberately compare against
/// <see cref="OutboundUrlPolicy"/>'s own verdict rather than against literal strings, so a second
/// hand-rolled scheme/userinfo check on this path would fail here instead of quietly diverging.
/// Two copies of a security rule is how the original defect existed.</para>
///
/// <para><b>Both directions.</b> A rule that refused every URL would pass a refusal-only suite, so
/// every refusal is paired with the save it must not break — including plain http on loopback,
/// which the shared policy allows by host on purpose.</para>
///
/// <para><b>Pre-existing configs keep working.</b> Refusing a stored endpoint mid-flight would
/// trade a security weakness for an outage, so the clone/rollback/republish/publish paths — which
/// carry a config that is already live — are pinned as still working, exactly as the delivery-config
/// path chose.</para>
/// </summary>
public class ConnectionRevisionTransportSecurityTests
{
    private const string CleartextUrlJson = """{"url":"http://supplier.example/orders"}""";
    private const string SecretPassword   = "hunter2";
    private const string UserInfoUrlJson  = $$"""{"url":"https://admin:{{SecretPassword}}@supplier.example/orders"}""";
    private const string SecureUrlJson    = """{"url":"https://supplier.example/orders"}""";

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

    private static ConnectionRevisionDraftInput Bundle(
        string? configJson, string protocol = DeliveryProtocolConstants.Http) =>
        new(
            InputMappingJson: "{}",
            OutputMappingJson: null,
            OutputFormat: "xml",
            DeliveryProtocol: protocol,
            DeliveryConfigJson: configJson,
            DeliveryAutoDeliver: false,
            CredentialsRef: null,
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

    /// <summary>
    /// Writes a revision row straight to the database, bypassing the service — the only way a
    /// config the policy now refuses can exist, i.e. one saved before enforcement did.
    /// </summary>
    private static async Task<SupplierConnectionRevision> SeedLegacyRevisionAsync(
        ProcuLinkDbContext db, SupplierConnection connection, string status, string configJson,
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
            DeliveryConfigJson = configJson,
        };
        db.SupplierConnectionRevisions.Add(rev);
        await db.SaveChangesAsync();
        return rev;
    }

    // ── Refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateDraft_WithACleartextEndpoint_IsRefusedAndPersistsNothing()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);

        var act = () => svc.CreateDraftAsync(
            orgId, conn.Id, Bundle(CleartextUrlJson), cloneFromActive: false, "user", CancellationToken.None);

        (await act.Should().ThrowAsync<OutboundUrlPolicyException>())
            .And.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);
        db.SupplierConnectionRevisions.Should().BeEmpty("a refused draft must not leave a row behind");
    }

    [Fact]
    public async Task UpdateDraft_WithACleartextEndpoint_IsRefusedAndLeavesTheStoredConfigUntouched()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);
        var draft = await svc.CreateDraftAsync(
            orgId, conn.Id, Bundle(SecureUrlJson), cloneFromActive: false, "user", CancellationToken.None);

        var act = () => svc.UpdateDraftAsync(
            orgId, conn.Id, draft!.Id, Bundle(CleartextUrlJson), CancellationToken.None);

        (await act.Should().ThrowAsync<OutboundUrlPolicyException>())
            .And.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);

        var stored = await db.SupplierConnectionRevisions.AsNoTracking()
            .FirstAsync(r => r.Id == draft!.Id);
        stored.DeliveryConfigJson.Should().Be(
            SecureUrlJson, "a refused update must not half-apply over the endpoint that was there");
    }

    /// <summary>
    /// The refusal is asserted BEFORE the message is inspected. Without that, disabling the
    /// userinfo check leaves no message at all and both "must not contain the secret" assertions
    /// pass vacuously — the trap the delivery-config suite was corrected for.
    /// </summary>
    [Fact]
    public async Task CreateDraft_WithCredentialsInTheUrl_IsRefused_AndTheMessageNeverQuotesTheSecret()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);

        var act = () => svc.CreateDraftAsync(
            orgId, conn.Id, Bundle(UserInfoUrlJson), cloneFromActive: false, "user", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<OutboundUrlPolicyException>();
        thrown.And.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorCredentialsInUrl);
        thrown.And.Message.Should().NotBeNullOrWhiteSpace();
        thrown.And.Message.Should().NotContain(SecretPassword);
        thrown.And.Message.Should().NotContain("supplier.example");
    }

    [Fact]
    public async Task UpdateDraft_WithCredentialsInTheUrl_IsRefused()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);
        var draft = await svc.CreateDraftAsync(
            orgId, conn.Id, Bundle(SecureUrlJson), cloneFromActive: false, "user", CancellationToken.None);

        var act = () => svc.UpdateDraftAsync(
            orgId, conn.Id, draft!.Id, Bundle(UserInfoUrlJson), CancellationToken.None);

        (await act.Should().ThrowAsync<OutboundUrlPolicyException>())
            .And.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorCredentialsInUrl);
    }

    /// <summary>
    /// Walks <see cref="DeliveryProtocolConstants.UrlBased"/> rather than naming protocols, so a new
    /// url-bearing protocol that joins that list is covered here the day it is added — and one that
    /// forgets to join it fails the delivery-config suite that walks the same list.
    /// </summary>
    [Fact]
    public async Task EveryUrlBearingProtocol_RefusesACleartextEndpointOnARevision()
    {
        DeliveryProtocolConstants.UrlBased.Should().NotBeEmpty(
            "an empty list would make this walk assert nothing at all");

        foreach (var protocol in DeliveryProtocolConstants.UrlBased)
        {
            await using var db = MakeDb();
            var svc = MakeSvc(db);
            var (orgId, _, conn) = await SeedAsync(db, svc);

            var act = () => svc.CreateDraftAsync(
                orgId, conn.Id, Bundle(CleartextUrlJson, protocol), cloneFromActive: false, "user",
                CancellationToken.None);

            (await act.Should().ThrowAsync<OutboundUrlPolicyException>(
                    $"protocol '{protocol}' carries a tenant-supplied URL"))
                .And.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);
        }
    }

    /// <summary>
    /// The "one policy, not two" pin. A second hand-rolled scheme or userinfo check on this path
    /// would refuse the same input with a different code or a differently-worded message, and this
    /// is what would catch it.
    /// </summary>
    [Theory]
    [InlineData(CleartextUrlJson, "http://supplier.example/orders")]
    [InlineData(UserInfoUrlJson,  "https://admin:hunter2@supplier.example/orders")]
    public async Task TheRevisionRefusal_IsTheSharedPolicysOwnVerdict(string configJson, string url)
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);

        var expected = OutboundUrlPolicy.Inspect(url, "Delivery endpoint");
        expected.Allowed.Should().BeFalse("this fixture only makes sense for a URL the policy refuses");

        var act = () => svc.CreateDraftAsync(
            orgId, conn.Id, Bundle(configJson), cloneFromActive: false, "user", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<OutboundUrlPolicyException>();
        thrown.And.ErrorCode.Should().Be(expected.ErrorCode);
        thrown.And.Message.Should().StartWith(expected.Message);
    }

    /// <summary>
    /// The validator and the dispatcher must not read different values out of the same blob.
    ///
    /// <para>System.Text.Json keeps BOTH properties when a key repeats: <c>JsonDocument</c> lists
    /// them in order, while <c>JsonSerializer.Deserialize</c> binds the LAST one. So a blob with two
    /// <c>url</c> keys was inspected as the first (an https endpoint that passes) and delivered to
    /// the second — a complete bypass of the rule, on this path and on the delivery-config save path
    /// that shares the same extraction.</para>
    /// </summary>
    [Theory]
    [InlineData("""{"url":"https://ok.example/orders","url":"http://evil.example/orders"}""")]
    [InlineData("""{"URL":"https://ok.example/orders","url":"http://evil.example/orders"}""")]
    [InlineData("""{"url":"http://evil.example/orders","url":"https://ok.example/orders"}""")]
    public async Task ARepeatedUrlKey_CannotSmuggleACleartextEndpointPastTheInspection(string configJson)
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);

        var act = () => svc.CreateDraftAsync(
            orgId, conn.Id, Bundle(configJson), cloneFromActive: false, "user", CancellationToken.None);

        (await act.Should().ThrowAsync<OutboundUrlPolicyException>())
            .And.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);
        db.SupplierConnectionRevisions.Should().BeEmpty();
    }

    // ── Allowances (the half a refuse-everything rule would break) ────────────

    [Fact]
    public async Task CreateDraft_WithAnHttpsEndpoint_Saves()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);

        var draft = await svc.CreateDraftAsync(
            orgId, conn.Id, Bundle(SecureUrlJson), cloneFromActive: false, "user", CancellationToken.None);

        draft.Should().NotBeNull();
        draft!.DeliveryConfigJson.Should().Be(SecureUrlJson);
    }

    /// <summary>
    /// Loopback is allowed by HOST, deliberately: the live e2e spec binds an ephemeral port and
    /// delivers to it over plain http, and that traffic never leaves the machine.
    /// </summary>
    [Theory]
    [InlineData("""{"url":"http://localhost:5223/orders"}""")]
    [InlineData("""{"url":"http://127.0.0.1:41235/orders"}""")]
    public async Task CreateDraft_WithPlainHttpOnLoopback_Saves(string configJson)
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);

        var draft = await svc.CreateDraftAsync(
            orgId, conn.Id, Bundle(configJson), cloneFromActive: false, "user", CancellationToken.None);

        draft.Should().NotBeNull();
        draft!.DeliveryConfigJson.Should().Be(configJson);
    }

    /// <summary>
    /// A config with no <c>url</c> key cannot deliver anything, and failing it here would be a
    /// different behaviour change from this one — the same line the delivery-config path drew.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData(null)]
    public async Task CreateDraft_WithNoEndpointUrl_Saves(string? configJson)
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);

        var draft = await svc.CreateDraftAsync(
            orgId, conn.Id, Bundle(configJson), cloneFromActive: false, "user", CancellationToken.None);

        draft.Should().NotBeNull();
    }

    /// <summary>
    /// A host+port protocol has no tenant-supplied URL scheme to police, so the same blob is not
    /// inspected under sftp. Pins the scope to <see cref="DeliveryProtocolConstants.UrlBased"/>.
    /// </summary>
    [Fact]
    public async Task CreateDraft_ForAHostBasedProtocol_IsNotInspected()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);

        var draft = await svc.CreateDraftAsync(
            orgId, conn.Id, Bundle(CleartextUrlJson, DeliveryProtocolConstants.Sftp),
            cloneFromActive: false, "user", CancellationToken.None);

        draft.Should().NotBeNull();
    }

    // ── Pre-existing configs keep working (no order is stranded) ──────────────

    [Fact]
    public async Task CreateDraft_CloneFromActive_StillClonesAConfigThatPredatesEnforcement()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);
        var legacy = await SeedLegacyRevisionAsync(
            db, conn, "published", CleartextUrlJson, publishedAt: DateTime.UtcNow.AddDays(-2));
        conn.ActiveRevisionId = legacy.Id;
        await db.SaveChangesAsync();

        var clone = await svc.CreateDraftAsync(
            orgId, conn.Id, input: null, cloneFromActive: true, "user", CancellationToken.None);

        clone.Should().NotBeNull();
        clone!.DeliveryConfigJson.Should().Be(
            CleartextUrlJson,
            "an operator editing a mapping must not be blocked by an endpoint that is already live");
    }

    [Fact]
    public async Task Rollback_StillRestoresAConfigThatPredatesEnforcement()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);
        var archived = await SeedLegacyRevisionAsync(
            db, conn, "archived", CleartextUrlJson, publishedAt: DateTime.UtcNow.AddDays(-2));
        var live = await SeedLegacyRevisionAsync(
            db, conn, "published", SecureUrlJson, versionNo: 2, publishedAt: DateTime.UtcNow.AddDays(-1));
        conn.ActiveRevisionId = live.Id;
        await db.SaveChangesAsync();

        var outcome = await svc.RollbackAsync(orgId, conn.Id, archived.Id, "user", CancellationToken.None);

        outcome.Status.Should().Be(ConnectionRollbackStatus.Completed,
            "rolling back to a version that was live before is the flow that must not be stranded");
        outcome.NewRevision!.DeliveryConfigJson.Should().Be(CleartextUrlJson);
    }

    [Fact]
    public async Task RepublishLiveDelivery_StillSnapshotsALiveConfigThatPredatesEnforcement()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db, revisionAuthority: true);
        var (orgId, supplierId, conn) = await SeedAsync(db, svc);
        var active = await SeedLegacyRevisionAsync(
            db, conn, "published", SecureUrlJson, publishedAt: DateTime.UtcNow.AddDays(-1));
        conn.ActiveRevisionId = active.Id;
        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            SupplierId = supplierId,
            Protocol   = DeliveryProtocolConstants.Http,
            ConfigJson = CleartextUrlJson,
            CreatedAt  = DateTime.UtcNow.AddDays(-5),
            UpdatedAt  = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var outcome = await svc.RepublishLiveDeliveryAsync(orgId, supplierId, "api", CancellationToken.None);

        outcome.Status.Should().Be(DeliveryRepublishStatus.Republished,
            "refusing here would leave the pinned revision pointing at a different endpoint than live");
    }

    [Fact]
    public async Task Publish_StillPublishesADraftWhoseConfigPredatesEnforcement()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);
        var draft = await SeedLegacyRevisionAsync(db, conn, "draft", CleartextUrlJson);

        var test = await svc.MarkTestAsync(orgId, conn.Id, draft.Id, CancellationToken.None);
        test.Status.Should().Be(ConnectionTestStatus.Completed);
        var outcome = await svc.PublishAsync(orgId, conn.Id, draft.Id, "user", CancellationToken.None);

        outcome.Should().Be(ConnectionPublishOutcome.Published,
            "publish activates a stored bundle; it is not a write of a new endpoint, and refusing it "
            + "would block every mapping-only revision for a supplier on a legacy endpoint");
    }

    // ── The documented cleartext invariant, enforced ──────────────────────────

    /// <summary>
    /// <see cref="SupplierDeliveryConfig.ConfigJson"/> documents that the column is cleartext BY
    /// DESIGN and states the invariant in words: never write a credential into it. The revision
    /// snapshot is the same blob under a different column, and the one credential that can be
    /// smuggled into it through a URL field is a userinfo password.
    ///
    /// <para>This walks every url-bearing protocol, then re-reads the database and asserts the
    /// secret is nowhere in it. The positive control in the same test is the anti-vacuity floor: an
    /// endpoint with no credentials must still persist, so a service that refused everything (or a
    /// harness that saved nothing) cannot pass by writing no rows at all.</para>
    /// </summary>
    [Fact]
    public async Task NoCredentialBearingUrlCanReachARevisionConfigJson()
    {
        DeliveryProtocolConstants.UrlBased.Should().NotBeEmpty();

        foreach (var protocol in DeliveryProtocolConstants.UrlBased)
        {
            await using var db = MakeDb();
            var svc = MakeSvc(db);
            var (orgId, _, conn) = await SeedAsync(db, svc);

            var act = () => svc.CreateDraftAsync(
                orgId, conn.Id, Bundle(UserInfoUrlJson, protocol), cloneFromActive: false, "user",
                CancellationToken.None);
            await act.Should().ThrowAsync<OutboundUrlPolicyException>();

            var persisted = await db.SupplierConnectionRevisions.AsNoTracking().ToListAsync();
            persisted.Should().NotContain(
                r => r.DeliveryConfigJson != null && r.DeliveryConfigJson.Contains(SecretPassword),
                $"config_json is stored in clear text, so a '{protocol}' revision must never hold a credential");

            // Anti-vacuity floor: the same call shape, without the credential, must still save.
            var allowed = await svc.CreateDraftAsync(
                orgId, conn.Id, Bundle(SecureUrlJson, protocol), cloneFromActive: false, "user",
                CancellationToken.None);
            allowed.Should().NotBeNull(
                $"'{protocol}' must still be configurable — this test must not pass by refusing everything");
        }
    }
}
