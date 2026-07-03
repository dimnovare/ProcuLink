using System.Reflection;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Catalog;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// BE-7 gate for <c>POST /api/ingress/{slug}/catalog/{supplierId}</c>:
/// multipart and raw-CSV bodies both upsert through the REAL catalog sink; a verbatim
/// replay is a no-op (0 created — natural-key idempotency, no Idempotency-Key needed);
/// slug mismatch → 403; unknown/foreign/soft-deleted supplier → 404; the supplier segment
/// is route-constrained to GUID-only; the body cap and rate-limit policy are pinned by
/// attribute (enforced by the server pipeline); a body over the catalog row cap → 400.
/// </summary>
public class IngressControllerCatalogTests
{
    private const string Slug = "acme-distribution";

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed record Harness(
        IngressController Controller,
        ProcuLinkDbContext Db,
        Guid OrgId,
        Guid SupplierId,
        SupplierCatalogService Catalog,
        DefaultHttpContext Http);

    private static Harness Build(string slug = Slug)
    {
        var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_{orgId:N}",
            Name = "Acme Distribution",
            Slug = slug,
            CreatedAt = DateTime.UtcNow,
        });
        db.Suppliers.Add(new Supplier
        {
            Id = supplierId,
            OrgId = orgId,
            Name = "Northwind",
            CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var http = new DefaultHttpContext();
        var controller = new IngressController(
            db, new IdempotencyService(db), tenant.Object, NullLogger<IngressController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        return new Harness(controller, db, orgId, supplierId, new SupplierCatalogService(db), http);
    }

    private static void SetMultipartBody(DefaultHttpContext http, string fileName, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/csv",
        };
        http.Request.ContentType = "multipart/form-data; boundary=test-boundary";
        http.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(),
            new FormFileCollection { file });
    }

    private static void SetRawCsvBody(DefaultHttpContext http, string content, string contentType = "text/csv")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        http.Request.ContentType = contentType;
        http.Request.Body = new MemoryStream(bytes);
        http.Request.ContentLength = bytes.Length;
    }

    private const string Csv = "code,name,unit_price\nA-1,Widget,9.50\nB-2,Gadget,1.25\n";

    // ── happy paths ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Push_Multipart_Upserts()
    {
        var h = Build();
        SetMultipartBody(h.Http, "catalog.csv", Csv);

        var result = await h.Controller.PushCatalog(Slug, h.SupplierId, h.Catalog, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        int created = ((dynamic)ok.Value!).created;
        int total = ((dynamic)ok.Value!).total;
        created.Should().Be(2);
        total.Should().Be(2);

        var rows = await h.Db.SupplierProducts
            .Where(p => p.OrgId == h.OrgId && p.SupplierId == h.SupplierId).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Should().ContainSingle(p => p.Code == "A-1" && p.Name == "Widget" && p.Price == 9.50m);
    }

    [Fact]
    public async Task Push_RawCsv_Upserts()
    {
        var h = Build();
        SetRawCsvBody(h.Http, Csv);

        var result = await h.Controller.PushCatalog(Slug, h.SupplierId, h.Catalog, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        int created = ((dynamic)ok.Value!).created;
        created.Should().Be(2);
        (await h.Db.SupplierProducts.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Push_Replay_IsNoOp_ZeroCreated()
    {
        var h = Build();
        SetRawCsvBody(h.Http, Csv);
        await h.Controller.PushCatalog(Slug, h.SupplierId, h.Catalog, CancellationToken.None);

        SetRawCsvBody(h.Http, Csv); // identical second push
        var result = await h.Controller.PushCatalog(Slug, h.SupplierId, h.Catalog, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        int created = ((dynamic)ok.Value!).created;
        int updated = ((dynamic)ok.Value!).updated;
        created.Should().Be(0, "natural-key upsert makes a verbatim replay a no-op");
        updated.Should().Be(2);
        (await h.Db.SupplierProducts.CountAsync()).Should().Be(2, "no duplicates on replay");
    }

    // ── auth / tenancy ────────────────────────────────────────────────────────

    [Fact]
    public async Task Push_SlugMismatch_Returns403()
    {
        var h = Build(slug: "real-slug");
        SetRawCsvBody(h.Http, Csv);

        var result = await h.Controller.PushCatalog("someone-elses-slug", h.SupplierId, h.Catalog, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        (await h.Db.SupplierProducts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Push_UnknownSupplier_Returns404()
    {
        var h = Build();
        SetRawCsvBody(h.Http, Csv);

        var result = await h.Controller.PushCatalog(Slug, Guid.NewGuid(), h.Catalog, CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Push_ForeignSupplier_Returns404_AndImportsNothing()
    {
        var h = Build();
        var foreign = new Supplier { Id = Guid.NewGuid(), OrgId = Guid.NewGuid(), Name = "Foreign", CreatedAt = DateTime.UtcNow };
        h.Db.Suppliers.Add(foreign);
        await h.Db.SaveChangesAsync();
        SetRawCsvBody(h.Http, Csv);

        var result = await h.Controller.PushCatalog(Slug, foreign.Id, h.Catalog, CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>("a wrong-org supplier GUID must 404, never import");
        (await h.Db.SupplierProducts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Push_SoftDeletedSupplier_Returns404()
    {
        var h = Build();
        var supplier = await h.Db.Suppliers.SingleAsync(s => s.Id == h.SupplierId);
        supplier.DeletedAt = DateTime.UtcNow;
        await h.Db.SaveChangesAsync();
        SetRawCsvBody(h.Http, Csv);

        var result = await h.Controller.PushCatalog(Slug, h.SupplierId, h.Catalog, CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    // ── body validation / caps ────────────────────────────────────────────────

    [Fact]
    public async Task Push_UnsupportedContentType_Returns415()
    {
        var h = Build();
        SetRawCsvBody(h.Http, "{\"rows\": []}", contentType: "application/json");

        var result = await h.Controller.PushCatalog(Slug, h.SupplierId, h.Catalog, CancellationToken.None);

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(StatusCodes.Status415UnsupportedMediaType,
            "JSON bodies are explicitly out of scope for v1");
    }

    [Fact]
    public async Task Push_NoCodeColumn_Returns400()
    {
        var h = Build();
        SetRawCsvBody(h.Http, "name,price\nThing,1.00\n");

        var result = await h.Controller.PushCatalog(Slug, h.SupplierId, h.Catalog, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Push_OverRowCap_Returns400()
    {
        // Cap-agnostic: reference SupplierCatalogFileParser.MaxCatalogRows so this test tracks
        // the real limit (raised 50k → 200k in plan 2026-07-02 P0.8 for real distributor feeds)
        // instead of a hardcoded threshold that silently rots when the cap moves.
        var h = Build();
        var sb = new StringBuilder("code\n", capacity: (SupplierCatalogFileParser.MaxCatalogRows + 1) * 8);
        for (var i = 0; i <= SupplierCatalogFileParser.MaxCatalogRows; i++) sb.Append("C-").Append(i).Append('\n');
        SetRawCsvBody(h.Http, sb.ToString());

        var result = await h.Controller.PushCatalog(Slug, h.SupplierId, h.Catalog, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        string error = ((dynamic)bad.Value!).error;
        error.Should().Contain("row limit");
        (await h.Db.SupplierProducts.CountAsync()).Should().Be(0, "the cap aborts before any upsert");
    }

    [Fact]
    public async Task Push_EmptyMultipart_Returns400()
    {
        var h = Build();
        h.Http.Request.ContentType = "multipart/form-data; boundary=test-boundary";
        h.Http.Request.Form = new FormCollection(
            new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(), new FormFileCollection());

        var result = await h.Controller.PushCatalog(Slug, h.SupplierId, h.Catalog, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ── pipeline-enforced contracts pinned by attribute ───────────────────────

    [Fact]
    public void Push_Route_IsGuidConstrained_SoNonGuidSegmentsAre404()
    {
        var method = typeof(IngressController).GetMethod(nameof(IngressController.PushCatalog))!;
        var post = method.GetCustomAttribute<HttpPostAttribute>()!;
        post.Template.Should().Be("catalog/{supplierId:guid}",
            "the :guid route constraint is what 404s a non-GUID supplier segment before binding");
    }

    [Fact]
    public void Push_RequestSizeLimit_IsTheSharedIngressCap()
    {
        var method = typeof(IngressController).GetMethod(nameof(IngressController.PushCatalog))!;
        var attrData = method.GetCustomAttributesData()
            .Single(a => a.AttributeType == typeof(RequestSizeLimitAttribute));
        attrData.ConstructorArguments[0].Value.Should().Be(IngressLimits.MaxFileBytes,
            "the body cap (413) is enforced by the server from this attribute");
    }

    [Fact]
    public void Push_IsRateLimited_WithUploadPolicy()
    {
        var method = typeof(IngressController).GetMethod(nameof(IngressController.PushCatalog))!;
        var attr = method.GetCustomAttribute<EnableRateLimitingAttribute>()!;
        attr.PolicyName.Should().Be("upload");
    }
}
