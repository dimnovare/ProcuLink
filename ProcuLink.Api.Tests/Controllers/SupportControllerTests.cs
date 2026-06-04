using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Tests that <see cref="SupportController.Contact"/> returns the correct
/// <c>delivered</c> flag depending on whether an SMTP-backed sender is registered.
/// </summary>
public class SupportControllerTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static SupportController BuildController(SupportContactResult serviceResult)
    {
        var svc = new Mock<ISupportContactService>();
        svc.Setup(s => s.SubmitAsync(
                It.IsAny<Guid?>(),
                It.IsAny<string?>(),
                It.IsAny<SupportContactRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(serviceResult);

        var ctrl = new SupportController(svc.Object, NullLogger<SupportController>.Instance);

        // Wire a minimal HttpContext so Request.Headers is non-null
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };

        return ctrl;
    }

    private static SupportContactRequest AnyRequest() =>
        new("billing", "Test subject", "Test body.", null, null, null);

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Contact_Returns_delivered_true_WhenSmtpConfigured()
    {
        var ctrl = BuildController(new SupportContactResult(Delivered: true, ContactEmail: "support@proculink.eu"));

        var actionResult = await ctrl.Contact(AnyRequest(), CancellationToken.None);

        var ok = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value!;
        body.GetType().GetProperty("ok")!.GetValue(body).Should().Be(true);
        body.GetType().GetProperty("delivered")!.GetValue(body).Should().Be(true);
        body.GetType().GetProperty("contactEmail")!.GetValue(body).Should().Be("support@proculink.eu");
    }

    [Fact]
    public async Task Contact_Returns_delivered_false_WhenNoSmtpConfigured()
    {
        var ctrl = BuildController(new SupportContactResult(Delivered: false, ContactEmail: "support@proculink.eu"));

        var actionResult = await ctrl.Contact(AnyRequest(), CancellationToken.None);

        var ok = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value!;
        body.GetType().GetProperty("ok")!.GetValue(body).Should().Be(true);
        body.GetType().GetProperty("delivered")!.GetValue(body).Should().Be(false,
            "ConsoleEmailSender only logs — the response must not imply the email was sent");
        body.GetType().GetProperty("contactEmail")!.GetValue(body).Should().Be("support@proculink.eu",
            "fallback address must always be present so the frontend can show it");
    }

    [Fact]
    public async Task Contact_Returns400_WhenRequestIsNull()
    {
        var ctrl = BuildController(new SupportContactResult(Delivered: true, ContactEmail: "support@proculink.eu"));
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = await ctrl.Contact(null!, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Contact_Returns400_WhenCategoryOrMessageBlank()
    {
        var ctrl = BuildController(new SupportContactResult(Delivered: true, ContactEmail: "support@proculink.eu"));
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = await ctrl.Contact(
            new SupportContactRequest("", "", "Body.", null, null, null),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
