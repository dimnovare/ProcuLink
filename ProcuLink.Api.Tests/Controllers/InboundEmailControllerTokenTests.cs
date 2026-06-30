using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Services.Email;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Verifies the Postmark inbound webhook accepts its shared token via the
/// X-Postmark-Server-Token header (back-compat), a ?token= query param, OR the
/// password half of HTTP Basic-Auth — because Postmark INBOUND webhooks cannot
/// attach custom request headers (the URL-embedded token / Basic-Auth is what
/// actually authenticates real inbound deliveries).
/// </summary>
public class InboundEmailControllerTokenTests
{
    private const string Token = "tok-secret-123";

    private static (InboundEmailController ctrl, DefaultHttpContext http) Build(bool routerSucceeds = true)
    {
        var router = new Mock<IInboundEmailRouter>();
        router.Setup(r => r.RouteAsync(It.IsAny<InboundEmailPayload>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new InboundEmailResult(routerSucceeds, Guid.NewGuid(), Array.Empty<Guid>(), routerSucceeds ? null : "err"));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Inbound:Postmark:WebhookToken"] = Token })
            .Build();

        var ctrl = new InboundEmailController(router.Object, config, NullLogger<InboundEmailController>.Instance);
        var http = new DefaultHttpContext();
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return (ctrl, http);
    }

    private static InboundEmailController.PostmarkInboundPayload ValidBody() =>
        new() { OriginalRecipient = "orders@acme.proculink.eu", Subject = "PO", From = "buyer@example.com" };

    [Fact]
    public async Task Header_token_is_accepted()
    {
        var (ctrl, http) = Build();
        http.Request.Headers["X-Postmark-Server-Token"] = Token;
        var result = await ctrl.Postmark(ValidBody(), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Query_token_is_accepted()
    {
        var (ctrl, http) = Build();
        http.Request.QueryString = new QueryString("?token=" + Token);
        var result = await ctrl.Postmark(ValidBody(), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>("Postmark inbound URL carries the token as ?token=");
    }

    [Fact]
    public async Task BasicAuth_password_is_accepted()
    {
        var (ctrl, http) = Build();
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("postmark:" + Token));
        http.Request.Headers["Authorization"] = "Basic " + b64;
        var result = await ctrl.Postmark(ValidBody(), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>("Postmark inbound URL can embed creds as https://user:token@host/...");
    }

    [Fact]
    public async Task Missing_token_is_rejected()
    {
        var (ctrl, _) = Build();
        var result = await ctrl.Postmark(ValidBody(), CancellationToken.None);
        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task Wrong_query_token_is_rejected()
    {
        var (ctrl, http) = Build();
        http.Request.QueryString = new QueryString("?token=not-the-token");
        var result = await ctrl.Postmark(ValidBody(), CancellationToken.None);
        result.Should().BeOfType<UnauthorizedObjectResult>();
    }
}
