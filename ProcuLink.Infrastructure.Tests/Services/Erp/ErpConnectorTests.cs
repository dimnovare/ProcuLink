using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure.Services.Erp;

namespace ProcuLink.Infrastructure.Tests.Services.Erp;

public class ErpConnectorTests
{
    [Fact]
    public async Task ErplyConnector_PostsArtifactWithBearerAuth()
    {
        HttpRequestMessage? captured = null;
        var dispatcher = new ErplyConnector(
            MakeFactory(request => captured = request, HttpStatusCode.Accepted, "accepted"),
            NullLogger<ErplyConnector>.Instance);
        var request = MakeRequest(
            DeliveryProtocolConstants.ErpErply,
            JsonSerializer.Serialize(new
            {
                url = "https://erply.example/api/orders",
                clientCode = "ACME",
                timeoutSeconds = 30
            }),
            JsonSerializer.Serialize(new { type = "bearer", token = "secret-token" }));

        var result = await dispatcher.SendAsync(request, default);

        result.Success.Should().BeTrue();
        result.ResponseCode.Should().Be(202);
        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.ToString().Should().Be("https://erply.example/api/orders");
        captured.Headers.Authorization!.Scheme.Should().Be("Bearer");
        captured.Headers.Authorization.Parameter.Should().Be("secret-token");
        captured.Headers.GetValues("X-Erply-Client-Code").Single().Should().Be("ACME");
        captured.Headers.GetValues("X-ProcuLink-FileName").Single().Should().Be("PO-1.xml");
        (await captured.Content!.ReadAsStringAsync()).Should().Be("<order id=\"PO-1\" />");
    }

    [Fact]
    public async Task DirectoConnector_PostsXmlAsFormData()
    {
        HttpRequestMessage? captured = null;
        var dispatcher = new DirectoConnector(
            MakeFactory(request => captured = request, HttpStatusCode.OK, "OK"),
            NullLogger<DirectoConnector>.Instance);
        var request = MakeRequest(
            DeliveryProtocolConstants.ErpDirecto,
            JsonSerializer.Serialize(new
            {
                url = "https://login.directo.ee/xmlcore",
                database = "acme",
                timeoutSeconds = 30
            }),
            JsonSerializer.Serialize(new { user = "apiuser", password = "secret" }));

        var result = await dispatcher.SendAsync(request, default);

        result.Success.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.Content!.Headers.ContentType!.MediaType.Should().Be("application/x-www-form-urlencoded");

        var body = await captured.Content.ReadAsStringAsync();
        body.Should().Contain("database=acme");
        body.Should().Contain("user=apiuser");
        body.Should().Contain("password=secret");
        body.Should().Contain("filename=PO-1.xml");
        body.Should().Contain("xmldata=%3Corder+id%3D%22PO-1%22+%2F%3E");
    }

    [Fact]
    public async Task ErplyConnector_InvalidConfig_ReturnsFailure()
    {
        var dispatcher = new ErplyConnector(
            MakeFactory(_ => { }, HttpStatusCode.OK, "OK"),
            NullLogger<ErplyConnector>.Instance);
        var request = MakeRequest(DeliveryProtocolConstants.ErpErply, "{}", "{}");

        var result = await dispatcher.SendAsync(request, default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Erply connector configuration is invalid.");
    }

    private static ProcuLink.Core.Services.Erp.ErpDeliveryRequest MakeRequest(
        string protocol,
        string configJson,
        string credentialsJson) =>
        new(
            "<order id=\"PO-1\" />"u8.ToArray(),
            "PO-1.xml",
            "application/xml",
            new SupplierDeliveryConfig
            {
                Id = Guid.NewGuid(),
                OrgId = Guid.NewGuid(),
                SupplierId = Guid.NewGuid(),
                Protocol = protocol,
                AutoDeliver = true,
                ConfigJson = configJson,
                EncryptedCredentials = string.Empty,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            credentialsJson);

    private static IHttpClientFactory MakeFactory(
        Action<HttpRequestMessage> capture,
        HttpStatusCode status,
        string body)
    {
        var handler = new CapturingHttpMessageHandler(capture, status, body);
        var client = new HttpClient(handler);
        var factory = new Moq.Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("delivery")).Returns(client);
        return factory.Object;
    }

    private sealed class CapturingHttpMessageHandler(
        Action<HttpRequestMessage> capture,
        HttpStatusCode status,
        string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain")
            });
        }
    }
}
