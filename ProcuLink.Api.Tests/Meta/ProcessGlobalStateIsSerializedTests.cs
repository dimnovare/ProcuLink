using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ProcuLink.Api.Tests.Architecture;
using Xunit;

namespace ProcuLink.Api.Tests.Meta;

/// <summary>
/// Every test class that can write the assembly's process-global state must sit in the ONE
/// collection that serialises it.
///
/// <para><b>The state.</b> <c>ProcuLink.Api.Controllers.MigrationReadiness</c> is a
/// <c>static volatile int</c>. It is per-PROCESS. xUnit, however, gives each test class its own
/// host and runs classes in DIFFERENT collections concurrently, so "process-global" and
/// "test-class-local" are not the same scope — and every write by one class is visible to every
/// other class running at that moment.</para>
///
/// <para><b>The two ways in.</b> A class writes it explicitly
/// (<c>MarkPending</c>/<c>MarkSucceeded</c>/<c>MarkFailed</c>), or implicitly by booting a
/// <c>WebApplicationFactory&lt;Program&gt;</c> — Program.cs registers an <c>ApplicationStarted</c>
/// task that calls <c>MarkSucceeded()</c> when migrations apply and <c>MarkFailed()</c> when they
/// do not. The implicit path is the easy one to miss, which is why this guard tests for the
/// factory base type and not just for the identifier — and for every SUBTYPE of it declared in the
/// project, so a class that merely takes someone else's factory as an
/// <c>IClassFixture&lt;&gt;</c> is caught too, not only the file that declares one.</para>
///
/// <para><b>Why a guard and not a convention.</b> The defect it pins was live on main and
/// reproduced here: over 6 consecutive full-suite runs, 2 runs failed in 2 different tests, and
/// one was <c>HealthEndpointTests.Readiness_PendingThenSucceeded_TransitionsToReady</c> with
/// "Expected: ServiceUnavailable / Actual: OK" — another class's <c>MarkSucceeded()</c> landing
/// between this test's <c>MarkPending()</c> and its request. The class carried a header comment
/// asserting the static "can't leak across tests" because methods within a class are serialised;
/// the comment was right about methods and silent about classes. A convention that is documented
/// in the place it is already misunderstood is not isolation. This is.</para>
///
/// <para><b>Direction.</b> This guard is one-directional by construction: it fails when a
/// host-booting or readiness-touching class is OUTSIDE the collection. It deliberately does not
/// assert the converse (that every collection member boots a host) — the collection's older and
/// still-primary job is serialising Testcontainers classes, so members that touch neither are
/// expected and fine.</para>
/// </summary>
public sealed class ProcessGlobalStateIsSerializedTests
{
    /// <summary>The one collection that serialises process-global state. See PostgresContainerCollection.</summary>
    private const string SerialisedCollection = "postgres-container";

    /// <summary>The process-global that started this.</summary>
    private const string ProcessGlobalType = "MigrationReadiness";

    /// <summary>Booting one of these runs Program.cs, which writes <see cref="ProcessGlobalType"/>.</summary>
    private const string HostFactoryBaseType = "WebApplicationFactory";

    // Anti-vacuity floors. Every one of these is a real, currently-true count; they exist so a
    // broken repo-root, a moved directory or an over-tightened filter fails LOUD instead of
    // reporting a clean sweep over nothing. 293 .cs files live under ProcuLink.Api.Tests today.
    private const int MinimumFilesParsed = 200;
    private const int MinimumFilesTouchingProcessGlobalState = 5;
    private const int MinimumTestClassesChecked = 5;

    [Fact]
    public void EveryTestClassThatCanWriteProcessGlobalState_IsInTheSerialisedCollection()
    {
        var scan = Scan();

        Assert.True(
            scan.FilesParsed >= MinimumFilesParsed,
            $"only {scan.FilesParsed} .cs files were parsed under {scan.TestProjectRoot} — the scan is " +
            $"broken, not the code (expected at least {MinimumFilesParsed})");

        Assert.True(
            scan.FilesTouchingProcessGlobalState.Count >= MinimumFilesTouchingProcessGlobalState,
            $"only {scan.FilesTouchingProcessGlobalState.Count} file(s) were detected as touching " +
            $"process-global readiness state (expected at least {MinimumFilesTouchingProcessGlobalState}). " +
            "Either the detection broke or the files moved; a sweep that finds nothing must not pass.");

        Assert.True(
            scan.TestClassesChecked >= MinimumTestClassesChecked,
            $"only {scan.TestClassesChecked} test class(es) were checked (expected at least " +
            $"{MinimumTestClassesChecked}) — the test-class detection is broken.");

        Assert.True(
            scan.Offenders.Count == 0,
            "These test classes can write the process-global MigrationReadiness flag — directly, or by " +
            $"booting a {HostFactoryBaseType}<Program> whose Program.cs migration task writes it — but are " +
            $"NOT in the \"{SerialisedCollection}\" collection, so they run CONCURRENTLY with the classes " +
            "that assert on that flag and will flake them intermittently.\n\n" +
            $"Fix: add [Collection(\"{SerialisedCollection}\")] to each class below.\n\n" +
            string.Join("\n", scan.Offenders));
    }

    /// <summary>
    /// The scan's own detection, tested on itself. Without this, a detector that silently matched
    /// nothing would still satisfy every count above once the floors were met by some other means.
    /// </summary>
    [Fact]
    public void TheScan_FindsTheKnownHostBootingClasses()
    {
        var scan = Scan();

        // Every class here really does boot a host or name the global; they are the corpus the
        // guard exists to hold. Named explicitly so a detector that stops seeing one of the two
        // entry paths (identifier vs base type) fails here rather than going quietly green.
        string[] expected =
        [
            "HealthEndpointTests",                    // names MigrationReadiness AND boots a host
            "RevisionAuthorityReadinessSurfaceTests",  // names MigrationReadiness AND boots a host
            "ProgramHardeningTests",                   // boots a host only
            "TenancyIsolationTests",                   // boots a host only
            "EndToEndPipelineTests",                   // boots a host only
            // Declares no factory of its own — it takes HardeningTestFactory, declared in
            // ProgramHardeningTests.cs, as an IClassFixture, and so boots its own host. A
            // file-local check missed it; this entry pins the cross-file path specifically.
            "RateLimitPolicyAppliedTests",
        ];

        foreach (var name in expected)
        {
            Assert.True(
                scan.TestClassNamesChecked.Contains(name),
                $"{name} is no longer detected as touching process-global state. Either it stopped " +
                "booting a host (fine — remove it from this list) or the detector regressed (not fine).");
        }
    }

    /// <summary>
    /// Parsed once per process, not once per test. Both tests below need the same sweep over ~293
    /// files, and doing it twice cost 5.5s of the assembly's wall — measurable against the 157s
    /// CI Test step #168 bought, and pure waste.
    /// </summary>
    private static readonly Lazy<ScanResult> CachedScan = new(RunScan, isThreadSafe: true);

    private static ScanResult Scan() => CachedScan.Value;

    private static ScanResult RunScan()
    {
        var testProjectRoot = Path.Combine(OrphanDetector.FindRepoRoot(), "ProcuLink.Api.Tests");

        var files = Directory
            .EnumerateFiles(testProjectRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();

        var trees = files.ToDictionary(
            path => path,
            path => CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot());

        // Pass 1 — every type in the project that IS a host factory. Checking only for the literal
        // `WebApplicationFactory` base type would catch the file that DECLARES a factory and miss a
        // test class that merely USES one declared elsewhere (e.g. [IClassFixture<HealthTestFactory>]
        // from another file) — which boots a host just the same. Subclass names are collected here so
        // pass 2 can flag the users too.
        var hostFactoryTypes = new HashSet<string>(StringComparer.Ordinal) { HostFactoryBaseType };
        var allClasses = trees.Values
            .SelectMany(root => root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            .Select(cls => (Name: cls.Identifier.ValueText,
                            Bases: (cls.BaseList?.Types.Select(b => BaseTypeName(b.Type)) ?? []).ToList()))
            .ToList();

        // To a fixed point, so a subclass of a subclass is caught regardless of file order.
        bool grew;
        do
        {
            grew = false;
            foreach (var (name, bases) in allClasses)
            {
                if (bases.Any(hostFactoryTypes.Contains))
                    grew |= hostFactoryTypes.Add(name);
            }
        }
        while (grew);

        var offenders = new List<string>();
        var touching = new List<string>();
        var classNames = new HashSet<string>(StringComparer.Ordinal);
        var classesChecked = 0;

        // Pass 2 — flag the files that touch process-global readiness, either way in.
        foreach (var (path, root) in trees)
        {
            // Syntax nodes, never raw text: PassportServiceTests mentions WebApplicationFactory in
            // a doc comment while using EF InMemory, and must not be dragged in by a grep.
            var identifiers = root
                .DescendantNodes()
                .OfType<SimpleNameSyntax>()
                .Select(n => n.Identifier.ValueText)
                .ToHashSet(StringComparer.Ordinal);

            var namesTheGlobal = identifiers.Contains(ProcessGlobalType);
            var usesHostFactory = identifiers.Overlaps(hostFactoryTypes);

            if (!namesTheGlobal && !usesHostFactory)
                continue;

            touching.Add(Path.GetRelativePath(testProjectRoot, path));

            foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(IsTestClass))
            {
                classesChecked++;
                classNames.Add(cls.Identifier.ValueText);

                if (IsInSerialisedCollection(cls))
                    continue;

                var reason = (namesTheGlobal, usesHostFactory) switch
                {
                    (true, true)  => $"names {ProcessGlobalType} and its file uses a {HostFactoryBaseType}",
                    (true, false) => $"names {ProcessGlobalType}",
                    _             => $"its file uses a {HostFactoryBaseType}<Program> subtype",
                };

                offenders.Add(
                    $"  {Path.GetRelativePath(testProjectRoot, path)}: {cls.Identifier.ValueText} — {reason}");
            }
        }

        return new ScanResult(testProjectRoot, files.Count, touching, classesChecked, classNames, offenders);
    }

    /// <summary>A class is a test class when it declares at least one xUnit fact/theory method.</summary>
    private static bool IsTestClass(ClassDeclarationSyntax cls) =>
        cls.Members
            .OfType<MethodDeclarationSyntax>()
            .SelectMany(m => m.AttributeLists)
            .SelectMany(a => a.Attributes)
            .Select(a => BaseTypeName(a.Name))
            // [Fact], [Theory], and the repo's gated variants: [DockerRequiredFact],
            // [EnvironmentGatedFact], [LocalPostgresRequiredFact], [DockerRequiredTheory].
            .Any(n => n.EndsWith("Fact", StringComparison.Ordinal) ||
                      n.EndsWith("Theory", StringComparison.Ordinal));

    private static bool IsInSerialisedCollection(ClassDeclarationSyntax cls) =>
        cls.AttributeLists
            .SelectMany(a => a.Attributes)
            .Where(a => BaseTypeName(a.Name) == "Collection")
            .SelectMany(a => a.ArgumentList?.Arguments ?? default)
            .Any(arg => arg.Expression is LiteralExpressionSyntax lit &&
                        lit.Token.ValueText == SerialisedCollection);

    /// <summary>Strips namespace qualification and generic arity: <c>a.b.Foo&lt;T&gt;</c> → <c>Foo</c>.</summary>
    private static string BaseTypeName(TypeSyntax type) => type switch
    {
        SimpleNameSyntax simple    => simple.Identifier.ValueText,
        QualifiedNameSyntax q      => BaseTypeName(q.Right),
        AliasQualifiedNameSyntax a => BaseTypeName(a.Name),
        _                          => type.ToString(),
    };

    private sealed record ScanResult(
        string TestProjectRoot,
        int FilesParsed,
        IReadOnlyList<string> FilesTouchingProcessGlobalState,
        int TestClassesChecked,
        IReadOnlySet<string> TestClassNamesChecked,
        IReadOnlyList<string> Offenders);
}
