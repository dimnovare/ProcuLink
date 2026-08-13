using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using FluentAssertions;
using ProcuLink.Transform.Conformance;

namespace ProcuLink.Transform.Tests.Conformance;

/// <summary>
/// Guards the VENDORED half of UBL schema validation: that the OASIS files are present, complete,
/// unmodified, and that the thing which loads them cannot reach the network.
///
/// <para>Everything here is about the schema set as an artifact. Whether ProcuLink's own output
/// passes it is <see cref="UblSchemaValidationTests"/>.</para>
/// </summary>
public class UblSchemaCatalogTests
{
    /// <summary>
    /// The closure walked from <c>UBL-Order-2.1.xsd</c> at the time of vendoring. Stated as a floor
    /// and a set, because both directions are failures worth knowing about: fewer means a schema
    /// went missing, and a name that is not on the list means someone vendored a file by hand
    /// instead of re-walking the imports.
    /// </summary>
    private static readonly string[] ExpectedSchemas =
    {
        "common/CCTS_CCT_SchemaModule-2.1.xsd",
        "common/UBL-CommonAggregateComponents-2.1.xsd",
        "common/UBL-CommonBasicComponents-2.1.xsd",
        "common/UBL-CommonExtensionComponents-2.1.xsd",
        "common/UBL-CommonSignatureComponents-2.1.xsd",
        "common/UBL-ExtensionContentDataType-2.1.xsd",
        "common/UBL-QualifiedDataTypes-2.1.xsd",
        "common/UBL-SignatureAggregateComponents-2.1.xsd",
        "common/UBL-SignatureBasicComponents-2.1.xsd",
        "common/UBL-UnqualifiedDataTypes-2.1.xsd",
        "common/UBL-XAdESv132-2.1.xsd",
        "common/UBL-XAdESv141-2.1.xsd",
        "common/UBL-xmldsig-core-schema-2.1.xsd",
        "maindoc/UBL-Order-2.1.xsd",
    };

    // ── The schemas are there, and they are the whole closure ────────────────────────────

    [Fact]
    public void EveryVendoredSchemaIsEmbeddedInTheAssembly()
    {
        UblSchemaCatalog.EmbeddedSchemaPaths().Should().BeEquivalentTo(ExpectedSchemas,
            "the vendored set must be the COMPLETE xsd:import closure of UBL-Order-2.1.xsd — a partial "
            + "set does not fail at validation, it fails at schema load, and the error then names a "
            + "missing TYPE, which reads like a defective document rather than a missing file");
    }

    [Fact]
    public void TheClosureCompilesWithEveryImportResolvedFromAVendoredCopy()
    {
        var set = UblSchemaCatalog.Compiled;

        set.IsCompiled.Should().BeTrue();
        set.GlobalElements.Contains(
                new XmlQualifiedName("Order", UblSchemaCatalog.OrderNamespace))
            .Should().BeTrue("the document element the whole closure exists to declare must be present");

        // Anti-vacuity. A schema set with unresolved imports still "compiles" — .NET downgrades the
        // failure to a warning — and the wreckage shows up as a tiny component count, not an error.
        set.GlobalElements.Count.Should().BeGreaterThan(1_000,
            "UBL 2.1's closure declares ~1600 global elements; a set that compiled with imports "
            + "silently unresolved would be a fraction of that and would still pass a naive check");
    }

    /// <summary>
    /// The property <see cref="UblSchemaCatalog"/> exists to hold, tested the only way it can be:
    /// by building a catalog that is deliberately incomplete. Against the real, complete resource
    /// set this can never fire, which is exactly why the injectable opener is there.
    /// </summary>
    [Fact]
    public void AnIncompleteClosureFailsLoudlyInsteadOfCompilingWithHoles()
    {
        const string hidden = "common/UBL-CommonBasicComponents-2.1.xsd";

        var act = () => UblSchemaCatalog.Build(path =>
            path == hidden ? null : UblSchemaCatalog.OpenEmbedded(path));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*did not resolve completely*",
                "a missing schema must abort the load; if it merely warned, every subsequent "
                + "document would fail validation with 'type not declared' and the operator would "
                + "read a missing FILE as a malformed DOCUMENT");
    }

    // ── Unmodified: the licences require it and the digests prove it ─────────────────────

    /// <summary>
    /// Re-hashes the embedded bytes against the table in PROVENANCE.md. PROVENANCE.md is the
    /// registry and this test derives from it — no digest is typed into this file, so the two cannot
    /// drift into agreeing with each other while both disagree with what OASIS published.
    ///
    /// <para>This is a licence check as much as a correctness one. The OASIS grant permits
    /// redistribution of the UNMODIFIED document; a reformat or an EOL rewrite is a modification.
    /// See the <c>-text</c> rule in <c>.gitattributes</c>.</para>
    /// </summary>
    [Fact]
    public void EveryVendoredSchemaMatchesTheDigestRecordedInProvenance()
    {
        var recorded = ProvenanceDigests();

        recorded.Should().HaveCount(ExpectedSchemas.Length,
            "PROVENANCE.md must record one row per vendored schema; a row that stopped parsing "
            + "would silently stop being checked");

        foreach (var path in ExpectedSchemas)
        {
            recorded.Should().ContainKey(path);

            using var stream = UblSchemaCatalog.OpenEmbedded(path);
            stream.Should().NotBeNull();

            using var memory = new MemoryStream();
            stream!.CopyTo(memory);
            var bytes = memory.ToArray();

            var (expectedLength, expectedDigest) = recorded[path];

            bytes.Length.Should().Be(expectedLength, $"{path} must be byte-identical to what OASIS published");
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
                .Should().Be(expectedDigest,
                    $"{path} has been modified since vendoring. Third-party schemas are redistributed "
                    + "under a licence conditioned on them being unmodified, and an edited schema also "
                    + "silently changes what 'schema-valid' means. Re-vendor rather than patch.");
        }
    }

    private static Dictionary<string, (int Length, string Digest)> ProvenanceDigests()
    {
        var text = ReadProvenance();

        // | `common/X.xsd` | `45268` | `dd54…` |
        var row = new Regex(
            @"^\|\s*`(?<path>[^`]+)`\s*\|\s*`(?<len>\d+)`\s*\|\s*`(?<sha>[0-9a-f]{64})`\s*\|\s*$",
            RegexOptions.Multiline);

        return row.Matches(text).ToDictionary(
            m => m.Groups["path"].Value,
            m => (int.Parse(m.Groups["len"].Value), m.Groups["sha"].Value));
    }

    private static string ReadProvenance()
    {
        using var stream = typeof(UblSchemaCatalog).Assembly
            .GetManifestResourceStream("ubl-2.1/PROVENANCE.md");
        stream.Should().NotBeNull("the licence notices must travel with the binary that redistributes the schemas");
        using var reader = new StreamReader(stream!, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // ── No DTD can reach outside the file it is in ───────────────────────────────────────

    /// <summary>
    /// The schema reader runs with <c>DtdProcessing.Parse</c> because
    /// <c>UBL-xmldsig-core-schema-2.1.xsd</c> carries a DOCTYPE and the closure will not load
    /// without it. That is only safe while the DOCTYPE stays internal, so the bytes are checked
    /// rather than the assumption: an external identifier would be a fetch target sitting inside a
    /// file whose whole point is that it never fetches anything.
    /// </summary>
    [Fact]
    public void NoVendoredSchemaDeclaresAnExternalDtdIdentifier()
    {
        var doctype = new Regex(@"<!DOCTYPE[^>\[]*\b(SYSTEM|PUBLIC)\b", RegexOptions.IgnoreCase);
        var offenders = new List<string>();

        foreach (var path in ExpectedSchemas)
        {
            using var stream = UblSchemaCatalog.OpenEmbedded(path)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            if (doctype.IsMatch(reader.ReadToEnd()))
            {
                offenders.Add(path);
            }
        }

        offenders.Should().BeEmpty(
            "OASIS republished the xmldsig schema with the PUBLIC/SYSTEM identifiers already removed, "
            + "leaving an internal subset resolvable from the file's own bytes. An external identifier "
            + "reintroduced by a re-vendor would be an outbound fetch from inside the validator.");
    }

    // ── The resolver is closed, and provably so ──────────────────────────────────────────

    /// <summary>
    /// The URL these very files were downloaded from. It is live, it is correct, and the resolver
    /// must refuse it anyway — that is the difference between "happens not to fetch" and "cannot".
    /// </summary>
    [Theory]
    [InlineData("https://docs.oasis-open.org/ubl/os-UBL-2.1/xsd/maindoc/UBL-Order-2.1.xsd")]
    [InlineData("http://www.w3.org/2001/XMLSchema.dtd")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    public void TheResolverRefusesEveryUriOutsideTheVendoredCatalog(string uri)
    {
        var resolver = new UblSchemaCatalog.VendoredOnlyResolver(UblSchemaCatalog.OpenEmbedded);

        var act = () => resolver.GetEntity(new Uri(uri), null, typeof(Stream));

        // Asserted on the MESSAGE, not merely on the throw. Every path out of GetEntity throws, so a
        // bare Throw<XmlException>() would still pass if the catalog-prefix check were deleted — the
        // request would just fail later, on a lookup miss. Only the prefix branch says "never the
        // network", so pinning that sentence is what makes the check load-bearing. Verified by
        // deleting the check and watching this test go red.
        act.Should().Throw<XmlException>(
                "an XmlResolver that can reach a URL is an SSRF surface and a build fragility; this "
                + "one resolves the vendored copies and nothing else")
            .WithMessage("*never the network*");
    }

    /// <summary>
    /// Inside the catalog's own URI space but not a vendored file. Separate from the case above
    /// because it must fail for the OTHER reason — "not vendored", not "not the network" — and a
    /// single test that accepted either message would prove neither.
    /// </summary>
    [Fact]
    public void TheResolverRefusesAPathInsideTheCatalogThatIsNotVendored()
    {
        var resolver = new UblSchemaCatalog.VendoredOnlyResolver(UblSchemaCatalog.OpenEmbedded);

        var act = () => resolver.GetEntity(
            new Uri(UblSchemaCatalog.CatalogPrefix + "common/Not-Vendored-2.1.xsd"), null, typeof(Stream));

        act.Should().Throw<XmlException>().WithMessage("*not vendored*");
    }

    [Fact]
    public void TheResolverServesTheVendoredCopies()
    {
        var resolver = new UblSchemaCatalog.VendoredOnlyResolver(UblSchemaCatalog.OpenEmbedded);

        var entity = resolver.GetEntity(
            new Uri(UblSchemaCatalog.CatalogPrefix + "common/UBL-CommonBasicComponents-2.1.xsd"),
            null, typeof(Stream));

        entity.Should().BeAssignableTo<Stream>();
        ((Stream)entity).Dispose();
    }

    /// <summary>
    /// Every URI the closure actually asked for, checked in one place. The refusal tests above prove
    /// the resolver CAN say no; this proves it never had to — that a real load stays inside the
    /// catalog rather than being saved by a refusal at the boundary.
    /// </summary>
    [Fact]
    public void LoadingTheClosureRequestsNothingOutsideTheCatalog()
    {
        var probe = new List<string>();

        UblSchemaCatalog.Build(path =>
        {
            probe.Add(path);
            return UblSchemaCatalog.OpenEmbedded(path);
        });

        probe.Should().NotBeEmpty();

        // Every schema in the closure except the root is reached BY IMPORT, so the walk proves the
        // vendored set is exactly what the standard needs — nothing dead, nothing missing. It also
        // means the refusal tests above are not the only thing standing between this validator and
        // the network: a real load never asks for anything they would have to refuse.
        probe.Distinct().Should().BeEquivalentTo(ExpectedSchemas,
            "every vendored file must be reachable from UBL-Order-2.1.xsd, and every file the "
            + "closure reaches must be vendored");
    }
}
