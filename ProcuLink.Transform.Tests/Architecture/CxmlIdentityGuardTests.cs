using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace ProcuLink.Transform.Tests.Architecture;

/// <summary>
/// The fourth rotation of the same defect, in the one category the previous three all declared
/// undetectable and deferred: <b>cXML network identities in source</b>.
///
/// <para>#153 scrubbed fixture CONTENTS. #179 found the defect rotated into fixture PATHS. #181
/// found it rotated again into fixture URLs and account identifiers. #184 found it in <c>.cs</c>
/// source and guarded the e-mail half. Each of the last three deferred the same thing, for the
/// same stated reason — <see cref="SourcePersonalDataGuardTests"/> puts it in its own doc:</para>
///
/// <para><i>"cXML &lt;Identity&gt; / EDI network identities. Arbitrary free text with no format
/// vocabulary at all."</i></para>
///
/// <h3>Why that reasoning was answering the wrong question</h3>
/// It is true that you cannot look at an arbitrary string and decide whether it names a real
/// company. That is the question "is this value real?", and it has no answer.
///
/// <para>But it is not the question a guard has to answer. The tractable question is <b>"may this
/// field hold this value?"</b> — and a cXML <c>&lt;Identity&gt;</c> is not arbitrary free text at
/// the point where it appears in this repository. It appears in exactly three
/// <b>format-delimited</b> positions, every one of which the cXML specification or this codebase's
/// own API names for us. So the rule inverts the same way #179's did: the value must be drawn from
/// a closed vocabulary of roles, shapes and locale codes, and anything outside it fails. That is a
/// constraint on the FIELD, which is decidable, instead of a judgement about the VALUE, which is
/// not.</para>
///
/// <para><b>Allow-list, never deny-list</b>, for #179's reason, which applies here with extra
/// force: a committed list of real network identities, on a public repository, in order to prove
/// no real network identities are committed, republishes exactly what it suppresses — and would
/// outlive any later scrub of the history, sitting in the tree handing the identifiers back. A
/// counterparty nobody has heard of is refused exactly as reliably as a famous one. <b>No real
/// value is stored anywhere in order to detect real values.</b></para>
///
/// <h3>Where it looks</h3>
/// <list type="number">
///   <item>A literal <c>&lt;Identity&gt;…&lt;/Identity&gt;</c> element inside a source string —
///     delimited by the cXML schema itself.</item>
///   <item>A member whose name ends in <c>Identity</c> (<c>FromIdentity</c>, <c>ToIdentity</c>,
///     <c>SenderIdentity</c>) set or asserted against a string literal — delimited by this
///     codebase's own API surface.</item>
///   <item>A cXML credential DOMAIN literal immediately followed by another string literal — the
///     positional <c>CxmlCredentialsInput("NetworkId", "…")</c> constructor form. The domain set
///     is closed by the cXML specification, so position 2 is always the identity.</item>
/// </list>
///
/// <h3><b>What it CANNOT detect, and will not pretend to</b></h3>
/// Stated here rather than discovered later by someone reading a green build as an all-clear.
/// <list type="bullet">
///   <item><b>Company names anywhere else.</b> A distributor named in a <c>///</c> doc comment, a
///     test method name, a <c>Supplier.Name</c> literal, or a markdown file is arbitrary free
///     text in an arbitrary position. There is no delimiter to hang a rule on, and no format
///     vocabulary to allow against. <b>The packet that added this guard scrubbed a number of
///     those by hand, and hand-applied conventions are precisely the thing that works until
///     someone forgets once. They are NOT protected.</b></item>
///   <item><b>Street addresses, VAT and registration numbers, personal names.</b> Unchanged from
///     #184's list, and unchanged for #184's reasons.</item>
///   <item><b>An identity built at runtime.</b> A value that is interpolated or concatenated is
///     allowed through, because the literal on the line is not the identity. That is a deliberate
///     hole and it is the same one #184 opened by splitting its own controls across a <c>+</c>.
///     </item>
/// </list>
///
/// <h3>Failure output withholds the value</h3>
/// Violations name the file, the line and the class — never the identity. CI logs and <c>.trx</c>
/// artifacts on a public repository are public, so a guard that printed the leak in order to
/// report it would not have removed it. Asserted, not assumed, by
/// <see cref="Detector_WithholdsTheOffendingIdentityFromTheViolationClass"/>.
/// </summary>
public class CxmlIdentityGuardTests
{
    // ── Detector ─────────────────────────────────────────────────────────────────────────

    /// <summary>Position 1: the cXML element itself.</summary>
    private static readonly Regex IdentityElement = new(
        @"<Identity>([^<]*)</Identity>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Position 2: this codebase's own API surface. <c>\b</c> before <c>Identity</c> means
    /// <c>ClaimsIdentity</c> and friends do not match — there is no word boundary inside an
    /// identifier — while <c>sender.Identity.Should().Be("…")</c> does.
    ///
    /// <para>The optional <c>"</c> after <c>Identity</c> picks up the JSON form
    /// (<c>{"fromIdentity":"…"}</c>), which is how a supplier connection's credentials are
    /// persisted. That column is a real leak surface: it is what actually lands in the database.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Case-insensitive, and that is load-bearing rather than defensive: the JSON key is
    /// <c>fromIdentity</c>, not <c>FromIdentity</c>. A case-sensitive version of this regex silently
    /// failed to match the persisted-credentials form, which — because the positional arm skips it
    /// as a JSON key — meant the value that actually reaches the database column was checked by
    /// nothing at all while the sweep stayed green. The positive control above pins this.
    /// It stays safe because <c>\b</c> still refuses <c>ClaimsIdentity</c> and friends: there is no
    /// word boundary in the middle of an identifier.
    /// </remarks>
    private static readonly Regex IdentityMember = new(
        @"\b(?:From|To|Sender)?Identity""?\s*(?:[:=]|\.Should\(\)\.Be\()\s*""([^""]*)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Position 3: the positional constructor form. The cXML <c>domain</c> attribute has a closed
    /// standard vocabulary, so a string literal holding one of those values is always immediately
    /// followed by the identity it labels.
    ///
    /// <para>The trailing <c>(?!\s*:)</c> rejects the JSON case, where the next string is a KEY
    /// rather than the identity (<c>"fromDomain":"NetworkId","fromIdentity":"…"</c>). That form is
    /// still checked — by <see cref="IdentityMember"/>, which reads the value instead of the key.
    /// </para>
    /// </summary>
    private static readonly Regex CredentialDomainPair = new(
        @"""(?:NetworkId|NetworkID|DUNS|AribaNetworkUserId|NetworkUserId|SystemID)""\s*,\s*""([^""]*)""(?!\s*:)",
        RegexOptions.Compiled);

    /// <summary>An Ariba Network ID is a legal identity value; its FORMAT rule lives in
    /// <see cref="FixtureDeIdentificationGuardTests"/> and is shared, not copied.</summary>
    private static readonly Regex AribaNetworkId = new(@"^AN([0-9]{9,})$", RegexOptions.Compiled);

    /// <summary>Splits an identity into vocabulary tokens on the separators cXML identities use.</summary>
    private static readonly Regex TokenSplitter = new(@"[_\-. /:]+", RegexOptions.Compiled);

    /// <summary>
    /// Every token an identity may be built from. Roles, shapes, placeholder vocabulary and
    /// locale codes only.
    ///
    /// <para><b>NOTHING IN THIS LIST MAY BE A TRADING COUNTERPARTY.</b> Adding a word is a
    /// deliberate, reviewable edit — which is the moment somebody gets to ask "is that a role, or
    /// is that a customer?" If the answer is "it is who we actually trade with", the fix is to
    /// change the test data, not to widen this list.</para>
    /// </summary>
    private static readonly HashSet<string> ApprovedIdentityTokens = new(StringComparer.Ordinal)
    {
        // Roles and canonical concepts.
        "buyer", "supplier", "sender", "receiver", "sup", "org", "orgid", "id", "ns", "identity",
        "net", "network", "networkuserid", "networkid", "user", "duns", "s", "b",

        // Placeholder / deliberately-fictitious vocabulary.
        "test", "testbuyer", "testsupplier", "acme", "example", "placeholder", "unknown",
        "matrix", "edge", "generic", "sample", "blank", "none", "dummy", "fake",

        // ProcuLink's own product name. Naming yourself is a disclosure choice about yourself,
        // not a counterparty leak — the same carve-out SourcePersonalDataGuardTests makes for
        // proculink.eu and FixtureDeIdentificationGuardTests makes in StandardsHosts.
        "proculink",

        // Locale / country codes. A country is a format fact, never a party. Listed explicitly
        // rather than admitting "any two letters", so that a two-letter company name is still
        // refused — the same choice FixtureNamingGuardTests makes.
        "at", "be", "cz", "de", "dk", "ee", "es", "fi", "fr", "gb", "ie", "it", "lt", "lv",
        "nl", "no", "pl", "pt", "se", "sk", "us",
    };

    /// <summary>
    /// Every identity-shaped value on one line. Shared by the detector and by the anti-vacuity
    /// floor, so the floor counts exactly what the detector examined — a regex broken into
    /// matching nothing drives the count to zero and fails loudly, instead of reading every byte
    /// of 1,200 files, examining none of them, and turning the sweep green.
    /// </summary>
    internal static IReadOnlyList<string> IdentitiesIn(string line)
    {
        var found = new List<string>();

        foreach (var regex in new[] { IdentityElement, IdentityMember, CredentialDomainPair })
        {
            foreach (Match m in regex.Matches(line))
            {
                var value = m.Groups[1].Value;

                // A capture spanning a C# string concatenation is not one literal, so it is not
                // one identity. It cannot be: a `"` inside the value would have ended the literal.
                // This is what makes SourcePersonalDataGuardTests' `+`-splitting technique work
                // for an element whose open and close tags sit on the same source line.
                if (value.Contains('"'))
                {
                    continue;
                }

                // The positional arm alone can run into a FluentAssertions `because` message
                // (`Should().Be("NetworkId", "the configured domain is preserved")`). A cXML
                // identity is a single token and never contains an interior space, so requiring
                // that removes the whole class without special-casing any one message. The other
                // two arms keep interior spaces, because there the delimiter is unambiguous and a
                // multi-word company name in an <Identity> is exactly the leak being hunted.
                if (regex == CredentialDomainPair
                    && value.Trim().Length > 0
                    && value.Any(char.IsWhiteSpace))
                {
                    continue;
                }

                found.Add(value);
            }
        }

        return found;
    }

    /// <summary>
    /// True when an identity is drawn entirely from the approved vocabulary. Empty and
    /// runtime-built values are allowed: a blank identity is a shape several tests deliberately
    /// exercise, and an interpolated one is not a literal at all.
    /// </summary>
    internal static bool IsApprovedIdentity(string identity)
    {
        var value = identity.Trim();

        if (value.Length == 0)
        {
            return true;
        }

        // Built at runtime — the literal on this line is not the identity. Stated as a hole in
        // the class doc rather than hidden.
        if (value.Contains('{') || value.Contains('}'))
        {
            return true;
        }

        var anid = AribaNetworkId.Match(value);
        if (anid.Success)
        {
            return FixtureDeIdentificationGuardTests.IsPlaceholderAribaNetworkId(anid.Groups[1].Value);
        }

        foreach (var token in TokenSplitter.Split(value))
        {
            var normalised = new string(token.ToLowerInvariant().Where(char.IsLetter).ToArray());

            // A purely numeric token (a serial, a registration number's digits) carries no name.
            if (normalised.Length == 0)
            {
                continue;
            }

            if (!ApprovedIdentityTokens.Contains(normalised))
            {
                return false;
            }
        }

        return true;
    }

    internal static IReadOnlyList<string> ViolationClasses(string line)
    {
        foreach (var identity in IdentitiesIn(line))
        {
            if (!IsApprovedIdentity(identity))
            {
                return new[]
                {
                    "cXML network identity is not drawn from the approved placeholder vocabulary",
                };
            }
        }

        return Array.Empty<string>();
    }

    // ── The guard ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TrackedSource_CarriesNoRealWorldCxmlIdentity()
    {
        var scan = ScanTrackedSource();

        scan.Violations.Should().BeEmpty(
            "a cXML <Identity> in source must name a ROLE, not a party — use the "
            + "TESTBUYER_<CC> / TESTSUPPLIER_<CC> placeholder convention this repository already "
            + "uses, or add the token to ApprovedIdentityTokens if it genuinely describes a role "
            + "rather than a counterparty. A <Credential> block pairing a real buyer identity with "
            + "a real supplier identity publishes a real trading relationship, and this repository "
            + "is public. Offending file:line and class (values withheld: this repository and its "
            + "CI logs are public):\n"
            + string.Join("\n", scan.Violations));
    }

    /// <summary>
    /// An empty sweep must fail, not pass. A file count is not a detection floor (BE #175, #181's
    /// <c>ScannedHosts</c>, #184's <c>ScannedEmails</c>): the corpus can be enumerated correctly,
    /// opened correctly, and still examined by nothing.
    /// </summary>
    [Fact]
    public void IdentitySweep_ActuallyReadsTheSourceCorpus()
    {
        var scan = ScanTrackedSource();

        scan.ScannedFiles.Should().BeGreaterThanOrEqualTo(
            900,
            $"only {scan.ScannedFiles} tracked source files were scanned under {scan.RepoRoot} — "
            + "the corpus query is broken, so the guard above proves nothing");

        scan.DistinctProjects.Should().BeGreaterThanOrEqualTo(
            6,
            $"only {scan.DistinctProjects} distinct projects were swept — the corpus query has "
            + "narrowed, and these identities spanned all three test projects, production source "
            + "AND tools/");

        scan.ScannedBytes.Should().BeGreaterThan(
            1_000_000,
            $"only {scan.ScannedBytes} bytes of source were read — the files were listed but not "
            + "opened, so the guard above proves nothing");

        scan.ScannedIdentities.Should().BeGreaterThanOrEqualTo(
            60,
            $"only {scan.ScannedIdentities} identity-shaped values were extracted and checked "
            + "across the corpus — the detector matched (almost) nothing, so the guard above "
            + "proves nothing. This is the floor that catches a sweep which reads every byte and "
            + "examines none of them");
    }

    /// <summary>
    /// Positive control. Every identity below is invented and belongs to nobody, but each has the
    /// shape of a real leak — a party name in a credential identity — in each of the three
    /// positions the detector watches.
    /// </summary>
    [Theory]
    // Position 1: the cXML element.
    [InlineData("<From><Credential domain=\"NetworkId\"><Identity>Inventedstores_SE</Identity></Credential></From>")]
    [InlineData("xml.Should().Contain(\"<Identity>Imaginarybank_DK</Identity>\");")]
    // Position 2: the API surface, as an assignment, a named argument and an assertion.
    [InlineData("FromIdentity = \"Nowhereworks_NO\",")]
    [InlineData("ToIdentity: \"Fictionalmills_FI\",")]
    [InlineData("resolved.SenderIdentity.Should().Be(\"Notarealgroup_EE\");")]
    [InlineData("sender.Identity.Should().Be(\"Madeupholding_PL\");")]
    // Position 3: the positional constructor.
    [InlineData("new CxmlCredentialsInput(\"NetworkId\", \"Pretendtrading_DE\", null, null)")]
    // Case must not hide it, and neither may a mixed role/party compound.
    [InlineData("<Identity>TESTBUYER_INVENTEDCO</Identity>")]
    // An Ariba Network ID outside the reserved all-zeros range is still a real allocation.
    [InlineData("<Identity>AN44556677889</Identity>")]
    // The JSON persistence form. This is the shape that actually reaches the database column, so
    // exempting it in order to dodge the "key vs value" ambiguity would have gutted the guard.
    [InlineData("CxmlConfigJson = \"\"\"{\"fromDomain\":\"NetworkId\",\"fromIdentity\":\"Inventedcables_LV\"}\"\"\",")]
    // A multi-word legal name in the element form: interior spaces must NOT buy an exemption here.
    [InlineData("<Identity>Imaginary Bearings Holding AS</Identity>")]
    public void Detector_FiresOnKnownBadInput(string line)
        => ViolationClasses(line).Should().ContainSingle();

    /// <summary>
    /// Negative control. A detector that flags everything is as useless as one that flags nothing:
    /// it gets suppressed within a week. Includes every identity shape this repository actually
    /// uses — so a future narrowing of the vocabulary fails HERE, loudly, rather than silently
    /// re-flagging every credential test in the tree.
    /// </summary>
    [Theory]
    // The placeholder convention this packet scrubbed TO, and the one #181 already merged.
    [InlineData("FromIdentity: \"TESTBUYER_SE\", ToIdentity: \"TESTSUPPLIER_SE\",")]
    [InlineData("new CxmlCredentialsInput(\"NetworkId\", \"TESTBUYER_DK\", \"NetworkId\", \"TESTSUPPLIER_DK\")")]
    [InlineData("<To><Credential domain=\"NetworkId\"><Identity>TESTSUPPLIER_EE</Identity></Credential></To>")]
    [InlineData("saved.CxmlCredentials!.FromIdentity.Should().Be(\"TESTBUYER_SE\");")]
    // Short role identities already in use across the parser tests.
    [InlineData("<Sender><Credential domain=\"NetworkUserId\"><Identity>s</Identity></Credential></Sender>")]
    [InlineData("<Identity>buyer-id</Identity>")]
    [InlineData("<Identity>supplier-ns</Identity>")]
    [InlineData("<Identity>sender</Identity>")]
    [InlineData("<Identity>Acme</Identity>")]
    [InlineData("<Identity>MATRIX_BUYER</Identity>")]
    [InlineData("<Identity>EDGE_BUYER</Identity>")]
    // ProcuLink's own legacy sender identity — naming yourself is a disclosure choice.
    [InlineData("<Sender><Credential domain=\"NetworkUserId\"><Identity>proculink</Identity></Credential></Sender>")]
    // A country-prefixed registration number: the letters are a locale, the rest carries no name.
    [InlineData("FromIdentity: \"FR99000000000\",")]
    // The reserved placeholder Ariba Network ID, whose format rule is shared with #181's guard.
    [InlineData("<Identity>AN00000000001</Identity>")]
    // Built at runtime, and deliberately blank — both are shapes tests exercise on purpose.
    [InlineData("<Identity>{orgId}</Identity>")]
    [InlineData("FromIdentity: \"\", ToIdentity: \"   \",")]
    // A DOMAIN literal is not an identity, and must not be read as one.
    [InlineData("sender.Domain.Should().Be(\"NetworkUserId\");")]
    // A FluentAssertions `because` message sits in the same position as a positional identity.
    // A detector that reported these would be suppressed within a week.
    [InlineData("from.Domain.Should().Be(\"NetworkId\", \"the configured From domain is preserved\");")]
    [InlineData("d.Should().Be(\"NetworkId\", \"a tax-id identity is a NetworkId, not the OrgId domain\");")]
    // The JSON persistence form carrying an approved value: the KEY must not be read as the value.
    [InlineData("CxmlConfigJson = \"\"\"{\"fromDomain\":\"NetworkId\",\"fromIdentity\":\"TESTBUYER_SE\"}\"\"\",")]
    [InlineData("CxmlConfigJson = \"\"\"{\"fromDomain\":\"DUNS\",\"fromIdentity\":\"123456789\"}\"\"\",")]
    // A capture spanning a C# concatenation is not one literal, so it is not one identity. This is
    // what lets FixtureDeIdentificationGuardTests keep its deliberately-non-reserved ANID controls.
    [InlineData("\"<Credential domain=\\\"NetworkID\\\"><Identity>AN\" + \"98765432109</Identity></Credential>\",")]
    // Not identities at all. A detector that fired on these would be silenced immediately.
    [InlineData("var user = new ClaimsIdentity(claims, \"Bearer\");")]
    [InlineData("public string Identity { get; init; } = string.Empty;")]
    [InlineData("// the <Identity> element carries the network id, never the legal name")]
    public void Detector_StaysSilentOnCleanInput(string line)
        => ViolationClasses(line).Should().BeEmpty();

    /// <summary>
    /// The withholding is load-bearing — a violation string that echoed the identity would publish
    /// the counterparty into a public CI log and a public <c>.trx</c> artifact, which is the
    /// precise thing this guard exists to prevent. Guarded, not assumed, exactly as in
    /// <see cref="FixtureNamingGuardTests"/>, <see cref="FixtureDeIdentificationGuardTests"/> and
    /// <see cref="SourcePersonalDataGuardTests"/>.
    /// </summary>
    [Theory]
    [InlineData("<Identity>Inventedstores_SE</Identity>", "Inventedstores")]
    [InlineData("FromIdentity = \"Nowhereworks_NO\",", "Nowhereworks")]
    [InlineData("new CxmlCredentialsInput(\"NetworkId\", \"Pretendtrading_DE\")", "Pretendtrading")]
    [InlineData("<Identity>AN44556677889</Identity>", "44556677889")]
    public void Detector_WithholdsTheOffendingIdentityFromTheViolationClass(string line, string secret)
    {
        var classes = ViolationClasses(line);

        classes.Should().NotBeEmpty("the line is a leak and must be reported at all");
        classes.Should().OnlyContain(
            c => !c.Contains(secret, StringComparison.OrdinalIgnoreCase),
            "a violation class must name the KIND of leak, never the value — this repository and "
            + "its CI logs are public, and the file:line already makes it findable locally");
    }

    // ── Corpus ───────────────────────────────────────────────────────────────────────────

    private sealed record IdentityScan(
        string RepoRoot,
        int ScannedFiles,
        long ScannedBytes,
        int ScannedIdentities,
        int DistinctProjects,
        IReadOnlyList<string> Violations);

    /// <summary>
    /// Tracked <c>.cs</c> AND <c>.mjs</c> files. The <c>.mjs</c> half is not incidental: one of
    /// the identities this packet removed lived in <c>tools/edge-case-orders/generate.mjs</c>,
    /// outside the test tree entirely, where <see cref="SourcePersonalDataGuardTests"/>' <c>.cs</c>
    /// corpus could never have seen it. A generator that emits cXML is exactly as capable of
    /// emitting a real identity as a test is.
    /// </summary>
    private static IReadOnlyList<string> TrackedSourceFiles(string repoRoot)
        => FixtureCorpus.TrackedFiles(repoRoot, "*.cs")
            .Concat(FixtureCorpus.TrackedFiles(repoRoot, "*.mjs"))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    private static IdentityScan ScanTrackedSource()
    {
        var repoRoot = FixtureCorpus.FindRepoRoot();
        var violations = new List<string>();
        var projects = new HashSet<string>(StringComparer.Ordinal);
        var scannedFiles = 0;
        var scannedIdentities = 0;
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
                scannedIdentities += IdentitiesIn(lines[i]).Count;

                foreach (var violationClass in ViolationClasses(lines[i]))
                {
                    violations.Add($"  {relative}:{i + 1}: {violationClass}");
                }
            }
        }

        return new IdentityScan(
            repoRoot, scannedFiles, scannedBytes, scannedIdentities, projects.Count, violations);
    }
}
