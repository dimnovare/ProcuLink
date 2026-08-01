using System.Text.RegularExpressions;
using ProcuLink.Transform.Mapping;
using Xunit;

namespace ProcuLink.Transform.Tests.Mapping;

/// <summary>
/// WP-15 · S3 — the manipulator CONTRACT, pinned from the C# side.
///
/// <para><b>Why a second manipulator test file.</b> <c>ManipulatorTests</c> pins BEHAVIOUR — what
/// each manipulator does to a value. This file pins the SHAPE the frontend has to mirror: which
/// types exist, how many params each accepts, and — for the three that read the ROW rather than the
/// incoming value — what those params actually mean.</para>
///
/// <para><b>What it is defending against, concretely.</b> The frontend's <c>MANIPULATOR_TYPES</c>
/// (<c>src/lib/api/types.ts</c>) is a hand-written mirror of <see cref="ManipulatorRegistry"/>; a
/// TypeScript file cannot import C#, so nothing but a pair of matching tests keeps them honest. It
/// had drifted, and both entries were shipping hazards:</para>
/// <list type="bullet">
///   <item><b>Concat</b> was declared as one param, a literal suffix. It requires <b>2 or more</b>,
///   the first being a separator and the rest NAMED ROW COLUMNS. A one-param Concat throws
///   <see cref="ArgumentException"/> at transform time — the order fails, in production.</item>
///   <item><b>Fallback</b> was declared as a literal default. Every param is a COLUMN NAME and it
///   returns <c>null</c> when none of them holds a value, so used as a literal default it silently
///   BLANKS the supplier's column.</item>
/// </list>
///
/// <para>The frontend's twin of this file is <c>outputRuleModel.test.ts</c>. Change an arity here and
/// that one has to change too — which is the entire point.</para>
/// </summary>
public class ManipulatorContractTests
{
    /// <summary>
    /// Every type the registry resolves, with the param counts its constructor accepts.
    ///
    /// <para><b>Authored by hand, deliberately.</b> Deriving it by reflecting over the constructors
    /// would make the test agree with the code by construction and prove nothing — the whole job
    /// here is to be an INDEPENDENT statement of the contract that a change has to come and edit.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (int Min, int Max)> Arity =
        new Dictionary<string, (int, int)>(StringComparer.Ordinal)
        {
            // Ignores its params entirely — the ctor takes and discards them.
            ["Trim"] = (0, 0),
            ["Replace"] = (2, 2),      // [find, with]
            ["DateFormat"] = (2, 2),   // [inputFormat, outputFormat]
            ["NumberFormat"] = (1, 2), // [format] or [format, culture]
            ["Split"] = (2, 2),        // [delimiter, index]
            ["Multiply"] = (1, 1),     // [factor]
            ["Divide"] = (1, 1),       // [divisor]
            // Unbounded above: [separator, col1, col2, …].
            ["Concat"] = (2, int.MaxValue),
            // Unbounded, and zero params is accepted by the ctor (it simply never matches).
            ["Fallback"] = (0, int.MaxValue),
            // Catalog enrichment, wired by the catalog pipeline — deliberately NOT offered in the
            // designer's formatting menu, so the frontend table does not carry it.
            ["LoadCatalogProduct"] = (1, 1),
        };

    /// <summary>Types an author may choose in the output designer — the set the frontend mirrors.</summary>
    public static readonly IReadOnlySet<string> AuthorFacing = new HashSet<string>(StringComparer.Ordinal)
    {
        "Trim", "Replace", "DateFormat", "NumberFormat", "Concat", "Fallback", "Split", "Multiply", "Divide",
    };

    private static readonly Dictionary<string, string> EmptyRow = new();

    // ── The set itself ───────────────────────────────────────────────────────

    /// <summary>
    /// A new manipulator added to the registry with no entry here — and therefore, by the pairing
    /// this file exists to enforce, no entry in the frontend table either — fails immediately rather
    /// than shipping as a type the designer can never offer.
    /// </summary>
    [Fact]
    public void EveryRegistryType_IsAccountedFor_AndNothingElseResolves()
    {
        foreach (var type in Arity.Keys)
            Assert.NotNull(ManipulatorRegistry.Resolve(type, ParamsFor(type)));

        Assert.Throws<InvalidOperationException>(
            () => ManipulatorRegistry.Resolve("NotAManipulator", Array.Empty<string>()));
    }

    /// <summary>
    /// The direction the table above cannot check on its own: a NEW manipulator CLASS added to the
    /// assembly with no entry here.
    ///
    /// <para>Iterating <see cref="Arity"/> only proves every declared type resolves — a tenth
    /// manipulator would sail past it, and past the frontend table it is paired with, arriving as a
    /// capability the designer can never offer and nobody notices is missing. Reflection over the
    /// implementations is the only way round <see cref="ManipulatorRegistry.Resolve"/> being a
    /// switch: there is nothing to enumerate but the types themselves.</para>
    ///
    /// <para>Naming convention is load-bearing here and is asserted rather than assumed: the
    /// registry key is the class name minus the <c>Manipulator</c> suffix.</para>
    /// </summary>
    [Fact]
    public void EveryManipulatorCLASS_HasAnEntry_SoATenthOneCannotArriveUnnoticed()
    {
        var implementations = typeof(ManipulatorRegistry).Assembly
            .GetTypes()
            .Where(t => typeof(IFieldManipulator).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false })
            .Select(t => t.Name)
            .ToList();

        Assert.NotEmpty(implementations);

        var missing = implementations
            .Where(n => n.EndsWith("Manipulator", StringComparison.Ordinal))
            .Select(n => n[..^"Manipulator".Length])
            .Where(key => !Arity.ContainsKey(key))
            .ToList();

        Assert.True(missing.Count == 0,
            "These manipulators exist in ProcuLink.Transform but are not declared in this file — and " +
            "therefore almost certainly not in the frontend's MANIPULATOR_TYPES either: " +
            string.Join(", ", missing));

        // And nothing named off-convention, which would slip past the check above entirely.
        var offConvention = implementations
            .Where(n => !n.EndsWith("Manipulator", StringComparison.Ordinal))
            .ToList();
        Assert.True(offConvention.Count == 0,
            "IFieldManipulator implementations must be named <Key>Manipulator — the registry key is " +
            "derived from the class name, and this check cannot see one that is not: " +
            string.Join(", ", offConvention));
    }

    /// <summary>
    /// The third direction, and the one the two checks above BOTH miss: a registry KEY with no
    /// table entry.
    ///
    /// <para>Found by mutation. Adding <c>"CatalogPrice" =&gt; new LoadCatalogProductManipulator(…)</c>
    /// to the switch introduces a resolvable type the frontend has never heard of — and it survives
    /// the class-reflection check, because it adds no CLASS, and the table walk, because the table
    /// is what it is drifting from. Two guards over one registry, and a whole shape between them.</para>
    ///
    /// <para>Reading the switch's source is the only way to enumerate its arms. The file is a single
    /// expression switch with no comments and no string literals other than the keys themselves, so
    /// a keys-of-the-arms regex is exact here — the caveat that makes this idiom risky elsewhere
    /// (a literal or a comment that looks like an arm) does not apply, and the assertion that the
    /// file still looks that way is the first thing below.</para>
    /// </summary>
    [Fact]
    public void EveryRegistryKEY_HasATableEntry_SoASecondKeyForOneClassCannotHide()
    {
        var source = File.ReadAllText(
            Path.Combine(RepoRoot(), "ProcuLink.Transform", "Mapping", "ManipulatorRegistry.cs"));

        // Guard the guard: if this file ever grows comments or unrelated string literals, the regex
        // below stops being exact and this test must be rewritten rather than quietly weakened.
        Assert.DoesNotContain("//", source);
        Assert.DoesNotContain("/*", source);

        var keys = Regex.Matches(source, "\"(?<key>[A-Za-z]+)\"\\s*=>")
            .Select(m => m.Groups["key"].Value)
            .ToList();

        Assert.True(keys.Count >= Arity.Count,
            $"Only {keys.Count} registry arms were parsed but {Arity.Count} types are declared — " +
            "the switch's shape changed and this regex no longer reads it.");

        var undeclared = keys.Where(k => !Arity.ContainsKey(k)).ToList();
        Assert.True(undeclared.Count == 0,
            "These registry keys resolve but are not declared in this file — and therefore not in " +
            "the frontend's MANIPULATOR_TYPES either: " + string.Join(", ", undeclared));
    }

    /// <summary>
    /// Walks up from the test assembly to the repository root, identified by the solution file.
    /// Local rather than shared: the repo's other source-reading guard lives in
    /// <c>ProcuLink.Api.Tests</c>, which this project does not reference.
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcuLink.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// <c>LoadCatalogProduct</c> resolves but is NOT author-facing. Stated as a test so that adding
    /// it to the designer's menu is a deliberate act with a failing assertion in front of it: it
    /// performs a catalog lookup, which is not a formatting choice and does not belong in a
    /// formatting menu.
    /// </summary>
    [Fact]
    public void LoadCatalogProduct_ResolvesButIsNotAuthorFacing()
    {
        Assert.True(Arity.ContainsKey("LoadCatalogProduct"));
        Assert.DoesNotContain("LoadCatalogProduct", AuthorFacing);
        Assert.Equal(Arity.Count - 1, AuthorFacing.Count);
    }

    // ── Arity ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Replace")]
    [InlineData("DateFormat")]
    [InlineData("NumberFormat")]
    [InlineData("Split")]
    [InlineData("Multiply")]
    [InlineData("Divide")]
    [InlineData("Concat")]
    public void TooFewParams_Throws(string type)
    {
        var (min, _) = Arity[type];
        Assert.Throws<ArgumentException>(
            () => ManipulatorRegistry.Resolve(type, Placeholders(type, min - 1)));
    }

    [Theory]
    [InlineData("Replace")]
    [InlineData("DateFormat")]
    [InlineData("NumberFormat")]
    [InlineData("Split")]
    [InlineData("Multiply")]
    [InlineData("Divide")]
    public void TooManyParams_Throws(string type)
    {
        var (_, max) = Arity[type];
        Assert.Throws<ArgumentException>(
            () => ManipulatorRegistry.Resolve(type, Placeholders(type, max + 1)));
    }

    /// <summary>
    /// The one manipulator with a RANGE. The frontend table declares a single <c>["format"]</c>
    /// param while the designer's own presets write two (<c>["N2", "de-DE"]</c>) — legal, because
    /// the culture is optional, but the table should describe the range rather than the minimum.
    /// Recorded here so the frontend fix has a stated contract to match.
    /// </summary>
    [Fact]
    public void NumberFormat_AcceptsOneOrTwoParams()
    {
        Assert.NotNull(ManipulatorRegistry.Resolve("NumberFormat", new[] { "N2" }));
        Assert.NotNull(ManipulatorRegistry.Resolve("NumberFormat", new[] { "N2", "de-DE" }));
        Assert.Throws<ArgumentException>(
            () => ManipulatorRegistry.Resolve("NumberFormat", new[] { "N2", "de-DE", "extra" }));
    }

    /// <summary>Concat has no upper bound — joining six columns is as legal as joining two.</summary>
    [Fact]
    public void Concat_HasNoUpperArityBound()
    {
        var six = new[] { "-", "A", "B", "C", "D", "E" };
        var row = new Dictionary<string, string> { ["A"] = "1", ["B"] = "2", ["C"] = "3", ["D"] = "4", ["E"] = "5" };
        Assert.Equal("1-2-3-4-5", ManipulatorRegistry.Resolve("Concat", six).Apply("ignored", row));
    }

    // ── Semantics of the three that read the ROW ─────────────────────────────

    /// <summary>
    /// Concat is the two-field JOIN, natively. Its params after the separator name COLUMNS, and the
    /// incoming value is discarded entirely — so a UI that labels it "append this text" produces
    /// either a hard failure (one param) or a silent lookup of a column that does not exist.
    /// </summary>
    [Fact]
    public void Concat_JoinsNamedColumns_AndIgnoresTheIncomingValue()
    {
        var row = new Dictionary<string, string> { ["Currency"] = "EUR", ["SupplierItemCode"] = "WRT-8891" };
        var m = ManipulatorRegistry.Resolve("Concat", new[] { "-", "Currency", "SupplierItemCode" });

        Assert.Equal("EUR-WRT-8891", m.Apply("THE INCOMING VALUE", row));
        // Same result with no incoming value at all: it was never part of the answer.
        Assert.Equal("EUR-WRT-8891", m.Apply(null, row));
    }

    /// <summary>A column Concat names but the row does not have contributes an EMPTY segment.</summary>
    [Fact]
    public void Concat_UnknownColumn_ContributesEmpty_RatherThanTheLiteral()
    {
        var row = new Dictionary<string, string> { ["Currency"] = "EUR" };
        var m = ManipulatorRegistry.Resolve("Concat", new[] { "-", "Currency", "not a column" });

        // NOT "EUR-not a column" — the second param is looked up, never emitted as text.
        Assert.Equal("EUR-", m.Apply(null, row));
    }

    /// <summary>
    /// Fallback chains to other COLUMNS and yields <c>null</c> when none of them holds a value. It
    /// cannot express "use the text N/A when this is empty" — labelling it that way blanks the
    /// column instead.
    /// </summary>
    [Fact]
    public void Fallback_ReadsColumns_AndReturnsNullWhenNoneHasAValue()
    {
        var row = new Dictionary<string, string> { ["Preferred"] = "", ["Backup"] = "B-1" };
        Assert.Equal("B-1", ManipulatorRegistry.Resolve("Fallback", new[] { "Preferred", "Backup" }).Apply("x", row));

        // The dangerous case: the param read as a literal default.
        Assert.Null(ManipulatorRegistry.Resolve("Fallback", new[] { "N/A" }).Apply("x", row));
    }

    /// <summary>Split's index is ZERO-based on the wire; a UI showing "part 1" must store "0".</summary>
    [Fact]
    public void Split_IndexIsZeroBased()
    {
        var m0 = ManipulatorRegistry.Resolve("Split", new[] { "-", "0" });
        var m1 = ManipulatorRegistry.Resolve("Split", new[] { "-", "1" });

        Assert.Equal("AAA", m0.Apply("AAA-BBB-CCC", EmptyRow));
        Assert.Equal("BBB", m1.Apply("AAA-BBB-CCC", EmptyRow));
    }

    /// <summary>A non-integer index is refused at construction, not silently treated as 0.</summary>
    [Fact]
    public void Split_NonIntegerIndex_Throws()
    {
        Assert.Throws<ArgumentException>(() => ManipulatorRegistry.Resolve("Split", new[] { "-", "first" }));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Minimum valid params for a type, used to prove every registry entry resolves.</summary>
    private static string[] ParamsFor(string type) => type switch
    {
        "NumberFormat" => new[] { "N2" },
        "Split" => new[] { "-", "0" },
        "Multiply" or "Divide" => new[] { "2" },
        // Its single param is not free text: the ctor validates it against
        // price|code|unit|barcode and throws otherwise. Worth knowing — it means
        // LoadCatalogProduct's contract is narrower than "1 param" implies.
        "LoadCatalogProduct" => new[] { "price" },
        _ => Placeholders(type, Arity[type].Min),
    };

    /// <summary>
    /// <paramref name="count"/> params that are individually VALID for <paramref name="type"/>, so
    /// the only thing wrong about them is how many there are.
    ///
    /// <para>This started out as <c>Enumerable.Repeat("x", count)</c> and a mutation caught it:
    /// widening <c>Multiply</c> to accept any number of params left the arity test GREEN, because
    /// <c>"x"</c> then failed <c>decimal.TryParse</c> and threw <see cref="ArgumentException"/>
    /// anyway. The test was passing on VALUE validation while claiming to test ARITY — the two
    /// throw the same exception type, so nothing distinguished them. Supplying parseable values is
    /// what makes the count the only variable.</para>
    /// </summary>
    private static string[] Placeholders(string type, int count)
    {
        if (count <= 0) return Array.Empty<string>();
        var value = type switch
        {
            "Multiply" or "Divide" => "2",
            // Every NumberFormat param is a format or culture string; "N2" is valid as either
            // position and an unparseable culture would throw for the wrong reason.
            "NumberFormat" => "N2",
            // Split's second param must parse as an integer; its first is a delimiter, and "0"
            // is a legal delimiter.
            "Split" => "0",
            _ => "x",
        };
        return Enumerable.Repeat(value, count).ToArray();
    }
}
