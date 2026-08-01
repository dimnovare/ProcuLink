using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure.Services.Security;
using Renci.SshNet.Common;
using Renci.SshNet.Security;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services.Security;

/// <summary>
/// The subscriber that turns <see cref="SshHostKeyPolicy"/> from a type nobody calls into the thing
/// that decides whether a connection happens.
///
/// <para>
/// The live run this fixes recorded the failure precisely: SSH.NET's <c>CanTrustHostKey</c> returns
/// <c>true</c> when <c>HostKeyReceived</c> has no subscriber, so the server's identity was not part
/// of the decision at all. Two host-key sets, same host/port/user/password, same
/// <c>Success: True</c> both times.
/// </para>
/// </summary>
public class SshHostKeyVerifierTests
{
    private static readonly byte[] ServerA = Encoding.ASCII.GetBytes("host-key-blob-A");
    private static readonly byte[] ServerB = Encoding.ASCII.GetBytes("host-key-blob-B");

    private static string FingerprintOf(byte[] blob) => SshHostKeyPolicy.Fingerprint(blob);

    // ── Trust on first use ───────────────────────────────────────────────────

    [Fact]
    public void First_connection_is_allowed_and_the_fingerprint_is_reported_for_recording()
    {
        var verifier = new SshHostKeyVerifier("SFTP delivery", Array.Empty<string>());

        verifier.Observe(ServerA).Should().BeTrue();

        verifier.Verdict.Should().Be(SshHostKeyVerdict.TrustedOnFirstUse);
        verifier.Observed.Should().Be(FingerprintOf(ServerA));
        verifier.LearnedFingerprint.Should().Be(FingerprintOf(ServerA));
        verifier.Rejection.Should().BeNull();
    }

    // ── The pinned server, unchanged ─────────────────────────────────────────

    [Fact]
    public void A_matching_host_key_connects_and_teaches_nothing_new()
    {
        var verifier = new SshHostKeyVerifier("SFTP delivery", new[] { FingerprintOf(ServerA) });

        verifier.Observe(ServerA).Should().BeTrue();

        verifier.Verdict.Should().Be(SshHostKeyVerdict.Matched);
        // Nothing to write back: re-recording an already-pinned value would be a pointless UPDATE on
        // every single delivery.
        verifier.LearnedFingerprint.Should().BeNull();
    }

    // ── The whole point of the packet ────────────────────────────────────────

    /// <summary>
    /// The exact observable of the proof run, now with the opposite answer: same connection, the
    /// server swapped its host key, and the verifier refuses.
    /// </summary>
    [Fact]
    public void A_changed_host_key_aborts_the_connection()
    {
        var verifier = new SshHostKeyVerifier("SFTP delivery", new[] { FingerprintOf(ServerA) });

        verifier.Observe(ServerB).Should().BeFalse();

        verifier.Verdict.Should().Be(SshHostKeyVerdict.Rejected);
        verifier.Rejection.Should().NotBeNull();
        verifier.Rejection!.Observed.Should().Be(FingerprintOf(ServerB));
        verifier.Rejection.Pinned.Should().Equal(FingerprintOf(ServerA));
    }

    /// <summary>
    /// A rejection must NEVER produce something to persist. If it did, the refusal would pin the
    /// attacker's key and the very next connection would sail through — a verification feature that
    /// disarms itself on first contact with the thing it exists to catch.
    /// </summary>
    [Fact]
    public void A_rejected_host_key_is_never_offered_for_recording()
    {
        var verifier = new SshHostKeyVerifier("SFTP delivery", new[] { FingerprintOf(ServerA) });

        verifier.Observe(ServerB);

        verifier.LearnedFingerprint.Should().BeNull();
    }

    [Fact]
    public void Any_key_from_a_load_balanced_supplier_set_is_accepted()
    {
        var verifier = new SshHostKeyVerifier(
            "SFTP delivery", new[] { FingerprintOf(ServerA), FingerprintOf(ServerB) });

        verifier.Observe(ServerB).Should().BeTrue();
        verifier.Verdict.Should().Be(SshHostKeyVerdict.Matched);
    }

    // ── The refusal an operator reads ────────────────────────────────────────

    [Fact]
    public void ThrowIfRejected_raises_the_authored_message_not_the_librarys()
    {
        var verifier = new SshHostKeyVerifier("SFTP delivery", new[] { FingerprintOf(ServerA) });
        verifier.Observe(ServerB);

        var act = () => verifier.ThrowIfRejected();

        act.Should().Throw<SshHostKeyRejectedException>()
            .Which.Message.Should()
                .Contain(FingerprintOf(ServerB)).And
                .Contain(FingerprintOf(ServerA)).And
                // Not "Key exchange negotiation failed." — the library's own text, which names
                // neither the cause nor a next step.
                .NotContain("Key exchange negotiation failed");
    }

    [Fact]
    public void ThrowIfRejected_does_nothing_when_the_host_key_was_accepted()
    {
        var verifier = new SshHostKeyVerifier("SFTP delivery", Array.Empty<string>());
        verifier.Observe(ServerA);

        var act = () => verifier.ThrowIfRejected();

        act.Should().NotThrow();
    }

    [Fact]
    public void ThrowIfRejected_does_nothing_before_any_host_key_has_been_seen()
    {
        // A connection that fails on the socket never reaches key exchange. The caller calls
        // ThrowIfRejected first in every connect-failure path, so this must be a no-op.
        var verifier = new SshHostKeyVerifier("SFTP delivery", new[] { "SHA256:whatever" });

        var act = () => verifier.ThrowIfRejected();

        act.Should().NotThrow();
        verifier.Verdict.Should().BeNull();
    }

    // ── The production wire ──────────────────────────────────────────────────

    /// <summary>
    /// <see cref="SshHostKeyVerifier.Attach"/> registers exactly this delegate, so invoking it with a
    /// real <see cref="HostKeyEventArgs"/> exercises the same statement SSH.NET executes during key
    /// exchange. Without this, <c>Observe</c> could be perfect while <c>e.CanTrust</c> was never
    /// assigned — which is precisely the shape of the defect being fixed.
    /// </summary>
    [Fact]
    public void The_registered_handler_sets_CanTrust_false_for_a_changed_key()
    {
        var (args, blob) = RealHostKeyEventArgs();
        var verifier = new SshHostKeyVerifier("SFTP delivery", new[] { FingerprintOf(ServerA) });

        // Sanity: the blob under test is genuinely not the pinned one.
        SshHostKeyPolicy.Fingerprint(blob).Should().NotBe(FingerprintOf(ServerA));
        args.CanTrust.Should().BeTrue("SSH.NET hands the subscriber a trusting default — that default is the defect");

        verifier.OnHostKeyReceived(null, args);

        args.CanTrust.Should().BeFalse();
    }

    [Fact]
    public void The_registered_handler_leaves_CanTrust_true_for_the_pinned_key()
    {
        var (args, blob) = RealHostKeyEventArgs();
        var verifier = new SshHostKeyVerifier("SFTP delivery", new[] { SshHostKeyPolicy.Fingerprint(blob) });

        verifier.OnHostKeyReceived(null, args);

        args.CanTrust.Should().BeTrue();
        verifier.Verdict.Should().Be(SshHostKeyVerdict.Matched);
    }

    /// <summary>
    /// The fingerprint the handler computes must come from the blob SSH.NET actually exposes, and be
    /// the format OpenSSH prints. SSH.NET computes the same digest independently
    /// (<c>FingerPrintSHA256</c>, documented as "non-padded base64, but without the SHA256: prefix"),
    /// so the two agreeing is a genuine cross-check of our own arithmetic against the library's —
    /// not a tautology, because the two computations share no code.
    /// </summary>
    [Fact]
    public void The_observed_fingerprint_agrees_with_SSH_NETs_own_computation()
    {
        var (args, _) = RealHostKeyEventArgs();
        var verifier = new SshHostKeyVerifier("SFTP delivery", Array.Empty<string>());

        verifier.OnHostKeyReceived(null, args);

        verifier.Observed.Should().Be("SHA256:" + args.FingerPrintSHA256);
    }

    /// <summary>
    /// A genuine <see cref="HostKeyEventArgs"/> built over a real RSA host key, so the test drives
    /// the library's own type rather than a stand-in. Returns the args and the exact blob the
    /// library will expose as <c>HostKey</c>.
    /// </summary>
    private static (HostKeyEventArgs Args, byte[] Blob) RealHostKeyEventArgs()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaKey(rsa.ExportRSAPrivateKey());
        var algorithm = new KeyHostAlgorithm("ssh-rsa", key);
        var args = new HostKeyEventArgs(algorithm);
        return (args, args.HostKey);
    }
}
