using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ProcuLink.Api.Tests.Meta;

/// <summary>One test method that can exit reporting Passed without asserting anything.</summary>
public sealed record VacuousPass(
    string RelativePath,
    int Line,
    string TestName,
    string Guard,
    string Statement)
{
    public override string ToString() =>
        $"{RelativePath}({Line})  {TestName}  —  {Guard} {Statement}";
}

/// <summary>
/// Source scanner behind <see cref="NoVacuousTestPassTests"/>.
///
/// <para><b>The rule.</b> A test method body must not contain an early <c>return</c> that is
/// reached <i>before any assertion has run</i>. Such a return makes the test exit having verified
/// nothing while the runner records <b>Passed</b> — the failure mode this guard exists to prevent.
/// A test that genuinely cannot run in the current environment must say so through a declared
/// skip (<c>[DockerRequiredFact]</c>, <c>[EnvironmentGatedFact]</c>,
/// <c>[LocalPostgresRequiredFact]</c>, <c>[Fact(Skip = "…")]</c>) which xUnit reports with a
/// human reason.</para>
///
/// <para><b>Why structural, not env-var-specific.</b> Gating on
/// <c>Environment.GetEnvironmentVariable</c> alone would have missed the two worst real offenders
/// found by this scan: a lost-update concurrency test gated on a live local Postgres socket, and a
/// parser test gated on <c>File.Exists</c>. "Returns before asserting" catches every escape hatch,
/// including ones nobody has invented yet.</para>
///
/// <para>Returns nested inside a lambda, an anonymous method or a local function belong to that
/// inner function, not to the test, and are not flagged. Neither are returns in non-test helper
/// methods, nor tests whose attribute already carries an explicit <c>Skip</c>.</para>
/// </summary>
public static class VacuousTestPassScanner
{
    /// <summary>
    /// Path segments never scanned: build output, VCS, and the full repo copies parallel agents
    /// keep under <c>.claude/worktrees</c> (reading those reports another session's in-progress
    /// code as if it were ours).
    ///
    /// <para>Matched against the path RELATIVE to the repo root, never the absolute path. This
    /// checkout may itself sit inside <c>.claude/worktrees/…</c>, and matching absolutely would
    /// then exclude every file in the repository — leaving the guard scanning nothing and passing
    /// vacuously, which is the exact bug it exists to catch. <see cref="TestSourceFiles"/> has a
    /// companion assertion in <c>NoVacuousTestPassTests.TheGuardActuallyReadsTheTestProjects</c>.</para>
    /// </summary>
    private static readonly string[] ExcludedSegments =
    {
        "obj/",
        "bin/",
        ".git/",
        "node_modules/",
        ".claude/worktrees/",
    };

    /// <summary>
    /// Walks up from the test assembly's output directory to the folder holding
    /// <c>ProcuLink.slnx</c>. Throws rather than returning null: a scanner that cannot find the
    /// source tree must fail loudly, not quietly scan nothing.
    /// </summary>
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProcuLink.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate ProcuLink.slnx walking up from '{AppContext.BaseDirectory}'. " +
            "The vacuous-pass guard cannot scan the source tree and must not report a pass.");
    }

    /// <summary>Every C# source file belonging to a <c>*.Tests</c> project, build output excluded.</summary>
    public static IReadOnlyList<string> TestSourceFiles(string repoRoot) =>
        Directory.EnumerateDirectories(repoRoot, "*.Tests", SearchOption.TopDirectoryOnly)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(p => !IsExcluded(RelativePath(repoRoot, p)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Scans every test source file under <paramref name="repoRoot"/>.</summary>
    public static IReadOnlyList<VacuousPass> ScanRepository(string repoRoot) =>
        TestSourceFiles(repoRoot)
            .SelectMany(path => Scan(File.ReadAllText(path), RelativePath(repoRoot, path)))
            .ToList();

    /// <summary>Repo-root-relative, forward-slashed — the form used for both filtering and reporting.</summary>
    public static string RelativePath(string repoRoot, string absolutePath) =>
        Path.GetRelativePath(repoRoot, absolutePath).Replace('\\', '/');

    private static bool IsExcluded(string relativePath) =>
        ExcludedSegments.Any(seg =>
            relativePath.StartsWith(seg, StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains($"/{seg}", StringComparison.OrdinalIgnoreCase));

    /// <summary>Scans one compilation unit. <paramref name="displayPath"/> is only for reporting.</summary>
    public static IReadOnlyList<VacuousPass> Scan(string source, string displayPath)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var found = new List<VacuousPass>();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (method.Body is not { } body) continue; // expression-bodied: cannot early-return

            var testAttributes = method.AttributeLists
                .SelectMany(list => list.Attributes)
                .Where(IsTestAttribute)
                .ToList();

            if (testAttributes.Count == 0) continue;          // a helper, not a test
            if (testAttributes.Any(HasExplicitSkip)) continue; // already a declared skip

            foreach (var ret in body.DescendantNodes().OfType<ReturnStatementSyntax>())
            {
                if (!IsValuelessReturn(ret)) continue;
                if (!BelongsDirectlyTo(ret, method)) continue; // it is a lambda's / local fn's return
                if (AssertionPrecedes(body, ret.SpanStart)) continue;

                found.Add(new VacuousPass(
                    displayPath,
                    tree.GetLineSpan(ret.Span).StartLinePosition.Line + 1,
                    method.Identifier.ValueText,
                    DescribeGuard(ret, method),
                    Collapse(ret.ToString())));
            }
        }

        return found;
    }

    // ── predicates ───────────────────────────────────────────────────────────

    /// <summary>xUnit's test markers, plus every project-local subclass (…Fact / …Theory).</summary>
    private static bool IsTestAttribute(AttributeSyntax attribute)
    {
        var name = attribute.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            SimpleNameSyntax s => s.Identifier.ValueText,
            _ => attribute.Name.ToString(),
        };

        if (name.EndsWith("Attribute", StringComparison.Ordinal))
            name = name[..^"Attribute".Length];

        return name.EndsWith("Fact", StringComparison.Ordinal)
            || name.EndsWith("Theory", StringComparison.Ordinal);
    }

    /// <summary><c>[Fact(Skip = "…")]</c> — the body never runs, so a return inside it is inert.</summary>
    private static bool HasExplicitSkip(AttributeSyntax attribute) =>
        attribute.ArgumentList?.Arguments.Any(a =>
            a.NameEquals?.Name.Identifier.ValueText == "Skip") == true;

    /// <summary><c>return;</c> and the Task-shaped equivalents that also assert nothing.</summary>
    private static bool IsValuelessReturn(ReturnStatementSyntax ret)
    {
        if (ret.Expression is null) return true;

        var text = ret.Expression.ToString().Replace(" ", string.Empty);
        return text is "default"
            or "Task.CompletedTask"
            or "ValueTask.CompletedTask"
            or "Task.FromResult(0)";
    }

    /// <summary>
    /// True when the nearest enclosing function is <paramref name="method"/> itself — i.e. the
    /// return exits the TEST, not a callback the test happens to pass to something.
    /// </summary>
    private static bool BelongsDirectlyTo(SyntaxNode ret, MethodDeclarationSyntax method)
    {
        for (var node = ret.Parent; node is not null; node = node.Parent)
        {
            if (ReferenceEquals(node, method)) return true;

            if (node is SimpleLambdaExpressionSyntax
                or ParenthesizedLambdaExpressionSyntax
                or AnonymousMethodExpressionSyntax
                or LocalFunctionStatementSyntax
                or AccessorDeclarationSyntax
                or MethodDeclarationSyntax)
                return false;
        }

        return false;
    }

    /// <summary>
    /// Does at least one assertion appear textually before <paramref name="position"/>? Covers
    /// xUnit's <c>Assert.*</c>, FluentAssertions' <c>.Should()</c> chains, and Moq's
    /// <c>.Verify(…)</c> — the three families this repo asserts with.
    /// </summary>
    private static bool AssertionPrecedes(BlockSyntax body, int position) =>
        body.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(inv => inv.SpanStart < position)
            .Any(inv => inv.Expression is MemberAccessExpressionSyntax member && IsAssertion(member));

    private static bool IsAssertion(MemberAccessExpressionSyntax member)
    {
        var name = member.Name.Identifier.ValueText;
        if (name is "Should" or "Verify" or "VerifyAll" or "VerifyNoOtherCalls") return true;

        // Assert.True(...), Assert.Equal(...), Xunit.Assert.NotNull(...)
        return member.Expression.ToString() is "Assert" or "Xunit.Assert";
    }

    /// <summary>The <c>if</c> condition that lets this return fire — quoted verbatim in the report.</summary>
    private static string DescribeGuard(SyntaxNode ret, MethodDeclarationSyntax method)
    {
        for (var node = ret.Parent; node is not null && !ReferenceEquals(node, method); node = node.Parent)
        {
            if (node is IfStatementSyntax ifStatement)
                return $"if ({Collapse(ifStatement.Condition.ToString())})";
        }

        return "(unconditional)";
    }

    private static string Collapse(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
