using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Transport security on the webhook subscription create path.
///
/// <para>This was the weakest URL surface in the product: <c>IntegrationController.Create</c>
/// checked only that the value was non-blank and parsed as an absolute URI — no scheme
/// restriction of any kind, and no SSRF guard. <c>file://</c> and <c>gopher://</c> were stored
/// happily, and plain <c>http://</c> shipped every order payload in the clear. There were no
/// controller tests at all.</para>
/// </summary>
public class IntegrationControllerTransportSecurityTests
{
    private sealed record Harness(IntegrationController Controller, ProcuLinkDbContext Db, Guid OrgId);

    private static Harness Build()
    {
        var db = new ProcuLinkDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var orgId = Guid.NewGuid();
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] =
                    Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
            })
            .Build();

        return new Harness(
            new IntegrationController(db, tenant.Object, new DeliveryEncryptionService(config)),
            db, orgId);
    }

    private static IntegrationController.CreateSubRequest Sub(string targetUrl) =>
        new("custom", "order.delivered", targetUrl, "shh");

    // ── Refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_PlainHttpTarget_IsRefusedAndStoresNothing()
    {
        var h = Build();

        var result = await h.Controller.Create(Sub("http://hooks.partner.example/orders"), default);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        ((string)((dynamic)bad.Value!).error).Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);
        (await h.Db.IntegrationSubscriptions.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://partner.example/1")]
    public async Task Create_ANonWebScheme_IsRefused(string url)
    {
        var h = Build();

        var result = await h.Controller.Create(Sub(url), default);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        ((string)((dynamic)bad.Value!).error).Should().Be(OutboundUrlPolicy.ErrorSchemeNotAllowed);
        (await h.Db.IntegrationSubscriptions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Create_CredentialsInTheTargetUrl_AreRefused()
    {
        var h = Build();

        var result = await h.Controller.Create(Sub("https://bot:hunter2@hooks.partner.example/orders"), default);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        ((string)((dynamic)bad.Value!).error).Should().Be(OutboundUrlPolicy.ErrorCredentialsInUrl);
        ((string)((dynamic)bad.Value!).message).Should().NotContain("hunter2");
    }

    // ── Allowances ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_HttpsTarget_IsStored()
    {
        var h = Build();

        var result = await h.Controller.Create(Sub("https://hooks.partner.example/orders"), default);

        result.Should().BeOfType<OkObjectResult>();
        (await h.Db.IntegrationSubscriptions.SingleAsync()).TargetUrl
            .Should().Be("https://hooks.partner.example/orders");
    }

    [Theory]
    [InlineData("http://localhost:4000/hook")]
    [InlineData("http://127.0.0.1:53412/hook")]
    public async Task Create_PlainHttpOnLoopback_IsStoredForLocalDevelopment(string url)
    {
        var h = Build();

        var result = await h.Controller.Create(Sub(url), default);

        result.Should().BeOfType<OkObjectResult>();
        (await h.Db.IntegrationSubscriptions.CountAsync()).Should().Be(1);
    }
}
