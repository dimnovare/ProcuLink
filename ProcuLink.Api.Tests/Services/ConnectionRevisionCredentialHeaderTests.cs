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
/// <para><b>UPDATE grandfathers an identical stored pair; CREATE refuses flat.</b> An earlier
/// version of this rule refused flat on both legs, on the reasoning that revision input is
/// caller-supplied and the paths carrying an ALREADY-LIVE bundle bypass <c>ApplyScalars</c>. That
/// reasoning covers the create leg and does NOT cover the update leg. Those bypasses —
/// clone-from-active, rollback, republish-from-live and the V1 backfill — are exactly how a draft
/// ACQUIRES a credential header; the frontend then opens an editable draft with
/// <c>cloneFromActive: true</c> and, because the PUT replaces the whole bundle, echoes the delivery
/// config back on every mapping autosave (deliberately — otherwise a mapping save would wipe the
/// draft's delivery channel). Flat on update therefore 400s every mapping autosave for precisely
/// the pre-enforcement customers the rule must not strand, with no headers field in the UI to clear
/// the fault. The update leg now compares against the draft's own stored blob: an unchanged echo
/// saves, an added header refuses, a rotated value refuses.</para>
///
/// <para><b>What this file pins vs. what the sibling suite pins.</b>
/// <c>ConnectionRevisionTransportSecurityTests</c> pins that clone-from-active (<c>:352</c>),
/// rollback (<c>:372</c>), republish-from-live (<c>:392</c>) and publish (<c>:419</c>) bypass
/// <c>ApplyScalars</c> at all — each carries a cleartext URL through and still succeeds. That
/// proves the bypass, not that THIS rule leaves those paths alone, so each is re-verified below
/// with a credential-bearing bundle, together with the V1 backfill.</para>
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
    private const string RotatedToken = "r0tat3d";
    /// <summary>Same header NAME as <see cref="WithToken"/>, different value — a token rotation.</summary>
    private static readonly string WithRotatedToken =
        $$$"""{"url":"https://supplier.example/orders","headers":{"Authorization":"Bearer {{{RotatedToken}}}"}}""";
    /// <summary>One credential header, stored before enforcement.</summary>
    private const string WithApiKey =
        """{"url":"https://supplier.example/orders","headers":{"X-Api-Key":"k3y"}}""";
    /// <summary><see cref="WithApiKey"/> echoed back unchanged, plus a SECOND credential header.</summary>
    private static readonly string WithApiKeyPlusToken =
        $$$"""{"url":"https://supplier.example/orders","headers":{"X-Api-Key":"k3y","Authorization":"Bearer {{{Token}}}"}}""";
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

    // inputMappingJson is parameterised so an "ordinary mapping edit" can be told apart from a
    // no-op save: the echo-allowance test must prove a real edit lands, not merely that nothing threw.
    private static ConnectionRevisionDraftInput Bundle(
        string? configJson, string protocol = DeliveryProtocolConstants.Http,
        string? inputMappingJson = "{}") =>
        new(
            InputMappingJson: inputMappingJson,
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

    /// <summary>
    /// The stored draft carries NO credential header, so there is nothing to grandfather and the
    /// caller is introducing one. Refused, and the stored blob is left exactly as it was.
    /// </summary>
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

    /// <summary>
    /// Rotation is a WRITE of a new secret, not an echo, so the grandfather does not cover it — and
    /// this is the moment the refusal is meant to bite, with a message saying where the value goes
    /// instead. Same header name, different value.
    /// </summary>
    [Fact]
    public async Task UpdateDraft_RotatingAStoredCredentialHeadersValue_IsRefused()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);
        var rev = await SeedLegacyRevisionAsync(db, conn, "draft", WithToken);

        var act = () => svc.UpdateDraftAsync(
            orgId, conn.Id, rev.Id, Bundle(WithRotatedToken), CancellationToken.None);

        // Refusal first: without it the "rotated value was not persisted" check below would pass
        // while proving nothing.
        (await act.Should().ThrowAsync<CredentialHeaderInConfigException>())
            .And.HeaderNames.Should().Equal("Authorization");

        var reread = await db.SupplierConnectionRevisions.AsNoTracking().SingleAsync(r => r.Id == rev.Id);
        reread.DeliveryConfigJson.Should().NotContain(RotatedToken);
    }

    /// <summary>
    /// Grandfathering one header must not license a second. The caller echoes the stored
    /// <c>X-Api-Key</c> back unchanged — allowed on its own — and ADDS an <c>Authorization</c> that
    /// was never stored. Only the added name is refused, so the refusal names what the caller
    /// actually introduced.
    /// </summary>
    [Fact]
    public async Task UpdateDraft_AddingASecondCredentialHeaderBesideAGrandfatheredOne_IsRefused()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);
        var rev = await SeedLegacyRevisionAsync(db, conn, "draft", WithApiKey);

        var act = () => svc.UpdateDraftAsync(
            orgId, conn.Id, rev.Id, Bundle(WithApiKeyPlusToken), CancellationToken.None);

        // new[] { … } rather than a params element: the params overload of Equal would swallow the
        // reason string as a second EXPECTED header name.
        (await act.Should().ThrowAsync<CredentialHeaderInConfigException>())
            .And.HeaderNames.Should().Equal(
                new[] { "Authorization" },
                "the echoed X-Api-Key is grandfathered; only the header the caller introduced is refused");

        var reread = await db.SupplierConnectionRevisions.AsNoTracking().SingleAsync(r => r.Id == rev.Id);
        reread.DeliveryConfigJson.Should().Be(WithApiKey);
        reread.DeliveryConfigJson.Should().NotContain(Token);
    }

    /// <summary>
    /// CREATE keeps its flat refusal even when the identical header is already stored on ANOTHER
    /// revision of the same connection. A create has no stored predecessor of its own, so there is
    /// nothing it is echoing back — grandfathering it would let any caller launder a credential
    /// header into a brand-new revision by pointing at an old one.
    /// </summary>
    [Fact]
    public async Task CreateDraft_IsStillFlat_EvenWhenAnIdenticalHeaderExistsOnAnotherRevision()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);
        var existing = await SeedLegacyRevisionAsync(
            db, conn, "published", WithToken, publishedAt: DateTime.UtcNow.AddDays(-2));
        conn.ActiveRevisionId = existing.Id;
        await db.SaveChangesAsync();

        var act = () => svc.CreateDraftAsync(
            orgId, conn.Id, Bundle(WithToken), cloneFromActive: false, "user", CancellationToken.None);

        (await act.Should().ThrowAsync<CredentialHeaderInConfigException>())
            .And.HeaderNames.Should().Equal("Authorization");
        (await db.SupplierConnectionRevisions.CountAsync())
            .Should().Be(1, "only the seeded revision may exist");
    }

    // ── Allowances ───────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The regression test for the mapper-autosave outage.</b> A draft that already carries a
    /// credential header — acquired through clone-from-active, republish-from-live, rollback or the
    /// V1 backfill, all of which bypass <c>ApplyScalars</c> — has its whole bundle echoed back by
    /// every mapping save (<c>useMapperModel.ts:471</c>,
    /// <c>deliveryConfigJson: rev?.deliveryConfigJson ?? null</c>). A flat refusal on this leg would
    /// 400 each one of those saves, during ordinary work, for exactly the pre-enforcement customers
    /// grandfathering exists to protect — with no headers field anywhere in the UI to clear the
    /// fault with.
    ///
    /// <para>The mapping edit is asserted to have LANDED, not merely to have not thrown: a version
    /// that accepted the call and silently dropped the update would otherwise pass.</para>
    /// </summary>
    [Fact]
    public async Task UpdateDraft_EchoingAnIdenticalStoredCredentialHeader_Saves()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);
        var rev = await SeedLegacyRevisionAsync(db, conn, "draft", WithToken);

        const string editedMapping = """{"Header":{"PoNumber":"po_number"}}""";
        var saved = await svc.UpdateDraftAsync(
            orgId, conn.Id, rev.Id,
            Bundle(WithToken, inputMappingJson: editedMapping),
            CancellationToken.None);

        saved.Should().BeTrue("an unchanged echo of a stored header is not a write of a secret");

        var reread = await db.SupplierConnectionRevisions.AsNoTracking().SingleAsync(r => r.Id == rev.Id);
        reread.InputMappingJson.Should().Be(editedMapping, "the operator's mapping edit must land");
        reread.DeliveryConfigJson.Should().Be(WithToken, "the echoed delivery bundle is preserved");
    }

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

    // ── The other bypass paths, against THIS rule ─────────────────────────────
    //
    // ConnectionRevisionTransportSecurityTests proves these four bypass ApplyScalars, using
    // cleartext URLs. That is a different rule: it shows the transport guard is not reached, not
    // that the credential-header guard leaves these paths alone. Each is therefore re-run here with
    // a credential-bearing bundle. Each also asserts the header SURVIVED the copy, so a path that
    // "succeeded" by silently dropping the delivery config could not pass.

    [Fact]
    public async Task CreateDraft_CloneFromActive_StillClonesABundleCarryingACredentialHeader()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);
        var legacy = await SeedLegacyRevisionAsync(
            db, conn, "published", WithToken, publishedAt: DateTime.UtcNow.AddDays(-2));
        conn.ActiveRevisionId = legacy.Id;
        await db.SaveChangesAsync();

        var clone = await svc.CreateDraftAsync(
            orgId, conn.Id, input: null, cloneFromActive: true, "user", CancellationToken.None);

        clone.Should().NotBeNull();
        clone!.DeliveryConfigJson.Should().Be(
            WithToken,
            "an operator editing a mapping must not be blocked by a header that is already live — "
            + "and this is the path that gives the draft the header its mapping saves then echo back");
    }

    [Fact]
    public async Task Rollback_StillRestoresABundleCarryingACredentialHeader()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);
        var archived = await SeedLegacyRevisionAsync(
            db, conn, "archived", WithToken, publishedAt: DateTime.UtcNow.AddDays(-2));
        var live = await SeedLegacyRevisionAsync(
            db, conn, "published", Clean, versionNo: 2, publishedAt: DateTime.UtcNow.AddDays(-1));
        conn.ActiveRevisionId = live.Id;
        await db.SaveChangesAsync();

        var outcome = await svc.RollbackAsync(orgId, conn.Id, archived.Id, "user", CancellationToken.None);

        outcome.Status.Should().Be(ConnectionRollbackStatus.Completed,
            "rolling back to a version that was live before is the flow that must not be stranded");
        outcome.NewRevision!.DeliveryConfigJson.Should().Be(WithToken);
    }

    [Fact]
    public async Task RepublishLiveDelivery_StillSnapshotsALiveConfigCarryingACredentialHeader()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db, revisionAuthority: true);
        var (orgId, supplierId, conn) = await SeedAsync(db, svc);
        var active = await SeedLegacyRevisionAsync(
            db, conn, "published", Clean, publishedAt: DateTime.UtcNow.AddDays(-1));
        conn.ActiveRevisionId = active.Id;
        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            SupplierId = supplierId,
            Protocol   = DeliveryProtocolConstants.Http,
            ConfigJson = WithToken,
            CreatedAt  = DateTime.UtcNow.AddDays(-5),
            UpdatedAt  = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var outcome = await svc.RepublishLiveDeliveryAsync(orgId, supplierId, "api", CancellationToken.None);

        outcome.Status.Should().Be(DeliveryRepublishStatus.Republished,
            "this is the path the delivery-config editor triggers after a grandfathered live save; "
            + "refusing it would leave the pinned revision pointing at a different config than live");
        // DeliveryRepublishOutcome carries only the new version number, so the snapshot is read back.
        var republished = await db.SupplierConnectionRevisions.AsNoTracking()
            .SingleAsync(r => r.ConnectionId == conn.Id && r.VersionNo == outcome.NewVersionNo);
        republished.DeliveryConfigJson.Should().Be(WithToken);
    }

    /// <summary>
    /// The V1 backfill mirrors the live row into a published rev-1. It has a reachable seam —
    /// <c>ConnectionBackfillService.BackfillAllAsync</c>, the same one
    /// <c>ConnectionBackfillServiceTests</c> drives — so it is covered rather than skipped. A
    /// delivery config alone makes a supplier a backfill candidate, and the supplier must have no
    /// connection yet, so this seeds its own org/supplier instead of reusing <c>SeedAsync</c>.
    /// </summary>
    [Fact]
    public async Task V1Backfill_StillSnapshotsALiveConfigCarryingACredentialHeader()
    {
        await using var db = MakeDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme OÜ", CreatedAt = now });
        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            SupplierId = supplierId,
            Protocol   = DeliveryProtocolConstants.Http,
            ConfigJson = WithToken,
            CreatedAt  = now,
            UpdatedAt  = now,
        });
        await db.SaveChangesAsync();

        var created = await new ConnectionBackfillService(db).BackfillAllAsync(CancellationToken.None);

        created.Should().Be(1, "a config that predates enforcement must still be backfillable");
        var rev = await db.SupplierConnectionRevisions.AsNoTracking().SingleAsync();
        rev.DeliveryConfigJson.Should().Be(WithToken, "the backfill mirrors the live row verbatim");
    }
}
