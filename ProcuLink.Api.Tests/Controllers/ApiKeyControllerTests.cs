using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Tests for /api/api-keys — List, Create, Revoke endpoints.
/// Uses Moq for IApiKeyService + ICurrentTenantService (no DB required).
/// </summary>
public class ApiKeyControllerTests
{
    // ── helpers ─────────────────────────────────────────────────────────────

    private static (ApiKeyController Controller, Mock<IApiKeyService> KeysSvc, Guid OrgId)
        Build()
    {
        var orgId = Guid.NewGuid();

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var keys = new Mock<IApiKeyService>();
        var ctrl = new ApiKeyController(keys.Object, tenant.Object);

        return (ctrl, keys, orgId);
    }

    private static TenantApiKey MakeKey(Guid orgId, string label = "Test Key") => new()
    {
        Id             = Guid.NewGuid(),
        OrganisationId = orgId,
        Label          = label,
        KeyHash        = "hash-placeholder",
        KeyPrefix      = "plk_abcd",
        IsActive       = true,
        CreatedAt      = DateTime.UtcNow,
    };

    // ── List ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_WhenServiceReturnsEmpty_Returns200WithEmptyCollection()
    {
        var (ctrl, keys, orgId) = Build();

        keys.Setup(k => k.ListAsync(orgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TenantApiKey>());

        var result = await ctrl.List(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().NotBeNull();

        // The controller projects to an anonymous type; verify it is an enumerable with no items.
        var items = ok.Value as IEnumerable<object> ?? ((System.Collections.IEnumerable)ok.Value!)
                        .Cast<object>();
        items.Should().BeEmpty("service returned empty list");
    }

    [Fact]
    public async Task List_WhenServiceReturnsTwoKeys_Returns200WithTwoItems()
    {
        var (ctrl, keys, orgId) = Build();

        var k1 = MakeKey(orgId, "Zapier");
        var k2 = MakeKey(orgId, "Make.com");

        keys.Setup(k => k.ListAsync(orgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { k1, k2 });

        var result = await ctrl.List(CancellationToken.None);

        var ok    = result.Should().BeOfType<OkObjectResult>().Subject;
        var items = ok.Value as IEnumerable<object> ?? ((System.Collections.IEnumerable)ok.Value!)
                        .Cast<object>();
        items.Should().HaveCount(2);
    }

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_WithValidLabel_Returns200WithRawKeyAndMetadata()
    {
        var (ctrl, keys, orgId) = Build();

        var entity = MakeKey(orgId, "CI Pipeline");
        const string rawKey = "plk_abcdEXAMPLEKEY";

        keys.Setup(k => k.CreateAsync(orgId, "CI Pipeline", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((entity, rawKey));

        var result = await ctrl.Create(
            new ApiKeyController.CreateKeyRequest("CI Pipeline", null),
            CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().NotBeNull();

        // Verify RawKey is present in the anonymous response.
        var rawKeyProp = ok.Value!.GetType().GetProperty("RawKey");
        rawKeyProp.Should().NotBeNull("controller must expose RawKey once");
        rawKeyProp!.GetValue(ok.Value).Should().Be(rawKey);
    }

    [Fact]
    public async Task Create_WithBlankLabel_Returns400WithError()
    {
        var (ctrl, keys, _) = Build();

        var result = await ctrl.Create(
            new ApiKeyController.CreateKeyRequest("   ", null),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        keys.Verify(k => k.CreateAsync(
            It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Revoke ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Revoke_WhenKeyExists_Returns204NoContent()
    {
        var (ctrl, keys, orgId) = Build();
        var keyId = Guid.NewGuid();

        keys.Setup(k => k.RevokeAsync(orgId, keyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await ctrl.Revoke(keyId, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Revoke_WhenKeyNotFound_Returns404()
    {
        var (ctrl, keys, orgId) = Build();
        var keyId = Guid.NewGuid();

        keys.Setup(k => k.RevokeAsync(orgId, keyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await ctrl.Revoke(keyId, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }
}
