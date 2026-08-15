using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Email;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services.Email;
using ProcuLink.TestSupport;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Drives a real Postmark webhook body through the REAL controller and the REAL router, and
/// asserts what a human can see afterwards.
///
/// <para>WHY THIS RUNS END TO END. The defect lived in the seam between the two: the controller's
/// <c>DecodeBase64</c> answered <c>Array.Empty&lt;byte&gt;()</c> both for a body it could not
/// decode and for a body that was genuinely absent, and the router — which by then had no way to
/// tell those apart — skipped with a log line and no audit row. Neither half is wrong on its own,
/// so neither half can be tested on its own: a router test cannot even express "the base64 was
/// corrupt" unless the contract carries that fact, and a controller test stops one call short of
/// the evidence. The base64 string is the input and the audit row is the output, so the test spans
/// both.</para>
///
/// <para>The starting condition is verbatim: a corrupt-base64 attachment produced NO order and NO
/// audit row, the webhook answered 200 so the provider never re-delivered, and the only trace was
/// a server log the customer cannot see and the operator has no surface for.</para>
/// </summary>
public class InboundEmailAttachmentDecodeTests
{
    private const string WebhookToken = "pm-inbound-secret-123";
    private const string AddressToken = "acme";
    private const string Recipient = "orders@acme.proculink.eu";

    /// <summary>Not valid base64 under any padding: '!' is outside the alphabet.</summary>
    private const string CorruptBase64 = "!!!this-is-not-base64!!!";

    private const string UndecodableAction = "inbound_email.attachment_skipped_undecodable";
    private const string EmptyAction = "inbound_email.attachment_skipped_empty";

    // ── The regression ───────────────────────────────────────────────────────

    [Fact]
    public async Task CorruptBase64Attachment_CreatesNoOrder_AndLeavesADurableAuditRow()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db);
        await SeedSupplierAsync(db, orgId);
        var (controller, orders) = BuildController(db, orgId);

        var result = await controller.Postmark(BodyWith(CorruptBase64), CancellationToken.None);

        // The webhook still answers 200 — see the class remarks on InboundEmailController.Ignored
        // and InboundEmailRejectionKind. That is deliberate and is NOT what this test is defending;
        // the audit row is.
        Assert.IsType<OkObjectResult>(result);

        orders.Created.Should().BeEmpty("an attachment whose bytes could not be recovered has nothing to parse");

        var actions = await ActionsAsync(db, orgId);
        actions.Should().Contain(UndecodableAction,
            "before this fix the skip wrote only a server log, so a purchase order lost to a corrupt "
            + "attachment was invisible from every position a customer or an operator can occupy");
    }

    /// <summary>
    /// The floor under the assertion above. Without it, "no order was created" would also pass on a
    /// harness that cannot create an order at all, and the regression test would be measuring
    /// nothing.
    /// </summary>
    [Fact]
    public async Task ValidBase64Attachment_StillCreatesAnOrder_AndWritesNoSkipRow()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db);
        await SeedSupplierAsync(db, orgId);
        var (controller, orders) = BuildController(db, orgId);

        var csv = Convert.ToBase64String("po,qty\r\nPO-1,5\r\n"u8.ToArray());

        var result = await controller.Postmark(BodyWith(csv), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        orders.Created.Should().ContainSingle().Which.FileName.Should().Be("po.csv");

        var actions = await ActionsAsync(db, orgId);
        actions.Should().NotContain(UndecodableAction);
        actions.Should().NotContain(EmptyAction);
    }

    // ── The two causes are told apart, in both directions ────────────────────

    [Fact]
    public async Task CorruptBase64Attachment_IsNeverRecordedAsAnEmptyOne()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db);
        await SeedSupplierAsync(db, orgId);
        var (controller, _) = BuildController(db, orgId);

        await controller.Postmark(BodyWith(CorruptBase64), CancellationToken.None);

        var actions = await ActionsAsync(db, orgId);
        actions.Should().NotContain(EmptyAction,
            "'we could not decode what they sent' and 'they sent nothing' are different facts about "
            + "a customer, and an operator chasing a missing order needs the one that is true");
    }

    [Fact]
    public async Task GenuinelyEmptyAttachment_WritesTheEmptyRow_NotTheUndecodableOne()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db);
        await SeedSupplierAsync(db, orgId);
        var (controller, orders) = BuildController(db, orgId);

        // A zero-byte file: Postmark encodes it as an empty Content string.
        await controller.Postmark(BodyWith(string.Empty), CancellationToken.None);

        orders.Created.Should().BeEmpty();

        var actions = await ActionsAsync(db, orgId);
        actions.Should().Contain(EmptyAction);
        actions.Should().NotContain(UndecodableAction,
            "nothing failed to decode here — the sender really did attach an empty file");
    }

    // ── The undecodable bytes stop at the decode site ────────────────────────

    [Fact]
    public async Task CorruptBase64_ReachesTheRouterAsUndecodable_CarryingNoneOfTheOffendingBytes()
    {
        // The router half is asserted above through a real database. This pins the OTHER half of
        // the seam directly: what the controller hands over. The offending string is a customer's
        // file content, so it must not travel past the decoder in any form.
        var captured = new CapturingRouter();
        var controller = BuildController(captured);

        await controller.Postmark(BodyWith(CorruptBase64), CancellationToken.None);

        var attachment = captured.Payload!.Attachments.Should().ContainSingle().Subject;
        attachment.Decode.Should().Be(InboundAttachmentDecode.Undecodable);
        attachment.Content.Should().BeEmpty("there is nothing to carry, and the raw base64 must not travel");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AbsentOrBlankContent_ReachesTheRouterAsDecoded_NotAsAFailure(string? content)
    {
        var captured = new CapturingRouter();
        var controller = BuildController(captured);

        await controller.Postmark(BodyWith(content), CancellationToken.None);

        var attachment = captured.Payload!.Attachments.Should().ContainSingle().Subject;
        attachment.Decode.Should().Be(InboundAttachmentDecode.Decoded,
            "an absent body decodes cleanly to nothing — it is an empty attachment, not a decode failure");
        attachment.Content.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidBase64_ReachesTheRouterDecoded_WithTheSendersBytes()
    {
        var captured = new CapturingRouter();
        var controller = BuildController(captured);
        var bytes = "po,qty\r\nPO-1,5\r\n"u8.ToArray();

        await controller.Postmark(BodyWith(Convert.ToBase64String(bytes)), CancellationToken.None);

        var attachment = captured.Payload!.Attachments.Should().ContainSingle().Subject;
        attachment.Decode.Should().Be(InboundAttachmentDecode.Decoded);
        attachment.Content.Should().Equal(bytes);
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static InboundEmailController.PostmarkInboundPayload BodyWith(string? attachmentContent) => new()
    {
        From = "buyer@example.com",
        To = Recipient,
        Subject = "PO #12345",
        MessageID = Guid.NewGuid().ToString(),
        Attachments = new List<InboundEmailController.PostmarkInboundAttachment>
        {
            new() { Name = "po.csv", ContentType = "text/csv", Content = attachmentContent },
        },
    };

    private static async Task<List<string>> ActionsAsync(ProcuLinkDbContext db, Guid orgId) =>
        await db.AuditEvents.AsNoTracking()
            .Where(a => a.OrgId == orgId)
            .Select(a => a.Action)
            .ToListAsync();

    /// <summary>Controller wired to the REAL router over <paramref name="db"/>.</summary>
    private static (InboundEmailController Controller, RecordingOrderCreator Orders) BuildController(
        ProcuLinkDbContext db, Guid orgId)
    {
        var config = Configuration();
        InboundAddressTestHarness.SeedAddress(db, orgId, AddressToken, config);

        var orders = new RecordingOrderCreator();
        var router = new InboundEmailRouter(
            db,
            orders,
            new NoOpEnqueuer(),
            NoOpBodyExtractor.Instance,
            InboundAddressTestHarness.Create(db, config),
            TestDoubles.PermissiveBilling.Service(),
            config,
            NullLogger<InboundEmailRouter>.Instance);

        return (Build(router, config), orders);
    }

    /// <summary>Controller wired to a capturing stub, for asserting what the controller hands over.</summary>
    private static InboundEmailController BuildController(IInboundEmailRouter router) =>
        Build(router, Configuration());

    private static InboundEmailController Build(IInboundEmailRouter router, IConfiguration config)
    {
        var http = new DefaultHttpContext();
        http.Request.Query = new QueryCollection(
            new Dictionary<string, StringValues> { ["token"] = WebhookToken });

        return new InboundEmailController(router, config, NullLogger<InboundEmailController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static IConfiguration Configuration() =>
        InboundAddressTestHarness.Configuration(new Dictionary<string, string?>
        {
            ["Inbound:Postmark:WebhookToken"] = WebhookToken,
        });

    private static async Task<Guid> SeedOrgAsync(ProcuLinkDbContext db)
    {
        var orgId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_{orgId:N}",
            Name = "Acme Distribution",
            AccountStatus = AccountStatusConstants.Active,
            CreatedAt = DateTime.UtcNow,
            EmailConfigJson = "{}",
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    private static async Task<Guid> SeedSupplierAsync(ProcuLinkDbContext db, Guid orgId)
    {
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier
        {
            Id = supplierId,
            OrgId = orgId,
            Name = "Acme Components",
            CreatedAt = DateTime.UtcNow,
        });

        var org = await db.Organisations.SingleAsync(o => o.Id == orgId);
        org.EmailConfigJson = (EmailPollingConfig.Empty with { DefaultSupplierId = supplierId }).ToJson();

        await db.SaveChangesAsync();
        return supplierId;
    }

    private static ProcuLinkDbContext CreateDb() =>
        new InboundEmailDecodeTestDbContext(
            new DbContextOptionsBuilder<ProcuLinkDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

    // ── Doubles ─────────────────────────────────────────────────────────────

    private sealed class CapturingRouter : IInboundEmailRouter
    {
        public InboundEmailPayload? Payload { get; private set; }

        public Task<InboundEmailResult> RouteAsync(InboundEmailPayload payload, CancellationToken ct)
        {
            Payload = payload;
            return Task.FromResult(new InboundEmailResult(
                Success: true, OrgId: Guid.NewGuid(), CreatedOrderIds: Array.Empty<Guid>(), Error: null));
        }
    }

    /// <summary>Records every stub the router asked for, and succeeds.</summary>
    private sealed class RecordingOrderCreator : IClaimedOrderCreator
    {
        public List<(Guid OrgId, Guid? SupplierId, string FileName, long Size)> Created { get; } = new();

        public Task<Result<PurchaseOrderEntity>> CreateClaimedStubAsync(
            Guid organisationId, Guid? supplierId, Guid orderId, Stream fileStream, string filename,
            string contentType, string? inboundSenderDomain, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            fileStream.CopyTo(ms);
            Created.Add((organisationId, supplierId, filename, ms.Length));

            return Task.FromResult(Result<PurchaseOrderEntity>.Ok(new PurchaseOrderEntity
            {
                Id = orderId,
                OrgId = organisationId,
                SupplierId = supplierId,
                Status = "parsing",
                SourceFileKey = $"{organisationId}/{orderId}/{filename}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }));
        }

        public Task<Result<PurchaseOrderEntity>> CreateClaimedFromParsedOrderAsync(
            Guid organisationId, Guid? supplierId, Guid orderId, ExtractedOrder order, string source,
            string? inboundSenderDomain, CancellationToken ct) =>
            throw new InvalidOperationException(
                "The body-NLP path must not run: these messages carry no body.");
    }

    private sealed class NoOpEnqueuer : IParseJobEnqueuer
    {
        public Task EnqueueAsync(Guid orderId, Guid orgId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoOpBodyExtractor : IEmailBodyOrderExtractor
    {
        public static readonly NoOpBodyExtractor Instance = new();

        public Task<EmailBodyExtractionResult> ExtractAsync(string emailBody, CancellationToken ct) =>
            Task.FromResult(new EmailBodyExtractionResult(
                Success: false, Confidence: 0, Order: null, FailureReason: "no-op fake"));
    }

    /// <summary>
    /// Materialises only what this path touches: Organisations, Suppliers, AuditEvents, the
    /// inbound-address table and the import-claim ledger. Same shape as the router suite's own
    /// context, for the same reason — nothing else has fixtures worth fabricating.
    /// </summary>
    private sealed class InboundEmailDecodeTestDbContext : ProcuLinkDbContext
    {
        public InboundEmailDecodeTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<PurchaseOrderEntity>();
            modelBuilder.Ignore<PurchaseOrderLineEntity>();
            modelBuilder.Ignore<OrderParty>();
            modelBuilder.Ignore<SourceCapture>();
            modelBuilder.Ignore<CanonicalFieldDef>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<OutboundArtifact>();
            modelBuilder.Ignore<DeliveryAttempt>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<SupplierDeliveryConfig>();
            modelBuilder.Ignore<IdempotencyKey>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();
            modelBuilder.Ignore<AiUsageMonthly>();
            modelBuilder.Ignore<PoPassportEvent>();
            modelBuilder.Ignore<SftpIngressConfig>();
            modelBuilder.Ignore<ImportedSftpFile>();
            modelBuilder.Ignore<S3IngressConfig>();
            modelBuilder.Ignore<ImportedS3Object>();
            modelBuilder.Ignore<Buyer>();
            modelBuilder.Ignore<InvoiceEntity>();
            modelBuilder.Ignore<InvoiceLineEntity>();
            modelBuilder.Ignore<AdvanceShippingNoticeEntity>();
            modelBuilder.Ignore<AsnPackageEntity>();
            modelBuilder.Ignore<AsnPackageLineEntity>();

            modelBuilder.Entity<Organisation>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Memberships);
                b.Ignore(x => x.PurchaseOrders);
                b.Ignore(x => x.ItemMappings);
                b.Ignore(x => x.OutboundArtifacts);
                b.Ignore(x => x.DeliveryAttempts);
                b.Ignore(x => x.AuditEvents);
                b.Ignore(x => x.ApiKeys);
                b.Ignore(x => x.IntegrationSubscriptions);
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
            });

            modelBuilder.Entity<AuditEvent>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.User);
                // JsonDocument is not supported by InMemory. The payload's own contract — no
                // attachment bytes, no raw sender or subject — is pinned by
                // InboundEmailRouterTests' BuildAuditSummary tests, which do not need a database.
                b.Ignore(x => x.Payload);
            });
        }
    }
}
