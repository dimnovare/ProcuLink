using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Ai;

namespace ProcuLink.Infrastructure.Tests.Services.Ai;

/// <summary>
/// Tests for the Build #1 schema inferencer scaffold.
/// All tests use the no-op path (no OpenAI key) or a mocked
/// <see cref="IAiUsageTracker"/> — none of them hit the live OpenAI API.
///
/// Mirrors the in-memory DbContext pattern from AiUsageTrackerTests so the
/// AiUsageTracker double can run against a real EF backing store when needed.
/// </summary>
public class OpenAiSchemaInferencerTests
{
    // ── Inference path ───────────────────────────────────────────────────────

    [Fact]
    public async Task InferSchemaAsync_NoApiKey_ReturnsEmptySchema()
    {
        // No Ai:OpenAI:ApiKey configured → inferencer is in no-op mode.
        var inferencer = CreateInferencer(
            config: new Dictionary<string, string?>
            {
                ["Ai:Provider"] = "openai"
            },
            tracker: null,
            orgId:   Guid.NewGuid());

        await using var sample = StreamOf("PoNumber,OrderDate\nPO-1,2026-05-28\n");
        var result = await inferencer.InferSchemaAsync(sample, "sample.csv", "text/csv", CancellationToken.None);

        result.Should().NotBeNull();
        result.Fields.Should().BeEmpty("no-op when Ai:OpenAI:ApiKey is missing");
        result.SampleRows.Should().BeEmpty();
        result.FileType.Should().Be("unknown");
    }

    [Fact]
    public async Task InferSchemaAsync_NonOpenAiProvider_ReturnsEmptySchema()
    {
        // Provider != openai → still in no-op mode even with an api key configured.
        var inferencer = CreateInferencer(
            config: new Dictionary<string, string?>
            {
                ["Ai:Provider"]      = "anthropic",
                ["Ai:OpenAI:ApiKey"] = "sk-test-key",
            },
            tracker: null,
            orgId:   Guid.NewGuid());

        await using var sample = StreamOf("{ \"poNumber\": \"PO-1\" }");
        var result = await inferencer.InferSchemaAsync(sample, "sample.json", "application/json", CancellationToken.None);

        result.Fields.Should().BeEmpty("provider is not openai");
        result.FileType.Should().Be("unknown");
    }

    [Fact]
    public async Task InferSchemaAsync_AtOrOverCap_DoesNotCallOpenAiAndReturnsEmpty()
    {
        // Provider configured + key configured, but the tracker says the org
        // is over its monthly cap → inferencer must short-circuit BEFORE
        // dispatching any OpenAI call.
        var orgId = Guid.NewGuid();
        var tracker = new Mock<IAiUsageTracker>(MockBehavior.Strict);
        tracker.Setup(t => t.IsAtOrOverLimitAsync(orgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);
        // The blocked-path log line resolves the org's limit via the snapshot.
        tracker.Setup(t => t.GetCurrentAsync(orgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new AiUsageSnapshot(orgId, 2026, 6, 1000, 1000));

        var inferencer = CreateInferencer(
            config: new Dictionary<string, string?>
            {
                ["Ai:Provider"]      = "openai",
                ["Ai:OpenAI:ApiKey"] = "sk-test-key",
            },
            tracker: tracker.Object,
            orgId:   orgId);

        await using var sample = StreamOf("PoNumber,OrderDate\nPO-1,2026-05-28\n");
        var result = await inferencer.InferSchemaAsync(sample, "sample.csv", "text/csv", CancellationToken.None);

        result.Fields.Should().BeEmpty("the per-org cap blocks the inference call");
        tracker.Verify(t => t.IsAtOrOverLimitAsync(orgId, It.IsAny<CancellationToken>()), Times.Once);
        // IncrementAsync MUST NOT have been called — no OpenAI call happened.
        tracker.Verify(
            t => t.IncrementAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task InferSchemaAsync_TrackerCapCheckFailure_FailsSafeWithEmptySchema()
    {
        // If the cap check throws, the inferencer must treat the org as
        // "blocked" rather than silently bypass the cap.
        var orgId = Guid.NewGuid();
        var tracker = new Mock<IAiUsageTracker>(MockBehavior.Strict);
        tracker.Setup(t => t.IsAtOrOverLimitAsync(orgId, It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("simulated DB outage"));

        var inferencer = CreateInferencer(
            config: new Dictionary<string, string?>
            {
                ["Ai:Provider"]      = "openai",
                ["Ai:OpenAI:ApiKey"] = "sk-test-key",
            },
            tracker: tracker.Object,
            orgId:   orgId);

        await using var sample = StreamOf("PoNumber,OrderDate\nPO-1,2026-05-28\n");
        var result = await inferencer.InferSchemaAsync(sample, "sample.csv", "text/csv", CancellationToken.None);

        result.Fields.Should().BeEmpty("cap-check failure must fail safe, not bypass the cap");
    }

    // ── No-egress gate (Organisation.SelfHostedOcr) ──────────────────────────

    [Fact]
    public async Task InferSchemaAsync_NoEgressOrg_DoesNotCallOpenAiAndReturnsEmpty()
    {
        // Provider + key configured, but the org opted into no-egress (self-hosted OCR).
        // The inferencer must short-circuit BEFORE the cap check or any OpenAI dispatch.
        // A strict tracker (no setups) proves the cap check is never even reached.
        var orgId = Guid.NewGuid();
        var tracker = new Mock<IAiUsageTracker>(MockBehavior.Strict);

        var inferencer = CreateInferencer(
            config: new Dictionary<string, string?>
            {
                ["Ai:Provider"]      = "openai",
                ["Ai:OpenAI:ApiKey"] = "sk-test-key",
            },
            tracker:  tracker.Object,
            orgId:    orgId,
            noEgress: true);

        await using var sample = StreamOf("PoNumber,OrderDate\nPO-1,2026-05-28\n");
        var result = await inferencer.InferSchemaAsync(sample, "sample.csv", "text/csv", CancellationToken.None);

        result.Fields.Should().BeEmpty("no-egress orgs never send sample data to OpenAI");
        result.FileType.Should().Be("unknown");
        // Strict mock would have thrown if the cap check (IsAtOrOverLimitAsync) ran.
    }

    [Fact]
    public async Task ProposeMappingAsync_NoEgressOrg_ReturnsEmptyMapping()
    {
        var orgId = Guid.NewGuid();
        var tracker = new Mock<IAiUsageTracker>(MockBehavior.Strict);

        var inferencer = CreateInferencer(
            config: new Dictionary<string, string?>
            {
                ["Ai:Provider"]      = "openai",
                ["Ai:OpenAI:ApiKey"] = "sk-test-key",
            },
            tracker:  tracker.Object,
            orgId:    orgId,
            noEgress: true);

        var schema = new InferredSchema(
            FileType: "csv",
            Fields: new[]
            {
                new SchemaField("PoNumber", "PoNumber", "string", new[] { "PO-1" }),
            },
            SampleRows: Array.Empty<IReadOnlyDictionary<string,string?>>());

        var proposal = await inferencer.ProposeMappingAsync(schema, CancellationToken.None);

        proposal.Mappings.Should().BeEmpty("no-egress orgs never send field data to OpenAI");
        // Strict mock would have thrown if the cap check ran.
    }

    // ── Proposal path ────────────────────────────────────────────────────────

    [Fact]
    public async Task ProposeMappingAsync_NoApiKey_ReturnsEmptyMapping()
    {
        var inferencer = CreateInferencer(
            config: new Dictionary<string, string?>
            {
                ["Ai:Provider"] = "openai"
            },
            tracker: null,
            orgId:   Guid.NewGuid());

        var schema = new InferredSchema(
            FileType: "csv",
            Fields: new[]
            {
                new SchemaField("PoNumber", "PoNumber", "string", new[] { "PO-1" }),
                new SchemaField("Qty",       "Qty",       "integer", new[] { "4" }),
            },
            SampleRows: Array.Empty<IReadOnlyDictionary<string,string?>>());

        var proposal = await inferencer.ProposeMappingAsync(schema, CancellationToken.None);

        proposal.Should().NotBeNull();
        proposal.Mappings.Should().BeEmpty("no-op when Ai:OpenAI:ApiKey is missing");
    }

    [Fact]
    public async Task ProposeMappingAsync_EmptyFields_ReturnsEmptyMappingWithoutCallingTracker()
    {
        var tracker = new Mock<IAiUsageTracker>(MockBehavior.Strict);
        // No setups — Strict mode will fail the test if any tracker method is invoked.

        var inferencer = CreateInferencer(
            config: new Dictionary<string, string?>
            {
                ["Ai:Provider"]      = "openai",
                ["Ai:OpenAI:ApiKey"] = "sk-test-key",
            },
            tracker: tracker.Object,
            orgId:   Guid.NewGuid());

        var schema = new InferredSchema(
            FileType:   "csv",
            Fields:     Array.Empty<SchemaField>(),
            SampleRows: Array.Empty<IReadOnlyDictionary<string,string?>>());

        var proposal = await inferencer.ProposeMappingAsync(schema, CancellationToken.None);

        proposal.Mappings.Should().BeEmpty();
        // Strict mock would have thrown if IsAtOrOverLimitAsync or IncrementAsync had been invoked.
    }

    // ── Cap integration with the real EF tracker (in-memory DB) ────────────

    [Fact]
    public async Task InferSchemaAsync_RealTrackerOverLimit_ReturnsEmpty()
    {
        // Exercises the real AiUsageTracker (in-memory EF) to prove the
        // inferencer respects whatever IsAtOrOverLimitAsync says.
        await using var db = CreateDb();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Provider"]                            = "openai",
                ["Ai:OpenAI:ApiKey"]                       = "sk-test-key",
                ["Ai:OpenAI:MonthlyTokenLimitPerOrg"]      = "100",
            })
            .Build();

        var tracker = new AiUsageTracker(db, config);
        var orgId   = Guid.NewGuid();
        // Push the org over the cap.
        await tracker.IncrementAsync(orgId, 500);

        var inferencer = CreateInferencer(config, tracker, orgId);

        await using var sample = StreamOf("PoNumber,OrderDate\nPO-1,2026-05-28\n");
        var result = await inferencer.InferSchemaAsync(sample, "sample.csv", "text/csv", CancellationToken.None);

        result.Fields.Should().BeEmpty("real tracker reports the org is over the cap, so the call is skipped");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static OpenAiSchemaInferencer CreateInferencer(
        Dictionary<string, string?> config,
        IAiUsageTracker?            tracker,
        Guid                        orgId,
        bool                        noEgress = false)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        return CreateInferencer(cfg, tracker, orgId, noEgress);
    }

    private static OpenAiSchemaInferencer CreateInferencer(
        IConfiguration  cfg,
        IAiUsageTracker? tracker,
        Guid             orgId,
        bool             noEgress = false)
    {
        // Internal test ctor: bypasses ICurrentTenantService and lets us inject
        // a Func<Guid> for the org id. Pass overrideClient=null so the API-key
        // presence check still gates whether a ChatClient is constructed.
        // noEgressCheck lets a test force the no-egress short-circuit without a DbContext.
        return new OpenAiSchemaInferencer(
            cfg,
            NullLogger<OpenAiSchemaInferencer>.Instance,
            tracker,
            () => orgId,
            overrideClient: null,
            noEgressCheck: noEgress ? (_, _) => Task.FromResult(true) : null);
    }

    private static MemoryStream StreamOf(string text) =>
        new(Encoding.UTF8.GetBytes(text));

    private static ProcuLinkDbContext CreateDb() =>
        new SchemaInferenceTestDbContext(
            new DbContextOptionsBuilder<ProcuLinkDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

    /// <summary>
    /// Same shape as AiUsageTrackerTests' helper context — ignores every other
    /// EF entity so the in-memory database only carries the table we need.
    /// </summary>
    private sealed class SchemaInferenceTestDbContext : ProcuLinkDbContext
    {
        public SchemaInferenceTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<Organisation>();
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<Supplier>();
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
            modelBuilder.Ignore<SupplierDeliveryConfig>();
            modelBuilder.Ignore<IdempotencyKey>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();
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
            modelBuilder.Ignore<PoPassportEvent>();

            modelBuilder.Entity<AiUsageMonthly>(b =>
            {
                b.HasKey(x => new { x.OrgId, x.Year, x.Month });
            });
        }
    }
}
