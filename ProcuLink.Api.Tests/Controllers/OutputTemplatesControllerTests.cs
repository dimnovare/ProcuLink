using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Endpoint contract tests for <c>GET /api/templates</c>.
///
/// Wires the REAL <see cref="OutputTemplateService"/> against an in-memory DB so the
/// projection is exercised end-to-end. Asserts that the previously-stubbed fields are now
/// populated from real stored data: <c>Config</c> is the template's stored JSON config, and
/// <c>SuppliersCount</c> is the live number of suppliers whose delivery config requires the
/// template's output format. Also asserts org-scoping (cross-tenant data never leaks).
/// </summary>
public class OutputTemplatesControllerTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new TemplatesTestDbContext(
            new DbContextOptionsBuilder<ProcuLinkDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

    private static (OutputTemplatesController Controller, Guid OrgId, ProcuLinkDbContext Db) Build()
    {
        var db    = MakeDb();
        var orgId = Guid.NewGuid();

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var controller = new OutputTemplatesController(
            new OutputTemplateService(db), // real — projects from in-memory DB
            tenant.Object);

        return (controller, orgId, db);
    }

    private static OutputTemplate AddTemplate(
        ProcuLinkDbContext db, Guid orgId, string name, string format, JsonDocument? config = null)
    {
        var now = DateTime.UtcNow;
        var t = new OutputTemplate
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            Name       = name,
            Format     = format,
            Version    = "v1.0",
            ConfigJson = config,
            CreatedAt  = now,
            UpdatedAt  = now,
        };
        db.OutputTemplates.Add(t);
        return t;
    }

    private static Supplier AddSupplier(ProcuLinkDbContext db, Guid orgId, string name = "Acme", bool deleted = false)
    {
        var s = new Supplier
        {
            Id        = Guid.NewGuid(),
            OrgId     = orgId,
            Name      = name,
            CreatedAt = DateTime.UtcNow,
            DeletedAt = deleted ? DateTime.UtcNow : null,
        };
        db.Suppliers.Add(s);
        return s;
    }

    private static void AddDeliveryConfig(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId, string? outputFormat)
    {
        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id           = Guid.NewGuid(),
            OrgId        = orgId,
            SupplierId   = supplierId,
            Protocol     = "http",
            OutputFormat = outputFormat,
            CreatedAt    = DateTime.UtcNow,
            UpdatedAt    = DateTime.UtcNow,
        });
    }

    private static List<TemplateDto> ReadDtos(IActionResult result)
    {
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        return ((IEnumerable<TemplateDto>)ok.Value!).ToList();
    }

    // ── Config ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTemplates_ReturnsRealStoredConfig_NotNull()
    {
        var (controller, orgId, db) = Build();
        AddTemplate(db, orgId, "cXML Default", "cXML",
            JsonDocument.Parse("""{"rootTag":"cXML","delimiter":",","headerRow":true}"""));
        await db.SaveChangesAsync();

        var dtos = ReadDtos(await controller.GetTemplates(CancellationToken.None));

        dtos.Should().ContainSingle();
        var dto = dtos[0];
        dto.Config.Should().NotBeNull();

        // The config round-trips as the real stored JSON object.
        var json = JsonSerializer.Serialize(dto.Config);
        using var parsed = JsonDocument.Parse(json);
        parsed.RootElement.GetProperty("rootTag").GetString().Should().Be("cXML");
        parsed.RootElement.GetProperty("delimiter").GetString().Should().Be(",");
        parsed.RootElement.GetProperty("headerRow").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetTemplates_ConfigIsNull_WhenNoneStored()
    {
        var (controller, orgId, db) = Build();
        AddTemplate(db, orgId, "Bare", "json", config: null);
        await db.SaveChangesAsync();

        var dtos = ReadDtos(await controller.GetTemplates(CancellationToken.None));

        dtos.Should().ContainSingle();
        dtos[0].Config.Should().BeNull();
    }

    // ── SuppliersCount ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTemplates_SuppliersCount_CountsSuppliersUsingTheFormat()
    {
        var (controller, orgId, db) = Build();
        var tXml = AddTemplate(db, orgId, "XML Out", "xml");
        AddTemplate(db, orgId, "CSV Out", "csv");

        // Two suppliers require xml, one requires csv.
        var s1 = AddSupplier(db, orgId, "S1");
        var s2 = AddSupplier(db, orgId, "S2");
        var s3 = AddSupplier(db, orgId, "S3");
        AddDeliveryConfig(db, orgId, s1.Id, "xml");
        AddDeliveryConfig(db, orgId, s2.Id, "xml");
        AddDeliveryConfig(db, orgId, s3.Id, "csv");
        await db.SaveChangesAsync();

        var dtos = ReadDtos(await controller.GetTemplates(CancellationToken.None));

        dtos.Single(d => d.Id == tXml.Id.ToString()).SuppliersCount.Should().Be(2);
        dtos.Single(d => d.Format == "csv").SuppliersCount.Should().Be(1);
    }

    [Fact]
    public async Task GetTemplates_SuppliersCount_IsCaseInsensitiveOnFormat()
    {
        var (controller, orgId, db) = Build();
        AddTemplate(db, orgId, "cXML Out", "cXML"); // template casing
        var s1 = AddSupplier(db, orgId);
        AddDeliveryConfig(db, orgId, s1.Id, "cxml"); // delivery-config casing differs
        await db.SaveChangesAsync();

        var dtos = ReadDtos(await controller.GetTemplates(CancellationToken.None));

        dtos.Should().ContainSingle();
        dtos[0].SuppliersCount.Should().Be(1);
    }

    [Fact]
    public async Task GetTemplates_SuppliersCount_DeduplicatesPerSupplier()
    {
        var (controller, orgId, db) = Build();
        AddTemplate(db, orgId, "XML Out", "xml");
        var s1 = AddSupplier(db, orgId);
        // Same supplier with two delivery configs of the same format must count once.
        AddDeliveryConfig(db, orgId, s1.Id, "xml");
        AddDeliveryConfig(db, orgId, s1.Id, "xml");
        await db.SaveChangesAsync();

        var dtos = ReadDtos(await controller.GetTemplates(CancellationToken.None));

        dtos[0].SuppliersCount.Should().Be(1);
    }

    [Fact]
    public async Task GetTemplates_SuppliersCount_ExcludesSoftDeletedSuppliers()
    {
        var (controller, orgId, db) = Build();
        AddTemplate(db, orgId, "XML Out", "xml");
        var active  = AddSupplier(db, orgId, "Active");
        var deleted = AddSupplier(db, orgId, "Gone", deleted: true);
        AddDeliveryConfig(db, orgId, active.Id, "xml");
        AddDeliveryConfig(db, orgId, deleted.Id, "xml");
        await db.SaveChangesAsync();

        var dtos = ReadDtos(await controller.GetTemplates(CancellationToken.None));

        dtos[0].SuppliersCount.Should().Be(1);
    }

    [Fact]
    public async Task GetTemplates_SuppliersCount_IsZero_WhenNoSupplierUsesTheFormat()
    {
        var (controller, orgId, db) = Build();
        AddTemplate(db, orgId, "X12 Out", "x12");
        var s1 = AddSupplier(db, orgId);
        AddDeliveryConfig(db, orgId, s1.Id, "json"); // different format
        AddDeliveryConfig(db, orgId, s1.Id, null);   // unset format ignored
        await db.SaveChangesAsync();

        var dtos = ReadDtos(await controller.GetTemplates(CancellationToken.None));

        dtos[0].SuppliersCount.Should().Be(0);
    }

    // ── org-scoping ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTemplates_IsOrgScoped_ForTemplatesAndCounts()
    {
        var (controller, orgId, db) = Build();
        var otherOrg = Guid.NewGuid();

        // This org: one xml template + one supplier using xml.
        var mine = AddTemplate(db, orgId, "Mine", "xml");
        var ms   = AddSupplier(db, orgId, "MineSupplier");
        AddDeliveryConfig(db, orgId, ms.Id, "xml");

        // Other org: its own template + a supplier also on xml — must not appear or inflate counts.
        AddTemplate(db, otherOrg, "Theirs", "xml");
        var ts = AddSupplier(db, otherOrg, "TheirSupplier");
        AddDeliveryConfig(db, otherOrg, ts.Id, "xml");
        await db.SaveChangesAsync();

        var dtos = ReadDtos(await controller.GetTemplates(CancellationToken.None));

        dtos.Should().ContainSingle("only this org's templates are returned");
        dtos[0].Id.Should().Be(mine.Id.ToString());
        dtos[0].SuppliersCount.Should().Be(1, "the other org's xml supplier must not be counted");
    }

    /// <summary>
    /// In-memory DbContext keeping the entities the templates projection touches
    /// (OutputTemplate, Supplier, SupplierDeliveryConfig) and dropping the rest, mirroring the
    /// Ignore pattern used by the other Api controller tests.
    /// </summary>
    private sealed class TemplatesTestDbContext : ProcuLinkDbContext
    {
        public TemplatesTestDbContext(DbContextOptions<ProcuLinkDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<Organisation>();
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<PurchaseOrderEntity>();
            modelBuilder.Ignore<PurchaseOrderLineEntity>();
            modelBuilder.Ignore<OrderParty>();
            modelBuilder.Ignore<SourceCapture>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<OutboundArtifact>();
            modelBuilder.Ignore<DeliveryAttempt>();
            modelBuilder.Ignore<AuditEvent>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<SupplierProduct>();
            modelBuilder.Ignore<IdempotencyKey>();
            modelBuilder.Ignore<AiUsageMonthly>();
            modelBuilder.Ignore<PoPassportEvent>();
            modelBuilder.Ignore<SftpIngressConfig>();
            modelBuilder.Ignore<ImportedSftpFile>();
            modelBuilder.Ignore<S3IngressConfig>();
            modelBuilder.Ignore<ImportedS3Object>();
            modelBuilder.Ignore<Buyer>();
            modelBuilder.Ignore<ValidationRule>();
            modelBuilder.Ignore<InvoiceEntity>();
            modelBuilder.Ignore<InvoiceLineEntity>();
            modelBuilder.Ignore<AdvanceShippingNoticeEntity>();
            modelBuilder.Ignore<AsnPackageEntity>();
            modelBuilder.Ignore<AsnPackageLineEntity>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();

            // OutputTemplate: keep the jsonb→string converter for ConfigJson so InMemory can
            // materialise it; drop the Organisation navigation.
            var jsonDocConverter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<JsonDocument?, string?>(
                v => v == null ? null : v.RootElement.GetRawText(),
                v => v == null ? null : JsonDocument.Parse(v, default));

            modelBuilder.Entity<OutputTemplate>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Property(x => x.ConfigJson).HasConversion(jsonDocConverter);
            });

            modelBuilder.Entity<Supplier>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.SupplierProfiles);
                b.Ignore(x => x.PurchaseOrders);
                b.Ignore(x => x.ItemMappings);
                b.Ignore(x => x.PoMappings);
                b.Ignore(x => x.DeliveryConfigs);
                b.Ignore(x => x.Products);
            });

            modelBuilder.Entity<SupplierDeliveryConfig>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                // Keep the Supplier navigation — the usage count filters on Supplier.DeletedAt.
                b.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId);
            });
        }
    }
}
