using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Connectors;
using ProcuLink.Core.Constants;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Group V7 — Connector SDK: unit tests for <see cref="ConnectorManifestsController"/>.
///
/// Tests are pure (no DB, no mocks needed) because the controller is entirely stateless —
/// it projects <see cref="ConnectorManifestCatalog"/> to DTOs and evaluates config shapes.
///
/// Coverage:
///   (a) Catalog completeness + shape (via ConnectorManifestCatalogTests companion file).
///   (b) GET all — 200 + correct count.
///   (c) GET by known key — 200 with matching DTO.
///   (d) GET by unknown key — 404.
///   (e) validate-config happy path (all required fields present) — valid: true, empty lists.
///   (f) validate-config missing required field — valid: false, missing list populated.
///   (g) validate-config unknown key — valid: false, unknown list populated.
///   (h) validate-config unknown key for unknown connector — 404.
/// </summary>
public sealed class ConnectorManifestsControllerTests
{
    private static ConnectorManifestsController MakeController() => new();

    // ── GET all ──────────────────────────────────────────────────────────────

    [Fact]
    public void GetAll_Returns200_WithAllManifests()
    {
        var result = MakeController().GetAll();

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dtos = ((IEnumerable<ConnectorManifestDto>)ok.Value!).ToList();

        dtos.Should().HaveCount(ConnectorManifestCatalog.All.Count);
    }

    [Fact]
    public void GetAll_ContainsExpectedKeys()
    {
        var result = MakeController().GetAll();
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var keys = ((IEnumerable<ConnectorManifestDto>)ok.Value!).Select(d => d.Key).ToList();

        keys.Should().BeEquivalentTo(new[]
        {
            DeliveryProtocolConstants.Http,
            DeliveryProtocolConstants.Sftp,
            DeliveryProtocolConstants.Ftps,
            DeliveryProtocolConstants.Email,
            DeliveryProtocolConstants.ErpErply,
            DeliveryProtocolConstants.ErpDirecto,
        });
    }

    [Fact]
    public void GetAll_DoesNotContainBareFtp()
    {
        // DeliveryProtocolConstants.Ftp ("ftp") has no real dispatcher registered.
        var result = MakeController().GetAll();
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var keys = ((IEnumerable<ConnectorManifestDto>)ok.Value!).Select(d => d.Key).ToList();

        keys.Should().NotContain(DeliveryProtocolConstants.Ftp,
            "bare FTP has no registered dispatcher — it would violate offer-equals-works");
    }

    // ── GET by key ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("http")]
    [InlineData("sftp")]
    [InlineData("ftps")]
    [InlineData("email")]
    [InlineData("erp_erply")]
    [InlineData("erp_directo")]
    public void GetByKey_KnownKey_Returns200WithMatchingDto(string key)
    {
        var result = MakeController().GetByKey(key);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ConnectorManifestDto>().Subject;

        dto.Key.Should().BeEquivalentTo(key, "key lookup is case-insensitive");
        dto.Fields.Should().NotBeEmpty();
        dto.DisplayName.Should().NotBeNullOrWhiteSpace();
        dto.Transport.Should().NotBeNullOrWhiteSpace();
        dto.AuthType.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("HTTP")] // case-insensitive lookup
    [InlineData("SFTP")]
    [InlineData("Email")]
    [InlineData("ERP_ERPLY")]
    public void GetByKey_CaseInsensitive_Returns200(string key)
    {
        var result = MakeController().GetByKey(key);
        result.Should().BeOfType<OkObjectResult>();
    }

    [Theory]
    [InlineData("ftp")]         // declared in constants, no dispatcher
    [InlineData("as2")]         // not wired
    [InlineData("peppol")]      // not wired
    [InlineData("does-not-exist")]
    [InlineData("")]
    public void GetByKey_UnknownKey_Returns404(string key)
    {
        var result = MakeController().GetByKey(key);
        result.Should().BeOfType<NotFoundResult>();
    }

    // ── validate-config — unknown key ───────────────────────────────────────

    [Fact]
    public void ValidateConfig_UnknownKey_Returns404()
    {
        var result = MakeController().ValidateConfig("no-such-connector",
            new Dictionary<string, object?> { ["url"] = "https://example.com" });

        result.Should().BeOfType<NotFoundResult>();
    }

    // ── validate-config — HTTP / REST (representative dispatcher) ───────────

    [Fact]
    public void ValidateConfig_Http_AllRequiredPresent_ReturnsValid()
    {
        var result = MakeController().ValidateConfig("http",
            new Dictionary<string, object?> { ["url"] = "https://api.example.com/orders" });

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ValidateConfigResultDto>().Subject;

        dto.Valid.Should().BeTrue();
        dto.Missing.Should().BeEmpty();
        dto.Unknown.Should().BeEmpty();
    }

    [Fact]
    public void ValidateConfig_Http_MissingRequiredUrl_ReturnsInvalidWithMissingList()
    {
        var result = MakeController().ValidateConfig("http",
            new Dictionary<string, object?> { ["method"] = "POST" });

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ValidateConfigResultDto>().Subject;

        dto.Valid.Should().BeFalse();
        dto.Missing.Should().Contain("url");
        dto.Unknown.Should().BeEmpty();
    }

    [Fact]
    public void ValidateConfig_Http_UnknownKey_ReturnsInvalidWithUnknownList()
    {
        var result = MakeController().ValidateConfig("http",
            new Dictionary<string, object?>
            {
                ["url"] = "https://api.example.com/orders",
                ["bogusKey"] = "someValue",
            });

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ValidateConfigResultDto>().Subject;

        dto.Valid.Should().BeFalse();
        dto.Missing.Should().BeEmpty();
        dto.Unknown.Should().Contain("bogusKey");
    }

    [Fact]
    public void ValidateConfig_Http_MissingAndUnknown_ReturnsBothLists()
    {
        // Missing "url", has unknown "endpoint"
        var result = MakeController().ValidateConfig("http",
            new Dictionary<string, object?> { ["endpoint"] = "https://api.example.com" });

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ValidateConfigResultDto>().Subject;

        dto.Valid.Should().BeFalse();
        dto.Missing.Should().Contain("url");
        dto.Unknown.Should().Contain("endpoint");
    }

    [Fact]
    public void ValidateConfig_Http_NullBody_TreatedAsEmptyConfig()
    {
        var result = MakeController().ValidateConfig("http", null!);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ValidateConfigResultDto>().Subject;

        dto.Valid.Should().BeFalse();
        dto.Missing.Should().Contain("url");
    }

    // ── validate-config — SFTP ───────────────────────────────────────────────

    [Fact]
    public void ValidateConfig_Sftp_RequiredHostPresent_ReturnsValid()
    {
        var result = MakeController().ValidateConfig("sftp",
            new Dictionary<string, object?>
            {
                ["host"]     = "sftp.example.com",
                ["username"] = "orders",       // secret / required
            });

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ValidateConfigResultDto>().Subject;

        dto.Valid.Should().BeTrue();
        dto.Missing.Should().BeEmpty();
    }

    [Fact]
    public void ValidateConfig_Sftp_MissingBothRequired_ListsBothInMissing()
    {
        var result = MakeController().ValidateConfig("sftp",
            new Dictionary<string, object?> { ["remotePath"] = "/orders" });

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ValidateConfigResultDto>().Subject;

        dto.Valid.Should().BeFalse();
        dto.Missing.Should().Contain("host");
        dto.Missing.Should().Contain("username");
    }

    // ── validate-config — ERP connectors ────────────────────────────────────

    [Fact]
    public void ValidateConfig_ErpErply_RequiredUrlPresent_ReturnsValid()
    {
        var result = MakeController().ValidateConfig("erp_erply",
            new Dictionary<string, object?> { ["url"] = "https://erply.example.com/api" });

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ValidateConfigResultDto>().Subject;

        dto.Valid.Should().BeTrue();
        dto.Missing.Should().BeEmpty();
    }

    [Fact]
    public void ValidateConfig_ErpDirecto_RequiredUrlAndDatabasePresent_ReturnsValid()
    {
        var result = MakeController().ValidateConfig("erp_directo",
            new Dictionary<string, object?>
            {
                ["url"]      = "https://directo.example.com/api",
                ["database"] = "my_db",
            });

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ValidateConfigResultDto>().Subject;

        dto.Valid.Should().BeTrue();
        dto.Missing.Should().BeEmpty();
    }

    [Fact]
    public void ValidateConfig_ErpDirecto_MissingDatabase_ReturnsInvalid()
    {
        var result = MakeController().ValidateConfig("erp_directo",
            new Dictionary<string, object?> { ["url"] = "https://directo.example.com/api" });

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ValidateConfigResultDto>().Subject;

        dto.Valid.Should().BeFalse();
        dto.Missing.Should().Contain("database");
    }

    // ── validate-config — Email ──────────────────────────────────────────────

    [Fact]
    public void ValidateConfig_Email_AllRequired_ReturnsValid()
    {
        // Mail is sent from ProcuLink's verified sender — the only required field is
        // the recipient list. No host/credentials are needed.
        var result = MakeController().ValidateConfig("email",
            new Dictionary<string, object?>
            {
                ["toAddresses"] = "supplier@example.com",
            });

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ValidateConfigResultDto>().Subject;

        dto.Valid.Should().BeTrue();
        dto.Missing.Should().BeEmpty();
    }

    [Fact]
    public void ValidateConfig_Email_MissingToAddresses_ReturnsInvalid()
    {
        var result = MakeController().ValidateConfig("email",
            new Dictionary<string, object?> { ["subjectTemplate"] = "PO {{ orderNumber }}" });

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ValidateConfigResultDto>().Subject;

        dto.Valid.Should().BeFalse();
        dto.Missing.Should().Contain("toAddresses");
    }

    // ── validate-config — case insensitivity ────────────────────────────────

    [Fact]
    public void ValidateConfig_Http_KeyLookupIsCaseInsensitive()
    {
        // Post "URL" (uppercase) against the "http" connector (field name is "url").
        var result = MakeController().ValidateConfig("http",
            new Dictionary<string, object?> { ["URL"] = "https://api.example.com/orders" });

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ValidateConfigResultDto>().Subject;

        // "URL" matches "url" in the manifest — should not appear in unknown OR missing.
        dto.Valid.Should().BeTrue();
        dto.Missing.Should().BeEmpty("URL matches url case-insensitively");
        dto.Unknown.Should().BeEmpty("URL matches url case-insensitively");
    }
}
