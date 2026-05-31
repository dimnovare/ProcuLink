using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

public class ExceptionsControllerTests
{
    private static (ExceptionsController Ctrl, Mock<IOrderExceptionService> Svc, Guid OrgId) Build()
    {
        var orgId  = Guid.NewGuid();
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);
        var svc = new Mock<IOrderExceptionService>();
        return (new ExceptionsController(svc.Object, tenant.Object), svc, orgId);
    }

    [Fact]
    public async Task List_ReturnsOk_WithMappedDtos()
    {
        var (ctrl, svc, orgId) = Build();
        svc.Setup(s => s.ListAsync(orgId, null, It.IsAny<CancellationToken>()))
           .ReturnsAsync(new List<OrderException>
           {
               new() { Id = Guid.NewGuid(), OrgId = orgId, OrderId = Guid.NewGuid(),
                       Stage = "Map", Code = "unresolved_mapping", Severity = "warning",
                       State = "open", Message = "x", CreatedAt = DateTime.UtcNow }
           });

        var result = await ctrl.List(null, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dtos = ok.Value.Should().BeAssignableTo<IEnumerable<OrderExceptionDto>>().Subject;
        dtos.Should().HaveCount(1);
    }

    [Fact]
    public async Task Resolve_Returns204_WhenServiceReturnsTrue()
    {
        var (ctrl, svc, orgId) = Build();
        var id = Guid.NewGuid();
        svc.Setup(s => s.ResolveAsync(orgId, id, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await ctrl.Resolve(id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Resolve_Returns404_WhenServiceReturnsFalse()
    {
        var (ctrl, svc, orgId) = Build();
        var id = Guid.NewGuid();
        svc.Setup(s => s.ResolveAsync(orgId, id, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await ctrl.Resolve(id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Ignore_Returns204_WhenServiceReturnsTrue()
    {
        var (ctrl, svc, orgId) = Build();
        var id = Guid.NewGuid();
        svc.Setup(s => s.IgnoreAsync(orgId, id, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await ctrl.Ignore(id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }
}
