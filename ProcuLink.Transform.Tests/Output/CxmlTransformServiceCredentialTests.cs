using System.Xml.Linq;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// The founder complaint: outbound cXML carried ProcuLink's internal OrgId / SupplierId GUIDs in
/// the Header credentials instead of real cXML network credentials. These tests pin the new
/// configurable behaviour: a <see cref="CxmlCredentialConfig"/> drives the From / To / Sender
/// credentials (domain + identity + Sender SharedSecret), and a null/blank config falls back
/// per-credential to the legacy GUID identities so existing orders never break.
/// </summary>
public class CxmlTransformServiceCredentialTests
{
    private static readonly Guid OrgId      = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SupplierId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static PurchaseOrderEntity BuildOrder() => new()
    {
        Id         = Guid.NewGuid(),
        OrgId      = OrgId,
        SupplierId = SupplierId,
        PoNumber   = "PO-CRED-1",
        OrderDate  = new DateOnly(2026, 6, 15),
        Currency   = "EUR",
        Status     = "ready",
        Lines =
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, BuyerItemCode = "B-1", SupplierItemCode = "SUP-1",
                Description = "Widget", Quantity = 2m, Unit = "EA", UnitPrice = 12.5m,
                NeedsReview = false, Confidence = 1.0f,
            },
        },
    };

    private static async Task<string> RenderAsync(CxmlCredentialConfig? creds)
    {
        var result = await new CxmlTransformService()
            .TransformAsync(BuildOrder(), OutputFormat.CXml, CancellationToken.None, creds);
        result.Content.Position = 0;
        using var reader = new StreamReader(result.Content);
        return await reader.ReadToEndAsync();
    }

    /// <summary>Reads a Header credential block (From/To/Sender) → (domain, identity, sharedSecret).</summary>
    private static (string? Domain, string? Identity, string? SharedSecret) ReadCredential(string xml, string wrapper)
    {
        var block = XDocument.Parse(xml).Descendants(wrapper).Single();
        var cred  = block.Element("Credential")!;
        return (cred.Attribute("domain")?.Value, cred.Element("Identity")?.Value, cred.Element("SharedSecret")?.Value);
    }

    // ── Legacy fallback (config absent) — byte-compatible behaviour ────────────

    [Fact]
    public async Task NullConfig_EmitsLegacyGuidIdentities()
    {
        var xml = await RenderAsync(null);

        ReadCredential(xml, "From").Should().Be(("OrgId", OrgId.ToString(), (string?)null));
        ReadCredential(xml, "To").Should().Be(("SupplierId", SupplierId.ToString(), (string?)null));
        var sender = ReadCredential(xml, "Sender");
        sender.Domain.Should().Be("NetworkUserId");
        sender.Identity.Should().Be("proculink");
        sender.SharedSecret.Should().BeNull("no shared secret is emitted for the legacy sender");
    }

    [Fact]
    public async Task EmptyConfig_AllBlankFields_FallsBackToLegacyIdentities()
    {
        var xml = await RenderAsync(new CxmlCredentialConfig(null, "", null, "  ", null, "", null));

        ReadCredential(xml, "From").Identity.Should().Be(OrgId.ToString());
        ReadCredential(xml, "To").Identity.Should().Be(SupplierId.ToString());
        ReadCredential(xml, "Sender").Identity.Should().Be("proculink");
    }

    // ── Configured credentials — the real-world Coupa example ──────────────────

    [Fact]
    public async Task ConfiguredCredentials_HeaderCarriesRealNetworkIds_NotGuids()
    {
        var creds = new CxmlCredentialConfig(
            FromDomain: "NetworkId", FromIdentity: "TESTBUYER_SE",
            ToDomain: "NetworkId", ToIdentity: "TESTSUPPLIER_SE",
            SenderDomain: "NetworkId", SenderIdentity: "TESTBUYER_SE",
            SenderSharedSecret: "s3cr3t-shared");

        var xml = await RenderAsync(creds);

        ReadCredential(xml, "From").Should().Be(("NetworkId", "TESTBUYER_SE", (string?)null));
        ReadCredential(xml, "To").Should().Be(("NetworkId", "TESTSUPPLIER_SE", (string?)null));

        var sender = ReadCredential(xml, "Sender");
        sender.Domain.Should().Be("NetworkId");
        sender.Identity.Should().Be("TESTBUYER_SE");
        sender.SharedSecret.Should().Be("s3cr3t-shared");

        // The founder's actual bug: the internal GUIDs must NOT leak into the From/To identities.
        var header = XDocument.Parse(xml).Descendants("Header").Single().ToString();
        header.Should().NotContain(OrgId.ToString());
        header.Should().NotContain(SupplierId.ToString());
    }

    [Fact]
    public async Task ConfiguredCredentials_WithoutSharedSecret_EmitsNoSharedSecretElement()
    {
        var creds = new CxmlCredentialConfig(
            "NetworkId", "TESTBUYER_SE", "NetworkId", "TESTSUPPLIER_SE", "NetworkId", "TESTBUYER_SE",
            SenderSharedSecret: null);

        var xml = await RenderAsync(creds);

        ReadCredential(xml, "Sender").SharedSecret.Should().BeNull();
        xml.Should().NotContain("SharedSecret");
    }

    // ── Per-credential fallback + blank-domain default ─────────────────────────

    [Fact]
    public async Task PartialConfig_OnlyFrom_ToAndSenderFallBackToLegacy()
    {
        var creds = new CxmlCredentialConfig(
            FromDomain: "NetworkId", FromIdentity: "TESTBUYER_SE",
            ToDomain: null, ToIdentity: null,
            SenderDomain: null, SenderIdentity: null,
            SenderSharedSecret: null);

        var xml = await RenderAsync(creds);

        ReadCredential(xml, "From").Should().Be(("NetworkId", "TESTBUYER_SE", (string?)null));
        ReadCredential(xml, "To").Should().Be(("SupplierId", SupplierId.ToString(), (string?)null)); // legacy
        ReadCredential(xml, "Sender").Identity.Should().Be("proculink");                              // legacy
    }

    [Fact]
    public async Task ConfiguredIdentity_BlankDomain_DefaultsToNetworkId()
    {
        var creds = new CxmlCredentialConfig(
            FromDomain: "", FromIdentity: "TESTBUYER_SE",
            ToDomain: null, ToIdentity: "TESTSUPPLIER_SE",
            SenderDomain: "   ", SenderIdentity: "TESTBUYER_SE",
            SenderSharedSecret: null);

        var xml = await RenderAsync(creds);

        ReadCredential(xml, "From").Domain.Should().Be("NetworkId");
        ReadCredential(xml, "To").Domain.Should().Be("NetworkId");
        ReadCredential(xml, "Sender").Domain.Should().Be("NetworkId");
    }

    [Fact]
    public async Task ConfiguredValues_AreTrimmed()
    {
        var creds = new CxmlCredentialConfig(
            "  NetworkId ", "  TESTBUYER_SE  ", "NetworkId", "TESTSUPPLIER_SE", "NetworkId", "TESTBUYER_SE", null);

        var xml = await RenderAsync(creds);

        ReadCredential(xml, "From").Should().Be(("NetworkId", "TESTBUYER_SE", (string?)null));
    }

    // ── Unrouted order (null SupplierId) — preview must stay DTD-valid ──────────

    /// <summary>
    /// An order ingested on a shared channel before its supplier is known is parked
    /// <c>unrouted</c> with a NULL SupplierId (routing Phase 0). It can never DELIVER while
    /// unrouted, but the Order Workshop live preview can render it in cXML. A null
    /// <c>Guid?.ToString()</c> is <c>""</c>, which would emit a DTD-invalid empty
    /// <c>&lt;Identity/&gt;</c>; coalescing to the zero GUID keeps the To credential non-empty.
    /// </summary>
    [Fact]
    public async Task NullSupplier_ToIdentityFallsBackToZeroGuid_NotEmpty()
    {
        var order = BuildOrder();
        order.SupplierId = null;

        var result = await new CxmlTransformService()
            .TransformAsync(order, OutputFormat.CXml, CancellationToken.None, (CxmlCredentialConfig?)null);
        result.Content.Position = 0;
        using var reader = new StreamReader(result.Content);
        var xml = await reader.ReadToEndAsync();

        var to = ReadCredential(xml, "To");
        to.Domain.Should().Be("SupplierId");
        to.Identity.Should().Be(Guid.Empty.ToString());
        to.Identity.Should().NotBeNullOrEmpty("an empty <Identity/> is DTD-invalid");
    }
}
