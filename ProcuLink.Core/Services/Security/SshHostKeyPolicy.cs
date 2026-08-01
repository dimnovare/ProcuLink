using System.Security.Cryptography;

namespace ProcuLink.Core.Services.Security;

/// <summary>
/// What ProcuLink does when an SSH server states who it is.
///
/// <para>
/// Until this existed, the answer was "nothing". A live run against a throwaway OpenSSH 10.3p1
/// container flipped the server between two independently generated host-key sets with host, port,
/// username and password held identical, and the SFTP delivery dispatcher returned
/// <c>Success: True</c> both times, with no warning and no log line — and, because supplier SFTP
/// accounts are usually password accounts, handed the password to the second identity along with the
/// purchase order (<c>docs/ops/2026-08-01-wp38-delivery-channel-proof.md</c> §1). SSH.NET's
/// <c>CanTrustHostKey</c> returns <c>true</c> when <c>HostKeyReceived</c> has no subscriber, and
/// nothing in the codebase subscribed.
/// </para>
///
/// <para><b>The policy is trust-on-first-use, plus an optional pin.</b> The first connection to a
/// server records the fingerprint it presented; every connection after that must present the same
/// one, or it is refused. Only the very first connection is unverified, and no existing supplier
/// needs a migration or an operator visit to become protected. An operator who wants the strict
/// case can type the supplier's fingerprint in before the first connection ever happens, and then
/// even that one is verified.</para>
///
/// <para>
/// This type is deliberately free of SSH.NET: it is the decision, not the transport. The subscriber
/// that feeds it lives in <c>ProcuLink.Infrastructure.Services.Security.SshHostKeyVerifier</c>, and
/// all three SFTP consumers — delivery, order polling and catalog pull — go through both.
/// </para>
/// </summary>
public static class SshHostKeyPolicy
{
    /// <summary>The one canonical prefix. OpenSSH prints it; so do we, so the two can be compared.</summary>
    public const string Sha256Prefix = "SHA256:";

    private static readonly char[] Separators = [',', ';', '\n', '\r', ' ', '\t'];

    /// <summary>
    /// The OpenSSH SHA-256 fingerprint of a raw host-key blob: <c>SHA256:</c> + unpadded base64 of
    /// the digest. Byte-identical to what <c>ssh-keygen -lf</c> prints and to what an operator sees
    /// in their own client's "REMOTE HOST IDENTIFICATION HAS CHANGED" warning.
    ///
    /// <para>
    /// Computed here rather than read from SSH.NET's own <c>FingerPrintSHA256</c> for one reason:
    /// the value is shown to an operator and asked to be compared against their terminal, so its
    /// format has to be a property of OUR code that a test can pin, not a property of a library
    /// version that a package bump could restyle underneath us.
    /// </para>
    /// </summary>
    public static string Fingerprint(ReadOnlySpan<byte> hostKeyBlob) =>
        Sha256Prefix + Convert.ToBase64String(SHA256.HashData(hostKeyBlob)).TrimEnd('=');

    /// <summary>
    /// Reads the pinned set out of whatever an operator or an earlier trust-on-first-use wrote.
    /// Empty (or absent) means nothing is pinned — the trust-on-first-use case.
    ///
    /// <para>
    /// A SET, not a value: a supplier behind a load balancer legitimately answers with several
    /// different host keys, and a scalar would refuse two thirds of its own connections.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return [];

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in stored.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalised = Normalise(raw);
            if (normalised.Length == 0) continue;
            if (seen.Add(normalised)) result.Add(normalised);
        }

        return result;
    }

    /// <summary>
    /// The pinned set as one storable string, or null when nothing is pinned so the column/JSON key
    /// stays genuinely empty rather than holding <c>""</c> — "never pinned" and "pinned to nothing"
    /// must not be two spellings of the same state.
    /// </summary>
    public static string? Serialise(IEnumerable<string> fingerprints)
    {
        var joined = string.Join("\n", fingerprints.Select(Normalise).Where(f => f.Length > 0).Distinct(StringComparer.Ordinal));
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>
    /// The three outcomes, and the only place the decision is made. Callers translate a verdict into
    /// a connection, a refusal, or a first-use record — none of them re-derives it.
    /// </summary>
    public static SshHostKeyVerdict Decide(IReadOnlyList<string> pinned, string observed)
    {
        if (pinned.Count == 0) return SshHostKeyVerdict.TrustedOnFirstUse;

        // A blank observation means the subscriber never ran. That is a bug on our side, and the
        // fail-safe reading of a bug is "we did not verify", not "it matched".
        if (string.IsNullOrWhiteSpace(observed)) return SshHostKeyVerdict.Rejected;

        var normalised = Normalise(observed);

        // Ordinal, because base64 is case-sensitive: 'SHA256:abc' and 'SHA256:ABC' are two different
        // servers, and a case-insensitive compare would make them indistinguishable.
        return pinned.Contains(normalised, StringComparer.Ordinal)
            ? SshHostKeyVerdict.Matched
            : SshHostKeyVerdict.Rejected;
    }

    /// <summary>
    /// What the operator reads when a pinned server presents a different key.
    ///
    /// <para>
    /// The library's own refusal is <c>Renci.SshNet.Common.SshConnectionException: Key exchange
    /// negotiation failed.</c> — verified live, and useless: it names neither the cause nor a next
    /// step, and it is indistinguishable from an algorithm mismatch. WP-38's acceptance criterion is
    /// "blocks the transfer with an actionable message", so the message is ours to write, exactly as
    /// <c>FtpsDeliveryDispatcher.DescribeTlsHandshakeFailure</c> had to be.
    /// </para>
    ///
    /// <para>
    /// It carries BOTH fingerprints because the operator's next action is a comparison they must be
    /// able to make themselves — against the supplier's own <c>ssh-keygen -lf</c> output — and it
    /// names the way forward because a refusal with no way forward is its own defect: a supplier who
    /// genuinely rebuilt their server would otherwise be permanently unreachable.
    /// </para>
    /// </summary>
    public static string RejectionMessage(string channelNoun, string observed, IReadOnlyList<string> pinned)
    {
        var pinnedList = pinned.Count switch
        {
            0 => "(none recorded)",
            1 => pinned[0],
            _ => string.Join(" or ", pinned),
        };

        return
            $"{channelNoun} stopped before anything was transferred: the server's SSH identity has changed " +
            $"since ProcuLink first recorded it. The server now identifies itself as {observed}, but this " +
            $"connection is pinned to {pinnedList}. A changed host key is what an intercepted connection " +
            "looks like — and also what a legitimately rebuilt server looks like — so ProcuLink refuses " +
            "rather than guess, and no credentials were sent to the new server. Ask the supplier whether " +
            "they rebuilt or replaced this server. If they did, compare their own fingerprint against the " +
            "one above and then replace the pinned fingerprint with it to trust the new server. If they " +
            "changed nothing, treat this as an interception and do not reconnect.";
    }

    /// <summary>
    /// One fingerprint, in the one spelling everything else compares against.
    ///
    /// <para>
    /// Deliberately narrow. The prefix is case-corrected and base64 padding is dropped, because both
    /// spellings genuinely name the same key and an operator should not be refused over punctuation.
    /// A bare digest gains the prefix for the same reason. Anything else — an MD5 fingerprint pasted
    /// from <c>ssh-keygen -E md5</c>, a <c>known_hosts</c> line, a typo — is left EXACTLY as typed, so
    /// it matches nothing and the operator is shown the mismatch. Reshaping an unrecognised value
    /// into something that might match is the one failure this function must not have.
    /// </para>
    /// </summary>
    private static string Normalise(string raw)
    {
        var value = raw.Trim();
        if (value.Length == 0) return string.Empty;

        if (value.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
            return Sha256Prefix + value[Sha256Prefix.Length..].TrimEnd('=');

        // No prefix and no other colon ⇒ a bare SHA-256 digest. A colon means it is some other
        // fingerprint format (MD5's hex pairs, a known_hosts entry) and must not be relabelled.
        return value.Contains(':') ? value : Sha256Prefix + value.TrimEnd('=');
    }
}

/// <summary>The three answers to "may we talk to this server?".</summary>
public enum SshHostKeyVerdict
{
    /// <summary>Nothing was pinned. Connect, and record what we saw so the next one is verified.</summary>
    TrustedOnFirstUse = 0,

    /// <summary>The server presented a fingerprint we already trust.</summary>
    Matched = 1,

    /// <summary>The server presented something else. Refuse.</summary>
    Rejected = 2,
}

/// <summary>
/// Thrown by the SFTP transports when a pinned server presents a different host key. Lives in Core
/// (not Infrastructure) so every consumer can catch it without taking a dependency on SSH.NET, and
/// carries both sides of the comparison so no caller has to re-author the sentence.
/// </summary>
public sealed class SshHostKeyRejectedException : Exception
{
    public SshHostKeyRejectedException(string channelNoun, string observed, IReadOnlyList<string> pinned)
        : base(SshHostKeyPolicy.RejectionMessage(channelNoun, observed, pinned))
    {
        Observed = observed;
        Pinned = pinned;
    }

    /// <summary>The fingerprint the server actually presented.</summary>
    public string Observed { get; }

    /// <summary>The fingerprint(s) this connection is pinned to.</summary>
    public IReadOnlyList<string> Pinned { get; }
}
