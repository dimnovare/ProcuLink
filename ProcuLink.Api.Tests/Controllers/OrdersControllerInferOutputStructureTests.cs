using FluentAssertions;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Moq;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

public class OrdersControllerInferOutputStructureTests
{
    [Fact]
    public void XmlSampleWithDoctype_IsRefusedBeforeTheInferrerExpandsIt()
    {
        // Parses fine without the guard: XDocument.Parse processes the DTD and expands &e;.
        const string sample = """
            <?xml version="1.0"?>
            <!DOCTYPE o [<!ENTITY e "expanded">]>
            <o><n>&e;</n></o>
            """;
        using var db = new ProcuLinkDbContext(ParkedOrderTestHarness.NewOptions());
        var controller = ParkedOrderTestHarness.NewController(
            db, new Mock<IOrderService>().Object, new Mock<ICurrentTenantService>().Object,
            new Mock<IBackgroundJobClient>().Object);

        var result = controller.InferOutputStructure(Guid.NewGuid(), new(sample, "xml"));

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.GetType().GetProperty("error")!.GetValue(bad.Value)
            .Should().Be("Could not read that sample as xml.");
    }
}
