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

    /// <summary>
    /// How many configuration fields each connector declares — i.e. the size every per-connector
    /// field sweep below must actually have inspected. A manifest field is a control on a
    /// supplier-facing form, so adding or removing one is a deliberate change that updates this
    /// table too. Without it, a manifest that declared ZERO fields would let every field sweep
    /// report Passed having validated nothing at all.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> ExpectedFieldCount =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [DeliveryProtocolConstants.Http]       = 14,
            [DeliveryProtocolConstants.Sftp]       = 10,
            [DeliveryProtocolConstants.Ftps]       = 9,
            [DeliveryProtocolConstants.Email]      = 6,
            [DeliveryProtocolConstants.ErpErply]   = 7,
            [DeliveryProtocolConstants.ErpDirecto] = 6,
        };

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
        // Pin the swept set BEFORE sweeping it: an emptied or shrunken All would let the loop
        // below report Passed without round-tripping a single key.
        ConnectorManifestCatalog.All.Should().HaveCount(ExpectedKeys.Length,
            $"the catalog must still declare all {ExpectedKeys.Length} registered dispatchers — the " +
            "consistency check is only worth anything if every one of them was actually looked up");

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

        // Both levels of the sweep, pinned. Outer: these six rows must still BE the catalog, so a
        // connector added without a row here cannot slip through untyped. Inner: this manifest
        // must actually declare its fields, because a manifest with none passes the loop below
        // having type-checked nothing.
        ConnectorManifestCatalog.All.Should().HaveCount(ExpectedKeys.Length,
            "the InlineData rows above are the whole catalog; a connector missing from them would " +
            "never have its field types checked at all");
        m.Fields.Should().HaveCount(ExpectedFieldCount[key],
            $"connector '{key}' declares a known set of configuration fields and every one of them " +
            "must have been type-checked below — zero fields is a broken supplier form, not a pass");

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

        // Both levels again: an intact catalog whose manifests all declare zero fields is just as
        // vacuous here as an empty catalog — every label the operator reads comes from this list.
        ConnectorManifestCatalog.All.Should().HaveCount(ExpectedKeys.Length,
            "the InlineData rows above are the whole catalog; a connector missing from them would " +
            "never have its field names and labels checked at all");
        m.Fields.Should().HaveCount(ExpectedFieldCount[key],
            $"connector '{key}' declares a known set of configuration fields and every one of them " +
            "must have had its Name and Label checked below — an empty Fields list renders a " +
            "supplier form with no controls, and this loop would call that a pass");

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
