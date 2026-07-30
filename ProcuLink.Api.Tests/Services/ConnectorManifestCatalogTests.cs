using FluentAssertions;
using ProcuLink.Core.Connectors;
using ProcuLink.Core.Constants;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Group V7 — Connector SDK: catalog completeness and shape tests.
///
/// These tests guard the OFFER-EQUALS-WORKS invariant: every manifest key must correspond
/// to a real delivery dispatcher registered in Program.cs, and no manifest may be declared
/// for an unregistered protocol.
///
/// Coverage:
///   (a) Catalog completeness — expected keys present, "ftp" (no dispatcher) absent.
///   (b) Every manifest has the required shape (key, displayName, transport, authType, fields).
///   (c) Required fields declared where the dispatchers validate them.
///   (d) Secret fields declared for credential-blob fields.
///   (e) ByKey / All are consistent with each other.
///   (f) All field Types are in the declared set.
/// </summary>
public sealed class ConnectorManifestCatalogTests
{
    private static readonly string[] ExpectedKeys =
    [
        DeliveryProtocolConstants.Http,
        DeliveryProtocolConstants.Sftp,
        DeliveryProtocolConstants.Ftps,
        DeliveryProtocolConstants.Email,
        DeliveryProtocolConstants.ErpErply,
        DeliveryProtocolConstants.ErpDirecto,
    ];

    private static readonly string[] ValidFieldTypes =
        ["string", "number", "bool", "secret", "url"];

    // ── Catalog completeness ─────────────────────────────────────────────────

    [Fact]
    public void All_ContainsExactlyExpectedConnectors()
    {
        ConnectorManifestCatalog.All
            .Select(m => m.Key)
            .Should().BeEquivalentTo(ExpectedKeys,
                "only dispatchers that are actually registered in Program.cs are declared");
    }

    [Fact]
    public void ByKey_ContainsExactlyExpectedConnectors()
    {
        ConnectorManifestCatalog.ByKey.Keys
            .Should().BeEquivalentTo(ExpectedKeys,
                options => options.WithoutStrictOrdering());
    }

    [Fact]
    public void ByKey_DoesNotContainBareFtp()
    {
        // "ftp" is in DeliveryProtocolConstants but has no registered dispatcher.
        ConnectorManifestCatalog.ByKey.ContainsKey(DeliveryProtocolConstants.Ftp)
            .Should().BeFalse("bare FTP has no registered dispatcher (FtpsDeliveryDispatcher handles ftps, not ftp)");
    }

    [Fact]
    public void All_And_ByKey_AreConsistent()
    {
        // Every manifest in All must also be retrievable from ByKey.
        foreach (var manifest in ConnectorManifestCatalog.All)
        {
            ConnectorManifestCatalog.ByKey.TryGetValue(manifest.Key, out var fromByKey)
                .Should().BeTrue($"key '{manifest.Key}' in All must also be in ByKey");
            fromByKey.Should().BeSameAs(manifest);
        }
    }

    // ── Shape: every manifest ────────────────────────────────────────────────

    [Theory]
    [InlineData("http")]
    [InlineData("sftp")]
    [InlineData("ftps")]
    [InlineData("email")]
    [InlineData("erp_erply")]
    [InlineData("erp_directo")]
    public void Manifest_HasRequiredScalarFields(string key)
    {
        var m = ConnectorManifestCatalog.ByKey[key];

        m.Key.Should().Be(key);
        m.DisplayName.Should().NotBeNullOrWhiteSpace();
        m.Transport.Should().NotBeNullOrWhiteSpace();
        m.AuthType.Should().NotBeNullOrWhiteSpace();
        m.Fields.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("http")]
    [InlineData("sftp")]
    [InlineData("ftps")]
    [InlineData("email")]
    [InlineData("erp_erply")]
    [InlineData("erp_directo")]
    public void Manifest_AllFieldTypesAreValid(string key)
    {
        var m = ConnectorManifestCatalog.ByKey[key];

        foreach (var field in m.Fields)
        {
            field.Type.Should().BeOneOf(ValidFieldTypes,
                $"field '{field.Name}' on connector '{key}' must use a declared type");
        }
    }

    [Theory]
    [InlineData("http")]
    [InlineData("sftp")]
    [InlineData("ftps")]
    [InlineData("email")]
    [InlineData("erp_erply")]
    [InlineData("erp_directo")]
    public void Manifest_AllFieldsHaveNonEmptyNamesAndLabels(string key)
    {
        var m = ConnectorManifestCatalog.ByKey[key];

        foreach (var field in m.Fields)
        {
            field.Name.Should().NotBeNullOrWhiteSpace($"connector '{key}' has a field with a blank Name");
            field.Label.Should().NotBeNullOrWhiteSpace($"connector '{key}' field '{field.Name}' has a blank Label");
        }
    }

    // ── Required fields match what each dispatcher validates ─────────────────

    [Fact]
    public void Http_RequiredFields_ContainsUrl()
    {
        var m = ConnectorManifestCatalog.ByKey[DeliveryProtocolConstants.Http];

        var required = m.Fields.Where(f => f.Required).Select(f => f.Name).ToList();
        required.Should().Contain("url",
            "HttpDeliveryDispatcher returns an error when ConfigJson.Url is blank");
    }

    [Fact]
    public void Sftp_RequiredFields_ContainsHostAndUsername()
    {
        var m = ConnectorManifestCatalog.ByKey[DeliveryProtocolConstants.Sftp];

        var required = m.Fields.Where(f => f.Required).Select(f => f.Name).ToList();
        required.Should().Contain("host",     "SftpDeliveryDispatcher validates Host");
        required.Should().Contain("username", "SftpDeliveryDispatcher validates Username");
    }

    [Fact]
    public void Ftps_RequiredFields_ContainsHostAndUsername()
    {
        var m = ConnectorManifestCatalog.ByKey[DeliveryProtocolConstants.Ftps];

        var required = m.Fields.Where(f => f.Required).Select(f => f.Name).ToList();
        required.Should().Contain("host",     "FtpsDeliveryDispatcher validates Host");
        required.Should().Contain("username", "FtpsDeliveryDispatcher validates Username");
    }

    [Theory]
    [InlineData("sftp")]
    [InlineData("ftps")]
    public void FileDropManifests_DeclareTheFileNamingAndOverwriteSwitches(string key)
    {
        // WP-20. Both switches are honoured by the dispatchers / DeliveryService straight out of
        // ConfigJson, so if the manifest does not declare them the connector UI cannot offer them
        // and an operator has no way to discover either:
        //   overwriteExisting=false — an append-only supplier drop directory.
        //   legacyFileName=true     — a supplier whose pickup automation matches the exact bare PO
        //                             filename, i.e. the escape hatch out of the new
        //                             '{PO}-{orderId}{ext}' name.
        var m = ConnectorManifestCatalog.ByKey[key];

        var names = m.Fields.Select(f => f.Name).ToList();
        names.Should().Contain("overwriteExisting");
        names.Should().Contain("legacyFileName");
        m.Fields.Where(f => f.Name is "overwriteExisting" or "legacyFileName")
            .Should().OnlyContain(f => !f.Required && !f.Secret && f.Type == "bool");
    }

    [Fact]
    public void Email_RequiredFields_ContainsToAddresses()
    {
        var m = ConnectorManifestCatalog.ByKey[DeliveryProtocolConstants.Email];

        var required = m.Fields.Where(f => f.Required).Select(f => f.Name).ToList();
        required.Should().Contain("toAddresses",
            "the email connector validates that at least one recipient is configured");
    }

    [Fact]
    public void ErpErply_RequiredFields_ContainsUrl()
    {
        var m = ConnectorManifestCatalog.ByKey[DeliveryProtocolConstants.ErpErply];

        var required = m.Fields.Where(f => f.Required).Select(f => f.Name).ToList();
        required.Should().Contain("url",
            "ErplyConnector returns an error when Url is blank");
    }

    [Fact]
    public void ErpDirecto_RequiredFields_ContainsUrlAndDatabase()
    {
        var m = ConnectorManifestCatalog.ByKey[DeliveryProtocolConstants.ErpDirecto];

        var required = m.Fields.Where(f => f.Required).Select(f => f.Name).ToList();
        required.Should().Contain("url",      "DirectoConnector validates Url");
        required.Should().Contain("database", "DirectoConnector validates Database");
    }

    // ── Secret fields declared for credential-blob fields ───────────────────

    [Theory]
    [InlineData("http")]
    [InlineData("sftp")]
    [InlineData("ftps")]
    [InlineData("erp_erply")]
    [InlineData("erp_directo")]
    public void Manifest_HasAtLeastOneSecretField(string key)
    {
        // Every credentialed connector has at least one secret field (even if optional).
        // The "email" connector is the documented exception — mail is sent from ProcuLink's
        // own verified sender, so it has no per-supplier credentials.
        var m = ConnectorManifestCatalog.ByKey[key];

        m.Fields.Should().Contain(f => f.Secret,
            $"connector '{key}' must declare at least one secret credential field");
    }

    [Fact]
    public void Email_HasNoSecretFields()
    {
        // Mail is sent from ProcuLink's verified sender — there are no per-supplier
        // credentials, so the email manifest must declare no secret fields.
        var m = ConnectorManifestCatalog.ByKey[DeliveryProtocolConstants.Email];

        m.Fields.Should().NotContain(f => f.Secret,
            "the email connector uses ProcuLink's verified sender and has no credentials");
    }

    [Fact]
    public void Http_PasswordField_IsSecret()
    {
        var m = ConnectorManifestCatalog.ByKey[DeliveryProtocolConstants.Http];
        m.Fields.First(f => f.Name == "password").Secret.Should().BeTrue();
    }

    [Fact]
    public void Sftp_PrivateKeyField_IsSecret()
    {
        var m = ConnectorManifestCatalog.ByKey[DeliveryProtocolConstants.Sftp];
        m.Fields.First(f => f.Name == "privateKey").Secret.Should().BeTrue();
    }

    [Fact]
    public void Ftps_PasswordField_IsSecret()
    {
        var m = ConnectorManifestCatalog.ByKey[DeliveryProtocolConstants.Ftps];
        m.Fields.First(f => f.Name == "password").Secret.Should().BeTrue();
    }

    [Fact]
    public void ErpErply_TokenField_IsSecret()
    {
        var m = ConnectorManifestCatalog.ByKey[DeliveryProtocolConstants.ErpErply];
        m.Fields.First(f => f.Name == "token").Secret.Should().BeTrue();
    }

    [Fact]
    public void ErpDirecto_KeyField_IsSecret()
    {
        var m = ConnectorManifestCatalog.ByKey[DeliveryProtocolConstants.ErpDirecto];
        m.Fields.First(f => f.Name == "key").Secret.Should().BeTrue();
    }
}
