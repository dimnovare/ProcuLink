using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// The cleartext invariant at the connection-revision write path — the second way a delivery
/// endpoint's configuration is chosen, and the one a pinned order actually delivers through.
///
/// <para><b>Flat, with no grandfathering, unlike the live delivery-config path.</b> This input is
/// caller-supplied, exactly as the transport rule's is, and the paths that carry an ALREADY-LIVE
/// bundle — clone-from-active, rollback, republish-from-live, publish, the V1 backfill — never reach
/// <c>ApplyScalars</c>. Republish-from-live is the one the delivery-config editor triggers, so the
/// ordinary operator flow keeps working after a grandfathered live save. Nothing pre-existing is
/// stranded by refusing here, because it is exactly that bypass of <c>ApplyScalars</c> that makes a
/// flat refusal safe — and the bypass is pinned by <c>ConnectionRevisionTransportSecurityTests</c>,
/// not by this file: clone-from-active at <c>ConnectionRevisionTransportSecurityTests.cs:352</c>,
/// rollback at <c>:372</c>, republish-from-live at <c>:392</c>, and publish at <c>:419</c>. Each of
/// those four tests asserts its path still succeeds while carrying a cleartext URL, so routing any
/// of them through <c>ApplyScalars</c> would trip the transport guard and turn that suite red. Only
/// publish is re-verified below, for the credential-header case specifically — not to re-pin the
/// bypass itself, which is already covered above.</para>
/// </summary>
public class ConnectionRevisionCredentialHeaderTests
{
    private const string Token = "t0ps3cret";
    // $$$ / {{{...}}} (not $$ / {{...}}): the JSON content has two consecutive literal '}' right
    // after the interpolated token (closing "headers" then closing the outer object), and CS9007
    // refuses that run of closing braces unless the interpolation delimiter is wide enough to be
    // unambiguously longer — same fix as ProcuLink.Infrastructure.Tests/Services/DeliveryConfigCredentialHeaderTests.cs:28-29.
    private static readonly string WithToken =
        $$$"""{"url":"https://supplier.example/orders","headers":{"Authorization":"Bearer {{{Token}}}"}}""";
    private const string Clean =
        """{"url":"https://supplier.example/orders","headers":{"X-Correlation-Id":"abc"}}""";

    // ── helpers copied verbatim from ConnectionRevisionTransportSecurityTests.cs:49-119. They are
    // private to that class, so this is a deliberate duplication rather than a shared fixture — see
    // task brief: that file is part of an open PR this branch is stacked on and must not be edited. ──

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
    public async Task CreateDraft_WithACredentialHeader_IsRefusedAndPersistsNothing()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);

        var act = () => svc.CreateDraftAsync(
            orgId, conn.Id, Bundle(WithToken), cloneFromActive: false, "user", CancellationToken.None);

        (await act.Should().ThrowAsync<CredentialHeaderInConfigException>())
            .And.HeaderNames.Should().Equal("Authorization");
        db.SupplierConnectionRevisions.Should().BeEmpty("a refused draft must not leave a row behind");
    }

    /// <summary>Refusal asserted before the message is checked, so the NotContain cannot pass vacuously.</summary>
    [Fact]
    public async Task CreateDraft_RefusalMessage_NeverCarriesTheToken()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);

        var act = () => svc.CreateDraftAsync(
            orgId, conn.Id, Bundle(WithToken), cloneFromActive: false, "user", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<CredentialHeaderInConfigException>();
        thrown.And.PolicyMessage.Should().Contain("'Authorization'");
        thrown.And.PolicyMessage.Should().NotContain(Token);
    }

    [Fact]
    public async Task UpdateDraft_WithACredentialHeader_IsRefusedAndLeavesTheStoredConfigUntouched()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);
        var rev = await SeedLegacyRevisionAsync(db, conn, "draft", Clean);

        var act = () => svc.UpdateDraftAsync(
            orgId, conn.Id, rev.Id, Bundle(WithToken), CancellationToken.None);

        await act.Should().ThrowAsync<CredentialHeaderInConfigException>();

        var reread = await db.SupplierConnectionRevisions.AsNoTracking().SingleAsync(r => r.Id == rev.Id);
        reread.DeliveryConfigJson.Should().Be(Clean);
        reread.DeliveryConfigJson.Should().NotContain(Token);
    }

    // ── Allowances ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateDraft_WithOrdinaryHeaders_Succeeds()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);

        var draft = await svc.CreateDraftAsync(
            orgId, conn.Id, Bundle(Clean), cloneFromActive: false, "user", CancellationToken.None);

        draft.Should().NotBeNull();
        draft!.DeliveryConfigJson.Should().Contain("X-Correlation-Id");
    }

    [Fact]
    public async Task CreateDraft_WithNoHeadersAtAll_Succeeds()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);

        var draft = await svc.CreateDraftAsync(
            orgId, conn.Id, Bundle("""{"url":"https://supplier.example/orders"}"""),
            cloneFromActive: false, "user", CancellationToken.None);

        draft.Should().NotBeNull();
    }

    /// <summary>
    /// A revision that predates enforcement stays publishable. Publish flips a status on a stored
    /// bundle rather than writing an endpoint, and refusing it would block every future revision —
    /// including a mapping-only fix — for a supplier whose config predates the rule. Same call #157
    /// made for cleartext endpoints, and the reason the warning and the dispatch log exist.
    ///
    /// <para><b>Deviation from the brief:</b> the brief's draft asserted
    /// <c>published.Should().NotBeNull()</c>. <see cref="ISupplierConnectionService.PublishAsync"/>
    /// actually returns the non-nullable enum <see cref="ConnectionPublishOutcome"/>, so that
    /// assertion would compile but could never fail — vacuous. This instead mirrors the sibling
    /// <c>Publish_StillPublishesADraftWhoseConfigPredatesEnforcement</c> in
    /// ConnectionRevisionTransportSecurityTests.cs: mark-test first (the evidence gate requires a
    /// fresh passing run before publish will proceed) and assert the real outcome value.</para>
    /// </summary>
    [Fact]
    public async Task Publish_OfAPreExistingRevisionCarryingACredentialHeader_StillWorks()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);
        var rev = await SeedLegacyRevisionAsync(db, conn, "draft", WithToken);

        var test = await svc.MarkTestAsync(orgId, conn.Id, rev.Id, CancellationToken.None);
        test.Status.Should().Be(ConnectionTestStatus.Completed);

        var outcome = await svc.PublishAsync(orgId, conn.Id, rev.Id, "user", CancellationToken.None);

        outcome.Should().Be(ConnectionPublishOutcome.Published,
            "publish activates a stored bundle; it is not a write of a new endpoint, and refusing it "
            + "would block every mapping-only revision for a supplier whose config predates the rule");
    }
}
