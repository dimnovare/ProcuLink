using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// V9 — GET /api/ai/calibration endpoint shape: returns the org's bucket breakdown, counts, smoothed
/// accept rates, thresholds, and active flag; org-scoped; honest cold-start state.
/// </summary>
public class AiCalibrationControllerTests
{
    [Fact]
    public async Task GetCalibration_ColdStart_ReturnsInactiveEmptySummary()
    {
        var orgId = Guid.NewGuid();
        var db = MakeDb();
        var controller = Build(db, orgId);

        var result = await controller.GetCalibration(CancellationToken.None);
        var dto = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<CalibrationSummaryDto>().Subject;

        dto.IsActive.Should().BeFalse();
        dto.TotalDecisions.Should().Be(0);
        dto.MinBucketSamples.Should().Be(ConfidenceCalibration.MinBucketSamples);
        dto.MinOrgSamples.Should().Be(ConfidenceCalibration.MinOrgSamples);
        dto.Buckets.Should().HaveCount(ConfidenceCalibration.BucketLowerBounds.Count);
        dto.Buckets.All(b => b.Total == 0).Should().BeTrue();
        // Top bucket label is rendered inclusive.
        dto.Buckets.Last().Label.Should().EndWith("]");
    }

    [Fact]
    public async Task GetCalibration_WithHistory_ReportsBucketsAndActive_OrgScoped()
    {
        var orgId = Guid.NewGuid();
        var otherOrg = Guid.NewGuid();
        var db = MakeDb();

        // This org: a trusted, over-confident [0.85,0.95) bucket.
        await SeedAsync(db, orgId, 0.90, accepted: 10, rejected: 10);
        // Another org's rich history must NOT leak into this org's summary.
        await SeedAsync(db, otherOrg, 0.60, accepted: 40, rejected: 0);

        var controller = Build(db, orgId);

        var result = await controller.GetCalibration(CancellationToken.None);
        var dto = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<CalibrationSummaryDto>().Subject;

        dto.IsActive.Should().BeTrue();
        dto.TotalDecisions.Should().Be(20, "only this org's 20 verdicts count — never org B's 40");

        var b3 = dto.Buckets[3];
        b3.Label.Should().Be("[0.85, 0.95)");
        b3.Accepted.Should().Be(10);
        b3.Rejected.Should().Be(10);
        b3.Total.Should().Be(20);
        b3.IsTrusted.Should().BeTrue();
        b3.SmoothedAcceptRate.Should().BeApproximately(0.5, 1e-9);

        // The bucket org B populated ([0.5,0.7)) is empty for THIS org.
        dto.Buckets[1].Total.Should().Be(0);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AiCalibrationController Build(ProcuLinkDbContext db, Guid orgId)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);
        var svc = new ConfidenceCalibrationService(db, new MemoryCache(new MemoryCacheOptions()));
        return new AiCalibrationController(svc, tenant.Object);
    }

    private static CalibrationEndpointDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task SeedAsync(
        ProcuLinkDbContext db, Guid orgId, double confidence, int accepted, int rejected)
    {
        var rows = new List<AiSuggestionDecision>();
        for (var i = 0; i < accepted; i++)
            rows.Add(Make(orgId, i, confidence, AiSuggestionDecisionKind.Accepted));
        for (var i = 0; i < rejected; i++)
            rows.Add(Make(orgId, 100_000 + i, confidence, AiSuggestionDecisionKind.Rejected));
        db.AiSuggestionDecisions.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static AiSuggestionDecision Make(Guid orgId, int lineNumber, double confidence, string decision) => new()
    {
        Id                        = Guid.NewGuid(),
        OrgId                     = orgId,
        OrderId                   = Guid.NewGuid(),
        LineNumber                = lineNumber,
        SuggestedSupplierItemCode = "SUP-A",
        ChosenSupplierItemCode    = decision == AiSuggestionDecisionKind.Accepted ? "SUP-A" : "SUP-B",
        Confidence                = confidence,
        Decision                  = decision,
        DecidedBy                 = "user",
        DecidedAt                 = DateTime.UtcNow,
    };

    private sealed class CalibrationEndpointDbContext : ProcuLinkDbContext
    {
        public CalibrationEndpointDbContext(DbContextOptions<ProcuLinkDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<Organisation>();
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<OutboundArtifact>();
            modelBuilder.Ignore<DeliveryAttempt>();
            modelBuilder.Ignore<AuditEvent>();
            modelBuilder.Ignore<Supplier>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<SupplierDeliveryConfig>();
            modelBuilder.Ignore<IdempotencyKey>();
            modelBuilder.Ignore<AiUsageMonthly>();
            modelBuilder.Ignore<PoPassportEvent>();
            modelBuilder.Ignore<SftpIngressConfig>();
            modelBuilder.Ignore<ImportedSftpFile>();
            modelBuilder.Ignore<S3IngressConfig>();
            modelBuilder.Ignore<ImportedS3Object>();
            modelBuilder.Ignore<Buyer>();
            modelBuilder.Ignore<ValidationRule>();
            modelBuilder.Ignore<OutputTemplate>();
            modelBuilder.Ignore<InvoiceEntity>();
            modelBuilder.Ignore<InvoiceLineEntity>();
            modelBuilder.Ignore<AdvanceShippingNoticeEntity>();
            modelBuilder.Ignore<AsnPackageEntity>();
            modelBuilder.Ignore<AsnPackageLineEntity>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();
            modelBuilder.Ignore<PurchaseOrderEntity>();
            modelBuilder.Ignore<PurchaseOrderLineEntity>();

            modelBuilder.Entity<AiSuggestionDecision>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
            });
        }
    }
}
