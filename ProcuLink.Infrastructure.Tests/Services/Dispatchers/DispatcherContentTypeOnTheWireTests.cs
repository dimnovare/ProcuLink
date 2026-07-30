using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Erp;
using ProcuLink.Infrastructure.Services.Security;
using ProcuLink.Infrastructure.Tests.TestDoubles;

namespace ProcuLink.Infrastructure.Tests.Services.Dispatchers;

/// <summary>
/// WP-20 D5 — the content type reaches the WIRE, per dispatcher.
///
/// <para>
/// The packet added a table saying what each format is, and a test proving <c>DeliveryService</c>
/// hands the right string to a <c>RecordingDispatcher</c>. Nothing proved a dispatcher then puts it
/// anywhere. <c>git grep ContentType -- ProcuLink.Infrastructure.Tests/Services/Dispatchers/</c>
/// returned nothing, and replacing <c>contentType</c> with <c>"application/octet-stream"</c> at
/// <c>HttpDeliveryDispatcher.cs:140-141</c> left the whole suite green — a table that is right and a
/// dispatcher that ignores it, which is exactly the shape of the defect this packet exists to fix.
/// </para>
///
/// <para>
/// One test per dispatcher that TRANSMITS it: the HTTP <c>Content-Type</c> header, the email-API
/// attachment, the SMTP attachment's parsed type, and both ERP connectors (a header on Erply, a form
/// field on Directo). <c>x12</c> is used throughout deliberately — it is the format the old switch
/// mis-typed, and it is not the type any of these paths would fall back to.
/// </para>
/// </summary>
public class DispatcherContentTypeOnTheWireTests
{
    private const string X12Type = "application/edi-x12";

    // ── HTTP: the request's Content-Type header ───────────────────────────────

    [Theory]
    [InlineData("application/xml")]
    [InlineData("application/json")]
    [InlineData("text/csv")]
    [InlineData(X12Type)]
    public async Task Http_PutsTheContentTypeOnTheRequest(string contentType)
    {
        HttpRequestMessage? captured = null;
        var dispatcher = MakeHttpDispatcher(req => captured = req);

        var result = await dispatcher.DispatchAsync(
            "<Order/>"u8.ToArray(), "PO-1.x12", contentType,
            HttpConfig("https://supplier.example/orders"),
            JsonSerializer.Serialize(new { type = "none" }), default);

        result.Success.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.Content!.Headers.ContentType!.MediaType
            .Should().BeEquivalentTo(contentType,
                "the receiver gates on this header — it is the whole reason the table exists");
    }

    // ── Email API (Postmark): the attachment's declared type ──────────────────

    [Fact]
    public async Task EmailApi_PutsTheContentTypeOnTheAttachment()
    {
        var email = new FakeEmailApiClient();
        var dispatcher = new EmailApiDeliveryDispatcher(email, NullLogger<EmailApiDeliveryDispatcher>.Instance);

        var result = await dispatcher.DispatchAsync(
            "ISA*00*"u8.ToArray(), "PO-1.x12", X12Type,
            EmailConfig(), string.Empty, default);

        result.Success.Should().BeTrue();
        email.LastMessage!.Attachments.Should().ContainSingle();
        email.LastMessage.Attachments![0].ContentType.Should().Be(X12Type);
    }

    // ── SMTP (legacy): the MIME part's parsed type ────────────────────────────

    [Fact]
    public void Smtp_PutsTheContentTypeOnTheMimeAttachment()
    {
        var message = SmtpDeliveryDispatcher.BuildMessage(
            "po@buyer.test", new[] { "supplier@vendor.test" },
            "Purchase Order PO-1", "See attached.",
            "PO-1.x12", "ISA*00*"u8.ToArray(), X12Type, idempotencyKey: null);

        var attachment = message.Attachments.OfType<MimePart>().Should().ContainSingle().Which;
        attachment.ContentType.MimeType.Should().BeEquivalentTo(X12Type);
    }

    // ── Erply: an HTTP POST, so the request's Content-Type header ─────────────

    [Fact]
    public async Task Erply_PutsTheContentTypeOnTheRequest()
    {
        HttpRequestMessage? captured = null;
        var connector  = new ErplyConnector(MakeFactory(req => captured = req), NullLogger<ErplyConnector>.Instance);
        var dispatcher = new ErplyDeliveryDispatcher(new[] { (ProcuLink.Core.Services.Erp.IErpConnector)connector });

        var result = await dispatcher.DispatchAsync(
            "ISA*00*"u8.ToArray(), "PO-1.x12", X12Type,
            ErpConfig(DeliveryProtocolConstants.ErpErply,
                      JsonSerializer.Serialize(new { url = "https://erply.example/api/orders", timeoutSeconds = 30 })),
            JsonSerializer.Serialize(new { type = "none" }), default);

        result.Success.Should().BeTrue();
        captured!.Content!.Headers.ContentType!.MediaType.Should().BeEquivalentTo(X12Type);
    }

    // ── Directo: a form post, so the 'contentType' field ──────────────────────

    [Fact]
    public async Task Directo_PutsTheContentTypeInTheFormPost()
    {
        HttpRequestMessage? captured = null;
        var connector  = new DirectoConnector(MakeFactory(req => captured = req), NullLogger<DirectoConnector>.Instance);
        var dispatcher = new DirectoDeliveryDispatcher(new[] { (ProcuLink.Core.Services.Erp.IErpConnector)connector });

        var result = await dispatcher.DispatchAsync(
            "<Order/>"u8.ToArray(), "PO-1.xml", "application/xml",
            ErpConfig(DeliveryProtocolConstants.ErpDirecto,
                      JsonSerializer.Serialize(new { url = "https://login.directo.ee/xmlcore", database = "acme", timeoutSeconds = 30 })),
            JsonSerializer.Serialize(new { user = "apiuser", password = "secret" }), default);

        result.Success.Should().BeTrue();
        var body = await captured!.Content!.ReadAsStringAsync();
        body.Should().Contain("contentType=application%2Fxml");
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static HttpDeliveryDispatcher MakeHttpDispatcher(Action<HttpRequestMessage> capture)
    {
        var factory = new Moq.Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("delivery"))
               .Returns(() => new HttpClient(new CapturingHandler(_ => { })));

        return new TestableHttpDispatcher(
            factory.Object, PermissiveGuard(), new HttpClient(new CapturingHandler(capture)));
    }

    private static IHttpClientFactory MakeFactory(Action<HttpRequestMessage> capture)
    {
        var factory = new Moq.Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("delivery")).Returns(new HttpClient(new CapturingHandler(capture)));
        return factory.Object;
    }

    private static OutboundRequestGuard PermissiveGuard()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:AllowPrivateNetworkTargets"] = "true",
            })
            .Build();
        return new OutboundRequestGuard(config, NullLogger<OutboundRequestGuard>.Instance);
    }

    private static SupplierDeliveryConfig HttpConfig(string url) =>
        Config(DeliveryProtocolConstants.Http,
               JsonSerializer.Serialize(new { url, method = "POST", timeoutSeconds = 30 }));

    private static SupplierDeliveryConfig EmailConfig() =>
        Config(DeliveryProtocolConstants.Email,
               JsonSerializer.Serialize(new { toAddresses = "supplier@vendor.test" }));

    private static SupplierDeliveryConfig ErpConfig(string protocol, string configJson) =>
        Config(protocol, configJson);

    private static SupplierDeliveryConfig Config(string protocol, string configJson) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            Protocol = protocol,
            AutoDeliver = false,
            ConfigJson = configJson,
            EncryptedCredentials = string.Empty,
        };

    /// <summary>Routes the send through the fake transport, bypassing the SSRF socket handler.</summary>
    private sealed class TestableHttpDispatcher : HttpDeliveryDispatcher
    {
        private readonly HttpClient _client;

        public TestableHttpDispatcher(IHttpClientFactory factory, OutboundRequestGuard guard, HttpClient client)
            : base(factory, guard, NullLogger<HttpDeliveryDispatcher>.Instance) => _client = client;

        internal override HttpClient CreateSendClient() => _client;
    }

    private sealed class CapturingHandler(Action<HttpRequestMessage> capture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Buffer the body BEFORE capturing: HttpClient disposes the request content once the
            // send completes, and a test that reads it afterwards would see an empty stream.
            if (request.Content is not null)
                await request.Content.LoadIntoBufferAsync();

            capture(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("OK", Encoding.UTF8, "text/plain"),
            };
        }
    }
}
