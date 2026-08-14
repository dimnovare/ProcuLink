using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using ProcuLink.Core.Services.Detection;
using ProcuLink.Infrastructure.Services.Detection;

namespace ProcuLink.Infrastructure.Tests.Services.Detection;

/// <summary>
/// The tripwire for "a deterministic result reported as a probability", third instance.
///
/// <para><b>The history this exists to stop repeating.</b> The same defect has now been fixed three
/// times in three places: the mapping-suggestions endpoint stamped <c>0.95</c> on a remembered
/// deterministic mapping; <c>OrderIngestionService</c> stamped <c>0.95f</c> on an exact catalog hit
/// and on a literal part-number echo; and this detector stamped <c>0.95</c> on <c>%PDF-</c> being the
/// leading five bytes of a file. Each was found by hand, one at a time, after shipping. What they
/// share is not a component — it is that a field typed to always hold a number left the honest
/// answer ("nothing scored this") unsayable, so every producer invented one.</para>
///
/// <para><b>Why a behavioural walk is not enough on its own.</b> A corpus can only exercise arms it
/// knows about. The fifteenth detection arm, written six months from now with a fabricated
/// <c>0.9</c> on a byte comparison, would sail past <see cref="EveryDetectionArm_ScoresOnlyWhenItGuesses"/>
/// because no corpus entry reaches it. So the second half of this class reads the detector's SOURCE
/// and requires every construction site to declare its basis and to pass <c>null</c> where that
/// basis is not a guess — which fails on the arm at the moment it is written, before it has a test.</para>
///
/// <para><b>The invariant, both directions.</b> <see cref="FormatDetectionBasis.Heuristic"/> carries a
/// number; <see cref="FormatDetectionBasis.MagicBytes"/> and <see cref="FormatDetectionBasis.Undetermined"/>
/// never do. The reverse direction matters as much as the forward one: a heuristic arm that quietly
/// starts returning <c>null</c> is not "more honest", it is a scored guess that stopped saying how
/// much it is guessing, and the wizard renders that as 0%.</para>
/// </summary>
public class FormatDetectorBasisInvariantTests
{
    private readonly FormatDetectorService _sut = new();

    // ── Half 1: the behavioural walk ────────────────────────────────────────────────────────────

    /// <summary>
    /// One corpus entry per reachable detection arm. <c>ExpectedBasis</c> is stated per case rather
    /// than derived, because the classification is the actual claim being made — deriving it from the
    /// output would assert nothing.
    /// </summary>
    public static TheoryData<string, byte[], string?, string, string> Corpus() => new()
    {
        // name                    bytes                                          fileName        format     basis
        { "pdf-magic",             Ascii("%PDF-1.7\n1 0 obj <<>> endobj"),         "po.pdf",       "pdf",     FormatDetectionBasis.MagicBytes },
        { "x12-isa",               Ascii("ISA*00*          *00*          *ZZ*B*ZZ*S*240115*1200*U*00401*1*0*P*>~BEG*00*NE*PO-1*200*20240115~"), "po.x12", "x12", FormatDetectionBasis.MagicBytes },
        { "edifact-una",           Ascii("UNA:+.?'UNB+UNOC:3+B+S+240115:1200+1'BGM+220+PO-2+9'"), "po.edi", "edifact", FormatDetectionBasis.MagicBytes },
        { "edifact-unb-only",      Ascii("UNB+UNOC:3+B+S+240115:1200+1'BGM+220+PO-3+9'"),         "po.edi", "edifact", FormatDetectionBasis.Heuristic },
        { "zip-named-xlsx",        new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 },             "book.xlsx", "xlsx", FormatDetectionBasis.Heuristic },
        { "zip-not-named-xlsx",    new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 },             "mystery.zip", "xlsx", FormatDetectionBasis.Heuristic },
        { "cxml-namespace",        Utf8("<?xml version=\"1.0\"?><cXML payloadID=\"a\"><Request><OrderRequest/></Request></cXML>"), "o.xml", "cxml", FormatDetectionBasis.Heuristic },
        { "ubl-namespace",         Utf8("<?xml version=\"1.0\"?><Order xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:Order-2\"><cbc:ID>P</cbc:ID></Order>"), "o.xml", "ubl", FormatDetectionBasis.Heuristic },
        { "xml-unknown-dialect",   Utf8("<?xml version=\"1.0\"?><SomeVendorEnvelope><Body/></SomeVendorEnvelope>"), "o.xml", "xml", FormatDetectionBasis.Heuristic },
        { "csv-separators",        Utf8("po,sku,qty,price\nP-1,A,1,2.00\nP-1,B,3,4.00\n"),        "o.csv",  "csv",     FormatDetectionBasis.Heuristic },
        { "unmatched-bytes",       new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0x11, 0x22 },       "junk.bin", "unknown", FormatDetectionBasis.Undetermined },
        { "empty-stream",          Array.Empty<byte>(),                                           "empty.bin", "unknown", FormatDetectionBasis.Undetermined },
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task EveryDetectionArm_ScoresOnlyWhenItGuesses(
        string caseName, byte[] bytes, string? fileName, string expectedFormat, string expectedBasis)
    {
        using var ms = new MemoryStream(bytes);

        var result = await _sut.DetectAsync(ms, fileName, CancellationToken.None);

        result.Format.Should().Be(expectedFormat, "corpus case '{0}'", caseName);
        result.Basis.Should().Be(expectedBasis, "corpus case '{0}'", caseName);

        if (expectedBasis == FormatDetectionBasis.Heuristic)
        {
            result.Confidence.Should().NotBeNull(
                "'{0}' really is a guess, and a guess that hides how much it is guessing renders as 0%", caseName);
            result.Confidence!.Value.Should().BeInRange(0.0, 1.0);
        }
        else
        {
            result.Confidence.Should().BeNull(
                "'{0}' is decided by a byte comparison or by nothing matching — neither produces a fraction", caseName);
        }
    }

    [Fact]
    public async Task TheCorpus_ReachesEveryBasis_AndMostOfTheArms()
    {
        // Anti-vacuity floor. A walk that silently stops reaching the interesting arms keeps passing;
        // this is what notices. It fails if a corpus entry is deleted, or if two entries collapse onto
        // the same arm because a detection predicate changed underneath them.
        var seen = new List<DetectedFormat>();
        foreach (var row in Corpus())
        {
            var bytes = (byte[])row[1];
            using var ms = new MemoryStream(bytes);
            seen.Add(await _sut.DetectAsync(ms, (string?)row[2], CancellationToken.None));
        }

        seen.Select(d => d.Basis).Distinct().Should().BeEquivalentTo(new[]
        {
            FormatDetectionBasis.MagicBytes, FormatDetectionBasis.Heuristic, FormatDetectionBasis.Undetermined,
        }, "all three kinds of answer must be exercised, or the invariant is only half-tested");

        seen.Select(d => d.Format).Distinct().Should().HaveCountGreaterOrEqualTo(8,
            "the corpus is meant to span the detector's format vocabulary, not one convenient arm");

        seen.Count(d => d.Basis == FormatDetectionBasis.MagicBytes).Should().BeGreaterOrEqualTo(3,
            "the deterministic arms are the ones this fix is about — PDF, X12 and EDIFACT-UNA");
    }

    // ── Half 2: the source scan, for arms no corpus reaches ─────────────────────────────────────

    /// <summary>
    /// Matches a whole <c>new DetectedFormat(...)</c> construction, across line breaks. The argument
    /// list never contains a <c>;</c>, so stopping at the first one is safe and keeps the pattern
    /// readable.
    /// </summary>
    private static readonly Regex ConstructionSite =
        new(@"new DetectedFormat\((?<args>[^;]*?)\);", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// The first two positional arguments: the format token and the confidence. The confidence must
    /// be a literal — <c>null</c> or a number — so this guard can read it. An arm that computes it
    /// into a local first is not wrong, but it must come back here and say so.
    /// </summary>
    private static readonly Regex FormatAndConfidence =
        new(@"^\s*""(?<format>[a-z0-9]+)""\s*,\s*(?<confidence>null|[0-9]+(\.[0-9]+)?)\s*,",
            RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex DeclaredBasis =
        new(@"Basis:\s*FormatDetectionBasis\.(?<basis>\w+)", RegexOptions.Singleline | RegexOptions.Compiled);

    [Fact]
    public void EveryConstructionSiteInTheDetector_DeclaresItsBasis_AndOnlyGuessesCarryNumbers()
    {
        var source = DetectorSource();
        var sites = ConstructionSite.Matches(source);

        sites.Should().HaveCountGreaterOrEqualTo(13,
            "the detector had 13 construction sites when this guard was written and the EDIFACT arm "
            + "was split into two; a sharp drop means the regex stopped matching, not that arms vanished");

        var offenders = new List<string>();
        var basisCounts = new Dictionary<string, int>();

        foreach (Match site in sites)
        {
            var args = site.Groups["args"].Value;
            var head = FormatAndConfidence.Match(args);
            var basis = DeclaredBasis.Match(args);

            if (!head.Success)
            {
                offenders.Add($"could not read the format/confidence literals from: {Compact(args)}");
                continue;
            }

            var format = head.Groups["format"].Value;

            if (!basis.Success)
            {
                offenders.Add($"'{format}' does not declare a Basis — say what kind of answer it is");
                continue;
            }

            var basisName = basis.Groups["basis"].Value;
            basisCounts[basisName] = basisCounts.GetValueOrDefault(basisName) + 1;

            var confidence = head.Groups["confidence"].Value;
            var isNull = confidence == "null";

            switch (basisName)
            {
                case nameof(FormatDetectionBasis.Heuristic) when isNull:
                    offenders.Add($"'{format}' is declared a heuristic but passes no score — "
                                  + "a guess that will not say how much it is guessing renders as 0%");
                    break;
                case nameof(FormatDetectionBasis.MagicBytes) when !isNull:
                case nameof(FormatDetectionBasis.Undetermined) when !isNull:
                    offenders.Add($"'{format}' is declared {basisName} but passes {confidence} — "
                                  + "this is exactly the defect: a fraction invented on the far side of a "
                                  + "byte comparison, which FingerprintBoost then does arithmetic on and "
                                  + "the upload wizard prints as a percentage");
                    break;
            }
        }

        offenders.Should().BeEmpty(
            "only FormatDetectionBasis.Heuristic may carry a number:\n  - {0}", string.Join("\n  - ", offenders));

        // Anti-vacuity for the scan itself: if the named-argument syntax ever changes, DeclaredBasis
        // stops matching and every site would silently be skipped by the `continue` above.
        basisCounts.Keys.Should().BeEquivalentTo(
            new[] { nameof(FormatDetectionBasis.MagicBytes), nameof(FormatDetectionBasis.Heuristic), nameof(FormatDetectionBasis.Undetermined) },
            "all three bases must be present in the source, or the scan is reading nothing");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static string Compact(string s) =>
        Regex.Replace(s, @"\s+", " ").Trim() is { Length: > 120 } long_ ? long_[..120] + "…" : Regex.Replace(s, @"\s+", " ").Trim();

    private static string DetectorSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcuLink.slnx")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the guard scans the detector's source, so it must find the repo root");

        var path = Path.Combine(dir!.FullName,
            "ProcuLink.Infrastructure", "Services", "Detection", "FormatDetectorService.cs");
        File.Exists(path).Should().BeTrue("the detector source must be readable at {0}", path);
        return File.ReadAllText(path);
    }
}
