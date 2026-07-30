using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services.StarterTemplates;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Repositories;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Detection;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// WP-12 defect D9(a) — an ordinary save from the supplier mapping editor DESTROYS the promoted
/// output.
///
/// <para><c>PUT /api/suppliers/{id}/po-mapping</c> binds <c>[FromBody] PoMappingConfig</c> and writes
/// it wholesale. The frontend's <c>PoMappingConfig</c> type carries only
/// <c>hasHeaderRecord / separator / header / lines</c> — it has never heard of <c>output</c> or
/// <c>outputTree</c> — so every save from the mapping editor (and every apply-template) silently
/// deletes the layout a promote just saved. Same defect class as the delivery-config whitelist fixed
/// in FE #43: preserve members the caller did not send instead of rebuilding the row.</para>
/// </summary>
public class SuppliersControllerPreservesPromotedOutputTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (SuppliersController Controller, Guid OrgId, ProcuLinkDbContext Db) BuildController()
    {
        var db = MakeDb();
        var orgId = Guid.NewGuid();

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var controller = new SuppliersController(
            new Mock<ISupplierProfileRepository>().Object,
            new Mock<IItemMappingService>().Object,
            db,
            tenant.Object,
            new Mock<IBillingService>().Object,
            new PoMappingService(db),               // real — persists
            new Mock<IDeliveryConfigService>().Object,
            new Mock<IDeliveryService>().Object,
            new TestDoubles.FakeAnalyticsService(),
            new Mock<IFileStorageService>().Object,
            new SourceColumnExtractor(),
            new StarterTemplateService(),           // real — loads embedded fixtures
            new Mock<ISupplierCatalogService>().Object,
            new Mock<ISupplierConnectionService>().Object);

        return (controller, orgId, db);
    }

    private static async Task<Guid> AddSupplierAsync(ProcuLinkDbContext db, Guid orgId)
    {
        var supplier = new Supplier
        {
            Id = Guid.NewGuid(), OrgId = orgId, Name = "D9 Supplier", CreatedAt = DateTime.UtcNow,
        };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }

    private static OutputNodeTemplate PromotedTree() => new()
    {
        Format = OutputFormat.Json,
        Root = OutputNode.Obj("root",
            OutputNode.FieldOf("orderNumber",
                new OutputFieldRule { OutputPath = "orderNumber", CanonicalField = "PoNumber" })),
    };

    private static OutputMappingConfig PromotedFlatOutput() => new()
    {
        Header = { ["po"] = new OutputFieldRule { OutputPath = "PromotedRef", CanonicalField = "PoNumber" } },
    };

    /// <summary>Exactly what the frontend editor sends: the four members its TS type declares.</summary>
    private static PoMappingConfig EditorBody() => new()
    {
        HasHeaderRecord = true,
        Separator = ",",
        Header = { ["PoNumber"] = new FieldMappingEntry { ExternalField = "Order No" } },
        Lines = { ["SupplierItemCode"] = new FieldMappingEntry { ExternalField = "SKU" } },
    };

    [Fact]
    public async Task SavingTheInboundMapping_DoesNotDeleteThePromotedOutputTree()
    {
        var (controller, orgId, db) = BuildController();
        var supplierId = await AddSupplierAsync(db, orgId);

        await new PoMappingService(db).UpsertAsync(orgId, supplierId, new PoMappingConfig
        {
            OutputTree = PromotedTree(),
            Output = PromotedFlatOutput(),
        }, CancellationToken.None);

        var result = await controller.UpsertPoMapping(supplierId, EditorBody(), CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        var stored = await new PoMappingService(db).GetAsync(orgId, supplierId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.NotNull(stored!.OutputTree);                                  // the layout survives
        Assert.NotNull(stored.Output);                                       // and so does the flat output
        Assert.Equal("Order No", stored.Header["PoNumber"].ExternalField);   // the edit still landed
    }

    [Fact]
    public async Task ApplyingAStarterTemplate_DoesNotDeleteThePromotedOutputTree()
    {
        var (controller, orgId, db) = BuildController();
        var supplierId = await AddSupplierAsync(db, orgId);

        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { OutputTree = PromotedTree() }, CancellationToken.None);

        var result = await controller.ApplyPoMappingTemplate(
            supplierId, new ApplyPoMappingTemplateRequest("erply"), CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        var stored = await new PoMappingService(db).GetAsync(orgId, supplierId, CancellationToken.None);
        Assert.NotNull(stored?.OutputTree);
    }

    [Fact]
    public async Task SavingAnExplicitOutputTree_StillReplacesTheStoredOne()
    {
        // Assert the DIFFERENCE: "always preserve" would make the layout un-editable. A body that
        // DOES carry a tree must overwrite.
        var (controller, orgId, db) = BuildController();
        var supplierId = await AddSupplierAsync(db, orgId);

        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { OutputTree = PromotedTree() }, CancellationToken.None);

        var replacement = new OutputNodeTemplate
        {
            Format = OutputFormat.Json,
            Root = OutputNode.Obj("root",
                OutputNode.FieldOf("replaced",
                    new OutputFieldRule { OutputPath = "replaced", CanonicalField = "PoNumber" })),
        };

        var body = EditorBody() with { OutputTree = replacement };
        Assert.IsType<OkObjectResult>(await controller.UpsertPoMapping(supplierId, body, CancellationToken.None));

        var stored = await new PoMappingService(db).GetAsync(orgId, supplierId, CancellationToken.None);
        Assert.Equal("replaced", stored!.OutputTree!.Root.Children[0].Name);
    }
}
