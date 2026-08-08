using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace ProcuLink.Transform.Tests.Architecture;

/// <summary>
/// The third rotation of the same defect, in the place the first two could not see: <b>.cs
/// source</b>.
///
/// <para>#153 scrubbed fixture CONTENTS and guarded them. #179 found the same defect rotated into
/// fixture PATHS and guarded those. #181 found it rotated again into fixture URLs and account
/// identifiers, guarded those too — and reported, without fixing, that the largest exposure was
/// never in a fixture at all. Both existing guards draw their corpus from
/// <see cref="FixtureCorpus"/>, which selects files under a directory segment ending in
/// "Fixtures". <b>A .cs file is invisible to both.</b> That is why real personal e-mail addresses
/// at live corporate domains, real companies with street addresses and registration numbers, and
/// test method names naming real counterparties all survived three scrubs.</para>
///
/// <para>This guard closes the half of that which has a detectable shape.</para>
///
/// <h3>What it detects</h3>
/// One class: <b>an e-mail address on a domain that is not reserved for documentation.</b>
/// The allow-list is inverted for #179's reason — a deny-list of real domains, committed to a
/// public repository in order to prove that no real domains are committed, republishes exactly
/// what it suppresses, and would outlive any later scrub of the history by sitting in the current
/// tree handing the names back. So a .cs file may name an e-mail domain only when it is:
/// <list type="bullet">
///   <item>reserved-for-documentation under RFC 2606 / RFC 6761 — decided by
///     <see cref="FixtureDeIdentificationGuardTests.IsPlaceholderDomain"/>, deliberately SHARED
///     with the fixture guard so the two cannot drift on what "reserved" means;</item>
///   <item>under <c>.internal</c>, reserved by ICANN in 2024 for private-network use;</item>
///   <item>ProcuLink's own product domain — the same carve-out the fixture guard's
///     <c>StandardsHosts</c> already makes, for the same reason: naming yourself is a disclosure
///     choice, not a counterparty leak.</item>
/// </list>
/// A counterparty nobody has heard of is refused exactly as reliably as a famous one, which a
/// deny-list can never do. No real value is stored anywhere in order to detect real values.
///
/// <h3><b>What it CANNOT detect, and will not pretend to</b></h3>
/// This is the honest limit of the technique, and it is stated here rather than discovered later
/// by someone reading a green build as an all-clear. #181 named the constraint precisely: the
/// allow-list works because a hostname has a closed FORMAT vocabulary to be allowed against.
/// These do not, and are therefore <b>outside this guard entirely</b>:
/// <list type="bullet">
///   <item><b>Personal names.</b> A given name and surname in a <c>ContactName</c> or
///     <c>DeliverTo</c> field is arbitrary free text. There is no format that distinguishes a real
///     person from an invented one, in either direction.</item>
///   <item><b>Company legal names.</b> Same problem. A corporate suffix (<c>GmbH</c>, <c>A/S</c>,
///     <c>ASA</c>) is a format token, but it says nothing about whether the name in front of it
///     belongs to anybody.</item>
///   <item><b>Street addresses, cities and postcodes.</b> A postcode has a shape per country; a
///     REAL postcode has the same shape as an invented one.</item>
///   <item><b>VAT / organisation / registration numbers.</b> These do have a country-prefixed
///     format, but no reserved range exists to allow-list against — unlike an Ariba Network ID,
///     where "never zero-padded" made a format rule possible (#181). A checksum-valid VAT number
///     is checksum-valid whether or not it was ever issued.</item>
///   <item><b>cXML <c>&lt;Identity&gt;</c> / EDI network identities.</b> Arbitrary free text with
///     no format vocabulary at all — exactly the reason #181 deferred the two credential literals
///     to their own packet rather than scrubbing them unguarded.</item>
/// </list>
/// Those categories were scrubbed by hand in the packet that added this guard, and a hand-applied
/// convention is precisely the thing that works until someone forgets once. <b>They are not
/// protected. Do not read this guard passing as evidence that they are clean.</b>
///
/// <h3>Failure output withholds the value</h3>
/// Violations name the file, the line and the class — never the address. CI logs and
/// <c>.trx</c> artifacts on a public repository are public, so a guard that printed the leak in
/// order to report the leak would not have removed it. Asserted, not assumed, by
/// <see cref="Detector_WithholdsTheOffendingAddressFromTheViolationClass"/>.
///
/// <h3>Why this file needs no path exclusions</h3>
/// The guard files carry deliberately-fictitious, deliberately-NON-reserved addresses as positive
/// controls, so a naive sweep of .cs source would fire on its own controls. Excluding a directory
/// would have opened a hole a future leak could sit in. Instead the control literals are SPLIT
/// across a <c>+</c> in source (<c>"name@" + "domain.example-tld"</c>): the compiler folds them to
/// exactly the same constant, so the controls test exactly what they always did, while no single
/// SOURCE LINE ever contains a matching address. The existing positive controls going green is
/// itself the proof that the split changed no value.
/// </summary>
public class SourcePersonalDataGuardTests
{
    // ── Detector ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Local-part @ dotted-domain, with the final label required to be ALPHABETIC.
    ///
    /// <para>The fixture guard's e-mail detector is deliberately looser, because a cXML
    /// <c>payloadID</c> leaks a hostname using the same syntax. C# source cannot afford that
    /// looseness: <c>user@10.1.2.3</c> in an SSH error string and <c>SUP-2@5.5x2</c> in a Scriban
    /// template output both match the loose form, and neither is an address. Requiring a
    /// real TLD shape removes both classes without special-casing either — a detector that fires
    /// on template syntax gets suppressed within a week.</para>
    ///
    /// <para>Known limitation, stated rather than hidden: a punycode TLD (<c>xn--…</c>) contains
    /// digits and would not match. No such address exists in this tree, and widening the rule to
    /// admit digits would re-admit the dotted-decimal false positives above.</para>
    /// </summary>
    private static readonly Regex EmailLike = new(
        @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}\b",
        RegexOptions.Compiled);

    /// <summary>
    /// ProcuLink's own product domain. The same carve-out the fixture guard makes in its
    /// <c>StandardsHosts</c>. Naming your own service is a disclosure choice about yourself;
    /// naming a counterparty is not.
    /// </summary>
    private static readonly HashSet<string> OwnDomains = new(StringComparer.Ordinal)
    {
        "proculink.eu", "proculink.test",
    };

    private static bool IsOwnDomain(string domain)
        => OwnDomains.Contains(domain)
           || OwnDomains.Any(own => domain.EndsWith("." + own, StringComparison.Ordinal));

    /// <summary>
    /// <c>.internal</c> was reserved by ICANN in 2024 for private-network use, so it can never be
    /// resolved on the public internet and can never name a real counterparty's mail domain. That
    /// makes it a FORMAT fact, in the same class as RFC 2606's reservations — not a judgement
    /// about whether some particular host is real.
    /// </summary>
    private static bool IsReservedPrivateUse(string domain)
        => domain.Equals("internal", StringComparison.Ordinal)
           || domain.EndsWith(".internal", StringComparison.Ordinal);

    private static bool IsApprovedEmailDomain(string domain)
    {
        var d = domain.ToLowerInvariant().TrimEnd('.');
        return FixtureDeIdentificationGuardTests.IsPlaceholderDomain(d)
               || IsReservedPrivateUse(d)
               || IsOwnDomain(d);
    }

    /// <summary>
    /// Every e-mail-shaped token on one line. Shared by the detector and by the anti-vacuity
    /// floor, so the floor counts exactly what the detector examined — a regex broken into
    /// matching nothing drives the count to zero and fails loudly, instead of reading every byte
    /// of 1,200 files, examining none of them, and turning the sweep green.
    /// </summary>
    internal static IReadOnlyList<string> EmailsIn(string line)
    {
        var found = new List<string>();
        foreach (Match m in EmailLike.Matches(line))
        {
            found.Add(m.Value);
        }

        return found;
    }

    internal static IReadOnlyList<string> ViolationClasses(string line)
    {
        foreach (var email in EmailsIn(line))
        {
            var domain = email[(email.IndexOf('@') + 1)..];
            if (!IsApprovedEmailDomain(domain))
            {
                return new[] { "e-mail address on a domain that is not reserved-for-documentation" };
            }
        }

        return Array.Empty<string>();
    }

    // ── The guard ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TrackedSource_CarriesNoEmailOnARealDomain()
    {
        var scan = ScanTrackedSource();

        scan.Violations.Should().BeEmpty(
            "a .cs file must not carry an e-mail address on a real domain — replace it with an "
            + "example.*-class placeholder, preserving the address's shape. A personal address at "
            + "a corporate domain names an identifiable individual AND their employer, and this "
            + "repository is public. Offending file:line and class (values withheld: this "
            + "repository and its CI logs are public):\n"
            + string.Join("\n", scan.Violations));
    }

    /// <summary>
    /// An empty sweep must fail, not pass. A file count is not a detection floor (BE #175, and
    /// #181's <c>ScannedHosts</c>): the corpus can be enumerated correctly, opened correctly, and
    /// still examined by nothing. Four floors, each catching a different way this sweep can go
    /// hollow — the corpus query breaking, the corpus narrowing to one project, the files never
    /// being opened, and the detector itself being broken into matching nothing.
    /// </summary>
    [Fact]
    public void SourceSweep_ActuallyReadsTheSourceCorpus()
    {
        var scan = ScanTrackedSource();

        scan.ScannedFiles.Should().BeGreaterThanOrEqualTo(
            900,
            $"only {scan.ScannedFiles} tracked .cs files were scanned under {scan.RepoRoot} — the "
            + "corpus query is broken, so the guard above proves nothing");

        scan.DistinctProjects.Should().BeGreaterThanOrEqualTo(
            6,
            $"only {scan.DistinctProjects} distinct projects were swept — the corpus query has "
            + "narrowed, and the leak this guard exists for spanned all three test projects AND "
            + "production source");

        scan.ScannedBytes.Should().BeGreaterThan(
            1_000_000,
            $"only {scan.ScannedBytes} bytes of source were read — the files were listed but not "
            + "opened, so the guard above proves nothing");

        scan.ScannedEmails.Should().BeGreaterThanOrEqualTo(
            100,
            $"only {scan.ScannedEmails} e-mail-shaped tokens were extracted and checked across the "
            + "corpus — the detector matched (almost) nothing, so the guard above proves nothing. "
            + "This is the floor that catches a sweep which reads every byte and examines none of "
            + "them");
    }

    /// <summary>
    /// Positive control. Every address below is fictitious and belongs to nobody, but each is
    /// shaped like a real leak — a personal address at a corporate domain under a real TLD, which
    /// is exactly the class this packet removed. Control literals are split across a <c>+</c> so
    /// this file's own source lines contain no matching address; the compiler folds them to the
    /// identical constant.
    /// </summary>
    [Theory]
    // A personal name at a company domain — the highest-severity shape.
    [InlineData("ContactEmail = \"firstname.surname@" + "invented-steelworks.at\";")]
    [InlineData("Recipient: \"f.surname@" + "nowhere-seafood.no\")")]
    // A role address at a company domain still names the counterparty.
    [InlineData("BillToEmail = \"accounts@" + "imaginary-tyres.fr\";")]
    // Case must not hide it.
    [InlineData("[InlineData(\"Orders@" + "Invented-Distributor.CoM\")]")]
    // A plus-tag and a subdomain must not hide it either.
    [InlineData("FromEmail: \"po+urgent@" + "mail.fictional-buyer.de\"")]
    // A free-mail provider is still a real domain.
    [InlineData("var sender = \"someone@" + "not-a-real-mailhost.com\";")]
    // In a comment, not only in a literal.
    [InlineData("// the ordering contact was a.person@" + "invented-classification.no")]
    public void Detector_FiresOnKnownBadInput(string line)
        => ViolationClasses(line).Should().ContainSingle();

    /// <summary>
    /// Negative control. A detector that flags everything is as useless as one that flags nothing:
    /// it gets suppressed within a week. Includes every placeholder shape this packet scrubbed TO,
    /// so a future narrowing of the allow-list fails here rather than silently re-flagging 300
    /// clean lines.
    /// </summary>
    [Theory]
    [InlineData("ContactEmail = \"alex.testperson@buyer.example.com\";")]
    [InlineData("Recipient: \"c.testperson@buyer.example.com\")")]
    [InlineData("var a = \"ops@supplier.example\";")]
    [InlineData("[InlineData(\"Orders@ACME.eXaMpLe\", \"acme.example\")]")]
    [InlineData("const string Recipient = \"buyer@practice.test\";")]
    [InlineData("msg.To.Add(new MailboxAddress(\"R\", \"po@buyer.example.com\"));")]
    [InlineData("var x = \"someone@mail.example\";")]
    [InlineData("Destination = \"orders@supplier.invalid\",")]
    [InlineData("var u = \"admin@localhost\";")]
    // ProcuLink's own product domain, including the inbound-address subdomain, which IS the
    // product's real addressing scheme and cannot be placeholdered without breaking the feature.
    [InlineData("emails: \"founder@proculink.eu\"")]
    [InlineData("const string Recipient = \"acme@orders.proculink.eu\";")]
    [InlineData("public string DefaultFrom => \"orders@proculink.test\";")]
    // ICANN-reserved private-use TLD.
    [InlineData("user: \"catalog-user@supplier-host.internal\",")]
    // Not e-mails at all. A detector that fired on these would be silenced immediately.
    [InlineData("var version = \"20.0601\";")]
    [InlineData("Assert.Equal(7.01m, line.UnitPrice);")]
    [InlineData("public Task<IReadOnlyList<string>> GetAsync() => _db.Set<Foo>();")]
    [InlineData("// see https://docs.proculink.eu/delivery for the retry contract")]
    public void Detector_StaysSilentOnCleanInput(string line)
        => ViolationClasses(line).Should().BeEmpty();

    /// <summary>
    /// The withholding is load-bearing — a violation string that echoed the address would publish
    /// the person's name and their employer into a public CI log and a public <c>.trx</c>
    /// artifact, which is the precise thing this guard exists to prevent. Guarded, not assumed,
    /// exactly as in <see cref="FixtureDeIdentificationGuardTests"/> and
    /// <see cref="FixtureNamingGuardTests"/>.
    /// </summary>
    [Theory]
    [InlineData("ContactEmail = \"firstname.surname@" + "invented-steelworks.at\";", "firstname.surname")]
    [InlineData("ContactEmail = \"firstname.surname@" + "invented-steelworks.at\";", "invented-steelworks")]
    [InlineData("Recipient: \"f.surname@" + "nowhere-seafood.no\")", "nowhere-seafood")]
    [InlineData("BillToEmail = \"accounts@" + "imaginary-tyres.fr\";", "imaginary-tyres")]
    public void Detector_WithholdsTheOffendingAddressFromTheViolationClass(string line, string secret)
    {
        var classes = ViolationClasses(line);

        classes.Should().NotBeEmpty("the line is a leak and must be reported at all");
        classes.Should().OnlyContain(
            c => !c.Contains(secret, StringComparison.OrdinalIgnoreCase),
            "a violation class must name the KIND of leak, never the value — this repository and "
            + "its CI logs are public, and the file:line already makes it findable locally");
    }

    // ── Corpus ───────────────────────────────────────────────────────────────────────────

    private sealed record SourceScan(
        string RepoRoot,
        int ScannedFiles,
        long ScannedBytes,
        int ScannedEmails,
        int DistinctProjects,
        IReadOnlyList<string> Violations);

    /// <summary>
    /// Tracked <c>.cs</c> files, from git rather than the filesystem — the same reasoning
    /// <see cref="FixtureCorpus"/> gives: untracked working files are deliberately not committed,
    /// and a filesystem walk would flag them while missing nothing real. <c>bin/</c> and
    /// <c>obj/</c> fall out for free because they are not tracked.
    /// </summary>
    private static IReadOnlyList<string> TrackedSourceFiles(string repoRoot)
        => FixtureCorpus.TrackedFiles(repoRoot, "*.cs");

    private static SourceScan ScanTrackedSource()
    {
        var repoRoot = FixtureCorpus.FindRepoRoot();
        var violations = new List<string>();
        var projects = new HashSet<string>(StringComparer.Ordinal);
        var scannedFiles = 0;
        var scannedEmails = 0;
        long scannedBytes = 0;

        foreach (var relative in TrackedSourceFiles(repoRoot))
        {
            var absolute = Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolute))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(absolute);
            scannedFiles++;
            scannedBytes += bytes.LongLength;

            var firstSlash = relative.IndexOf('/');
            projects.Add(firstSlash < 0 ? string.Empty : relative[..firstSlash]);

            var lines = Encoding.UTF8.GetString(bytes).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                scannedEmails += EmailsIn(lines[i]).Count;

                foreach (var violationClass in ViolationClasses(lines[i]))
                {
                    violations.Add($"  {relative}:{i + 1}: {violationClass}");
                }
            }
        }

        return new SourceScan(
            repoRoot, scannedFiles, scannedBytes, scannedEmails, projects.Count, violations);
    }
}
