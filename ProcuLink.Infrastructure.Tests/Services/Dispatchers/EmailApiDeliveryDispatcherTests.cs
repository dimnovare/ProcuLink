using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Tests.TestDoubles;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services.Dispatchers;

// ════════════════════════════════════════════════════════════════════════════
//  EmailApiDeliveryDispatcher — the 'email' delivery protocol. Sends the
//  generated artifact as an attachment via IEmailApiClient. These tests pin the
//  config translation (CSV vs JSON-array recipients, template substitution,
//  From / ReplyTo defaults), the unconfigured + malformed-config + no-recipient
//  guards, and the SendAsync result→DeliveryResult mapping. Uses FakeEmailApiClient
//  (no Postmark) so the exact EmailApiMessage built can be asserted.
// ════════════════════════════════════════════════════════════════════════════
public class EmailApiDeliveryDispatcherTests
{
    private static EmailApiDeliveryDispatcher MakeDispatcher(FakeEmailApiClient fake) =>
        new(fake, NullLogger<EmailApiDeliveryDispatcher>.Instance);

    private static SupplierDeliveryConfig MakeConfig(object configShape) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            Protocol = "email",
            ConfigJson = JsonSerializer.Serialize(configShape),
            EncryptedCredentials = string.Empty,
        };

    private static SupplierDeliveryConfig MakeConfigRaw(string rawJson) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            Protocol = "email",
            ConfigJson = rawJson,
            EncryptedCredentials = string.Empty,
        };

    private static Task<ProcuLink.Core.Services.Delivery.DeliveryResult> Dispatch(
        EmailApiDeliveryDispatcher dispatcher, SupplierDeliveryConfig config, string fileName = "PO-1.xml") =>
        dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes("<Order/>"), fileName, "application/xml", config, string.Empty, default);

    // 1. Email API not configured on this deploy → failure, no send.
    [Fact]
    public async Task NotConfigured_ReturnsFailure()
    {
        var fake = new FakeEmailApiClient { IsConfigured = false };
        var dispatcher = MakeDispatcher(fake);

        var result = await Dispatch(dispatcher, MakeConfig(new { toAddresses = "a@x.example" }));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not configured");
        fake.LastMessage.Should().BeNull();
    }

    // 2. CSV recipients → split + trimmed; one attachment named == fileName; default subject.
    [Fact]
    public async Task CsvRecipients_BuildsMessageWithDefaultSubjectAndAttachment()
    {
        var fake = new FakeEmailApiClient();
        var dispatcher = MakeDispatcher(fake);

        var result = await Dispatch(dispatcher, MakeConfig(new { toAddresses = "a@x.example, b@y.example" }), "PO-1.xml");

        result.Success.Should().BeTrue();
        fake.LastMessage.Should().NotBeNull();
        fake.LastMessage!.To.Should().Equal("a@x.example", "b@y.example");
        fake.LastMessage.Attachments.Should().ContainSingle();
        fake.LastMessage.Attachments![0].FileName.Should().Be("PO-1.xml");
        fake.LastMessage.Subject.Should().Be("Purchase Order PO-1");
    }

    // 3. JSON-array recipients → parsed directly.
    [Fact]
    public async Task JsonArrayRecipients_ParsedToList()
    {
        var fake = new FakeEmailApiClient();
        var dispatcher = MakeDispatcher(fake);

        var result = await Dispatch(dispatcher, MakeConfig(new { toAddresses = new[] { "a@x.example" } }));

        result.Success.Should().BeTrue();
        fake.LastMessage!.To.Should().Equal("a@x.example");
    }

    // 4. No recipients → failure, no send.
    [Fact]
    public async Task EmptyRecipients_ReturnsFailure()
    {
        var fake = new FakeEmailApiClient();
        var dispatcher = MakeDispatcher(fake);

        var result = await Dispatch(dispatcher, MakeConfig(new { toAddresses = "" }));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("no valid recipient");
        fake.LastMessage.Should().BeNull();
    }

    // 5. Malformed config JSON → failure, no send.
    [Fact]
    public async Task MalformedConfigJson_ReturnsParseFailure()
    {
        var fake = new FakeEmailApiClient();
        var dispatcher = MakeDispatcher(fake);

        var result = await Dispatch(dispatcher, MakeConfigRaw("{not json"));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("could not be parsed");
        fake.LastMessage.Should().BeNull();
    }

    // 6. Subject template {poNumber} substituted from filename-without-ext; ReplyTo passed through.
    [Fact]
    public async Task SubjectTemplateAndReplyTo_AreApplied()
    {
        var fake = new FakeEmailApiClient();
        var dispatcher = MakeDispatcher(fake);

        var config = MakeConfig(new
        {
            toAddresses = "a@x.example",
            subjectTemplate = "PO {poNumber}",
            replyTo = "buyer@proculink.eu",
        });

        var result = await Dispatch(dispatcher, config, "PO-123.xml");

        result.Success.Should().BeTrue();
        fake.LastMessage!.Subject.Should().Be("PO PO-123");
        fake.LastMessage.ReplyTo.Should().Be("buyer@proculink.eu");
    }

    // 7. From defaults to client.DefaultFrom; custom fromAddress is honoured.
    [Fact]
    public async Task FromAddress_DefaultsToClientDefault_OrCustomWhenSet()
    {
        var fake = new FakeEmailApiClient { DefaultFrom = "platform@proculink.eu" };
        var dispatcher = MakeDispatcher(fake);

        await Dispatch(dispatcher, MakeConfig(new { toAddresses = "a@x.example" }));
        fake.LastMessage!.From.Should().Be("platform@proculink.eu");

        await Dispatch(dispatcher, MakeConfig(new { toAddresses = "a@x.example", fromAddress = "x@x.example" }));
        fake.LastMessage!.From.Should().Be("x@x.example");
    }

    // 8. A failed SendAsync result maps straight through to DeliveryResult (error + code preserved).
    [Fact]
    public async Task SendFailure_MapsToDeliveryFailureWithCode()
    {
        var fake = new FakeEmailApiClient
        {
            ResultToReturn = new ProcuLink.Core.Services.Email.EmailApiResult(false, "boom", 502),
        };
        var dispatcher = MakeDispatcher(fake);

        var result = await Dispatch(dispatcher, MakeConfig(new { toAddresses = "a@x.example" }));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("boom");
        result.ResponseCode.Should().Be(502);
    }

    // 9. Bounce attribution — the link the whole bounce webhook hangs from.
    //
    // Postmark's Bounce / SpamComplaint payloads echo METADATA and do NOT echo custom MIME headers,
    // so the deterministic Message-ID above cannot carry the correlation. If this stamp is missing,
    // every real bounce arrives unattributable and the webhook is a check that cannot fire — while
    // every test of the webhook itself, which supplies the key by hand, still passes.
    [Fact]
    public async Task Dispatch_StampsTheDeliveryKeyIntoProviderMetadata_SoABounceCanFindTheOrder()
    {
        var fake = new FakeEmailApiClient();
        var dispatcher = MakeDispatcher(fake);

        await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes("<Order/>"), "PO-1.xml", "application/xml",
            MakeConfig(new { toAddresses = "a@x.example" }), string.Empty, default,
            idempotencyKey: "delivery-key-abc");

        fake.LastMessage!.Metadata.Should().NotBeNull(
            "a bounce webhook has no other way to reach the order — Postmark echoes metadata, not headers");
        fake.LastMessage.Metadata!
            .Should().ContainKey(ProcuLink.Core.Services.Delivery.DeliveryBounceMetadata.IdempotencyKeyField)
            .WhoseValue.Should().Be("delivery-key-abc",
                "the stamped value must be the delivery idempotency key, which is what " +
                "DeliveryAttempt.IdempotencyKey already stores");
    }

    // 10. A TEST FIRE has no order, so there is nothing to attribute and nothing to stamp. Without
    // this, a change that stamped a constant or a fabricated key would look correct in test 9.
    [Fact]
    public async Task Dispatch_WithNoDeliveryKey_StampsNoMetadata()
    {
        var fake = new FakeEmailApiClient();
        var dispatcher = MakeDispatcher(fake);

        await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes("<Order/>"), "PO-1.xml", "application/xml",
            MakeConfig(new { toAddresses = "a@x.example" }), string.Empty, default,
            idempotencyKey: null, isTestFire: true);

        fake.LastMessage!.Metadata.Should().BeNull(
            "a test fire has no order, so a bounce for it must stay honestly unattributable");
    }
}
