using System.Text.RegularExpressions;
using FluentAssertions;

namespace ProcuLink.Transform.Tests.Architecture;

/// <summary>
/// The fifth sibling in this family, and the one that closes the hole the other four leave BY
/// CORPUS rather than by rule. Every previous pass here failed the same way — not because its
/// detector was wrong, but because its sweep could not see the file the next leak was sitting in:
///
/// <list type="bullet">
/// <item>#153 and #179 select files under a directory segment ending in <c>Fixtures</c>, so a
/// <c>.cs</c> file was invisible to both.</item>
/// <item>#184 swept <c>.cs</c>, so an identity in <c>tools/…/generate.mjs</c> was invisible to it.</item>
/// <item>#186's corpus is every tracked file under <c>docs/</c> — which is why the densest
/// remaining concentration of live operational data sat in <c>STATUS.md</c>, one directory level
/// up, and in <c>NuGet.Config</c>, <c>appsettings.Development.json</c> and
/// <c>tools/edge-case-orders/run-live.md</c>.</item>
/// </list>
///
/// <para>So this guard sweeps TWO corpora, each stated exactly rather than implied.</para>
///
/// <para><b>Corpus A — the non-C# complement</b>, carrying the five shared operational-data
/// classes. Every tracked file that is not under <c>docs/</c>, not inside the fixture corpus, and
/// not <c>*.cs</c>. Concretely today that is <c>.csproj</c>, <c>.slnx</c>, <c>.json</c>,
/// <c>.xml</c>, <c>.yml</c>, <c>.md</c>, <c>.mjs</c>, <c>.toml</c>, <c>.html</c>, <c>.http</c>,
/// <c>.csv</c>, <c>.cif</c>, <c>.Config</c>, <c>.worker</c>, <c>Dockerfile</c> and the dot-files —
/// but the corpus is NOT expressed as an extension list, so a new file type added tomorrow is
/// swept the day it lands rather than creating a fresh blind spot. Repository-root markdown and
/// <c>tools/</c> are in it deliberately: those are the two regions every previous corpus missed.</para>
///
/// <para><b>Corpus B — every tracked <c>*.cs</c></b>, carrying the protocol-discriminator class
/// only. That class is new, so no sibling covers it anywhere.</para>
///
/// <para><b>The honest limit, stated because a green build must not be read as more than it is.</b>
/// The five shared classes are NOT applied to <c>*.cs</c>. Running them there was tried and
/// reports ~200 lines, of which the overwhelming majority are deliberate: SSRF and
/// transport-security tests that must name public addresses to prove they are refused, UN/EDIFACT
/// specification URLs in parser doc comments, and — the blocking share — the four sibling guards'
/// own positive controls, which are non-reserved BY CONSTRUCTION and would have to be split across
/// a <c>+</c> the way #184 and #185 split theirs before this could ever go green. That is a
/// mechanical follow-up with a known shape, not a judgement call, and it is sized in the PR that
/// added this file. Until it is done, <b>a live hostname or public IP in a <c>.cs</c> file is
/// caught by nothing</b> — which is exactly how the live vendor endpoint this packet removed
/// survived four previous passes. <see cref="Sweep_CorpusIsTheStatedComplement"/> pins both
/// corpora so neither can quietly narrow.</para>
///
/// <para><b>Nothing is reimplemented.</b> The five operational-data detectors are
/// <see cref="DocsOperationalDataGuardTests.ViolationClasses(string, string)"/> called directly,
/// which in turn shares <c>IsPlaceholderDomain</c> / <c>IsStandardsHost</c> with
/// <see cref="FixtureDeIdentificationGuardTests"/> and the mail rule with
/// <see cref="SourcePersonalDataGuardTests"/>. One vocabulary, one place to change it: this guard
/// cannot drift from its siblings on what "reserved" means, because it holds no opinion of its
/// own. That is the point of extending the family rather than forking a fifth implementation.</para>
///
/// <para>What is NEW here is the sixth class, and it is the one this packet's defect actually
/// needed: <b>a catalog-source protocol discriminator drawn from a closed vocabulary</b>. That
/// value is written to a database column and is the key a vendor fetcher is resolved by, so
/// naming it after the distributor it fetches from publishes a trading relationship in a field
/// that no name-shaped rule will ever look at — and it cannot be quietly renamed later, because
/// production rows already carry it. See the class doc on <c>SupplierCatalogSource.Protocol</c>.</para>
///
/// <para>Failures name file, line and CLASS, never the value. CI logs on a public repository are
/// public, and a guard that prints the leak in order to prove the leak has not removed it.</para>
/// </summary>
public class TrackedSourceOperationalDataGuardTests
{
    // ── Class 6: the catalog-source protocol discriminator ───────────────────────────────

    /// <summary>
    /// A member or local named <c>protocol</c> in an assignment or comparison position, followed
    /// by string literals. This is the same format-delimited move <see cref="CxmlIdentityGuardTests"/>
    /// makes: the question "is this value real?" is undecidable, but "may this FIELD hold this
    /// value?" is decided by the codebase's own API. The scan stops at the first <c>,</c>,
    /// <c>;</c> or comment so a neighbouring property in the same object initialiser is not
    /// mistaken for a protocol.
    /// </summary>
    private static readonly Regex ProtocolAssignment = new(
        @"\bprotocol\b\s*(?:==|=>|=|\bis\b(?:\s+not)?)\s*(?<operand>[^;,]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StringLiteral = new("\"(?<v>[^\"]*)\"", RegexOptions.Compiled);

    /// <summary>
    /// Every value the <c>Protocol</c> discriminator may hold. A transport, or the auth MECHANISM
    /// a dedicated vendor fetcher implements — never a vendor's name.
    ///
    /// <para><b>Allow-list, never deny-list</b>, for #179's reason, which bites hardest on exactly
    /// this field: a committed list of real distributor names, on a public repository, written in
    /// order to prove that no real distributor name is committed, republishes what it suppresses
    /// and would outlive the history rewrite this scrub exists to unblock.</para>
    ///
    /// <para>Adding a word here is a deliberate, reviewable edit — which is the moment somebody
    /// gets to ask "is that a mechanism, or is that a customer?" If the answer is customer, the
    /// answer is also a database migration, so the question is worth stopping for.</para>
    /// </summary>
    private static readonly HashSet<string> ApprovedProtocolTokens = new(StringComparer.Ordinal)
    {
        // File and network transports.
        "sftp", "ftp", "ftps", "http", "https", "smtp", "imap", "s3", "as2",
        // Delivery channels and dispatcher kinds that occupy the same field on sibling entities.
        "email", "webhook", "api", "none", "silent",
        // Auth mechanisms that earn a dedicated vendor fetcher.
        "aes2fa",
        // TLS/transport version tokens, which share the word in an unrelated sense.
        "tls", "ssl",
    };

    /// <summary>
    /// Protocol-shaped values on one line. Internal so the anti-vacuity floor counts what the
    /// DETECTOR extracted rather than what a second, possibly-broken copy of the regex would.
    ///
    /// <para><b>Value position only.</b> After the operator, the operand must begin with a string
    /// literal — optionally behind opening parentheses, which is the <c>is not ("a" or "b")</c>
    /// pattern-match form. If any other token comes first the literal is an ARGUMENT, not the
    /// discriminator, and reading it as one is a category error. That single rule removes both
    /// false-positive shapes the real corpus produced: EF's
    /// <c>protocol = table.Column&lt;string&gt;(type: "text", …)</c>, where <c>"text"</c> is a
    /// column type; and — usefully — C# source that QUOTES C# source, because an escaped
    /// <c>\"</c> does not begin a literal. That is why this guard's own controls do not need the
    /// split-across-a-<c>+</c> treatment #184 and #185 had to give theirs, and
    /// <see cref="Detector_IsNotTrippedByItsOwnControlsInSource"/> asserts it rather than assuming
    /// it — this file is inside this guard's own corpus.</para>
    /// </summary>
    internal static IReadOnlyList<string> ProtocolLiteralsIn(string line)
    {
        var found = new List<string>();

        foreach (Match m in ProtocolAssignment.Matches(line))
        {
            var operand = m.Groups["operand"].Value;

            var comment = operand.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
            {
                operand = operand[..comment];
            }

            var head = operand.TrimStart();
            while (head.StartsWith('('))
            {
                head = head[1..].TrimStart();
            }

            if (!head.StartsWith('"'))
            {
                continue;
            }

            foreach (Match lit in StringLiteral.Matches(head))
            {
                found.Add(lit.Groups["v"].Value);
            }
        }

        return found;
    }

    /// <summary>
    /// Every violation class on one line: the five shared operational-data classes plus the
    /// protocol vocabulary. Never returns the offending value.
    /// </summary>
    internal static IReadOnlyList<string> ViolationClasses(string line, string relativePath = "")
    {
        var classes = new List<string>(
            DocsOperationalDataGuardTests.ViolationClasses(line, relativePath));

        if (ProtocolLiteralsIn(line).Any(v => v.Length > 0 && !ApprovedProtocolTokens.Contains(v)))
        {
            classes.Add("catalog-source protocol discriminator is not drawn from the approved vocabulary");
        }

        return classes;
    }


    // ── Class 7: the manufacturer / brand field ──────────────────────────────────────────

    /// <summary>
    /// A field whose NAME says it holds a manufacturer or brand, in assignment position, followed
    /// by string literals. Class 6's move applied to a second field: "is this brand real?" is
    /// undecidable free text, but "may this field hold a value that is not a known placeholder?"
    /// is decided by a committed vocabulary.
    ///
    /// <para>The field names are the closed set this codebase actually uses for the concept —
    /// the canonical entity property, the DTO/record parameter, the two catalog-format element
    /// names, and the AI brand hint. A new name for the same concept is a deliberate edit, and
    /// adding it here is part of that edit.</para>
    ///
    /// <para><b>Value position only</b>, exactly as class 6: after the operator the operand must
    /// begin with a string literal. That is what lets this file hold its own positive controls —
    /// C# source quoting C# source escapes the inner quote, and <c>\"</c> does not open a literal.
    /// Asserted by <see cref="Detector_IsNotTrippedByItsOwnControlsInSource"/>.</para>
    /// </summary>
    private static readonly Regex BrandAssignment = new(
        @"\b(?:ManufacturerName|Manufacturer|MANUFACTURER|brandHint|Brand)\b\s*(?:==|=>|=|:)\s*(?<operand>[^;,]*)",
        RegexOptions.Compiled);

    /// <summary>An XML element or JSON key naming the brand, for the fixture corpus, where the
    /// value is delimited by the markup rather than by an operator.</summary>
    private static readonly Regex BrandElement = new(
        @"<(?<tag>ManufacturerName|Manufacturer|MANUFACTURER|Brand)>(?<v>[^<]*)</\k<tag>>"
        + @"|""(?:Manufacturer|manufacturer|manufacturerName|ManufacturerName|brand|Brand)""\s*:\s*""(?<j>[^""]*)""",
        RegexOptions.Compiled);

    /// <summary>
    /// Every brand a manufacturer field is allowed to name. Each entry is a canonical
    /// FICTITIOUS-company name, so a reader can tell at a glance that it is a placeholder rather
    /// than an unfamiliar real vendor — which is the property that makes this list safe to commit.
    ///
    /// <para><b>Allow-list, never deny-list</b>, for #179's reason. The inverse — a committed list
    /// of the real manufacturer names this packet removed — would republish on a public repository
    /// exactly what it was written to suppress, and would outlive the history rewrite.</para>
    ///
    /// <para>Adding a name here is the moment to ask "is this invented, or is this somebody we
    /// actually buy from?" A real brand beside a real part number is what the founder ruled out,
    /// so that question is worth stopping for.</para>
    /// </summary>
    private static readonly HashSet<string> ApprovedBrandPlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Adatum", "Adventure", "Adventureworks", "Alpine", "Bellows", "Bestfor", "Blueyonder",
        "Bramwell", "Coho", "Coho Inc.", "Corvello", "Fabrikam", "Fincher", "Fourthcoffee",
        "Halden", "Humongous", "Lamna", "Lamna Solution", "Litware", "Lucerne", "Margie",
        "Munson", "Munson Digital", "Nod", "Proseware", "Relecloud", "Southridge", "Tailspin",
        "Trey", "VanArsdel", "Wideworld", "Wingtip", "Woodgrove",
        // Placeholder vocabulary already established by earlier passes in this family.
        "Acme", "Contoso", "Exemplar", "Northwind", "Widget",
    };

    /// <summary>
    /// Brand-shaped values on one line. Internal so the anti-vacuity floor counts what the
    /// DETECTOR extracted, not what a second copy of the regex would.
    /// </summary>
    internal static IReadOnlyList<string> BrandLiteralsIn(string line)
    {
        var found = new List<string>();

        foreach (Match m in BrandAssignment.Matches(line))
        {
            var operand = m.Groups["operand"].Value;

            var comment = operand.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
            {
                operand = operand[..comment];
            }

            var head = operand.TrimStart();
            while (head.StartsWith('('))
            {
                head = head[1..].TrimStart();
            }

            if (!head.StartsWith('"'))
            {
                continue;
            }

            foreach (Match lit in StringLiteral.Matches(head))
            {
                found.Add(lit.Groups["v"].Value);
            }
        }

        foreach (Match m in BrandElement.Matches(line))
        {
            var v = m.Groups["v"].Success && m.Groups["v"].Value.Length > 0
                ? m.Groups["v"].Value
                : m.Groups["j"].Value;

            if (v.Length > 0)
            {
                found.Add(v);
            }
        }

        return found;
    }

    /// <summary>
    /// The brand name inside a value, stripped of the corporate-form and division suffixes a
    /// catalog feed spells the same brand a dozen ways with. The normalisation exists because
    /// <c>SupplierProduct.ManufacturerName</c>'s own doc comment says feeds do exactly that, so a
    /// guard that demanded an exact match would fire on a legitimate placeholder written with a
    /// suffix and be suppressed within a week.
    /// </summary>
    private static bool IsApprovedBrand(string value)
    {
        var v = value.Trim();
        if (v.Length == 0 || ApprovedBrandPlaceholders.Contains(v))
        {
            return true;
        }

        foreach (var suffix in new[] { " ADC", " S.p.A.", " Inc.", " GmbH", " AS", " OY", " Solution" })
        {
            if (v.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && ApprovedBrandPlaceholders.Contains(v[..^suffix.Length].Trim()))
            {
                return true;
            }
        }

        // A placeholder followed by a model designation ("<brand> LTQ2500 handheld scanner").
        var firstWord = v.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return ApprovedBrandPlaceholders.Contains(firstWord);
    }

    /// <summary>Classes 6, 7 and 8 — what corpus B carries.</summary>
    internal static IReadOnlyList<string> ProtocolViolationClasses(string line)
    {
        var classes = new List<string>();

        if (ProtocolLiteralsIn(line).Any(v => v.Length > 0 && !ApprovedProtocolTokens.Contains(v)))
        {
            classes.Add("catalog-source protocol discriminator is not drawn from the approved vocabulary");
        }

        classes.AddRange(BrandAndPathClasses(line));

        return classes;
    }

    /// <summary>
    /// Classes 7 and 8, shared by corpus B and the fixture corpus.
    ///
    /// <para>Class 8 is <see cref="DocsOperationalDataGuardTests.LocalPathsIn"/> called directly,
    /// never reimplemented. It is the one identity shape a party-name allow-list structurally
    /// cannot see: an employer's name sits INSIDE a filesystem path, where every name rule is
    /// looking at name fields and every path rule is looking at paths without caring who is named
    /// in one. It needs no vocabulary at all, which is why it survives where brand detection
    /// cannot — and until now it ran over <c>docs/</c>, the fixtures and corpus A, but NOT over
    /// <c>.cs</c>, which is the largest corpus in the tree.</para>
    /// </summary>
    internal static IReadOnlyList<string> BrandAndPathClasses(string line)
    {
        var classes = new List<string>();

        if (BrandLiteralsIn(line).Any(v => !IsApprovedBrand(v)))
        {
            classes.Add("manufacturer/brand field names a value that is not an approved placeholder");
        }

        if (DocsOperationalDataGuardTests.LocalPathsIn(line).Count > 0)
        {
            classes.Add("absolute path into a local machine");
        }

        return classes;
    }

    // ── The guard ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TrackedNonCSharpSource_CarriesNoLiveOperationalData()
    {
        var scan = ScanCorpusA();

        scan.Violations.Should().BeEmpty(
            "a tracked file outside docs/ and outside the fixture corpus must not name a live "
            + "endpoint, address, credential or mail domain. State the CLASS, never the party — "
            + "this repository and its CI logs are public. If a host is genuinely a platform "
            + "rather than a counterparty, add it to the ONE shared vocabulary in "
            + "DocsOperationalDataGuardTests rather than starting a second one here. Offending "
            + "file:line and class (values withheld):\n"
            + string.Join("\n", scan.Violations));
    }

    [Fact]
    public void TrackedCSharp_NamesNoVendorInAProtocolDiscriminator()
    {
        var scan = ScanCorpusB();

        scan.Violations.Should().BeEmpty(
            "a protocol discriminator must name a TRANSPORT or an auth MECHANISM, never the vendor "
            + "it talks to. This value is written to a database column and is the key a vendor "
            + "fetcher is resolved by, so it is also the one string here that cannot be fixed by a "
            + "later find-and-replace: production rows already carry it, and changing it needs a "
            + "data migration. Add the token to ApprovedProtocolTokens only if it genuinely "
            + "describes a mechanism. Offending file:line and class (values withheld):\n"
            + string.Join("\n", scan.Violations));
    }

    [Fact]
    public void TrackedFixtures_NameNoRealManufacturerInABrandField()
    {
        var scan = ScanCorpusC();

        scan.Violations.Should().BeEmpty(
            "a catalog fixture's manufacturer field must name an invented brand. A real brand "
            + "beside a real manufacturer part number is the pairing the founder ruled out: the "
            + "brand alone is public commerce data, but the pair says what actually moved through "
            + "this system. Add the name to ApprovedBrandPlaceholders only if it is invented. "
            + "Offending file:line and class (values withheld):\n"
            + string.Join("\n", scan.Violations));
    }

    // ── Anti-vacuity ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A file count is not a detection floor: the corpus can be enumerated perfectly and every
    /// byte still go unexamined. Each floor below catches a different way the sweep goes hollow,
    /// and the last ones are those that matter — they count tokens the DETECTOR's own helpers
    /// actually extracted, so a regex broken into matching nothing drives them to zero and fails
    /// loudly instead of reading megabytes, examining none of it, and turning green.
    ///
    /// <para>Only these structural floors carry a number. The six violation CLASSES are expected
    /// to sit at zero in a clean tree, so a floor on any of them would assert that the leak still
    /// exists. That asymmetry is deliberate, and is stated so it is not read as an oversight.</para>
    /// </summary>
    [Fact]
    public void Sweep_ActuallyExaminesTheCorpus()
    {
        var a = ScanCorpusA();
        var b = ScanCorpusB();

        a.ScannedFiles.Should().BeGreaterThanOrEqualTo(
            40,
            $"only {a.ScannedFiles} non-C# files were swept from {a.RepoRoot} — corpus A's query "
            + "is broken, so its guard proves nothing");

        a.DistinctExtensions.Should().BeGreaterThanOrEqualTo(
            8,
            $"only {a.DistinctExtensions} distinct file extensions were swept — corpus A has "
            + "collapsed towards one file type, which is the exact failure mode this guard exists "
            + "to close");

        a.DistinctTopLevelDirectories.Should().BeGreaterThanOrEqualTo(
            6,
            $"only {a.DistinctTopLevelDirectories} top-level directories were swept — corpus A "
            + "has narrowed to one project");

        (a.ScannedHosts + a.ScannedAddresses).Should().BeGreaterThanOrEqualTo(
            80,
            $"only {a.ScannedHosts} hosts and {a.ScannedAddresses} addresses were extracted and "
            + "checked — the shared host/address detectors matched almost nothing, so corpus A "
            + "was read without being examined");

        b.ScannedFiles.Should().BeGreaterThanOrEqualTo(
            900,
            $"only {b.ScannedFiles} C# files were swept — corpus B's query is broken");

        b.ScannedBytes.Should().BeGreaterThan(
            1_000_000,
            $"only {b.ScannedBytes} bytes of C# were read — the files were listed but not opened");

        b.ScannedProtocolLiterals.Should().BeGreaterThanOrEqualTo(
            40,
            $"only {b.ScannedProtocolLiterals} protocol literals were extracted and checked — the "
            + "protocol arm matched nothing, so class 6 proves nothing");

        var c = ScanCorpusC();

        c.ScannedFiles.Should().BeGreaterThanOrEqualTo(
            20,
            $"only {c.ScannedFiles} fixture files were swept — corpus C's query is broken");

        c.ScannedBytes.Should().BeGreaterThan(
            50_000,
            $"only {c.ScannedBytes} bytes of fixture were read — the files were listed but not opened");

        // The floor that actually protects class 7: count what the brand detector EXTRACTED. A
        // regex edited into matching nothing drives this to zero and fails loudly, instead of
        // reading every fixture, examining no brand, and turning green.
        // 21 today (9 in C#, 12 in fixtures). The floor sits below that rather than at it: this
        // counts literals in ASSIGNMENT position only, so ordinary test churn moves it by ones and
        // twos, and a floor pinned to the exact number would fail on a clean refactor and be
        // raised without thought. Breaking the regex drives it to zero, which is what it is for.
        (b.ScannedBrandLiterals + c.ScannedBrandLiterals).Should().BeGreaterThanOrEqualTo(
            15,
            $"only {b.ScannedBrandLiterals} brand literals in C# and {c.ScannedBrandLiterals} in "
            + "fixtures were extracted and checked — the brand arm matched almost nothing, so "
            + "class 7 proves nothing");

        // Class 8 gets NO floor, and the asymmetry is the same one stated above: every local path
        // this detector extracts IS a violation, so a floor on it would assert that the leak still
        // exists. Its anti-vacuity comes from a positive control instead —
        // Detector_RefusesAbsoluteLocalPathsAndUnapprovedBrands.
        b.ScannedLocalPaths.Should().Be(
            b.Violations.Count(v => v.EndsWith("absolute path into a local machine", StringComparison.Ordinal)),
            "every extracted local path must be reported: if these diverge, paths are being "
            + "counted but not classified");
    }

    /// <summary>
    /// Pins both corpus definitions. Narrowing corpus A to an extension list is how this guard
    /// would quietly stop covering the next file type somebody adds; widening it into
    /// <c>docs/</c> or the fixtures would turn a sibling's deliberately non-reserved controls into
    /// violations, whose natural "fix" is a path exclusion — the hole #184 refused to open.
    /// </summary>
    [Fact]
    public void Sweep_CorpusIsTheStatedComplement()
    {
        var a = ScanCorpusA();

        a.Files.Should().NotContain(
            f => f.StartsWith("docs/", StringComparison.Ordinal),
            "docs/ belongs to DocsOperationalDataGuardTests");

        a.Files.Should().NotContain(
            f => FixtureCorpus.IsUnderFixturesDirectory(f),
            "the fixture corpus belongs to FixtureDeIdentificationGuardTests");

        a.Files.Should().NotContain(
            f => f.EndsWith(".cs", StringComparison.Ordinal),
            "corpus A is the NON-C# complement; the .cs limit is stated in the class doc and must "
            + "not be silently changed here");

        a.Files.Should().Contain(
            f => !f.Contains('/', StringComparison.Ordinal),
            "repository-root files must be swept — STATUS.md sat one level above docs/ and was the "
            + "densest concentration of live operational data left in the tree");

        a.Files.Should().Contain(
            f => f.StartsWith("tools/", StringComparison.Ordinal),
            "tools/ must be swept — #185 found a removed identity there, outside every .cs sweep");

        a.Files.Should().Contain(
            f => f.EndsWith(".mjs", StringComparison.Ordinal),
            "the .mjs generator must be swept — it is where #185's escaped identity lived");

        ScanCorpusB().Files.Should().OnlyContain(
            f => f.EndsWith(".cs", StringComparison.Ordinal),
            "corpus B is every tracked C# file, including production, migrations and all three "
            + "test projects");
    }

    // ── Controls ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Positive controls. The sweep going green means something only if the detector can still go
    /// red. Every value below is invented and belongs to nobody, which is the point: the detector
    /// needs no knowledge of any real party to refuse them.
    /// </summary>
    public static TheoryData<string> PositiveControls() => new()
    {
        "feed pulled from https://catalog.invented-distributor.net/v2",
        "sftp host edi.nowhere-invented.net",
        "probe resolved to 93.184.99.7 during the audit",
        // These two carry an address-shaped token on a deliberately NON-reserved domain, which is
        // what makes them positive controls — and also what makes SourcePersonalDataGuardTests
        // (whose corpus is every tracked .cs, including this file) fire on them. Split across a
        // `+` exactly as #184 and #185 split theirs: the compiler folds each to the identical
        // constant, so the control tests precisely what it always did, while no single SOURCE
        // LINE here contains a matching address. DO NOT JOIN THEM BACK — the sibling guard turns
        // red the moment you do, and these tests still passing is itself the proof that the split
        // changed no value.
        "https://user:pw@" + "files.invented-vendor.net/drop",
        "ops@" + "invented-counterparty.com",
        @"worktree at C:" + @"\Users\someoperator\source\repos\ProcuLink",
        "source.Protocol = \"inventedvendor\";",
        "if (protocol is not (\"sftp\" or \"madeupco\"))",
        "s.Protocol == \"nowhereplc\"",
    };

    [Theory]
    [MemberData(nameof(PositiveControls))]
    public void Detector_RefusesLiveOperationalData(string line)
        => ViolationClasses(line).Should().NotBeEmpty();

    /// <summary>
    /// Negative controls. A detector that refuses everything is as useless as one that refuses
    /// nothing — it gets suppressed within a week. Most of these are drawn from lines that really
    /// occur in this corpus, so a future narrowing of the allow-lists fails HERE rather than
    /// silently re-flagging hundreds of clean lines.
    /// </summary>
    [Theory]
    // Reserved-for-documentation and placeholder hosts.
    [InlineData("var url = \"https://vendor-api.example/api\";")]
    [InlineData("supplier endpoint https://nordmark.example/po")]
    [InlineData("buyer@heinrich.example.com")]
    [InlineData("host: sftp.example.invalid")]
    // Standards and platform hosts, decided by the ONE shared vocabulary.
    [InlineData("xmlns=\"http://www.w3.org/2001/XMLSchema\"")]
    [InlineData("see https://github.com/dimnovare/ProcuLink/pull/185")]
    [InlineData("Authority: https://golden-alpaca-43.clerk.accounts.dev")]
    [InlineData("posts to https://webhook.site/9a1f85b7-0000-0000-0000-000000000000")]
    // Own domains, including subdomains.
    [InlineData("inbound address orders@inbound.proculink.eu")]
    // RFC-reserved addresses: loopback, RFC 1918, link-local, TEST-NET.
    [InlineData("Host=127.0.0.1;Port=5435;Database=proculink")]
    [InlineData("SSRF probe against 169.254.169.254 must be refused")]
    [InlineData("private range 10.1.2.3 and 192.168.0.1")]
    [InlineData("documentation range 198.51.100.7 and 203.0.113.9")]
    // Every protocol token this tree actually uses, in every position it uses them.
    [InlineData("source.Protocol = \"sftp\";")]
    [InlineData("s.Protocol = \"aes2fa\";")]
    [InlineData("if (protocol is not (\"sftp\" or \"ftp\" or \"ftps\" or \"http\" or \"https\" or \"aes2fa\"))")]
    [InlineData("var isHttp = protocol is \"http\" or \"https\";")]
    [InlineData("cfg.Protocol == \"email\"")]
    [InlineData("public string Protocol => \"silent\";")]
    [InlineData("Protocol = source.Protocol,")]
    // A literal in ARGUMENT position after the word, not in value position.
    [InlineData("protocol = table.Column<string>(type: \"text\", nullable: false),")]
    [InlineData("// the delivery protocol is decided by the connection revision")]
    // Shapes a sloppier detector fires on.
    [InlineData("assembly version 8.0.11 and file.cs:254")]
    [InlineData("var generic = new Dictionary<string, List<int>>();")]
    [InlineData("SUP-2@5.5x2")]
    public void Detector_PermitsACleanLine(string line)
        => ViolationClasses(line).Should().BeEmpty();

    /// <summary>
    /// This file is inside corpus B, so its own controls are subject to its own rule. They survive
    /// without the split-across-a-<c>+</c> treatment #184 and #185 needed, because C# source that
    /// quotes C# source escapes the inner quote, and an escaped <c>\"</c> does not open a literal
    /// in value position. That is a property of the detector, not luck, so it is asserted here —
    /// #185's mutation check found seven violations inside its own guard file the moment that file
    /// became tracked, and this is the assertion that would have caught it first.
    /// </summary>
    [Fact]
    public void Detector_IsNotTrippedByItsOwnControlsInSource()
    {
        ProtocolLiteralsIn("    [InlineData(\"source.Protocol = \\\"sftp\\\";\")]")
            .Should().BeEmpty("an escaped quote does not open a literal in value position");

        ProtocolLiteralsIn("        \"s.Protocol == \\\"nowhereplc\\\"\",")
            .Should().BeEmpty();

        // …while the same line, once the compiler has unescaped it, is still decided.
        ProtocolLiteralsIn("source.Protocol = \"sftp\";")
            .Should().ContainSingle().Which.Should().Be("sftp");

        // Class 7 inherits the property, so its controls need no split either.
        BrandLiteralsIn("        BrandAndPathClasses(\"ManufacturerName = \\\"Notarealplaceholder\\\",\")")
            .Should().BeEmpty("an escaped quote does not open a literal in value position");

        BrandLiteralsIn("ManufacturerName = \"Litware\",")
            .Should().ContainSingle().Which.Should().Be("Litware");
    }

    /// <summary>
    /// Positive controls for classes 7 and 8 — the anti-vacuity for class 8, which deliberately
    /// carries no numeric floor because every path it extracts is a violation.
    ///
    /// <para><b>The path controls are SPLIT ACROSS A <c>+</c> on purpose. Do not join them back.</b>
    /// This file is inside corpus B, and class 8 needs no vocabulary at all — a drive letter
    /// followed by <c>\Users\</c> is a format fact — so a control written as a single literal
    /// would be a real violation of this guard, inside this guard. #184 established the split for
    /// exactly this case: the compiler folds the halves into the identical constant, so the
    /// control tests precisely what it always did while no single source LINE carries a matching
    /// path. The class-7 controls need no such treatment, because an escaped <c>\"</c> does not
    /// open a literal in value position.</para>
    /// </summary>
    [Fact]
    public void Detector_RefusesAbsoluteLocalPathsAndUnapprovedBrands()
    {
        const string windowsPath = @"C:" + @"\Users\someone\source\repos\thing";
        BrandAndPathClasses($"**Worktree:** `{windowsPath}`")
            .Should().Contain("absolute path into a local machine");

        const string macPath = "/Users" + "/someone/work";
        BrandAndPathClasses($"see {macPath}/notes.md")
            .Should().Contain("absolute path into a local machine");

        // A brand nobody has approved, in each shape the corpora actually use.
        const string unapproved = "Notarealplaceholder";
        const string expected = "manufacturer/brand field names a value that is not an approved placeholder";

        // C# assignment: the escaped \" keeps this source line out of value position, so the
        // control is safe to write inline.
        BrandAndPathClasses("ManufacturerName = \"Notarealplaceholder\",")
            .Should().Contain(expected);

        // The MARKUP arms get no such protection — an XML element is not quote-delimited, so the
        // tag name is INTERPOLATED rather than written literally. Spelled out because it is the
        // one asymmetry between class 6's controls and class 7's, and the obvious "tidy-up" is to
        // inline it, which turns this control into a violation of its own guard.
        const string tag = "ManufacturerName";
        BrandAndPathClasses($"<{tag}>{unapproved}</{tag}>").Should().Contain(expected);

        BrandAndPathClasses($"      \"Manufacturer\": \"{unapproved}\"").Should().Contain(expected);
    }

    /// <summary>
    /// Negative controls, drawn from shapes that really occur in these corpora — so a future
    /// narrowing of the allow-list fails HERE rather than silently re-flagging clean lines.
    /// </summary>
    [Theory]
    // Approved placeholders, bare and with the corporate-form suffixes a feed spells them with.
    [InlineData("ManufacturerName = \"Litware\",")]
    [InlineData("<ManufacturerName>Coho Inc.</ManufacturerName>")]
    [InlineData("        \"Manufacturer\": \"Relecloud\"")]
    [InlineData("same brand a dozen ways (\"Litware\" / \"LITWARE ADC\" / \"Litware S.p.A.\")")]
    // A placeholder followed by a model designation.
    [InlineData("Name: \"Proseware PW421 label printer\", ManufacturerName: \"Proseware\"),")]
    // An absent brand is legitimate and must stay legitimate.
    [InlineData("ManufacturerName = null,")]
    [InlineData("NullIfEmpty(manufacturerName)));")]
    // Not a value position: the field is being READ, not assigned a literal.
    [InlineData("Assert.Equal(expected, line.ManufacturerName);")]
    [InlineData("var brand = Clean(draft.ManufacturerName);")]
    // A relative path, a registry key and a non-profile absolute path name nobody.
    [InlineData("docs/superpowers/plans/2026-08-13-thing.md")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    public void Detector_PermitsACleanBrandOrPathLine(string line)
        => BrandAndPathClasses(line).Should().BeEmpty();

    /// <summary>
    /// The redaction is load-bearing: a failure message that echoed the value would leak it into a
    /// public CI log, which is the thing this guard exists to prevent. Asserted, not assumed.
    /// </summary>
    [Fact]
    public void Detector_WithholdsTheOffendingValueFromTheViolationClass()
    {
        const string vendor = "inventedvendorname";
        var classes = ProtocolViolationClasses("s.Protocol = \"" + vendor + "\";");

        classes.Should().NotBeEmpty();
        classes.Should().NotContain(c => c.Contains(vendor, StringComparison.OrdinalIgnoreCase),
            "the violation class must never echo the offending value");

        const string host = "catalog.invented-distributor-two.net";
        var hostClasses = ViolationClasses("pull from https://" + host + "/v2");

        hostClasses.Should().NotBeEmpty();
        hostClasses.Should().NotContain(
            c => c.Contains("invented-distributor-two", StringComparison.OrdinalIgnoreCase));
    }

    // ── Corpora ──────────────────────────────────────────────────────────────────────────

    private sealed record CorpusScan(
        string RepoRoot,
        IReadOnlyList<string> Files,
        int ScannedFiles,
        long ScannedBytes,
        int DistinctExtensions,
        int DistinctTopLevelDirectories,
        int ScannedHosts,
        int ScannedAddresses,
        int ScannedProtocolLiterals,
        int ScannedBrandLiterals,
        int ScannedLocalPaths,
        IReadOnlyList<string> Violations);

    private static CorpusScan ScanCorpusA() => Scan(
        f => !f.StartsWith("docs/", StringComparison.Ordinal)
             && !f.EndsWith(".cs", StringComparison.Ordinal)
             && !FixtureCorpus.IsUnderFixturesDirectory(f),
        (line, rel) => DocsOperationalDataGuardTests.ViolationClasses(line, rel));

    private static CorpusScan ScanCorpusB() => Scan(
        f => f.EndsWith(".cs", StringComparison.Ordinal),
        (line, _) => ProtocolViolationClasses(line));

    /// <summary>
    /// Corpus C — the fixture corpus, carrying class 7 ONLY.
    ///
    /// <para>It deliberately overlaps <see cref="FixtureDeIdentificationGuardTests"/>'s corpus
    /// rather than avoiding it, because the two ask different questions of the same files: that
    /// guard asks whether a line names a real person, host or account, and has no opinion about
    /// manufacturers. The brand class is a leak class it structurally cannot see, and the fixtures
    /// are where brands actually live — a catalog feed IS a list of manufacturers.</para>
    ///
    /// <para>Class 8 is NOT applied here: the fixture guard already carries its own
    /// absolute-local-path rule over exactly these files, and adding a second would mean two
    /// guards reporting one line.</para>
    /// </summary>
    private static CorpusScan ScanCorpusC() => Scan(
        FixtureCorpus.IsUnderFixturesDirectory,
        (line, _) => BrandLiteralsIn(line).Any(v => !IsApprovedBrand(v))
            ? new[] { "manufacturer/brand field names a value that is not an approved placeholder" }
            : Array.Empty<string>());

    private static CorpusScan Scan(
        Func<string, bool> include,
        Func<string, string, IReadOnlyList<string>> detect)
    {
        var repoRoot = FixtureCorpus.FindRepoRoot();
        var files = FixtureCorpus.TrackedFiles(repoRoot, null).Where(include).ToList();

        var violations = new List<string>();
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var topLevel = new HashSet<string>(StringComparer.Ordinal);
        long bytes = 0;
        var hosts = 0;
        var addresses = 0;
        var protocolLiterals = 0;
        var brandLiterals = 0;
        var localPaths = 0;
        var scanned = 0;

        foreach (var relative in files)
        {
            var full = Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                continue;
            }

            scanned++;
            extensions.Add(Path.GetExtension(relative) is { Length: > 0 } ext ? ext : "(none)");
            var slash = relative.IndexOf('/', StringComparison.Ordinal);
            topLevel.Add(slash < 0 ? "(root)" : relative[..slash]);

            var lineNumber = 0;
            foreach (var line in File.ReadAllLines(full))
            {
                lineNumber++;
                bytes += line.Length;

                hosts += DocsOperationalDataGuardTests.HostsIn(line).Count;
                addresses += DocsOperationalDataGuardTests.AddressesIn(line).Count;
                protocolLiterals += ProtocolLiteralsIn(line).Count;
                brandLiterals += BrandLiteralsIn(line).Count;
                localPaths += DocsOperationalDataGuardTests.LocalPathsIn(line).Count;

                foreach (var cls in detect(line, relative))
                {
                    violations.Add($"  {relative}:{lineNumber}: {cls}");
                }
            }
        }

        return new CorpusScan(
            repoRoot, files, scanned, bytes, extensions.Count, topLevel.Count,
            hosts, addresses, protocolLiterals, brandLiterals, localPaths, violations);
    }
}
