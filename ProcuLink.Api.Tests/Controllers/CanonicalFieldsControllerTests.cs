using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Phase 2 (extensible canonical) — /api/connections/{connectionId}/canonical-fields CRUD.
/// Closes the offer⇔works gap: the entity/migration/persistence shipped but no route existed.
/// Uses the real <see cref="ProcuLinkDbContext"/> on EF InMemory + a mocked
/// <see cref="ICurrentTenantService"/> (mirrors <c>OnboardingControllerTests</c>).
/// Covers create → list → soft-delete → list-excludes, idempotent delete, validation/conflict,
/// connection ownership, and org isolation.
/// </summary>
public class CanonicalFieldsControllerTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (CanonicalFieldsController Ctrl, Guid OrgId, ProcuLinkDbContext Db) Build()
    {
        var db     = MakeDb();
        var orgId  = Guid.NewGuid();
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);
        return (new CanonicalFieldsController(db, tenant.Object), orgId, db);
    }

    /// <summary>Seed a connection (FK parent for the def's connection scope) and return its id.</summary>
    private static Guid SeedConnection(ProcuLinkDbContext db, Guid orgId)
    {
        var id = Guid.NewGuid();
        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = id, OrgId = orgId, SupplierId = Guid.NewGuid(), Name = "Acme",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private static async Task<List<CanonicalFieldDto>> ListAsync(CanonicalFieldsController ctrl, Guid connectionId)
    {
        var result = await ctrl.List(connectionId, CancellationToken.None);
        var ok     = result.Should().BeOfType<OkObjectResult>().Subject;
        return ((IReadOnlyList<CanonicalFieldDto>)ok.Value!).ToList();
    }

    private static CreateCanonicalFieldRequest Req(
        string key, string label = "Label", string? scope = null,
        string? type = null, string? standardsRef = null, int? order = null) =>
        new(key, label, scope, type, standardsRef, order);

    // ── create → list → soft-delete → list-excludes ──────────────────────────

    [Fact]
    public async Task Create_Then_List_Returns_TheField()
    {
        var (ctrl, orgId, db) = Build();
        var connId = SeedConnection(db, orgId);

        var created = await ctrl.Create(connId,
            Req("incoterms2", "Incoterms (extra)", scope: "header", type: "string", standardsRef: "cbc:CustomizationID", order: 7),
            CancellationToken.None);

        var dto = created.Should().BeOfType<CreatedAtActionResult>().Subject
            .Value.Should().BeOfType<CanonicalFieldDto>().Subject;
        dto.Key.Should().Be("incoterms2");
        dto.Label.Should().Be("Incoterms (extra)");
        dto.Scope.Should().Be("header");
        dto.Type.Should().Be("string");
        dto.StandardsRef.Should().Be("cbc:CustomizationID");
        dto.Order.Should().Be(7);

        var list = await ListAsync(ctrl, connId);
        list.Should().ContainSingle().Which.Key.Should().Be("incoterms2");
    }

    [Fact]
    public async Task Delete_SoftDeletes_AndList_Excludes_ButRowSurvives()
    {
        var (ctrl, orgId, db) = Build();
        var connId = SeedConnection(db, orgId);
        await ctrl.Create(connId, Req("custom1"), CancellationToken.None);

        var del = await ctrl.Delete(connId, "custom1", CancellationToken.None);
        del.Should().BeOfType<NoContentResult>();

        // Excluded from the active list …
        (await ListAsync(ctrl, connId)).Should().BeEmpty();

        // … but the row physically survives with DeletedAt stamped (pinned revisions still see it).
        var row = await db.CanonicalFieldDefs.AsNoTracking()
            .SingleAsync(x => x.ConnectionId == connId && x.Key == "custom1");
        row.DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Order_Defaults_To_AppendAfterMax_WhenOmitted()
    {
        var (ctrl, orgId, db) = Build();
        var connId = SeedConnection(db, orgId);

        await ctrl.Create(connId, Req("a", order: 3), CancellationToken.None);
        var second = await ctrl.Create(connId, Req("b"), CancellationToken.None); // order omitted

        var dto = second.Should().BeOfType<CreatedAtActionResult>().Subject
            .Value.Should().BeOfType<CanonicalFieldDto>().Subject;
        dto.Order.Should().Be(4); // max(3) + 1
    }

    [Fact]
    public async Task List_OrdersBy_DisplayOrder()
    {
        var (ctrl, orgId, db) = Build();
        var connId = SeedConnection(db, orgId);

        await ctrl.Create(connId, Req("third", order: 30), CancellationToken.None);
        await ctrl.Create(connId, Req("first", order: 10), CancellationToken.None);
        await ctrl.Create(connId, Req("second", order: 20), CancellationToken.None);

        var keys = (await ListAsync(ctrl, connId)).Select(d => d.Key).ToList();
        keys.Should().ContainInOrder("first", "second", "third");
    }

    // ── validation + conflict ────────────────────────────────────────────────

    [Fact]
    public async Task Create_WithBlankKey_Returns400()
    {
        var (ctrl, orgId, db) = Build();
        var connId = SeedConnection(db, orgId);

        var result = await ctrl.Create(connId, Req("   "), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        db.CanonicalFieldDefs.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_DuplicateActiveKey_Returns409()
    {
        var (ctrl, orgId, db) = Build();
        var connId = SeedConnection(db, orgId);
        await ctrl.Create(connId, Req("dup"), CancellationToken.None);

        var result = await ctrl.Create(connId, Req("dup"), CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        db.CanonicalFieldDefs.Count(x => x.Key == "dup" && x.DeletedAt == null).Should().Be(1);
    }

    [Fact]
    public async Task Create_AfterSoftDelete_AllowsReuseOfKey()
    {
        var (ctrl, orgId, db) = Build();
        var connId = SeedConnection(db, orgId);
        await ctrl.Create(connId, Req("recyclable"), CancellationToken.None);
        await ctrl.Delete(connId, "recyclable", CancellationToken.None);

        var result = await ctrl.Create(connId, Req("recyclable"), CancellationToken.None);

        result.Should().BeOfType<CreatedAtActionResult>();
        db.CanonicalFieldDefs.Count(x => x.Key == "recyclable" && x.DeletedAt == null).Should().Be(1);
    }

    // ── idempotent delete ────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_AlreadyDeletedKey_Returns204_Idempotent()
    {
        var (ctrl, orgId, db) = Build();
        var connId = SeedConnection(db, orgId);
        await ctrl.Create(connId, Req("once"), CancellationToken.None);
        await ctrl.Delete(connId, "once", CancellationToken.None);

        var second = await ctrl.Delete(connId, "once", CancellationToken.None);
        second.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Delete_NeverExistedKey_Returns204_Idempotent()
    {
        var (ctrl, orgId, db) = Build();
        var connId = SeedConnection(db, orgId);

        var result = await ctrl.Delete(connId, "ghost", CancellationToken.None);
        result.Should().BeOfType<NoContentResult>();
    }

    // ── connection ownership / tenancy ───────────────────────────────────────

    [Fact]
    public async Task List_UnknownConnection_Returns404()
    {
        var (ctrl, _, _) = Build();
        var result = await ctrl.List(Guid.NewGuid(), CancellationToken.None);
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Create_OnAnotherOrgsConnection_Returns404_AndWritesNothing()
    {
        var (ctrl, _, db) = Build();
        // Connection belongs to a DIFFERENT org than the controller's tenant.
        var otherOrg = Guid.NewGuid();
        var foreignConn = SeedConnection(db, otherOrg);

        var result = await ctrl.Create(foreignConn, Req("sneaky"), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
        db.CanonicalFieldDefs.Should().BeEmpty();
    }

    [Fact]
    public async Task List_DoesNotLeak_OtherOrgsFields_OnSameConnectionId()
    {
        var (ctrl, orgId, db) = Build();
        var connId = SeedConnection(db, orgId);

        // A field row tagged with the SAME connection id but a DIFFERENT org must never surface.
        db.CanonicalFieldDefs.Add(new CanonicalFieldDef
        {
            Id = Guid.NewGuid(), OrgId = Guid.NewGuid(), ConnectionId = connId,
            Key = "foreign", Label = "Foreign", Scope = "header", Type = "string",
            Order = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        await ctrl.Create(connId, Req("mine"), CancellationToken.None);

        var list = await ListAsync(ctrl, connId);
        list.Should().ContainSingle().Which.Key.Should().Be("mine");
    }

    [Fact]
    public async Task Delete_OnAnotherOrgsConnection_Returns404()
    {
        var (ctrl, _, db) = Build();
        var otherOrg = Guid.NewGuid();
        var foreignConn = SeedConnection(db, otherOrg);

        var result = await ctrl.Delete(foreignConn, "anything", CancellationToken.None);
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    // ── normalization defaults ───────────────────────────────────────────────

    [Fact]
    public async Task Create_DefaultsScopeHeader_AndTypeString_WhenOmittedOrUnknown()
    {
        var (ctrl, orgId, db) = Build();
        var connId = SeedConnection(db, orgId);

        var created = await ctrl.Create(connId,
            Req("k", scope: "GARBAGE", type: null), CancellationToken.None);

        var dto = created.Should().BeOfType<CreatedAtActionResult>().Subject
            .Value.Should().BeOfType<CanonicalFieldDto>().Subject;
        dto.Scope.Should().Be("header");
        dto.Type.Should().Be("string");
    }
}
