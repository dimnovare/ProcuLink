using System.Text.Json;
using FluentAssertions;
using ProcuLink.Infrastructure.Services.Security;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services.Security;

/// <summary>
/// Where a supplier's trusted SSH host key lives on a delivery config, and — the part that decides
/// whether the whole feature is real — what happens to it when an operator saves the config from a
/// client that has never heard of it.
/// </summary>
public class DeliveryHostKeyConfigTests
{
    // ── Read ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"host\":\"sftp.supplier.com\"}")]
    public void Nothing_pinned_reads_as_nothing_pinned(string? configJson)
    {
        DeliveryHostKeyConfig.Read(configJson).Should().BeEmpty();
    }

    [Fact]
    public void Reads_the_array_it_writes()
    {
        var json = """{"host":"sftp.supplier.com","hostKeyFingerprints":["SHA256:aaa","SHA256:bbb"]}""";

        DeliveryHostKeyConfig.Read(json).Should().Equal("SHA256:aaa", "SHA256:bbb");
    }

    [Fact]
    public void Reads_a_single_fingerprint_typed_as_a_bare_string()
    {
        // What an operator pasting one fingerprint by hand most naturally produces. Refusing their
        // supplier's delivery over JSON punctuation would be its own defect.
        var json = """{"hostKeyFingerprints":"SHA256:aaa"}""";

        DeliveryHostKeyConfig.Read(json).Should().Equal("SHA256:aaa");
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"hostKeyFingerprints\":42}")]
    [InlineData("{\"hostKeyFingerprints\":null}")]
    public void Unusable_config_reads_as_nothing_pinned_never_as_a_pin_nobody_set(string configJson)
    {
        // The fail-safe direction here is the PERMISSIVE one, unusually: fabricating a pin out of
        // malformed config would refuse a working supplier's purchase orders over a typo in an
        // unrelated field. The config's own parse reports the malformation.
        DeliveryHostKeyConfig.Read(configJson).Should().BeEmpty();
    }

    // ── Write ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The contract that matters: this runs on a LIVE delivery config carrying the host, the remote
    /// path, the timeout and the operator's overwrite setting. A write that rebuilt the object from
    /// the fields it knows about would silently reset the ones it does not — a worse bug than the
    /// one being fixed.
    /// </summary>
    [Fact]
    public void Write_preserves_every_other_property()
    {
        var json = """{"host":"sftp.supplier.com","port":2222,"remotePath":"/in","overwriteExisting":false,"timeoutSeconds":45}""";

        var written = DeliveryHostKeyConfig.Write(json, ["SHA256:aaa"]);

        using var doc = JsonDocument.Parse(written);
        doc.RootElement.GetProperty("host").GetString().Should().Be("sftp.supplier.com");
        doc.RootElement.GetProperty("port").GetInt32().Should().Be(2222);
        doc.RootElement.GetProperty("remotePath").GetString().Should().Be("/in");
        doc.RootElement.GetProperty("overwriteExisting").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("timeoutSeconds").GetInt32().Should().Be(45);
        DeliveryHostKeyConfig.Read(written).Should().Equal("SHA256:aaa");
    }

    [Fact]
    public void Write_replaces_rather_than_appends()
    {
        var json = """{"hostKeyFingerprints":["SHA256:old"]}""";

        DeliveryHostKeyConfig.Read(DeliveryHostKeyConfig.Write(json, ["SHA256:new"]))
            .Should().Equal("SHA256:new");
    }

    [Fact]
    public void Write_of_nothing_removes_the_property_entirely()
    {
        var json = """{"host":"h","hostKeyFingerprints":["SHA256:old"]}""";

        var written = DeliveryHostKeyConfig.Write(json, []);

        DeliveryHostKeyConfig.IsPresent(written).Should().BeFalse(
            "'never pinned' and 'pinned to nothing' must not be two spellings of the same state");
        JsonDocument.Parse(written).RootElement.GetProperty("host").GetString().Should().Be("h");
    }

    [Fact]
    public void Write_refuses_to_replace_config_it_cannot_parse()
    {
        const string broken = "{ not json";

        DeliveryHostKeyConfig.Write(broken, ["SHA256:aaa"]).Should().Be(broken);
    }

    [Fact]
    public void Write_starts_an_object_when_there_is_no_config_at_all()
    {
        DeliveryHostKeyConfig.Read(DeliveryHostKeyConfig.Write(null, ["SHA256:aaa"]))
            .Should().Equal("SHA256:aaa");
    }

    // ── IsPresent ────────────────────────────────────────────────────────────

    [Fact]
    public void An_empty_array_is_present_even_though_it_reads_as_nothing_pinned()
    {
        const string json = """{"hostKeyFingerprints":[]}""";

        DeliveryHostKeyConfig.IsPresent(json).Should().BeTrue();
        DeliveryHostKeyConfig.Read(json).Should().BeEmpty();
    }

    [Fact]
    public void An_absent_property_is_not_present()
    {
        DeliveryHostKeyConfig.IsPresent("""{"host":"h"}""").Should().BeFalse();
    }

    // ── PreserveRecordedFingerprints — the save path ─────────────────────────

    /// <summary>
    /// The defect this prevents: the delivery-config save is a whole-object replace, and no client
    /// sends a property it has never heard of. Without this, an operator changing the timeout would
    /// un-pin the supplier, and the next delivery would trust-on-first-use all over again — a
    /// verification feature that disarms itself the first time anyone edits anything.
    /// </summary>
    [Fact]
    public void A_save_from_a_client_that_never_heard_of_host_keys_keeps_the_pin()
    {
        const string stored = """{"host":"h","timeoutSeconds":30,"hostKeyFingerprints":["SHA256:pinned"]}""";
        const string incoming = """{"host":"h","timeoutSeconds":45}""";

        var saved = DeliveryHostKeyConfig.PreserveRecordedFingerprints(incoming, stored);

        DeliveryHostKeyConfig.Read(saved).Should().Equal("SHA256:pinned");
        JsonDocument.Parse(saved).RootElement.GetProperty("timeoutSeconds").GetInt32().Should().Be(45,
            "the operator's actual edit must still land");
    }

    /// <summary>
    /// The other half, and why "keep" cannot simply be "always keep": an operator whose supplier
    /// genuinely rebuilt their server needs a way forward. Sending the property explicitly IS that
    /// way, and an empty array is how they say "trust the next one you meet".
    /// </summary>
    [Fact]
    public void An_explicit_empty_array_clears_the_pin_and_is_the_re_trust_path()
    {
        const string stored = """{"hostKeyFingerprints":["SHA256:pinned"]}""";
        const string incoming = """{"hostKeyFingerprints":[]}""";

        var saved = DeliveryHostKeyConfig.PreserveRecordedFingerprints(incoming, stored);

        DeliveryHostKeyConfig.Read(saved).Should().BeEmpty();
    }

    [Fact]
    public void An_explicit_new_fingerprint_replaces_the_stored_one()
    {
        const string stored = """{"hostKeyFingerprints":["SHA256:old"]}""";
        const string incoming = """{"hostKeyFingerprints":["SHA256:new"]}""";

        DeliveryHostKeyConfig.Read(DeliveryHostKeyConfig.PreserveRecordedFingerprints(incoming, stored))
            .Should().Equal("SHA256:new");
    }

    [Fact]
    public void An_ordinary_save_with_nothing_pinned_is_left_byte_identical()
    {
        // No pin to preserve ⇒ no reason to rewrite the operator's JSON at all. Keeps this out of
        // the way of anything that compares ConfigJson as a string.
        const string incoming = """{"host":"h",  "timeoutSeconds":45}""";

        DeliveryHostKeyConfig.PreserveRecordedFingerprints(incoming, """{"host":"h"}""")
            .Should().BeSameAs(incoming);
    }
}
