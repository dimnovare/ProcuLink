using System.Text.Json;
using FluentAssertions;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Infrastructure.Tests.Security;

/// <summary>
/// Which delivery-config header names are treated as credential-bearing.
///
/// <para>The whole guard turns on this predicate, and it can fail in two directions that cost
/// different things. A false NEGATIVE leaves a secret in cleartext under a name someone chose
/// obscurely. A false POSITIVE hard-blocks a legitimate save — and the delivery editor has no
/// headers field, so there is no UI workaround. Both directions are therefore asserted, and the
/// rule is deliberately precise rather than aggressive: never bare <c>auth</c>, never bare
/// <c>key</c>.</para>
/// </summary>
public class CredentialHeaderNamesTests
{
    // ── Refused ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("AUTHORIZATION")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Cookie")]
    [InlineData("X-Api-Key")]
    [InlineData("x-api-key")]
    [InlineData("ApiKey")]
    [InlineData("X-Auth-Token")]
    [InlineData("X-Access-Token")]
    [InlineData("Ocp-Apim-Subscription-Key")]
    [InlineData("Private-Token")]
    public void KnownCredentialNames_AreRefused(string name) =>
        DeliveryConfigTransport.IsCredentialHeaderName(name).Should().BeTrue();

    /// <summary>The segment rule, which is what catches a bespoke supplier-specific name.</summary>
    [Theory]
    [InlineData("X-Supplier-Token")]
    [InlineData("X-Acme-Secret")]
    [InlineData("X-Client-Password")]
    [InlineData("X-Legacy-Passwd")]
    [InlineData("X-Old-Pwd")]
    [InlineData("X-Supplier-Credentials")]
    [InlineData("X-Foo-Api-Key")]
    [InlineData("X-Aws-Access-Key")]
    [InlineData("X-Signing-Key")]
    [InlineData("X_Supplier_Token")]
    public void CredentialShapedNames_AreRefused(string name) =>
        DeliveryConfigTransport.IsCredentialHeaderName(name).Should().BeTrue();

    // ── Allowed ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The line the segment rule is drawn on. Every one of these is a header a real tenant sends,
    /// and refusing any of them would block a save with nowhere to go. <c>X-Idempotency-Key</c> and
    /// <c>X-Auth-Email</c> are the two that a sloppier rule (bare <c>key</c>, bare <c>auth</c>)
    /// would take out.
    /// </summary>
    [Theory]
    [InlineData("Content-Type")]
    [InlineData("Accept")]
    [InlineData("X-Correlation-Id")]
    [InlineData("X-Request-Id")]
    [InlineData("X-Supplier-Account")]
    [InlineData("X-Idempotency-Key")]
    [InlineData("X-Partition-Key")]
    [InlineData("X-Sort-Key")]
    [InlineData("X-Auth-Email")]
    [InlineData("X-Message-Id")]
    public void OrdinaryHeaders_AreAllowed(string name) =>
        DeliveryConfigTransport.IsCredentialHeaderName(name).Should().BeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankNames_AreNotCredentials(string? name) =>
        DeliveryConfigTransport.IsCredentialHeaderName(name).Should().BeFalse();

    [Fact]
    public void SurroundingWhitespace_DoesNotEvadeTheRule() =>
        DeliveryConfigTransport.IsCredentialHeaderName("  Authorization  ").Should().BeTrue();

    /// <summary>
    /// Walks the published list itself, so an entry added to it can never be added without being
    /// covered. The count floor is the anti-vacuity guard: an emptied list would otherwise make
    /// this test assert nothing at all and still pass.
    /// </summary>
    [Fact]
    public void EveryKnownCredentialHeaderName_IsRefusedByThePredicate()
    {
        var names = DeliveryConfigTransport.KnownCredentialHeaderNames;

        names.Should().HaveCountGreaterThan(10,
            "an emptied or gutted list would make this walk assert nothing");

        foreach (var name in names)
            DeliveryConfigTransport.IsCredentialHeaderName(name)
                .Should().BeTrue($"'{name}' is on the published known-credential list");
    }

    // ── Extraction ───────────────────────────────────────────────────────────

    [Fact]
    public void AHeaderMapWithACredential_IsFound() =>
        DeliveryConfigTransport.FindCredentialHeaders(
                """{"url":"https://s.example/o","headers":{"Authorization":"Bearer t0ps3cret"}}""")
            .Should().ContainSingle().Which.Should().Be("Authorization");

    [Fact]
    public void AHeaderMapWithoutOne_IsClean() =>
        DeliveryConfigTransport.FindCredentialHeaders(
                """{"url":"https://s.example/o","headers":{"Content-Type":"application/xml","X-Correlation-Id":"abc"}}""")
            .Should().BeEmpty();

    [Theory]
    [InlineData("""{"url":"https://s.example/o"}""")]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not json at all")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""{"headers":"a string, not a map"}""")]
    public void BlobsWithNoHeaderMap_AreClean(string? configJson) =>
        DeliveryConfigTransport.FindCredentialHeaders(configJson).Should().BeEmpty();

    /// <summary>
    /// The dispatchers deserialize with <c>PropertyNameCaseInsensitive = true</c>, so
    /// <c>{"HEADERS":{"AUTHORIZATION":…}}</c> binds and is sent. A lookup that matched only the
    /// exact lowercase key would be bypassed by changing one character.
    /// </summary>
    [Theory]
    [InlineData("HEADERS")]
    [InlineData("Headers")]
    [InlineData("hEaDeRs")]
    public void TheHeadersKeyInAnyCasing_IsStillInspected(string key) =>
        DeliveryConfigTransport.FindCredentialHeaders(
                $$$"""{"{{{key}}}":{"Authorization":"Bearer t0ps3cret"}}""")
            .Should().ContainSingle();

    [Fact]
    public void TwoCredentialHeaders_AreBothNamed() =>
        DeliveryConfigTransport.FindCredentialHeaders(
                """{"headers":{"Authorization":"Bearer a","X-Api-Key":"b","Content-Type":"application/xml"}}""")
            .Should().Equal("Authorization", "X-Api-Key");

    /// <summary>
    /// The #157 trap, applied to the headers key. A JSON object may repeat a key and
    /// System.Text.Json keeps both: <see cref="JsonDocument"/> enumerates them in document order
    /// while <c>JsonSerializer.Deserialize</c> — what the dispatcher uses — binds the LAST.
    /// Inspecting only the first would validate the clean map and deliver the credential-bearing
    /// one.
    ///
    /// <para>The bypass is confirmed against the REAL serializer first, not reasoned about, so this
    /// test still means something if System.Text.Json ever changes which duplicate wins.</para>
    /// </summary>
    [Fact]
    public void ARepeatedHeadersKey_CannotHideACredential()
    {
        const string blob = """
            {"headers":{"Content-Type":"application/xml"},"headers":{"Authorization":"Bearer t0ps3cret"}}
            """;

        // What the dispatcher will actually send.
        var bound = JsonSerializer.Deserialize<HeaderProbe>(
            blob, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        bound!.Headers.Should().ContainKey("Authorization",
            "the deserializer binds the LAST duplicate — that is the map that reaches the wire");

        DeliveryConfigTransport.FindCredentialHeaders(blob)
            .Should().ContainSingle().Which.Should().Be("Authorization");
    }

    private sealed record HeaderProbe(Dictionary<string, string> Headers);

    // ── Grandfathering ───────────────────────────────────────────────────────

    private const string StoredWithToken =
        """{"url":"https://s.example/o","headers":{"Authorization":"Bearer t0ps3cret"}}""";

    /// <summary>
    /// The case the whole design turns on. The delivery editor has no headers field, so it carries
    /// the stored map through every save untouched. Refusing that identical echo would lock an
    /// operator out of changing a timeout, and there would be no UI anywhere to remove the header.
    /// </summary>
    [Fact]
    public void AnUnchangedRoundTripOfAStoredHeader_IsAllowed() =>
        DeliveryConfigTransport.FindCredentialHeaders(StoredWithToken, StoredWithToken)
            .Should().BeEmpty();

    [Fact]
    public void AnUnchangedHeaderAlongsideAnUnrelatedEdit_IsAllowed() =>
        DeliveryConfigTransport.FindCredentialHeaders(
                """{"url":"https://s.example/o","timeoutSeconds":60,"headers":{"Authorization":"Bearer t0ps3cret"}}""",
                StoredWithToken)
            .Should().BeEmpty();

    [Fact]
    public void AddingACredentialHeader_IsRefusedEvenWhenSomethingElseWasStored() =>
        DeliveryConfigTransport.FindCredentialHeaders(
                """{"headers":{"Content-Type":"application/xml","X-Api-Key":"new"}}""",
                """{"headers":{"Content-Type":"application/xml"}}""")
            .Should().ContainSingle().Which.Should().Be("X-Api-Key");

    /// <summary>Rotation is a WRITE of a secret, which is exactly what this refuses.</summary>
    [Fact]
    public void ChangingTheValueOfAStoredCredentialHeader_IsRefused() =>
        DeliveryConfigTransport.FindCredentialHeaders(
                """{"url":"https://s.example/o","headers":{"Authorization":"Bearer rotated"}}""",
                StoredWithToken)
            .Should().ContainSingle().Which.Should().Be("Authorization");

    [Fact]
    public void RemovingAStoredCredentialHeader_IsAllowed() =>
        DeliveryConfigTransport.FindCredentialHeaders(
                """{"url":"https://s.example/o","headers":{"Content-Type":"application/xml"}}""",
                StoredWithToken)
            .Should().BeEmpty();

    [Fact]
    public void WithNoStoredBlob_NothingIsGrandfathered() =>
        DeliveryConfigTransport.FindCredentialHeaders(StoredWithToken, storedConfigJson: null)
            .Should().ContainSingle();

    /// <summary>
    /// A client that re-serialises the blob may change only the escaping of a value. That is not a
    /// rotated secret and must not be treated as one, or an unchanged round-trip would start being
    /// refused for a reason no operator could see.
    /// </summary>
    [Fact]
    public void AReEscapedButIdenticalValue_IsStillGrandfathered() =>
        DeliveryConfigTransport.FindCredentialHeaders(
                """{"headers":{"Authorization":"Bearer A1"}}""",
                """{"headers":{"Authorization":"Bearer \u00411"}}""")
            .Should().BeEmpty();

    [Fact]
    public void TheStoredHeaderNameMatchesCaseInsensitively() =>
        DeliveryConfigTransport.FindCredentialHeaders(
                """{"headers":{"authorization":"Bearer t0ps3cret"}}""",
                StoredWithToken)
            .Should().BeEmpty();

    // ── The operator-facing message ──────────────────────────────────────────

    /// <summary>
    /// The refusal is asserted FIRST. Asserting only that the message hides the token would pass
    /// vacuously the moment the guard stopped producing a message at all.
    /// </summary>
    [Fact]
    public void TheMessageNamesTheHeaderAndNeverItsValue()
    {
        DeliveryConfigTransport.FindCredentialHeaders(StoredWithToken).Should().NotBeEmpty();

        var message = DeliveryConfigTransport.DescribeCredentialHeaders(StoredWithToken);

        message.Should().NotBeNullOrWhiteSpace();
        message.Should().Contain("'Authorization'");
        message.Should().Contain("Remove the header and save the token as a credential instead.");
        message.Should().NotContain("t0ps3cret");
        message.Should().NotContain("Bearer t0ps3cret");
    }

    [Fact]
    public void TwoOffendingHeaders_ReadAsAPlural()
    {
        var message = DeliveryConfigTransport.DescribeCredentialHeaders(
            """{"headers":{"Authorization":"Bearer a","X-Api-Key":"b"}}""");

        message.Should().StartWith("Delivery config headers 'Authorization', 'X-Api-Key' hold credentials.");
    }

    [Fact]
    public void ACleanConfig_HasNoMessage() =>
        DeliveryConfigTransport.DescribeCredentialHeaders(
                """{"url":"https://s.example/o","headers":{"Content-Type":"application/xml"}}""")
            .Should().BeNull();
}
