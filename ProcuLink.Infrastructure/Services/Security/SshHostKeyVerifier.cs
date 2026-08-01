using Renci.SshNet;
using Renci.SshNet.Common;
using ProcuLink.Core.Services.Security;

namespace ProcuLink.Infrastructure.Services.Security;

/// <summary>
/// The SSH.NET half of host-key verification: the subscriber that <see cref="SshHostKeyPolicy"/>
/// needs in order to be consulted at all.
///
/// <para>
/// SSH.NET's <c>CanTrustHostKey</c> returns <c>true</c> when <see cref="BaseClient.HostKeyReceived"/>
/// has no subscriber, and before this class nothing in the repository subscribed — verified live, not
/// decompiled: a probe client that DOES subscribe was handed <c>e.CanTrust == True</c> before it
/// looked at anything (<c>docs/ops/2026-08-01-wp38-delivery-channel-proof.md</c> §1). Attaching one
/// verifier per connection is therefore the entire difference between "we check who we are talking
/// to" and "we do not".
/// </para>
///
/// <para>
/// One verifier per connection attempt — it carries the outcome of that attempt and is not reusable.
/// All three SFTP consumers (delivery, order polling, catalog pull) attach one.
/// </para>
/// </summary>
public sealed class SshHostKeyVerifier
{
    private readonly string _channelNoun;
    private readonly IReadOnlyList<string> _pinned;

    /// <param name="channelNoun">
    /// How the refusal names itself to an operator — "SFTP delivery", "SFTP polling",
    /// "The catalog sync". Appears at the head of the sentence they read.
    /// </param>
    /// <param name="pinned">
    /// The fingerprints this connection already trusts. Empty ⇒ trust-on-first-use: connect, and
    /// report back what was seen so the caller can record it.
    /// </param>
    public SshHostKeyVerifier(string channelNoun, IReadOnlyList<string> pinned)
    {
        _channelNoun = channelNoun;
        _pinned = pinned;
    }

    /// <summary>The fingerprint the server presented, once it has presented one.</summary>
    public string? Observed { get; private set; }

    /// <summary>Null until the server has stated its identity; one of the three verdicts after.</summary>
    public SshHostKeyVerdict? Verdict { get; private set; }

    /// <summary>
    /// The authored refusal, built at the moment of the decision so it holds the exact pair that was
    /// compared. Null unless the verdict was <see cref="SshHostKeyVerdict.Rejected"/>.
    /// </summary>
    public SshHostKeyRejectedException? Rejection { get; private set; }

    /// <summary>
    /// The fingerprint the CALLER should persist, and only when there is genuinely something new to
    /// learn. Null on a matched connection (already stored), null on a rejected one (never store what
    /// we just refused — that would turn a refusal into a silent re-pin on the next attempt), and
    /// null when nothing was observed at all.
    /// </summary>
    public string? LearnedFingerprint =>
        Verdict == SshHostKeyVerdict.TrustedOnFirstUse && !string.IsNullOrEmpty(Observed)
            ? Observed
            : null;

    /// <summary>
    /// Subscribe to the client's host-key event. MUST be called before <c>Connect()</c> — SSH.NET
    /// raises the event during key exchange, and a subscription added afterwards verifies nothing
    /// while looking exactly like one that does.
    /// </summary>
    public void Attach(BaseClient client)
    {
        client.HostKeyReceived += OnHostKeyReceived;
    }

    /// <summary>
    /// The production handler, as a named method rather than a lambda so a test can invoke the exact
    /// delegate <see cref="Attach"/> registers. It has one statement: whatever <see cref="Observe"/>
    /// decides IS <c>e.CanTrust</c>. Nothing may be added here that the byte-level tests cannot see.
    /// </summary>
    internal void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        e.CanTrust = Observe(e.HostKey);
    }

    /// <summary>
    /// The decision, taken on the raw host-key blob. Public and byte-oriented rather than
    /// event-oriented so the security-critical branch is reachable by a test that needs no SSH
    /// server, while remaining the SAME expression the live path evaluates — <see cref="Attach"/>
    /// forwards its return value straight into <c>e.CanTrust</c> and does nothing else.
    /// </summary>
    /// <returns><c>true</c> to let the connection proceed; <c>false</c> to abort it.</returns>
    public bool Observe(byte[] hostKeyBlob)
    {
        Observed = SshHostKeyPolicy.Fingerprint(hostKeyBlob);
        Verdict = SshHostKeyPolicy.Decide(_pinned, Observed);

        if (Verdict != SshHostKeyVerdict.Rejected) return true;

        Rejection = new SshHostKeyRejectedException(_channelNoun, Observed, _pinned);
        return false;
    }

    /// <summary>
    /// Replaces the library's refusal with ours.
    ///
    /// <para>
    /// When a subscriber sets <c>CanTrust = false</c>, SSH.NET aborts the connection — verified live
    /// on the pinned 2024.2.0 — and surfaces it as
    /// <c>Renci.SshNet.Common.SshConnectionException: Key exchange negotiation failed.</c>, which
    /// names neither the cause nor a next step and is indistinguishable from an algorithm mismatch.
    /// Every caller therefore calls this in its connect-failure path BEFORE mapping the library's own
    /// exception, so the operator reads the sentence that tells them what happened.
    /// </para>
    /// </summary>
    public void ThrowIfRejected()
    {
        if (Rejection is not null) throw Rejection;
    }
}
