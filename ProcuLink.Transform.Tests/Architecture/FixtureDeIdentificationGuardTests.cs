using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace ProcuLink.Transform.Tests.Architecture;

/// <summary>
/// Keeps the de-identification claim in <c>ProcuLink.Transform.Tests.csproj</c> honest.
///
/// That csproj called the real-vendor fixtures "trimmed + de-identified" and nothing checked it,
/// so two fixtures shipped with third-party identifiers still in them: a contributor's local
/// filesystem path (naming a customer organisation and its environment) and a set of
/// personal email addresses on a live corporate domain. Every other real fixture in the tree
/// had been scrubbed to <c>example.*</c>-class placeholders by hand — which is exactly the
/// failure mode a hand-applied convention has: it works until someone forgets once.
///
/// This guard sweeps every *tracked* fixture (git, not the filesystem — real customer documents
/// are deliberately kept untracked for this reason, and a filesystem walk would flag them) for
/// five classes that only ever appear by accident:
///
///   1. an email address on a domain that is not reserved-for-documentation,
///   2. an absolute local filesystem path (drive-letter, POSIX home, or UNC),
///   3. a <c>file://</c> URI,
///   4. a URL or bare hostname on a host that is neither reserved-for-documentation nor a
///      standards host,
///   5. an Ariba Network ID outside the reserved all-zeros placeholder range.
///
/// <para><b>Classes 4 and 5 are the rotation the first three could not see.</b> #179 renamed ten
/// fixtures and de-identified two, and reported — without fixing — that
/// <c>Catalog/Fixtures/cxml-index-bare.xml</c> and <c>cif-3.0.cif</c> still carried live product
/// and CDN URLs on a real catalog domain, a real customer id, and a real Ariba Network ID. The
/// content guard passed all of it, because a URL is neither an e-mail, a <c>file://</c> URI, nor
/// a local path. The same shape had already been scrubbed correctly in the sibling
/// <c>cxml-index-soap.xml</c> — so this was not an unknown convention, it was a convention applied
/// to one file and forgotten on its neighbours, which is precisely the failure mode a
/// hand-applied convention has.</para>
///
/// <para><b>Why the host rule is an allow-list.</b> The same reasoning as
/// <see cref="FixtureNamingGuardTests"/>: a deny-list of real domains, committed to a public
/// repository in order to prove no real domains are committed, republishes exactly what it
/// suppresses — and it would outlive any later scrub of the history, sitting in the current tree
/// handing the names back. So the rule is inverted. A fixture may reference a host only if it is
/// reserved-for-documentation (RFC 2606 / RFC 6761) or appears in <see cref="StandardsHosts"/>.
/// The distinction is the same one the naming guard draws: <b>a standards host is a format we
/// speak; a catalog, CDN or shop host is a party we traded with</b>. Nothing is disclosed by an
/// XML namespace or a DTD system id; a great deal is disclosed by whose product catalog a fixture
/// was captured from. A counterparty nobody has heard of is caught exactly as reliably as a
/// famous one, which a deny-list cannot do.</para>
///
/// <para><b>Why the Ariba Network ID rule is a format rule.</b> An ANID is a fixed shape
/// (<c>AN</c> + digits), and a really-allocated one is never zero-padded. Requiring the reserved
/// all-zeros form states what is permitted rather than enumerating what is forbidden, so again no
/// real identifier is stored to detect real identifiers.</para>
///
/// Failures name the file, the line and the class — never the value. This repository is public
/// and CI logs are public with it; a guard that prints the leak to prove the leak has not
/// removed it. Open the named file locally to see what tripped. That withholding is load-bearing,
/// so it is asserted by <see cref="Detectors_WithholdTheOffendingValueFromTheViolationClass"/>
/// rather than assumed.
///
/// This guard only reads what is INSIDE a fixture. Its sibling
/// <see cref="FixtureNamingGuardTests"/> polices the same corpus's PATHS, because on a public
/// repository a file name leaks just as effectively as a file body — and this guard, written from
/// a content instance, could not see that. Both draw their corpus from <see cref="FixtureCorpus"/>
/// so the two cannot drift apart.
/// </summary>
public class FixtureDeIdentificationGuardTests
{
    // ── Detectors ────────────────────────────────────────────────────────────────────────

    /// <summary>Local-part @ dotted-domain. Deliberately loose: cXML <c>payloadID</c> attributes
    /// use the same syntax and leak the buyer's hosted-instance hostname just as effectively.</summary>
    private static readonly Regex EmailLike = new(
        @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+",
        RegexOptions.Compiled);

    private static readonly Regex FileUri = new(
        @"file://",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Drive-letter path (<c>C:\</c> / <c>C:/</c>), POSIX home path, or UNC share.
    /// The lookbehind keeps <c>https://</c> and friends out — a scheme's last letter is preceded
    /// by another letter, a drive letter is not.</summary>
    private static readonly Regex AbsoluteLocalPath = new(
        @"(?<![A-Za-z0-9])[A-Za-z]:[\\/]"
        + @"|(?<![A-Za-z0-9])/(?:Users|home)/[A-Za-z0-9._\-]+/"
        + @"|\\\\[A-Za-z0-9._\-]+\\",
        RegexOptions.Compiled);

    /// <summary>Authority of any <c>scheme://…</c> URL. Deliberately scheme-agnostic: an
    /// <c>ftp://</c> or <c>sftp://</c> host on a real domain leaks exactly as much as an
    /// <c>https://</c> one.</summary>
    private static readonly Regex UrlAuthority = new(
        @"\b[A-Za-z][A-Za-z0-9+.\-]*://([^/\s""'<>\\)\]}]+)",
        RegexOptions.Compiled);

    /// <summary>A bare hostname written without a scheme. Restricted to the <c>www.</c> prefix on
    /// purpose: a general "dotted token" pattern would match version numbers, file names and
    /// namespace fragments, and a detector that flags everything gets suppressed within a week.</summary>
    private static readonly Regex BareWwwHost = new(
        @"(?<![A-Za-z0-9.@/\-])www\.[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Ariba Network ID: <c>AN</c> followed by digits.</summary>
    private static readonly Regex AribaNetworkId = new(
        @"\bAN([0-9]{9,})\b",
        RegexOptions.Compiled);

    /// <summary>
    /// The closed set of hosts a fixture may name. Every one is a standards body, a schema
    /// registry or ProcuLink's own product domain — a FORMAT, never a counterparty. Read the
    /// class doc before adding to this list; the question to ask is "is that a specification, or
    /// is that somebody we trade with?"
    /// </summary>
    private static readonly HashSet<string> StandardsHosts = new(StringComparer.Ordinal)
    {
        // XML / SOAP / OOXML infrastructure.
        "w3.org", "www.w3.org", "schemas.xmlsoap.org", "schemas.openxmlformats.org",
        "purl.org", "www.purl.org",

        // Procurement and e-invoicing standards.
        "xml.cxml.org", "cxml.org", "www.cxml.org",
        "docs.oasis-open.org", "www.oasis-open.org",
        "uri.etsi.org", "busdox.org", "peppol.eu", "docs.peppol.eu",
        "unece.org", "www.unece.org", "iso.org", "www.iso.org",

        // ProcuLink's own product domain.
        "proculink.eu", "www.proculink.eu", "api.proculink.eu",
    };

    /// <summary>Strips userinfo and port from a URL authority, leaving the bare host.</summary>
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

        return host.Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static bool IsApprovedHost(string host)
        => host.Length == 0 || IsPlaceholderDomain(host) || StandardsHosts.Contains(host);

    /// <summary>
    /// A really-allocated Ariba Network ID is never zero-padded; the reserved placeholder form
    /// used across this corpus is all zeros with a short trailing serial. Stating the permitted
    /// shape keeps this a format rule rather than a list of real identifiers.
    /// </summary>
    private static bool IsPlaceholderAribaNetworkId(string digits)
        => digits.Length >= 9 && digits[..^3].All(c => c == '0');

    /// <summary>RFC 2606 + RFC 6761 reserved names. Anything else is somebody's real domain.
    ///
    /// <para>Internal rather than private because <see cref="SourcePersonalDataGuardTests"/> — the
    /// guard that polices <c>.cs</c> source — reuses this exact decision. Sharing it is
    /// deliberate: if the two guards each owned a copy of "what counts as reserved", one could be
    /// widened to let a leak through while the other kept passing, and the passing one would be
    /// read as evidence the tree is clean. That is the same drift <see cref="FixtureCorpus"/>
    /// exists to prevent one level up.</para></summary>
    internal static bool IsPlaceholderDomain(string domain)
    {
        var d = domain.ToLowerInvariant().TrimEnd('.');

        if (d is "example" or "localhost" or "invalid" or "test")
        {
            return true;
        }

        foreach (var tld in new[] { ".example", ".invalid", ".test", ".localhost" })
        {
            if (d.EndsWith(tld, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var sld in new[] { "example.com", "example.net", "example.org" })
        {
            if (d == sld || d.EndsWith("." + sld, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every host named on one line, normalised for lookup. Shared by the detector and by the
    /// anti-vacuity floor, so the floor counts exactly what the detector examined — a host regex
    /// broken into matching nothing drives the count to zero and fails loudly, instead of turning
    /// the sweep green.
    /// </summary>
    internal static IReadOnlyList<string> HostsIn(string line)
    {
        var hosts = new List<string>();

        foreach (Match m in UrlAuthority.Matches(line))
        {
            hosts.Add(HostOf(m.Groups[1].Value));
        }

        foreach (Match m in BareWwwHost.Matches(line))
        {
            hosts.Add(HostOf(m.Value));
        }

        return hosts;
    }

    internal static IReadOnlyList<string> ViolationClasses(string line)
    {
        var found = new List<string>();

        foreach (Match m in EmailLike.Matches(line))
        {
            var domain = m.Value[(m.Value.IndexOf('@') + 1)..];
            if (!IsPlaceholderDomain(domain))
            {
                found.Add("email address on a non-placeholder domain");
                break;
            }
        }

        if (FileUri.IsMatch(line))
        {
            found.Add("file:// URI");
        }

        if (AbsoluteLocalPath.IsMatch(line))
        {
            found.Add("absolute local filesystem path");
        }

        if (HostsIn(line).Any(host => !IsApprovedHost(host)))
        {
            found.Add("URL or hostname on a host that is neither reserved-for-documentation "
                + "nor an approved standards host");
        }

        foreach (Match m in AribaNetworkId.Matches(line))
        {
            if (!IsPlaceholderAribaNetworkId(m.Groups[1].Value))
            {
                found.Add("Ariba Network ID outside the reserved all-zeros placeholder range");
                break;
            }
        }

        return found;
    }

    // ── The guard ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TrackedFixtures_CarryNoRealWorldIdentifiers()
    {
        var scan = ScanTrackedFixtures();

        scan.Violations.Should().BeEmpty(
            "a tracked fixture must not carry real-world identifiers — replace them with "
            + "example.*-class placeholders, preserving the document's shape. Offending "
            + "file:line and class (values withheld: this repository and its CI logs are public):\n"
            + string.Join("\n", scan.Violations));
    }

    /// <summary>
    /// An empty sweep must fail, not pass. A file count is not a detection floor (BE #175): the
    /// corpus can be enumerated correctly, opened correctly, and still examined by nothing. Four
    /// floors, each catching a different way the sweep can go hollow — the corpus query breaking,
    /// the corpus narrowing to one folder, the files never being opened, and the host detector
    /// itself being broken into matching nothing.
    /// </summary>
    [Fact]
    public void FixtureSweep_ActuallyReadsTheFixtureCorpus()
    {
        var scan = ScanTrackedFixtures();

        scan.ScannedFiles.Should().BeGreaterThanOrEqualTo(
            20,
            $"only {scan.ScannedFiles} tracked fixture files were scanned under {scan.RepoRoot} "
            + "— the corpus query is broken, so the guard above proves nothing");

        scan.DistinctDirectories.Should().BeGreaterThanOrEqualTo(
            4,
            $"only {scan.DistinctDirectories} distinct fixture directories were swept — the corpus "
            + "query has narrowed to one folder, so the guard above proves nothing about the rest");

        scan.ScannedBytes.Should().BeGreaterThan(
            20_000,
            $"only {scan.ScannedBytes} bytes of fixture content were read — the files were "
            + "listed but not opened, so the guard above proves nothing");

        scan.ScannedHosts.Should().BeGreaterThanOrEqualTo(
            20,
            $"only {scan.ScannedHosts} hostnames were extracted and checked across the corpus — "
            + "the host detector matched (almost) nothing, so the host half of the guard above "
            + "proves nothing. This is the floor that catches a sweep which reads every byte and "
            + "examines none of them");
    }

    /// <summary>
    /// Positive control. The sweep going green is only meaningful if the detectors can still
    /// go red — a regex broken into matching nothing would pass <see cref="TrackedFixtures_CarryNoRealWorldIdentifiers"/>
    /// silently and forever. Inputs here are synthetic and belong to nobody.
    ///
    /// <para><b>Do not join the split string literals below.</b> Three of these controls are
    /// e-mail-shaped tokens on deliberately non-reserved domains, which is exactly what
    /// <see cref="SourcePersonalDataGuardTests"/> sweeps <c>.cs</c> source for. Splitting them
    /// across a <c>+</c> means no single SOURCE LINE contains a matching address, so that guard
    /// needs no path exclusion — and a path exclusion would have been a hole a future real leak
    /// could sit in. The compiler folds each pair to the identical constant, so these controls
    /// test precisely what they always did; this test passing is itself the proof of that.</para>
    /// </summary>
    [Theory]
    [InlineData("<Email>firstname.lastname@" + "some-trading-company.de</Email>", "email address on a non-placeholder domain")]
    [InlineData("<cXML payloadID=\"1700000000.1@" + "buyer.somehost.com\">", "email address on a non-placeholder domain")]
    [InlineData("xsi:schemaLocation=\"urn:x file:///C:/Users/someone/Docs/schema.xsd\"", "file:// URI")]
    [InlineData("xsi:schemaLocation=\"urn:x file:///C:/Users/someone/Docs/schema.xsd\"", "absolute local filesystem path")]
    [InlineData("<Path>D:\\build\\artifacts\\out.xml</Path>", "absolute local filesystem path")]
    [InlineData("<Path>/home/someone/build/out.xml</Path>", "absolute local filesystem path")]
    [InlineData("<Path>redacted-fixture</Path>", "absolute local filesystem path")]
    [InlineData("<Path>\\\\fileserver\\share\\out.xml</Path>", "absolute local filesystem path")]
    // The rotation #179 reported and did not fix: a live URL is not an e-mail, not a file:// URI
    // and not a local path, so the first three detectors waved all of these through. Every host
    // below is fictitious — the detector needs no knowledge of any real company to refuse them.
    [InlineData(
        "<URL>https://www.nowhere-catalog-co.eu/ee/en/some-product/v2p1000001</URL>",
        "URL or hostname on a host that is neither reserved-for-documentation nor an approved standards host")]
    [InlineData(
        "<Extrinsic name=\"Image\">https://cdn.nowhere-catalog-co.eu/images/big/abc.jpg</Extrinsic>",
        "URL or hostname on a host that is neither reserved-for-documentation nor an approved standards host")]
    [InlineData(
        "<Result UrlBase=\"https://eshop.imaginary-distributor.sk/img.asp?stiid=\">",
        "URL or hostname on a host that is neither reserved-for-documentation nor an approved standards host")]
    [InlineData(
        "UrlExt=\"http://www.invented-manufacturer.com.tw/en/product/product.php?id=13120\"",
        "URL or hostname on a host that is neither reserved-for-documentation nor an approved standards host")]
    // Scheme-agnostic: a delivery endpoint leaks the same party an https:// catalog link does.
    [InlineData(
        "<Transport>sftp://edi.imaginary-buyer.de/inbound</Transport>",
        "URL or hostname on a host that is neither reserved-for-documentation nor an approved standards host")]
    // A bare host with no scheme at all.
    [InlineData(
        "COMMENTS: catalog exported from www.nowhere-catalog-co.eu",
        "URL or hostname on a host that is neither reserved-for-documentation nor an approved standards host")]
    // Userinfo and port must not hide the host.
    [InlineData(
        "<Feed>https://user:pw@" + "feed.imaginary-distributor.sk:8443/v1</Feed>",
        "URL or hostname on a host that is neither reserved-for-documentation nor an approved standards host")]
    // Category (d): a really-allocated account identifier. #179 de-identified one of these and
    // this one survived three lines deep in a CIF data row.
    [InlineData(
        "AN01234567891,13080788,REDACTED-ORDER-DATA,\"Patch cable\",43223303,4.24,EA",
        "Ariba Network ID outside the reserved all-zeros placeholder range")]
    [InlineData(
        "<Credential domain=\"NetworkID\"><Identity>REDACTED-NETWORK-ID</Identity></Credential>",
        "Ariba Network ID outside the reserved all-zeros placeholder range")]
    public void Detectors_FireOnKnownBadInput(string line, string expectedClass)
        => ViolationClasses(line).Should().Contain(expectedClass);

    /// <summary>
    /// Negative control. A detector that flags everything is as useless as one that flags
    /// nothing: it would be silenced within a week.
    /// </summary>
    [Theory]
    [InlineData("<Email name=\"default\">test@example.com</Email>")]
    [InlineData("<Email>test.user@buyer.example.com</Email>")]
    [InlineData("<Email>ops@supplier.example</Email>")]
    [InlineData("<cXML payloadID=\"matrix-1@proculink.test\" timestamp=\"2026-06-08T10:00:00+00:00\">")]
    [InlineData("<!DOCTYPE cXML SYSTEM \"http://xml.cxml.org/schemas/cXML/1.2.014/cXML.dtd\"[]>")]
    [InlineData("<URL>https://catalog.example.com/de/de/some-product/v2p34762852</URL>")]
    [InlineData("<Envelope xmlns=\"http://schemas.xmlsoap.org/soap/envelope/\" xml:lang=\"en-US\">")]
    [InlineData("<OrderRequestHeader orderDate=\"2026-06-14T01:42:54-04:00\" type=\"new\">")]
    // Standards hosts: a namespace, a schema registry and a DTD system id are FORMATS, and a
    // fixture that could not name them would stop being a valid instance of the format it pins.
    [InlineData("<Envelope xmlns=\"http://schemas.xmlsoap.org/soap/envelope/\">")]
    [InlineData("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">")]
    [InlineData("<!DOCTYPE cXML SYSTEM \"http://xml.cXML.org/schemas/cXML/1.2.014/cXML.dtd\">")]
    [InlineData("<cbc:ID schemeID=\"http://docs.oasis-open.org/ubl/os-UBL-2.1/\">1</cbc:ID>")]
    // Placeholder hosts — including the shapes this packet scrubbed the corpus TO.
    [InlineData("<Extrinsic name=\"Image\">https://cdn.example.com/images/big/abc.jpg</Extrinsic>")]
    [InlineData("<Result UrlBase=\"https://catalog.example.com/sk/default.asp?cls=stoitem&amp;stiid=\">")]
    [InlineData("UrlExt=\"https://manufacturer.example.com/en/product/product.php?id=13120#ov\"")]
    [InlineData("<Url>https://www.example.invalid/callback</Url>")]
    [InlineData("<ApiBase>http://localhost:5223/api/orders</ApiBase>")]
    // The reserved placeholder Ariba Network ID must not trip the ANID rule.
    [InlineData("<Credential domain=\"NetworkID\"><Identity>AN00000000001</Identity></Credential>")]
    [InlineData("AN00000000001,13080788,REDACTED-ORDER-DATA,\"Patch cable\",43223303,4.24,EA")]
    // Dotted tokens that are not hosts: a version, a file name, a decimal. A host detector that
    // fired on these would be suppressed within a week.
    [InlineData("<UnspscVersion>20.0601</UnspscVersion>")]
    [InlineData("CIF_I_V3.0")]
    [InlineData("<Money currency=\"EUR\">7.01</Money>")]
    public void Detectors_StaySilentOnCleanInput(string line)
        => ViolationClasses(line).Should().BeEmpty();

    /// <summary>
    /// The withholding is load-bearing, exactly as it is in
    /// <see cref="FixtureNamingGuardTests"/>: a violation string that echoed the host or the
    /// account identifier would republish it into a public CI log and a public <c>.trx</c>
    /// artifact, which is the thing this guard exists to prevent. Guarded, not assumed.
    /// </summary>
    [Theory]
    [InlineData(
        "<URL>https://www.nowhere-catalog-co.eu/ee/en/thing/v2p1000001</URL>",
        "nowhere-catalog-co")]
    [InlineData(
        "COMMENTS: catalog exported from www.nowhere-catalog-co.eu",
        "nowhere-catalog-co")]
    [InlineData(
        "<Identity>AN01234567891</Identity>",
        "AN01234567891")]
    [InlineData(
        "<Email>firstname.lastname@" + "some-trading-company.de</Email>",
        "some-trading-company")]
    public void Detectors_WithholdTheOffendingValueFromTheViolationClass(string line, string secret)
    {
        var classes = ViolationClasses(line);

        classes.Should().NotBeEmpty("the line is a leak and must be reported at all");
        classes.Should().OnlyContain(
            c => !c.Contains(secret, StringComparison.OrdinalIgnoreCase),
            "a violation class must name the KIND of leak, never the value — this repository and "
            + "its CI logs are public, and the file:line already makes it findable locally");
    }

    // ── Corpus ───────────────────────────────────────────────────────────────────────────

    private sealed record FixtureScan(
        string RepoRoot,
        int ScannedFiles,
        long ScannedBytes,
        int ScannedHosts,
        int DistinctDirectories,
        IReadOnlyList<string> Violations);

    private static FixtureScan ScanTrackedFixtures()
    {
        var repoRoot = FixtureCorpus.FindRepoRoot();
        var violations = new List<string>();
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var scannedFiles = 0;
        var scannedHosts = 0;
        long scannedBytes = 0;

        foreach (var relative in FixtureCorpus.TrackedFixtureFiles(repoRoot))
        {
            var absolute = Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolute))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(absolute);
            if (Array.IndexOf(bytes, (byte)0) >= 0)
            {
                continue; // binary fixture — not text-scannable
            }

            scannedFiles++;
            scannedBytes += bytes.LongLength;

            var lastSlash = relative.LastIndexOf('/');
            directories.Add(lastSlash < 0 ? string.Empty : relative[..lastSlash]);

            var lines = Encoding.UTF8.GetString(bytes).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                scannedHosts += HostsIn(lines[i]).Count;

                foreach (var violationClass in ViolationClasses(lines[i]))
                {
                    violations.Add($"  {relative}:{i + 1}: {violationClass}");
                }
            }
        }

        return new FixtureScan(
            repoRoot, scannedFiles, scannedBytes, scannedHosts, directories.Count, violations);
    }

}
