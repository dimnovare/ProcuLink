using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Erp;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Email;
using ProcuLink.Infrastructure.Services.Erp;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services.Delivery;

/// <summary>
/// WP-19 follow-up — the DISCRIMINATOR the bare-400 rule needs, on the channels that had none.
///
/// <para><b>The defect.</b> <c>SupplierResponseClassification</c> splits a 400 on whether the
/// supplier said WHY: with a reason it is a business rejection (<c>rejected_by_supplier</c>, queue
/// stops), bare it is an unexplained refusal (<c>delivery_failed</c>, queue keeps re-sending to the
/// real endpoint up to the cap). That rule reads <c>DeliveryResult.ResponseBody</c> — and on three
/// channels nothing ever set it. <c>ErpDeliveryDispatcherBase</c> returned
/// <c>new DeliveryResult(result.Success, result.ErrorMessage, result.ResponseCode)</c> and
/// <c>EmailApiDeliveryDispatcher</c> returned
/// <c>new DeliveryResult(false, result.Error ?? …, result.StatusCode)</c>. Both connectors had
/// already READ the body — they folded it into a summary string and threw the original away — so on
/// <c>erp_erply</c>, <c>erp_directo</c> and <c>email</c> EVERY 400 classified bare, whatever the
/// supplier actually said.</para>
///
/// <para><b>Why it matters most on exactly these three.</b> Email is the canonical outbound path
/// (Postmark over HTTPS; SMTP is retired on the host) and is live on production. Both ERP channels
/// declare <see cref="Core.Services.Delivery.ResendSafety.Unsafe"/> — the tier this codebase
/// elsewhere refuses to auto-re-send, because no dedupe signal reaches the endpoint.</para>
///
/// <para>These tests dispatch through the REAL connector/client against a faked transport, so they
/// assert the whole capture chain (HTTP response → connector result → dispatcher result) rather than
/// a hand-built double that could be made to pass by construction.</para>
/// </summary>
public class BareBadRequestDiscriminatorTests
{
    private const string ErplyRejection =
        """{"status":{"errorCode":1011},"message":"unknown buyer code BC-9 on line 3"}""";

    private const string DirectoRejection =
        """<results><Error type="1">Article ART-77 is not sold to this customer</Error></results>""";

    private const string PostmarkRejection =
        """{"ErrorCode":300,"Message":"Invalid 'To' address: not-an-address"}""";

    // ── ERP ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ErplyDispatcher_BadRequestCarryingAReason_CapturesTheSuppliersOwnWords()
    {
        var dispatcher = new ErplyDeliveryDispatcher(new IErpConnector[]
        {
            new ErplyConnector(
                Factory(HttpStatusCode.BadRequest, ErplyRejection),
                NullLogger<ErplyConnector>.Instance),
        });

        var result = await dispatcher.DispatchAsync(
            "<order/>"u8.ToArray(), "PO-1.xml", "application/xml",
            ErpConfig(DeliveryProtocolConstants.ErpErply, "https://erply.example/api/orders"),
            "{}", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ResponseCode.Should().Be(400);
        result.ResponseBody.Should().Contain("unknown buyer code BC-9",
            "the classification splits a 400 on whether the supplier said why. The connector READ " +
            "this body and dropped it, so every ERP 400 — including a real refusal of the document " +
            "— looked bare and was re-dispatched to a live endpoint that declares ResendSafety.Unsafe");
    }

    [Fact]
    public async Task DirectoDispatcher_BadRequestCarryingAReason_CapturesTheSuppliersOwnWords()
    {
        var dispatcher = new DirectoDeliveryDispatcher(new IErpConnector[]
        {
            new DirectoConnector(
                Factory(HttpStatusCode.BadRequest, DirectoRejection),
                NullLogger<DirectoConnector>.Instance),
        });

        var result = await dispatcher.DispatchAsync(
            "<order/>"u8.ToArray(), "PO-1.xml", "application/xml",
            ErpConfig(DeliveryProtocolConstants.ErpDirecto, "https://login.directo.ee/xmlcore"),
            "{}", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ResponseCode.Should().Be(400);
        result.ResponseBody.Should().Contain("is not sold to this customer");
    }

    // ── Email (the canonical, production-proven path) ──────────────────────────────────────────

    [Fact]
    public async Task EmailDispatcher_BadRequestCarryingAReason_CapturesTheProvidersOwnWords()
    {
        var dispatcher = new EmailApiDeliveryDispatcher(
            PostmarkClient(HttpStatusCode.BadRequest, PostmarkRejection),
            NullLogger<EmailApiDeliveryDispatcher>.Instance);

        var result = await dispatcher.DispatchAsync(
            "PO,DATE"u8.ToArray(), "PO-1.csv", "text/csv",
            EmailConfig(), string.Empty, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ResponseCode.Should().Be(400);
        result.ResponseBody.Should().Contain("Invalid 'To' address",
            "email is the canonical outbound channel and is live on production; a provider refusal " +
            "that names the fault must not be re-sent as though the provider had said nothing");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private static IHttpClientFactory Factory(HttpStatusCode status, string body)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("delivery"))
               .Returns(() => new HttpClient(new FixedResponseHandler(status, body)));
        return factory.Object;
    }

    private static PostmarkEmailApiClient PostmarkClient(HttpStatusCode status, string body)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(PostmarkEmailApiClient.HttpClientName))
               .Returns(() => new HttpClient(new FixedResponseHandler(status, body)));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:Postmark:ServerToken"] = "test-server-token",
            })
            .Build();

        return new PostmarkEmailApiClient(config, factory.Object, NullLogger<PostmarkEmailApiClient>.Instance);
    }

    private static SupplierDeliveryConfig ErpConfig(string protocol, string url) =>
        Config(protocol, JsonSerializer.Serialize(new { url, timeoutSeconds = 30, database = "acme" }));

    private static SupplierDeliveryConfig EmailConfig() =>
        Config(DeliveryProtocolConstants.Email,
            JsonSerializer.Serialize(new { toAddresses = new[] { "supplier@example.com" } }));

    private static SupplierDeliveryConfig Config(string protocol, string configJson) => new()
    {
        Id = Guid.NewGuid(),
        OrgId = Guid.NewGuid(),
        SupplierId = Guid.NewGuid(),
        Protocol = protocol,
        AutoDeliver = true,
        ConfigJson = configJson,
        EncryptedCredentials = string.Empty,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private sealed class FixedResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public FixedResponseHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
    }
}
