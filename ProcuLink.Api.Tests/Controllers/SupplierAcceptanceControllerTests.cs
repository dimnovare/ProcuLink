using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Tests for /api/suppliers/{supplierId}/acceptance-profile — GetActive, ListVersions,
/// CreateVersion, Activate. Pure Moq (no DB) following ApiKeyControllerTests.
/// </summary>
public class SupplierAcceptanceControllerTests
{
    private static (SupplierAcceptanceController Controller, Mock<ISupplierAcceptanceService> Svc, Guid OrgId, Guid SupplierId)
        Build()
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var svc  = new Mock<ISupplierAcceptanceService>();
        var ctrl = new SupplierAcceptanceController(svc.Object, tenant.Object);

        return (ctrl, svc, orgId, supplierId);
    }

    private static SupplierAcceptanceProfile MakeProfile(Guid orgId, Guid supplierId) => new()
    {
        Id           = Guid.NewGuid(),
        OrgId        = orgId,
        SupplierId   = supplierId,
        VersionNo    = 1,
        Status       = "active",
        Protocol     = "http",
        OutputFormat = "cxml",
        CreatedAt    = DateTime.UtcNow,
        Rules        = new List<SupplierAcceptanceRule>
        {
            new()
            {
                Id          = Guid.NewGuid(),
                Scope       = "line",
                FieldPath   = "supplierItemCode",
                Operator    = "required",
                Severity    = "error",
                BlockOnFail = true,
            },
        },
    };

    [Fact]
    public async Task GetActive_Returns404_WhenNull()
    {
        var (ctrl, svc, orgId, supplierId) = Build();

        svc.Setup(s => s.GetLatestAsync(orgId, supplierId, It.IsAny<CancellationToken>()))
           .ReturnsAsync((SupplierAcceptanceProfile?)null);

        var result = await ctrl.GetActive(supplierId, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetActive_Returns200_WhenProfileExists()
    {
        var (ctrl, svc, orgId, supplierId) = Build();

        svc.Setup(s => s.GetLatestAsync(orgId, supplierId, It.IsAny<CancellationToken>()))
           .ReturnsAsync(MakeProfile(orgId, supplierId));

        var result = await ctrl.GetActive(supplierId, CancellationToken.None);

        var ok  = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<AcceptanceProfileDto>().Subject;
        dto.VersionNo.Should().Be(1);
        dto.Status.Should().Be("active");
        dto.Rules.Should().HaveCount(1);
        dto.Rules[0].FieldPath.Should().Be("supplierItemCode");
    }

    [Fact]
    public async Task CreateVersion_Returns200_WithDto()
    {
        var (ctrl, svc, orgId, supplierId) = Build();

        svc.Setup(s => s.CreateVersionAsync(
                orgId, supplierId, "http", "cxml",
                It.IsAny<IReadOnlyList<AcceptanceRuleInput>>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(MakeProfile(orgId, supplierId));

        var request = new CreateAcceptanceProfileRequest(
            "http", "cxml",
            new List<AcceptanceRuleDto>
            {
                new(null, "line", "supplierItemCode", "required", null, "error", true),
            });

        var result = await ctrl.CreateVersion(supplierId, request, CancellationToken.None);

        var ok  = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<AcceptanceProfileDto>().Subject;
        dto.Protocol.Should().Be("http");
        dto.Rules.Should().HaveCount(1);
    }

    [Fact]
    public async Task Activate_Returns204_WhenTrue()
    {
        var (ctrl, svc, orgId, supplierId) = Build();

        svc.Setup(s => s.ActivateVersionAsync(orgId, supplierId, 2, It.IsAny<CancellationToken>()))
           .ReturnsAsync(true);

        var result = await ctrl.Activate(supplierId, 2, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Activate_Returns404_WhenFalse()
    {
        var (ctrl, svc, orgId, supplierId) = Build();

        svc.Setup(s => s.ActivateVersionAsync(orgId, supplierId, 99, It.IsAny<CancellationToken>()))
           .ReturnsAsync(false);

        var result = await ctrl.Activate(supplierId, 99, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }
}
