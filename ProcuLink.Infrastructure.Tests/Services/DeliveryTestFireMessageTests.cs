using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Security;
using ProcuLink.Infrastructure.Tests.TestDoubles;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// "Send a test now" on an email connection used to arrive at the supplier's real order-intake
/// address titled <c>"Purchase Order proculink-test"</c>.
///
/// <para>
/// Both email dispatchers defaulted the subject to <c>"Purchase Order " + fileNameWithoutExtension</c>
/// and the body to <c>"Please find the attached purchase order (…)"</c>. Those defaults were written
/// for the only thing that used to travel the path — an order — and the test fire, which carries the
/// fixed non-order <see cref="DeliveryTestArtifact"/>, inherited them. A human at accounts payable
/// reads a PO with a mangled number; an intake rule keyed on the prefix files it as one.
/// </para>
///
/// <para>
/// These tests assert the SUBJECT AND BODY A TEST FIRE ACTUALLY PRODUCES — end to end through the
/// real <see cref="DeliveryService.TestFireAsync"/> and the real dispatcher, read back off the
/// message handed to the email client — not that some method was consulted.
/// </para>
///
/// <para>
/// Half of what is here guards the OPPOSITE direction, because the cheap way to pass the first half
/// is to title everything a test. A real order must still be titled a purchase order, must still
/// render the operator's own template, and — the sharp case — must still be titled a purchase order
/// when its PO number happens to be <c>proculink-test</c>.
/// </para>
/// </summary>
public class DeliveryTestFireMessageTests
{
    // ── The subject and body a test fire produces, end to end ─────────────────

    [Fact]
    public async Task TestFire_SubjectSaysItIsATestAndNotAPurchaseOrder()
    {
        var (service, fake, orgId, supplierId) = await EmailSetupAsync();

        var result = await service.TestFireAsync(orgId, supplierId, default);

        result.Success.Should().BeTrue();
        fake.LastMessage.Should().NotBeNull();

        // The exact line the supplier sees in their mail list.
        fake.LastMessage!.Subject.Should().Be(
            "ProcuLink connection test — this is not a purchase order");

        // The defect, verbatim.
        fake.LastMessage.Subject.Should().NotBe("Purchase Order proculink-test");
        fake.LastMessage.Subject.Should().NotContain("Purchase Order ",
            "the subject is the part a recipient reads before opening anything — it has to disqualify "
            + "itself there, not only in the body");
    }

    [Fact]
    public async Task TestFire_BodyExplainsItselfAndAsksForNothing()
    {
        var (service, fake, orgId, supplierId) = await EmailSetupAsync();

        await service.TestFireAsync(orgId, supplierId, default);

        var body = fake.LastMessage!.TextBody;

        body.Should().NotContain("Please find the attached purchase order",
            "the defect's body default — there is no purchase order attached");
        body.Should().Contain("It is not a purchase order");
        body.Should().Contain("connection test");
        body.Should().Contain(DeliveryTestArtifact.FileName,
            "the supplier should be able to match the sentence to the file that arrived");
        body.Should().Contain("delete this message",
            "the supplier did not ask for this mail and needs to be told no action is required");
    }

    [Fact]
    public async Task TestFire_AttachesUnderTheTestArtifactsOwnName_NotAConfiguredOrderName()
    {
        // An operator who set attachmentFileName for their real orders must not have a two-line
        // sample delivered to the supplier dressed as "purchase-order.csv".
        var (service, fake, orgId, supplierId) = await EmailSetupAsync(
            attachmentFileName: "purchase-order.csv");

        await service.TestFireAsync(orgId, supplierId, default);

        fake.LastMessage!.Attachments.Should().ContainSingle();
        fake.LastMessage.Attachments![0].FileName.Should().Be(DeliveryTestArtifact.FileName);
    }

    // ── The operator's own template ───────────────────────────────────────────

    [Fact]
    public async Task TestFire_DoesNotRenderTheOperatorsSubjectOrBodyTemplate()
    {
        // Filling their PO template with test values is the same defect wearing their words:
        // "PO {poNumber} from Heinrich" merely becomes "PO proculink-test from Heinrich", which
        // still reads as an order. The decision to set it aside is disclosed in the delivery UI.
        var (service, fake, orgId, supplierId) = await EmailSetupAsync(
            subjectTemplate: "PO {poNumber} from Heinrich",
            bodyTemplate: "Your order {poNumber} is attached as {fileName}.");

        await service.TestFireAsync(orgId, supplierId, default);

        fake.LastMessage!.Subject.Should().Be(DeliveryTestArtifact.EmailSubject);
        fake.LastMessage.Subject.Should().NotContain("Heinrich");
        fake.LastMessage.Subject.Should().NotContain("PO proculink-test",
            "rendering their template with a fake PO number is not a preview of anything — a template "
            + "is a function of an order, and a test has none");

        fake.LastMessage.TextBody.Should().Be(DeliveryTestArtifact.EmailBody);
        fake.LastMessage.TextBody.Should().NotContain("Your order");
    }

    // ── The other direction: a real order is still a purchase order ───────────

    [Fact]
    public async Task RealOrder_KeepsThePurchaseOrderSubjectAndTheOperatorsTemplate()
    {
        var fake = new FakeEmailApiClient();
        var dispatcher = new EmailApiDeliveryDispatcher(fake, NullLogger<EmailApiDeliveryDispatcher>.Instance);

        // Default subject — isTestFire omitted, exactly as the order path calls it.
        await dispatcher.DispatchAsync(
            "<Order/>"u8.ToArray(), "PO-4471.xml", "application/xml",
            EmailConfig(), string.Empty, default);
        fake.LastMessage!.Subject.Should().Be("Purchase Order PO-4471");
        fake.LastMessage.TextBody.Should().Contain("Please find the attached purchase order");

        // Operator template — still rendered, still substituted.
        await dispatcher.DispatchAsync(
            "<Order/>"u8.ToArray(), "PO-4471.xml", "application/xml",
            EmailConfig(subjectTemplate: "PO {poNumber} from Heinrich"), string.Empty, default);
        fake.LastMessage!.Subject.Should().Be("PO PO-4471 from Heinrich");
    }

    [Fact]
    public async Task RealOrderNumberedLikeTheTestArtifact_IsStillTitledAPurchaseOrder()
    {
        // The sharp case, and the reason the dispatchers are TOLD it is a test rather than sniffing
        // the filename. DeliveryService.BuildFileName appends the -{8 hex} order qualifier for
        // FILE-DROP protocols only; on email an order's name is the sanitised PO number plus the
        // artifact extension. So a purchase order numbered "proculink-test", emitted as CSV, is
        // named "proculink-test.csv" — byte-identical to the test artifact's name.
        //
        // Recognition by name would title this genuine order "this is not a purchase order" and
        // invite the supplier to delete it: the defect inverted, and worse.
        var order = new PurchaseOrderEntity { Id = Guid.NewGuid(), PoNumber = "proculink-test" };
        var fileName = DeliveryService.BuildFileName(
            order, new OutboundArtifact { Format = "csv" }, "email");

        fileName.Should().Be(DeliveryTestArtifact.FileName,
            "if this ever stops colliding the trap is gone, but the flag is still the honest signal");

        var fake = new FakeEmailApiClient();
        var dispatcher = new EmailApiDeliveryDispatcher(fake, NullLogger<EmailApiDeliveryDispatcher>.Instance);

        await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes("sku,qty\r\nA-1,5\r\n"), fileName, "text/csv",
            EmailConfig(), string.Empty, default);

        fake.LastMessage!.Subject.Should().Be("Purchase Order proculink-test");
        fake.LastMessage.Subject.Should().NotBe(DeliveryTestArtifact.EmailSubject);
        fake.LastMessage.TextBody.Should().Contain("Please find the attached purchase order");
    }

    // ── Both email channels, not just the one that is easy to drive ───────────

    [Fact]
    public void BothEmailChannelsComposeTheSameTestFireMessage()
    {
        // SmtpDeliveryDispatcher cannot be driven end to end without an SMTP server, so its wording
        // is pinned at the shared composer both dispatchers now call. The duplication that used to
        // sit here is the reason this is shared: fixing one path and not the other would have left
        // "Purchase Order proculink-test" going out over SMTP.
        EmailMessageComposer.Subject(
                isTestFire: true, template: null, poNumber: "proculink-test", attachmentName: "proculink-test.csv")
            .Should().Be("ProcuLink connection test — this is not a purchase order");

        EmailMessageComposer.Subject(
                isTestFire: true, template: "PO {poNumber}", poNumber: "proculink-test", attachmentName: "x.csv")
            .Should().Be(DeliveryTestArtifact.EmailSubject);

        EmailMessageComposer.Subject(
                isTestFire: false, template: null, poNumber: "PO-9", attachmentName: "PO-9.xml")
            .Should().Be("Purchase Order PO-9");

        EmailMessageComposer.AttachmentName(isTestFire: true, configured: "orders.csv", fileName: "x.csv")
            .Should().Be(DeliveryTestArtifact.FileName);
        EmailMessageComposer.AttachmentName(isTestFire: false, configured: "orders.csv", fileName: "x.csv")
            .Should().Be("orders.csv");
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static SupplierDeliveryConfig EmailConfig(
        string? subjectTemplate = null, string? bodyTemplate = null, string? attachmentFileName = null)
    {
        var parts = new List<string> { "\"toAddresses\":\"intake@supplier.example\"" };
        if (subjectTemplate is not null) parts.Add($"\"subjectTemplate\":\"{subjectTemplate}\"");
        if (bodyTemplate is not null) parts.Add($"\"bodyTemplate\":\"{bodyTemplate}\"");
        if (attachmentFileName is not null) parts.Add($"\"attachmentFileName\":\"{attachmentFileName}\"");

        return new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            Protocol = "email",
            ConfigJson = "{" + string.Join(",", parts) + "}",
            EncryptedCredentials = string.Empty,
        };
    }

    /// <summary>
    /// A saved email delivery config plus a DeliveryService wired to the real
    /// EmailApiDeliveryDispatcher, so TestFireAsync runs its actual production path.
    /// </summary>
    private static async Task<(DeliveryService Service, FakeEmailApiClient Fake, Guid OrgId, Guid SupplierId)>
        EmailSetupAsync(
            string? subjectTemplate = null, string? bodyTemplate = null, string? attachmentFileName = null)
    {
        var db = new ProcuLinkDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        var config = EmailConfig(subjectTemplate, bodyTemplate, attachmentFileName);
        config.OrgId = orgId;
        config.SupplierId = supplierId;
        config.AutoDeliver = true;
        config.CreatedAt = DateTime.UtcNow;
        config.UpdatedAt = DateTime.UtcNow;

        db.SupplierDeliveryConfigs.Add(config);
        await db.SaveChangesAsync();

        var fake = new FakeEmailApiClient();
        var service = new DeliveryService(
            db,
            new FakeFileStorage(),
            CreateEncryption(),
            new IDeliveryDispatcher[]
            {
                new EmailApiDeliveryDispatcher(fake, NullLogger<EmailApiDeliveryDispatcher>.Instance),
            },
            new NoOpIntegrationTriggerService(),
            new FakeAnalyticsService(),
            new OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance);

        return (service, fake, orgId, supplierId);
    }

    private static DeliveryEncryptionService CreateEncryption() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build());

    private sealed class NoOpIntegrationTriggerService : IIntegrationTriggerService
    {
        public Task EnqueueAsync(Guid organisationId, string eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class FakeFileStorage : IFileStorageService
    {
        public Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct) =>
            Task.FromResult(key);
        public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) =>
            Task.FromResult($"https://files.example/{key}");
        public Task<Stream> DownloadAsync(string key, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream("PAYLOAD"u8.ToArray()));
        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }
}
