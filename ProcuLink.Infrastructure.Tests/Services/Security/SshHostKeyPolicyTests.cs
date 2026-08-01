using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using ProcuLink.Core.Services.Security;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services.Security;

/// <summary>
/// The pure half of SFTP host-key verification: what a fingerprint IS, what an operator may have
/// typed into the pinned field, and which of the three verdicts a given pair produces.
///
/// <para>
/// The fingerprint format is not ours to invent. It is OpenSSH's — <c>SHA256:</c> followed by the
/// unpadded base64 of the SHA-256 of the raw host-key blob, exactly what <c>ssh-keygen -lf</c> and
/// the client's own "REMOTE HOST IDENTIFICATION HAS CHANGED" warning print. An operator comparing
/// what ProcuLink recorded against what their own terminal shows must see the same string, or the
/// re-trust decision they are being asked to make is unverifiable.
/// </para>
/// </summary>
public class SshHostKeyPolicyTests
{
    // ── Fingerprint ──────────────────────────────────────────────────────────

    /// <summary>
    /// Pinned against an INDEPENDENTLY computed value rather than against
    /// <see cref="SshHostKeyPolicy.Fingerprint"/> calling itself: the constant below is
    /// SHA-256("ssh-host-key-blob") base64'd with padding stripped, computed here from primitives.
    /// A test that asserted Fingerprint(x) == Fingerprint(x) would prove determinism and nothing
    /// about the format.
    /// </summary>
    [Fact]
    public void Fingerprint_is_openssh_sha256_base64_unpadded()
    {
        var blob = Encoding.ASCII.GetBytes("ssh-host-key-blob");
        var expected = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

        SshHostKeyPolicy.Fingerprint(blob).Should().Be(expected);
    }

    [Fact]
    public void Fingerprint_never_carries_base64_padding()
    {
        // 32 bytes of SHA-256 always base64 to 44 chars ending in exactly one '='. OpenSSH prints
        // 43. If padding ever leaks through, every fingerprint an operator pastes from ssh-keygen
        // mismatches the one we stored, and the feature refuses every legitimate connection.
        var fingerprint = SshHostKeyPolicy.Fingerprint(new byte[] { 1, 2, 3 });

        fingerprint.Should().NotEndWith("=");
        fingerprint.Should().HaveLength("SHA256:".Length + 43);
    }

    [Fact]
    public void Fingerprint_distinguishes_two_different_host_keys()
    {
        SshHostKeyPolicy.Fingerprint(Encoding.ASCII.GetBytes("key-set-A"))
            .Should().NotBe(SshHostKeyPolicy.Fingerprint(Encoding.ASCII.GetBytes("key-set-B")));
    }

    // ── Parse ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    public void Parse_treats_absent_or_blank_as_nothing_pinned(string? stored)
    {
        SshHostKeyPolicy.Parse(stored).Should().BeEmpty();
    }

    [Fact]
    public void Parse_reads_several_fingerprints_from_one_field()
    {
        // A supplier behind a load balancer legitimately presents more than one host key.
        var stored = "SHA256:aaa\nSHA256:bbb , SHA256:ccc;SHA256:ddd";

        SshHostKeyPolicy.Parse(stored)
            .Should().Equal("SHA256:aaa", "SHA256:bbb", "SHA256:ccc", "SHA256:ddd");
    }

    [Fact]
    public void Parse_canonicalises_prefix_case_and_strips_padding()
    {
        // ssh-keygen prints no padding, but a fingerprint copied out of a script that base64'd the
        // digest itself will carry it. Both name the same key, so both must match it.
        SshHostKeyPolicy.Parse("sha256:AbC=").Should().Equal("SHA256:AbC");
    }

    [Fact]
    public void Parse_adds_the_prefix_to_a_bare_digest()
    {
        SshHostKeyPolicy.Parse("AbC+/9").Should().Equal("SHA256:AbC+/9");
    }

    [Fact]
    public void Parse_leaves_a_non_sha256_fingerprint_alone_so_it_can_never_match()
    {
        // An MD5 fingerprint (ssh-keygen -E md5) pasted by mistake must NOT be silently reshaped
        // into something that could collide with a SHA-256 value. It stays as typed, matches
        // nothing, and the operator is shown the mismatch.
        SshHostKeyPolicy.Parse("MD5:aa:bb:cc").Should().Equal("MD5:aa:bb:cc");
    }

    [Fact]
    public void Parse_drops_duplicates_but_keeps_order()
    {
        SshHostKeyPolicy.Parse("SHA256:aaa, SHA256:bbb, sha256:aaa")
            .Should().Equal("SHA256:aaa", "SHA256:bbb");
    }

    [Fact]
    public void Parse_keeps_base64_case_because_base64_is_case_sensitive()
    {
        // 'SHA256:abc' and 'SHA256:ABC' are different keys. Upper-casing for a "tolerant" compare
        // would make two distinct servers indistinguishable.
        SshHostKeyPolicy.Parse("SHA256:abc").Should().Equal("SHA256:abc");
        SshHostKeyPolicy.Parse("SHA256:abc").Should().NotEqual(new[] { "SHA256:ABC" });
    }

    // ── Decide ───────────────────────────────────────────────────────────────

    [Fact]
    public void Decide_trusts_on_first_use_when_nothing_is_pinned()
    {
        SshHostKeyPolicy.Decide(Array.Empty<string>(), "SHA256:observed")
            .Should().Be(SshHostKeyVerdict.TrustedOnFirstUse);
    }

    [Fact]
    public void Decide_matches_a_pinned_fingerprint()
    {
        SshHostKeyPolicy.Decide(new[] { "SHA256:aaa" }, "SHA256:aaa")
            .Should().Be(SshHostKeyVerdict.Matched);
    }

    [Fact]
    public void Decide_matches_any_member_of_a_pinned_set()
    {
        SshHostKeyPolicy.Decide(new[] { "SHA256:aaa", "SHA256:bbb" }, "SHA256:bbb")
            .Should().Be(SshHostKeyVerdict.Matched);
    }

    /// <summary>
    /// The whole packet in one assertion: the exact observable the proof run produced — same host,
    /// same port, same user, same password, DIFFERENT host key — must now come back Rejected.
    /// </summary>
    [Fact]
    public void Decide_rejects_a_changed_host_key()
    {
        var setA = "SHA256:a4SDSyjWzHZRGJboAZH7YdDdochcU+JCeh2Yj+GXTsw";
        var setB = "SHA256:ai1X2iIAsJtHWuquGw8cQxn5DUD55PDciTIy6PfdAmw";

        SshHostKeyPolicy.Decide(new[] { setA }, setB).Should().Be(SshHostKeyVerdict.Rejected);
    }

    [Fact]
    public void Decide_is_case_sensitive_on_the_digest()
    {
        SshHostKeyPolicy.Decide(new[] { "SHA256:aaa" }, "SHA256:AAA")
            .Should().Be(SshHostKeyVerdict.Rejected);
    }

    [Fact]
    public void Decide_rejects_a_blank_observation_rather_than_calling_it_first_use()
    {
        // Defence in depth: if the HostKeyReceived subscriber ever failed to run, an empty observed
        // value must not be able to pass a pinned check.
        SshHostKeyPolicy.Decide(new[] { "SHA256:aaa" }, "").Should().Be(SshHostKeyVerdict.Rejected);
    }

    // ── Serialise ────────────────────────────────────────────────────────────

    [Fact]
    public void Serialise_round_trips_through_Parse()
    {
        var pinned = SshHostKeyPolicy.Parse("SHA256:aaa, SHA256:bbb");

        SshHostKeyPolicy.Parse(SshHostKeyPolicy.Serialise(pinned)).Should().Equal(pinned);
    }

    [Fact]
    public void Serialise_of_nothing_is_null_so_the_column_stays_empty()
    {
        SshHostKeyPolicy.Serialise(Array.Empty<string>()).Should().BeNull();
    }

    // ── The operator-facing sentence ─────────────────────────────────────────

    /// <summary>
    /// WP-38's acceptance criterion is "blocks the transfer with an ACTIONABLE message", and the
    /// library's own refusal — <c>SshConnectionException: Key exchange negotiation failed.</c> —
    /// is not one. The message must carry both fingerprints (so the operator can compare them
    /// against their own terminal) and the next step (so a legitimate server rebuild has a way
    /// forward). A refusal with no way forward is its own defect.
    /// </summary>
    [Fact]
    public void RejectionMessage_names_both_fingerprints_and_the_next_step()
    {
        var message = SshHostKeyPolicy.RejectionMessage(
            "SFTP delivery",
            observed: "SHA256:newnewnew",
            pinned: new[] { "SHA256:oldoldold" });

        message.Should().Contain("SHA256:newnewnew");
        message.Should().Contain("SHA256:oldoldold");
        message.Should().Contain("SFTP delivery");
        // The way forward, not just the refusal.
        message.Should().ContainEquivalentOf("trust");
    }

    /// <summary>
    /// The sentence must state that the refusal happened BEFORE any bytes moved. On delivery that is
    /// the difference between "your purchase order is sitting on a stranger's server" and "it is
    /// still here"; on polling it is the difference between "a file was imported from an unknown
    /// server" and "nothing was read". Both readers need the same reassurance, so the wording is
    /// channel-neutral rather than "nothing was sent".
    /// </summary>
    [Fact]
    public void RejectionMessage_says_the_refusal_happened_before_any_bytes_moved()
    {
        var message = SshHostKeyPolicy.RejectionMessage("SFTP delivery", "SHA256:x", new[] { "SHA256:y" });

        message.Should().ContainEquivalentOf("before anything was transferred");
    }

    [Fact]
    public void RejectionMessage_lists_every_pinned_fingerprint()
    {
        var message = SshHostKeyPolicy.RejectionMessage(
            "SFTP polling", "SHA256:z", new[] { "SHA256:one", "SHA256:two" });

        message.Should().Contain("SHA256:one");
        message.Should().Contain("SHA256:two");
    }

    // ── The exception the transports throw ───────────────────────────────────

    [Fact]
    public void RejectedException_carries_both_sides_of_the_comparison()
    {
        var ex = new SshHostKeyRejectedException("SFTP delivery", "SHA256:seen", new[] { "SHA256:stored" });

        ex.Observed.Should().Be("SHA256:seen");
        ex.Pinned.Should().Equal("SHA256:stored");
        ex.Message.Should().Contain("SHA256:seen").And.Contain("SHA256:stored");
    }
}
