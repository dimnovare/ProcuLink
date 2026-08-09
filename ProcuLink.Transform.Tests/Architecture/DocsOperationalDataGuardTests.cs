using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace ProcuLink.Transform.Tests.Architecture;

/// <summary>
/// The fourth rotation of the same defect, in the place the first three could not see:
/// <b><c>docs/</c></b>.
///
/// <para>#153 scrubbed fixture CONTENTS, #179 fixture PATHS, #181 fixture URLs and account
/// identifiers, #184 personal data in <c>.cs</c> source, #185 cXML network identities. Every one
/// of those guards draws a corpus that <b>cannot see a markdown file</b>:
/// <see cref="FixtureCorpus.TrackedFixtureFiles"/> selects directories ending in "Fixtures", and
/// <see cref="SourcePersonalDataGuardTests"/> selects <c>*.cs</c>. #185 reported the consequence
/// without fixing it — live SFTP/FTP/HTTPS vendor hostnames, resolved IP addresses, service
/// banners, a customer account id inside a feed URL, production record ids, a VAT number and real
/// PO numbers, all sitting in QA transcripts and handover prompts under <c>docs/</c>.</para>
///
/// <para><b>Scan breadth is the recurring failure in this family, so this corpus is every tracked
/// file under <c>docs/</c> — not every tracked <c>*.md</c>.</b> That distinction is load-bearing:
/// <c>docs/</c> also holds a deployed Cloudflare Worker (<c>.js</c>/<c>.mjs</c>), a SQL artefact,
/// design-system <c>.tsx</c>/<c>.ts</c>/<c>.css</c>/<c>.json</c>, an <c>.html</c> showcase and
/// <c>.svg</c> brand assets. #185 found a removed identity in <c>tools/…/generate.mjs</c>,
/// invisible to a <c>.cs</c>-only sweep; an <c>.md</c>-only sweep here would repeat that mistake
/// one directory over.</para>
///
/// <h3>What it detects</h3>
/// Five classes, each chosen because it has a <b>closed format vocabulary</b> to allow-list
/// against — the constraint #181 named and #184 restated:
/// <list type="number">
///   <item><b>A hostname that is not drawn from the approved vocabulary.</b> Reserved-for-
///     documentation names, the standards/schema hosts the fixture guard already allows,
///     ProcuLink's own domains, and a closed list of platform hosts the product openly
///     integrates with or whose documentation these files cite.</item>
///   <item><b>A public, routable IPv4 address.</b> This is the sharpest rule in the guard,
///     because "reserved" is decided entirely by RFC: loopback, RFC 1918 private, RFC 6598
///     CGNAT, link-local, RFC 5737 documentation, benchmarking, multicast and reserved space are
///     allowed, and <b>everything else is somebody's real host</b>. No judgement, no stored
///     value.</item>
///   <item><b>A URL authority carrying embedded credentials</b> (<c>scheme://user:pass@host</c>).
///     Credential-in-URL is a leak by construction regardless of which host follows it, and the
///     product's own API refuses it (<c>credentials_in_url_not_allowed</c>).</item>
///   <item><b>An e-mail address on a domain that is not reserved-for-documentation</b> — decided
///     by <see cref="SourcePersonalDataGuardTests.ViolationClasses"/> rather than reimplemented,
///     so <c>docs/</c> and <c>.cs</c> cannot drift on what counts as a real mail domain.</item>
///   <item><b>An absolute path into a local machine</b> — see <see cref="AbsoluteLocalPath"/>.
///     The account segment of such a path routinely encodes a person and their employer, which
///     makes it the one identity shape that sits in a path field where no party-name rule looks.
///     It needs no vocabulary at all, so unlike company-name detection it actually survives.</item>
/// </list>
///
/// <para>Only the first two classes carry an anti-vacuity floor. The other three are expected to
/// reach and stay at <b>zero</b> occurrences in a clean tree, so a floor on them would assert the
/// leak still exists. Hosts and addresses are different: hundreds of legitimate ones remain in
/// this corpus, which is exactly what makes a silent regex breakage invisible without a floor.</para>
///
/// <b>Allow-list, never deny-list</b>, for #179's reason, which bites hardest here: a committed
/// list of real vendor hostnames or real customer names, on a public repository, in order to
/// prove that none are committed, republishes exactly what it suppresses — and would outlive the
/// history rewrite this packet exists to unblock, sitting in the tree handing the names back.
///
/// <h3><b>What it CANNOT detect, and will not pretend to</b></h3>
/// A green build here is <b>not</b> an all-clear for the categories the purge removed by hand:
/// <list type="bullet">
///   <item><b>Company and counterparty names.</b> Arbitrary free text. This is the single largest
///     category the purge removed and it is entirely unguarded, for #184's reason: no format
///     distinguishes a real company from an invented one.</item>
///   <item><b>Production record UUIDs.</b> A captured production id and an invented one are
///     byte-identical in shape. There is no reserved UUID range to allow-list against, so this
///     fails the same test VAT numbers fail.</item>
///   <item><b>PO numbers, order numbers, VAT / registration numbers.</b> A real one has the same
///     shape as an invented one — #184's finding, unchanged.</item>
///   <item><b>A bare two-label vendor domain whose first label is not a service prefix.</b> The
///     host arms below are deliberately prefix- and delimiter-anchored (see
///     <see cref="ServicePrefixedHost"/>). A general "dotted token" pattern is not available in
///     markdown: <c>.md</c>, <c>.sh</c>, <c>.io</c>, <c>.no</c>, <c>.de</c>, <c>.pl</c> and
///     <c>.it</c> are all real TLDs <em>and</em> common file extensions, so such a rule would
///     fire on nearly every filename in the corpus and be suppressed within a week. In this
///     corpus every vendor endpoint appeared either in a <c>scheme://</c> URL, behind a service
///     prefix, or with an explicit <c>:port</c> — all three of which are covered.</item>
///   <item><b>IPv6.</b> No instance exists in this tree; admitting the format would need its own
///     reserved-range table, and an untested one is worse than a stated gap.</item>
/// </list>
///
/// <h3>The one carve-out, and exactly how narrow it is</h3>
/// <see cref="PublishedVendorIngressDirectory"/> exempts <b>the IP class only</b>, in <b>one
/// directory</b>: <c>docs/infra/postmark-inbound-verify-worker/</c>. That directory's whole
/// purpose is to hold the mail vendor's <b>published</b> inbound IP allow-list — a public fact
/// the vendor documents so customers can configure firewalls — and
/// <c>.github/workflows/postmark-ip-drift.yml</c> re-derives it weekly and fails when it drifts.
/// Guarding it would either force the values into this file (storing real addresses in the guard,
/// which is the thing #179 forbids) or break a live security control.
/// <para><b>The hole is one class in one directory and is asserted to stay that way</b> by
/// <see cref="Carveout_IsNarrow_OneDirectory_AndOnlyTheAddressClass"/>: the other four detectors
/// still run over that directory, and the carve-out fails closed — rename the directory and the
/// exemption stops applying rather than silently widening.</para>
///
/// <h3>Failure output withholds the value</h3>
/// Violations name file, line and class — never the host, address or credential. CI logs and
/// <c>.trx</c> artifacts on a public repository are public, so a guard that printed the leak in
/// order to report the leak would not have removed it. Asserted, not assumed, by
/// <see cref="Detector_WithholdsTheOffendingValueFromTheViolationClass"/>.
///
/// <h3>Why this file needs no path exclusion for itself</h3>
/// <para>Note first that its control literals containing an <c>@</c> are SPLIT across a <c>+</c>.
/// That is not cosmetic: this file is <c>.cs</c>, so it is inside
/// <see cref="SourcePersonalDataGuardTests"/>'s corpus, and its fictitious-but-non-reserved
/// control addresses fire that sibling guard. #184 established the split for exactly this case —
/// the compiler folds the halves to the identical constant, so the controls test exactly what
/// they always did while no single source LINE carries a matching address. <b>Do not join them
/// back.</b> These controls passing is itself the proof the split changed no value.</para>
///
/// Its positive controls are deliberately non-reserved hosts and non-reserved addresses, so a
/// naive sweep would fire on them — except that the corpus is <c>docs/</c> and this file is not
/// in it. That is a property of the corpus, not an exclusion, and it is pinned by
/// <see cref="DocsSweep_CorpusIsDocsOnly"/> so nobody "helpfully" widens the sweep to the whole
/// tree and silently turns the controls into violations.
/// </summary>
public class DocsOperationalDataGuardTests
{
    // ── Detectors ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A hostname written with an explicit service prefix, or with an explicit port. Both are
    /// closed, delimiter-like signals that the dotted token is an endpoint rather than a file
    /// name or a namespace — the same move <c>BareWwwHost</c> makes in
    /// <see cref="FixtureDeIdentificationGuardTests"/>, widened from one prefix to the closed set
    /// of service labels that actually front a feed, and joined by the <c>host:port</c> form that
    /// the vendor-probe transcripts used.
    /// </summary>
    private static readonly Regex ServicePrefixedHost = new(
        @"(?<![A-Za-z0-9.@/\-])(?:ftp|ftps|sftp|www|api|mail|smtp|imap|pop3?|s3|cdn|portal|edi|shop|web|extranet|files?|feed|inbound|assets|dashboard|clerk|ingest)\.[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A dotted host followed by an explicit port. A version string is not written with
    /// a port, and a file name is not either.</summary>
    private static readonly Regex HostWithPort = new(
        @"(?<![A-Za-z0-9.@/\-])([A-Za-z][A-Za-z0-9\-]*(?:\.[A-Za-z0-9\-]+)*)\.([A-Za-z]{2,}):[0-9]{1,5}\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Dotted-quad, anchored so it cannot be a fragment of a longer dotted run (a five-part
    /// version string, a truncated hash). Octet range is validated in <see cref="AddressesIn"/>.
    /// </summary>
    private static readonly Regex DottedQuad = new(
        @"(?<![0-9A-Za-z.\-])([0-9]{1,3})\.([0-9]{1,3})\.([0-9]{1,3})\.([0-9]{1,3})(?![0-9A-Za-z.\-])",
        RegexOptions.Compiled);

    /// <summary>A URL authority whose userinfo carries a password (<c>user:pass@host</c>).</summary>
    private static readonly Regex CredentialInUrl = new(
        @"\b[A-Za-z][A-Za-z0-9+.\-]*://([^/\s""'<>\\)\]}]*:[^/\s""'<>\\)\]}@]*@)",
        RegexOptions.Compiled);

    /// <summary>
    /// An absolute path into somebody's local machine: a Windows drive-letter root, or a Unix
    /// per-user home root.
    ///
    /// <para>This is the one shape a name-based scrub structurally misses, and it is the reason
    /// it needed its own rule rather than a line-by-line fix. The account segment of
    /// <c>C:\Users\&lt;account&gt;\…</c> routinely encodes a person's name and, on a
    /// domain-joined machine, their employer — so the identifier sits in a <em>path</em> field
    /// while every party-name allow-list is looking at <em>party</em> fields, and every path
    /// scrubber is looking for paths without caring who is named in them. Neither half sees it.
    /// A worktree header carrying one opens many of the documents in this corpus.</para>
    ///
    /// <para>It is detectable with <b>no vocabulary at all</b> — a drive letter followed by a
    /// backslash is a format fact — which is precisely why this rule survives where company-name
    /// detection cannot. <b>It catches paths, not names.</b> A green build here says nothing
    /// whatever about whether a person or a company is named elsewhere in the corpus; see the
    /// "cannot detect" list above.</para>
    ///
    /// <para><b>Scoped to the user-profile root, not to every absolute path, and that is a
    /// deliberate precision choice.</b> The identity lives in the account segment:
    /// <c>C:\Users\&lt;account&gt;</c>, <c>/home/&lt;account&gt;</c>, <c>/Users/&lt;account&gt;</c>.
    /// A path like <c>C:\tmp\out.mp4</c> or <c>C:\Program Files\…</c> names nobody, and a rule
    /// broad enough to flag those would fire on ordinary shell examples throughout this corpus —
    /// which is how a guard gets suppressed rather than fixed. Residual gap, stated rather than
    /// hidden: a non-home absolute path still discloses a little machine layout and is not
    /// flagged.</para>
    ///
    /// <para>Anchored at a profile root deliberately for a second reason: a bare
    /// backslash-separated fragment would fire on Windows registry keys
    /// (<c>HKEY_LOCAL_MACHINE\SYSTEM\…</c>), which this corpus legitimately documents. The
    /// separator is allowed to repeat (<c>[\\/]{1,2}</c>) so the escaped form that appears inside
    /// JSON and code samples is caught too — that doubling is exactly the sort of variant a
    /// hand-written scrub misses.</para>
    ///
    /// <para><b><c>/home/…</c> is deliberately NOT matched, and this is the honest cost.</b> In
    /// this corpus a Linux home path is almost always a throwaway container account —
    /// <c>/home/plkuser</c> in the SFTP delivery proof, <c>/home/testuser</c> in the routing
    /// proof — which name nobody and are documented as disposable. Matching them would make the
    /// rule fire on clean lines in operational proofs, and a rule that does that gets suppressed
    /// long before it catches anything. The consequence, stated rather than hidden: a real
    /// operator's Linux home directory would not be flagged. <c>/Users/…</c> IS matched — it is a
    /// macOS profile root, and containers do not use it.</para>
    /// </summary>
    private static readonly Regex AbsoluteLocalPath = new(
        @"(?<![A-Za-z0-9])(?:[A-Za-z]:[\\/]{1,2}Users[\\/]{1,2}[A-Za-z0-9._$~\-]+|/Users/[A-Za-z0-9._\-]+)",
        RegexOptions.Compiled);

    /// <summary>
    /// The only directory exempted from the address class, and only from the address class.
    /// Read the "one carve-out" section of the class doc before touching this.
    /// </summary>
    internal const string PublishedVendorIngressDirectory = "docs/infra/postmark-inbound-verify-worker/";

    /// <summary>
    /// The closed set of top-level domains the two <b>bare-host</b> arms will accept. It exists
    /// because of a collision that is specific to a documentation corpus and that broke the first
    /// version of this guard: <b>file extensions and source-code member names occupy the same
    /// grammatical position as a TLD.</b>
    ///
    /// <para>Without this, <c>SftpIngressService.cs:254</c> parses as host
    /// <c>SftpIngressService</c>, TLD <c>cs</c>, port <c>254</c> — and this corpus is built out of
    /// <c>file.cs:line</c> citations, so the guard fired on hundreds of clean lines. Likewise
    /// <c>data.Lines.Count</c> parses as a three-label host. A guard with that false-positive rate
    /// does not get fixed, it gets deleted.</para>
    ///
    /// <para><b>This is a vocabulary of formats, not of parties</b>, which is why it does not
    /// violate the allow-list-never-deny-list rule: it stores no counterparty, no endpoint and no
    /// identity, and a vendor host on any TLD in this list is still refused unless it is
    /// separately approved. <c>.md</c>, <c>.sh</c> and <c>.py</c> are real ccTLDs and are
    /// deliberately OMITTED — in this corpus they are overwhelmingly <c>README.md:12</c>-shaped
    /// citations, and a vendor endpoint on one of them is a trade this guard makes knowingly.</para>
    ///
    /// <para>Note the <c>scheme://</c> arm does NOT consult this list — a URL is unambiguous
    /// without it, so <c>https://anything.internalext/</c> is still checked.</para>
    /// </summary>
    private static readonly HashSet<string> BareHostTlds = new(StringComparer.Ordinal)
    {
        // Generic.
        "com", "net", "org", "info", "biz", "io", "dev", "app", "co", "cloud", "tech",
        "online", "site", "shop", "store", "xyz", "ai", "me", "tv", "eu",
        // European ccTLDs — where this product's counterparties actually live.
        "de", "at", "ch", "nl", "be", "fr", "es", "pt", "it", "dk", "se", "no", "fi",
        "ee", "lv", "lt", "cz", "sk", "hu", "ro", "bg", "gr", "ie", "uk", "si", "hr",
        "rs", "tr", "lu", "ua",
    };

    /// <summary>
    /// Platform hosts. Every entry is a service ProcuLink itself runs on, integrates with, or
    /// whose public documentation these files cite — a FORMAT or a supplier of infrastructure,
    /// never a party whose purchase orders flow through the product. That is #179's
    /// platform-vs-counterparty rule; the question to ask before adding one is "would this name
    /// appear on an invoice we send or receive?" If yes, it does not belong here.
    ///
    /// <para>Deliberately NOT here: any catalog, feed, shop or extranet host. #179's ruling was
    /// that "a standards host is a format we speak; a catalog, CDN or shop host is a party we
    /// traded with", and every hostname this packet removed was of the second kind.</para>
    /// </summary>
    private static readonly HashSet<string> PlatformHosts = new(StringComparer.Ordinal)
    {
        // Source hosting and CI.
        "github.com", "www.github.com", "raw.githubusercontent.com", "design-tokens.github.io",

        // Hosting / runtime the product is deployed on.
        "vercel.com", "railway.app", "up.railway.app", "neon.tech", "cloudflare.com",
        "dash.cloudflare.com", "developers.cloudflare.com",

        // Third-party services the product calls, and their documentation.
        "api.openai.com", "platform.openai.com", "openai.com",
        "stripe.com", "api.stripe.com", "dashboard.stripe.com", "docs.stripe.com",
        "postmarkapp.com", "api.postmarkapp.com", "inbound.postmarkapp.com",
        "clerk.com", "clerk.accounts.dev", "sentry.io", "posthog.com", "eu.posthog.com",

        // Font and asset CDNs referenced as design dependencies.
        "fonts.googleapis.com", "fonts.gstatic.com", "fonts.bunny.net",

        // Disposable test endpoints and consumer mail providers named in QA steps and in
        // user-facing help copy ("your provider's IMAP server — …"). These are tools and generic
        // providers, not parties whose orders flow through the product. Note that allowing the
        // HOST says nothing about addresses: a personal mailbox at one of these domains is still
        // caught by the e-mail class, which is decided separately.
        "webhook.site", "sftpcloud.io", "ethereal.email", "smtp.ethereal.email",
        "imap.ethereal.email", "mailinator.com",
        "gmail.com", "imap.gmail.com", "smtp.gmail.com",
        "outlook.com", "imap-mail.outlook.com", "smtp-mail.outlook.com",
    };

    /// <summary>
    /// Approved by full ADDRESS, not by domain — a deliberately tighter allow-list than the
    /// domain-level one the <c>.cs</c> guard uses.
    ///
    /// <para>The only entry is the coding-agent commit trailer, which plan documents quote inside
    /// worked commit-message examples. Allowing the <em>domain</em> would also allow a personal
    /// mailbox at it; allowing the exact no-reply address allows the template and nothing
    /// else.</para>
    /// </summary>
    /// <remarks>
    /// Split across a <c>+</c> deliberately, and do NOT join it back. The compiler folds it to the
    /// identical constant, while no single SOURCE LINE of this file contains a matching address —
    /// which is what keeps <see cref="SourcePersonalDataGuardTests"/> from firing on this guard's
    /// own allow-list. #184 established the convention for exactly this situation; joining the
    /// literal turns that sibling guard red.
    /// </remarks>
    private static readonly string[] ApprovedMailAddresses =
    {
        "noreply@" + "anthropic.com",
    };

    private static bool IsApprovedHost(string host)
    {
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();

        if (h.Length == 0)
        {
            return true;
        }

        // Not hostname-shaped at all. A `file://${process.argv[1].replace(...)}` in a worker
        // script parses as a scheme and an "authority"; judging that against a hostname
        // vocabulary is a category error, and reporting it teaches the reader to ignore this
        // guard. Anything outside the DNS character set is simply not a host.
        if (h.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-')))
        {
            return true;
        }

        // An IP literal written where a host goes is an ADDRESS, and the address class already
        // decides it by RFC. Judging it here as well would report every documented SSRF probe
        // against link-local and RFC 1918 space as a hostname violation — the same value getting
        // two verdicts from two rules, one of them wrong.
        if (AddressesIn(h).Count == 1 && AddressesIn(h)[0] == h)
        {
            return true;
        }

        if (FixtureDeIdentificationGuardTests.IsPlaceholderDomain(h)
            || FixtureDeIdentificationGuardTests.IsStandardsHost(h))
        {
            return true;
        }

        // ProcuLink's own domains, including subdomains. Naming yourself is a disclosure choice
        // about yourself, not a counterparty leak — the same carve-out both sibling guards make.
        foreach (var own in new[] { "proculink.eu", "proculink.test", "proculink.local" })
        {
            if (h == own || h.EndsWith("." + own, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // .internal is ICANN-reserved for private use and can never resolve publicly.
        if (h == "internal" || h.EndsWith(".internal", StringComparison.Ordinal))
        {
            return true;
        }

        if (PlatformHosts.Contains(h))
        {
            return true;
        }

        // A subdomain of an approved platform host is still that platform.
        return PlatformHosts.Any(p => h.EndsWith("." + p, StringComparison.Ordinal));
    }

    /// <summary>
    /// Strips userinfo and port, leaving the bare host.
    ///
    /// <para>Also strips trailing markdown punctuation, which the fixture guard's version does not
    /// need to. That guard's corpus is XML and JSON fixtures; this one is overwhelmingly markdown,
    /// where a URL is nearly always wrapped — <c>`https://api.example.com`</c>,
    /// <c>(https://…)</c>, <c>"https://…",</c>. Without this, an approved host arrives at the
    /// allow-list carrying a backtick, misses every entry, and the guard fires on hundreds of
    /// clean lines — a false-positive flood being the fastest route to a guard getting deleted.
    /// </para>
    /// </summary>
    private static string HostOf(string authority)
    {
        var host = authority;

        var at = host.LastIndexOf('@');
        if (at >= 0)
        {
            host = host[(at + 1)..];
        }

        var colon = host.LastIndexOf(':');
        if (colon >= 0 && host[(colon + 1)..].All(char.IsAsciiDigit))
        {
            host = host[..colon];
        }

        return host.Trim().Trim('`', '"', '\'', '*', '_', ',', ';', ')', ']', '}', '>', '.')
            .ToLowerInvariant();
    }

    /// <summary>Whether a bare (schemeless) dotted token ends in a TLD this guard will treat as a
    /// hostname at all. See <see cref="BareHostTlds"/> for why a documentation corpus needs
    /// this and a fixture corpus does not.</summary>
    private static bool HasBareHostTld(string host)
    {
        var lastDot = host.LastIndexOf('.');
        return lastDot >= 0 && BareHostTlds.Contains(host[(lastDot + 1)..]);
    }

    /// <summary>
    /// Every host named on one line. Shared by the detector and by the anti-vacuity floor, so the
    /// floor counts exactly what the detector examined — a regex broken into matching nothing
    /// drives the count to zero and fails loudly instead of turning the sweep green.
    /// </summary>
    internal static IReadOnlyList<string> HostsIn(string line)
    {
        var hosts = new List<string>();

        // scheme://authority and www.* — reused rather than reimplemented, but re-normalised
        // through this guard's HostOf: the fixture guard trims only a trailing dot, which is
        // right for its XML/JSON corpus and wrong for markdown, where the same host arrives
        // wrapped in a backtick or a closing parenthesis.
        hosts.AddRange(FixtureDeIdentificationGuardTests.HostsIn(line).Select(HostOf));

        foreach (Match m in ServicePrefixedHost.Matches(line))
        {
            var host = HostOf(m.Value);
            if (HasBareHostTld(host))
            {
                hosts.Add(host);
            }
        }

        foreach (Match m in HostWithPort.Matches(line))
        {
            var host = HostOf(m.Groups[1].Value + "." + m.Groups[2].Value);
            if (HasBareHostTld(host))
            {
                hosts.Add(host);
            }
        }

        return hosts;
    }

    /// <summary>
    /// Every dotted-quad on one line whose four octets are all in range. Shared with the
    /// anti-vacuity floor for the same reason as <see cref="HostsIn"/>.
    /// </summary>
    internal static IReadOnlyList<string> AddressesIn(string line)
    {
        var found = new List<string>();

        foreach (Match m in DottedQuad.Matches(line))
        {
            var octets = new int[4];
            var ok = true;

            for (var i = 0; i < 4; i++)
            {
                if (!int.TryParse(m.Groups[i + 1].Value, out octets[i]) || octets[i] > 255)
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                found.Add(m.Value);
            }
        }

        return found;
    }

    /// <summary>
    /// Reserved by RFC, therefore never a real counterparty's host. This is a pure format fact —
    /// the same class of decision as <see cref="FixtureDeIdentificationGuardTests.IsPlaceholderDomain"/>
    /// and the "never zero-padded" ANID rule from #181 — and it is why the address class needs no
    /// list of real addresses in order to detect real addresses.
    /// </summary>
    internal static bool IsReservedAddress(string address)
    {
        var parts = address.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        var o = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (!int.TryParse(parts[i], out o[i]) || o[i] > 255)
            {
                return false;
            }
        }

        return o[0] switch
        {
            0 => true,                                          // "this network"
            10 => true,                                         // RFC 1918
            127 => true,                                        // loopback
            100 => o[1] >= 64 && o[1] <= 127,                   // RFC 6598 CGNAT
            169 => o[1] == 254,                                 // link-local (incl. metadata)
            172 => o[1] >= 16 && o[1] <= 31,                    // RFC 1918
            192 => (o[1] == 168)                                // RFC 1918
                   || (o[1] == 0 && o[2] == 0)                  // IETF protocol assignments
                   || (o[1] == 0 && o[2] == 2),                 // RFC 5737 TEST-NET-1
            198 => (o[1] == 18 || o[1] == 19)                   // RFC 2544 benchmarking
                   || (o[1] == 51 && o[2] == 100),              // RFC 5737 TEST-NET-2
            203 => o[1] == 0 && o[2] == 113,                    // RFC 5737 TEST-NET-3
            >= 224 => true,                                     // multicast + reserved + broadcast
            _ => false,
        };
    }

    internal static IReadOnlyList<string> ViolationClasses(string line, string relativePath = "")
    {
        var classes = new List<string>();

        if (CredentialInUrl.IsMatch(line))
        {
            classes.Add("URL authority carrying embedded credentials");
        }

        if (AbsoluteLocalPath.IsMatch(line))
        {
            classes.Add("absolute path into a local machine");
        }

        if (HostsIn(line).Any(h => !IsApprovedHost(h)))
        {
            classes.Add("hostname that is not drawn from the approved vocabulary");
        }

        var addressesExempt = relativePath.StartsWith(
            PublishedVendorIngressDirectory, StringComparison.Ordinal);

        if (!addressesExempt && AddressesIn(line).Any(a => !IsReservedAddress(a)))
        {
            classes.Add("public routable IP address");
        }

        // Shared with the .cs guard rather than reimplemented, so "what counts as a real mail
        // domain" cannot drift between the two corpora. The approved addresses are removed from
        // the line first rather than second-guessing the shared verdict afterwards: that keeps
        // the shared rule authoritative for every address it still sees.
        var forMail = line;
        foreach (var approved in ApprovedMailAddresses)
        {
            forMail = forMail.Replace(approved, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        classes.AddRange(SourcePersonalDataGuardTests.ViolationClasses(forMail));

        return classes;
    }

    // ── The guard ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TrackedDocs_CarryNoLiveOperationalData()
    {
        var scan = ScanTrackedDocs();

        scan.Violations.Should().BeEmpty(
            "a file under docs/ must not name a live endpoint, address, credential or mail "
            + "domain. State the CLASS, never the party or the endpoint — a QA transcript that "
            + "records which vendor host answered on which port, with which banner, is a reusable "
            + "target list, and this repository is public. Replace with a reserved-for-"
            + "documentation placeholder, or delete the transcript. Offending file:line and class "
            + "(values withheld: this repository and its CI logs are public):\n"
            + string.Join("\n", scan.Violations));
    }

    /// <summary>
    /// An empty sweep must fail, not pass. A file count is not a detection floor (#181's
    /// <c>ScannedHosts</c>, #184's <c>ScannedEmails</c>, #185's <c>ScannedIdentities</c>): the
    /// corpus can be enumerated correctly, opened correctly, and still examined by nothing. Five
    /// floors, each catching a different way this sweep goes hollow — the corpus query breaking,
    /// the corpus collapsing to one directory, the corpus narrowing to markdown only, the files
    /// never being opened, and the detectors themselves being broken into matching nothing.
    /// </summary>
    [Fact]
    public void DocsSweep_ActuallyReadsTheDocsCorpus()
    {
        var scan = ScanTrackedDocs();

        scan.ScannedFiles.Should().BeGreaterThanOrEqualTo(
            90,
            $"only {scan.ScannedFiles} tracked files under docs/ were scanned in {scan.RepoRoot} "
            + "— the corpus query is broken, so the guard above proves nothing");

        scan.DistinctDirectories.Should().BeGreaterThanOrEqualTo(
            10,
            $"only {scan.DistinctDirectories} distinct directories under docs/ were swept — the "
            + "corpus has narrowed, and the leak this guard exists for spanned qa/, prompts/, "
            + "superpowers/, ops/, infra/, proposals/ and design-system/");

        scan.DistinctExtensions.Should().BeGreaterThanOrEqualTo(
            6,
            $"only {scan.DistinctExtensions} distinct file extensions were swept — the corpus has "
            + "collapsed towards markdown. docs/ also holds a deployed worker (.js/.mjs), SQL, "
            + "TypeScript, CSS, JSON, HTML and SVG, and #185's leak lived in a .mjs outside the "
            + "test tree precisely because a single-extension corpus could not see it");

        scan.ScannedBytes.Should().BeGreaterThan(
            500_000,
            $"only {scan.ScannedBytes} bytes were read — the files were listed but not opened, so "
            + "the guard above proves nothing");

        (scan.ScannedHosts + scan.ScannedAddresses).Should().BeGreaterThanOrEqualTo(
            120,
            $"only {scan.ScannedHosts} host-shaped and {scan.ScannedAddresses} address-shaped "
            + "tokens were extracted and checked across the corpus — the detectors matched "
            + "(almost) nothing, so the guard above proves nothing. This is the floor that catches "
            + "a sweep which reads every byte and examines none of them");
    }

    /// <summary>
    /// The corpus is docs/ and nothing else. Pinned because this file's own positive controls are
    /// deliberately non-reserved hosts and addresses: widening the sweep to the whole tree would
    /// turn the controls into violations, and the natural "fix" for that is a path exclusion —
    /// which is the hole #184 refused to open. Fail here instead, loudly, with the reason.
    /// </summary>
    [Fact]
    public void DocsSweep_CorpusIsDocsOnly()
    {
        var repoRoot = FixtureCorpus.FindRepoRoot();

        TrackedDocsFiles(repoRoot).Should().OnlyContain(
            p => p.StartsWith("docs/", StringComparison.Ordinal),
            "this guard's corpus is docs/. Do not widen it to the whole tree: the positive "
            + "controls below are deliberately non-reserved hosts and addresses, and a wider "
            + "sweep would flag them, inviting a path exclusion that a future leak could sit in");
    }

    /// <summary>
    /// The carve-out is one class in one directory. Widening it — to another directory, or to
    /// another detector class — fails here rather than silently blinding the sweep.
    /// </summary>
    [Fact]
    public void Carveout_IsNarrow_OneDirectory_AndOnlyTheAddressClass()
    {
        const string exempt = PublishedVendorIngressDirectory + "check-postmark-ips.test.mjs";
        const string elsewhere = "docs/qa/some-transcript.md";

        // The address class is exempt in that directory...
        ViolationClasses("<li>198.18.7.9</li> and <li>91.198.174.192</li>", exempt)
            .Should().BeEmpty("the vendor's published ingress list is a public fact, and a CI "
                              + "drift job re-derives it weekly");

        // ...and only there.
        ViolationClasses("<li>91.198.174.192</li>", elsewhere)
            .Should().ContainSingle().Which.Should().Be("public routable IP address");

        // The other three classes still apply inside the carve-out directory.
        ViolationClasses("pull from sftp.invented-distributor.de", exempt)
            .Should().Contain("hostname that is not drawn from the approved vocabulary");
        ViolationClasses("curl https://someone:hunter2@invented-portal.example/x", exempt)
            .Should().Contain("URL authority carrying embedded credentials");
        ViolationClasses("mail ops@" + "invented-distributor.de for access", exempt)
            .Should().Contain("e-mail address on a domain that is not reserved-for-documentation");
    }

    /// <summary>
    /// Positive control. Every host, address and address-holder below is fictitious or belongs to
    /// documentation space, but each is shaped exactly like the leaks this packet removed: a
    /// vendor feed endpoint, a resolved probe result, an account-keyed catalog URL, a credential
    /// in a URL. None of them is a real party's value.
    /// </summary>
    [Theory]
    // A vendor feed host behind a service prefix — the exact shape of the removed probe table.
    [InlineData("| Vendor | ftp `ftp.invented-distributor.de` | AUTH OK |")]
    // A vendor host with no service prefix — caught by the explicit port, which is the other
    // format-anchored arm. Without the port this line is NOT detectable; see the "cannot detect"
    // list in the class doc.
    [InlineData("SFTP mercury.invented-wholesale.de:22 / PRICE.ZIP")]
    // A scheme:// URL on a non-approved host.
    [InlineData("pull from https://example.invalid/api")]
    // An account-keyed catalog URL — the customer id is in the path, and the host gives it away.
    [InlineData("https://www.invented-reseller.com/customer_pricelist/204109_79263.xml")]
    // host:port, the form the probes used.
    [InlineData("probe: invented-shop.cz:21 OPEN")]
    // A public routable address, with a banner beside it.
    [InlineData("`91.198.174.192` **OPEN**, `220 SC-WEBSHOP3 FTP SERVICE`")]
    [InlineData("the outbound POST egressed from 8.8.4.4 (US)")]
    // Credentials in a URL.
    [InlineData("configured as https://feeduser:s3cr3t@" + "invented-distributor.de/price.csv")]
    // A mail domain that is not reserved — shares the .cs guard's decision.
    [InlineData("escalated to ops@" + "invented-distributor.de on Tuesday")]
    // Absolute local paths: the worktree header that opens many documents in this corpus, a
    // Windows user profile, and a Unix home. The account segment is the identity.
    [InlineData(@"**Worktree:** C:\Users\some.account\source\repos\Thing\.claude\worktrees\x")]
    [InlineData(@"key lives at C:\Users\an.operator\.secrets\provider.key")]
    [InlineData("captures staged under /Users/anoperator/Videos/drafts")]
    // The escaped form, as it appears inside JSON and code samples.
    [InlineData(@"{""out"":""C:\\Users\\some.account\\Videos\\draft.mp4""}")]
    public void Detector_FiresOnKnownBadInput(string line)
        => ViolationClasses(line).Should().NotBeEmpty();

    /// <summary>
    /// Negative control. A detector that flags everything is as useless as one that flags nothing:
    /// it gets suppressed within a week. This list is mostly drawn from lines that really occur in
    /// this corpus, so a future narrowing of the allow-list fails here rather than silently
    /// re-flagging hundreds of clean lines.
    /// </summary>
    [Theory]
    // Reserved-for-documentation hosts and the placeholders the purge rewrote TO.
    [InlineData("Host `sftp.supplier.example`, Port `22`, Path `/exports/orders`")]
    [InlineData("posts to https://erp.supplier.example/xmlcore")]
    [InlineData("Alerting__Email__To=you@example.com")]
    // Own domains, including the product's real inbound addressing scheme.
    [InlineData("`api.proculink.eu` is NOT proxied through Cloudflare")]
    [InlineData("`{slug}@orders.proculink.eu` MX -> the mail vendor")]
    // Standards hosts, shared with the fixture guard.
    [InlineData("xmlns=\"http://www.w3.org/2001/XMLSchema\"")]
    [InlineData("see https://docs.oasis-open.org/ubl/os-UBL-2.1/")]
    // Platform hosts and their documentation.
    [InlineData("https://github.com/dimnovare/ProcuLink/pull/58")]
    [InlineData("no override -> SDK default `https://api.openai.com`")]
    [InlineData("@import url('https://fonts.googleapis.com/css2?family=Inter');")]
    // Reserved address space: loopback, RFC 1918, link-local metadata, documentation range.
    [InlineData("local PG on 127.0.0.1:5435 and the API on 127.0.0.1:5223")]
    [InlineData("blocked egress to 169.254.169.254 (cloud metadata)")]
    [InlineData("SSRF guard rejects 10.0.0.5 and 172.16.4.1")]
    [InlineData("the worker saw x-forwarded-for 203.0.113.9")]
    // Not hosts or addresses at all. A detector firing on these would be silenced immediately.
    [InlineData("see `ProcuLink.Core/Services/Mapping/OrderMappingOverride.cs:65`")]
    [InlineData("run `node docs/infra/postmark-inbound-verify-worker/check-postmark-ips.mjs`")]
    [InlineData("bumped posthog-js to 1.376.3 and shipped it")]
    // file.cs:line citations, which this corpus is built out of. `.cs` sits exactly where a TLD
    // sits, and `:254` exactly where a port sits — the collision that broke the first version of
    // this guard. See BareHostTlds.
    [InlineData("SFTP `SftpIngressService.cs:254`, S3 `S3IngressService.cs:303`")]
    [InlineData("`SshHostKeyPolicy.cs:97` learns on first use, `:101` records it")]
    [InlineData("see README.md:12 and docs/ops/runbook.md:88")]
    [InlineData("`SftpDeliveryDispatcher.cs:418-423` builds the verifier")]
    // A C# member chain has the same shape as a three-label host.
    [InlineData("iterate data.Lines.Count and files.Add(entry)")]
    // An IP literal written where a host goes is decided by the ADDRESS class, not this one.
    [InlineData("set the delivery URL to http://169.254.169.254/latest/meta-data/")]
    [InlineData("or http://10.0.0.5/ or http://127.0.0.1/ to prove the SSRF guard refuses it")]
    // A file:// URL built from a template literal is not a host at all.
    [InlineData(@"import.meta.url === new URL(`file://${process.argv[1].replace(/x/g, ""/"")}`)")]
    // The coding-agent commit trailer, quoted inside worked commit-message examples.
    // Literal split for the reason given on ApprovedMailAddresses — do not join it back.
    [InlineData("Co-Authored-By: Claude Opus 4.8 <noreply@" + "anthropic.com>")]
    // A disposable request bin with the token elided, and provider help copy.
    [InlineData("| Endpoint BEFORE | `https://webhook.site/<token-A>` | A disposable bin |")]
    [InlineData("Your provider's IMAP server — imap.gmail.com (Gmail), imap-mail.outlook.com")]
    [InlineData("Assert.Equal(7.01m, line.UnitPrice);")]
    [InlineData("| Operations | 500 orders | 10 suppliers |")]
    // A URL with a port but no credentials, on an approved host.
    [InlineData("healthcheck https://api.proculink.eu:443/health/ready")]
    // Repo-relative paths — what the absolute paths were rewritten TO.
    [InlineData(@"**Worktree:** .claude/worktrees/park-ui")]
    [InlineData("see docs/ops/catalog-source-api-notes.md for the recipe")]
    [InlineData(@"scripts\demo-video\capture.ts writes into .qa-screenshots\")]
    // A Windows registry key is backslash-separated but is not a path into anyone's machine.
    [InlineData(@"set HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters")]
    // An absolute path that is not a profile root names nobody — see AbsoluteLocalPath's doc.
    [InlineData(@"ffmpeg -i C:\tmp\before.mp4 -c copy out.mp4")]
    [InlineData(@"installed under C:\Program Files\dotnet\sdk")]
    // Throwaway container home directories, documented as disposable. Matching these is how the
    // path rule would get suppressed; see AbsoluteLocalPath's doc for the cost of excluding them.
    [InlineData("atmoz/sftp plkuser:plkpass:::upload -> /home/plkuser/upload")]
    [InlineData("landed at /home/testuser/incoming/order.csv inside the container")]
    // Auto-generated worktree/branch codenames are adjective + surname + hash. They LOOK like a
    // person and are not one. Pinned so that nobody "improves" this guard with a name-shaped
    // rule: the first false positive of that kind is how a guard gets deleted rather than fixed.
    [InlineData("branch `claude/wizardly-spence-6b738a` was merged and pruned")]
    [InlineData("worktree `beautiful-torvalds-04a9a5` is detached at 009dc67")]
    [InlineData("Railway service `aware-amazement`, project `lucid-generosity`")]
    public void Detector_StaysSilentOnCleanInput(string line)
        => ViolationClasses(line).Should().BeEmpty();

    /// <summary>
    /// The withholding is load-bearing. A violation string that echoed the host would publish a
    /// vendor's endpoint into a public CI log and a public <c>.trx</c> artifact, which is the
    /// precise thing this guard exists to prevent — and unlike a name, an endpoint is directly
    /// actionable by whoever reads it. Guarded, not assumed, exactly as in the three sibling
    /// guards.
    /// </summary>
    [Theory]
    [InlineData("| ftp `ftp.invented-distributor.de` | AUTH OK |", "invented-distributor")]
    [InlineData("probe: invented-shop.cz:21 OPEN", "invented-shop")]
    [InlineData("`91.198.174.192` **OPEN**", "91.198.174.192")]
    [InlineData("https://feeduser:s3cr3t@" + "invented-distributor.de/x", "s3cr3t")]
    [InlineData("escalated to ops@" + "invented-distributor.de", "ops@" + "invented-distributor.de")]
    [InlineData(@"**Worktree:** C:\Users\some.account\source\repos\Thing", "some.account")]
    [InlineData("captures staged under /Users/anoperator/Videos", "anoperator")]
    public void Detector_WithholdsTheOffendingValueFromTheViolationClass(string line, string secret)
    {
        var classes = ViolationClasses(line);

        classes.Should().NotBeEmpty("the line is a leak and must be reported at all");
        classes.Should().OnlyContain(
            c => !c.Contains(secret, StringComparison.OrdinalIgnoreCase),
            "a violation class must name the KIND of leak, never the value — this repository and "
            + "its CI logs are public, and the file:line already makes it findable locally");
    }

    // ── Corpus ───────────────────────────────────────────────────────────────────────────

    private sealed record DocsScan(
        string RepoRoot,
        int ScannedFiles,
        long ScannedBytes,
        int ScannedHosts,
        int ScannedAddresses,
        int DistinctDirectories,
        int DistinctExtensions,
        IReadOnlyList<string> Violations);

    /// <summary>
    /// Every tracked file under <c>docs/</c>, from git rather than the filesystem — the same
    /// reasoning <see cref="FixtureCorpus"/> gives: untracked working files are deliberately not
    /// committed (real customer documents are kept untracked precisely so they are not), and a
    /// filesystem walk would flag them while missing nothing real.
    /// </summary>
    private static IReadOnlyList<string> TrackedDocsFiles(string repoRoot)
        => FixtureCorpus.TrackedFiles(repoRoot, "docs");

    private static DocsScan ScanTrackedDocs()
    {
        var repoRoot = FixtureCorpus.FindRepoRoot();
        var violations = new List<string>();
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scannedFiles = 0;
        var scannedHosts = 0;
        var scannedAddresses = 0;
        long scannedBytes = 0;

        foreach (var relative in TrackedDocsFiles(repoRoot))
        {
            var absolute = Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolute))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(absolute);
            scannedFiles++;
            scannedBytes += bytes.LongLength;

            var lastSlash = relative.LastIndexOf('/');
            directories.Add(lastSlash < 0 ? string.Empty : relative[..lastSlash]);
            extensions.Add(Path.GetExtension(relative));

            var lines = Encoding.UTF8.GetString(bytes).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                scannedHosts += HostsIn(lines[i]).Count;
                scannedAddresses += AddressesIn(lines[i]).Count;

                foreach (var violationClass in ViolationClasses(lines[i], relative))
                {
                    violations.Add($"  {relative}:{i + 1}: {violationClass}");
                }
            }
        }

        return new DocsScan(
            repoRoot,
            scannedFiles,
            scannedBytes,
            scannedHosts,
            scannedAddresses,
            directories.Count,
            extensions.Count,
            violations);
    }
}
