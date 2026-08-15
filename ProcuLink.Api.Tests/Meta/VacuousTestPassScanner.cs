using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ProcuLink.Api.Tests.Architecture;

namespace ProcuLink.Api.Tests.Meta;

/// <summary>The rule a <see cref="VacuousPass"/> broke. Quoted verbatim in the failure report.</summary>
public static class VacuousRule
{
    /// <summary>A <c>return</c>/<c>goto</c>/<c>break</c>/<c>continue</c> that skips the assertions.</summary>
    public const string EarlyExit = "early-exit-before-assert";

    /// <summary>The body contains no assertion at all, anywhere.</summary>
    public const string NoAssertion = "no-assertion-at-all";

    /// <summary>Every assertion sits behind an <c>if</c>, a loop, or a <c>catch</c> that may never run.</summary>
    public const string ConditionalOnly = "every-assertion-is-conditional";

    /// <summary>An assertion that cannot fail — <c>Assert.True(true)</c> and its family.</summary>
    public const string Tautology = "tautological-assertion";

    /// <summary>A <c>catch</c> that absorbs the exception an assertion failure throws.</summary>
    public const string SwallowedFailure = "swallowed-assertion-failure";
}

/// <summary>One test method that can exit reporting Passed without verifying anything.</summary>
public sealed record VacuousPass(
    string RelativePath,
    int Line,
    string TestName,
    string Rule,
    string Guard,
    string Statement)
{
    public override string ToString() =>
        $"{RelativePath}({Line})  {TestName}  [{Rule}]  —  {Guard} {Statement}";
}

/// <summary>
/// Source scanner behind <see cref="NoVacuousTestPassTests"/>.
///
/// <para><b>The rule.</b> A test method must verify something on every path a green run can take.
/// xUnit records <b>Passed</b> for any method that returns without throwing, so a body that
/// silently does nothing is indistinguishable from a body that checked the world — which is how
/// <c>Live_ImapIngress</c> stayed broken for two and a half weeks while reporting green. A test
/// that genuinely cannot run in the current environment must say so through a declared skip
/// (<c>[DockerRequiredFact]</c>, <c>[EnvironmentGatedFact]</c>, <c>[LocalPostgresRequiredFact]</c>,
/// <c>[Fact(Skip = "…")]</c>) which xUnit reports with a human reason.</para>
///
/// <para><b>Five rules, not one.</b> The first version of this scanner enforced only
/// <see cref="VacuousRule.EarlyExit"/>, and an adversarial pass showed eight distinct shapes
/// scanning clean while verifying nothing. Its green therefore read as "every test asserts
/// something" when it only meant "no bare early return precedes an assertion". The four rules
/// added here close those shapes; see <c>NoVacuousTestPassTests</c>, which carries a caught/allowed
/// pair for every one of them.</para>
///
/// <para><b>Why structural, not env-var-specific.</b> Gating on
/// <c>Environment.GetEnvironmentVariable</c> alone would have missed the two worst real offenders
/// found by the original scan: a lost-update concurrency test gated on a live local Postgres
/// socket, and a parser test gated on <c>File.Exists</c>. "Cannot verify anything" catches every
/// escape hatch, including ones nobody has invented yet.</para>
///
/// <para>Statements nested inside a lambda, an anonymous method or a local function belong to that
/// inner function, not to the test. Neither their exits nor their assertions count for the test —
/// a callback that is never invoked asserts nothing, which is the scope-blindness the original
/// <c>AssertionPrecedes</c> had. Non-test helper methods and tests whose attribute already carries
/// an explicit <c>Skip</c> are not scanned at all.</para>
/// </summary>
public static class VacuousTestPassScanner
{
    /// <summary>
    /// Top-level project-directory suffixes that hold test code.
    ///
    /// <para><c>.TestSupport</c> is here because <c>ProcuLink.TestSupport</c> is compiled into two
    /// of the three test assemblies through a linked <c>&lt;Compile Include&gt;</c> item, yet its
    /// directory name does not end in <c>.Tests</c>. It held zero <c>[Fact]</c>s when this was
    /// found, so the hole was latent rather than live — but a shared file compiled into every test
    /// assembly is the last place a vacuous test should be able to hide. <see cref="TestCodeRoots"/>
    /// has a companion assertion that no other top-level directory with "Test" in its name is
    /// silently left out.</para>
    /// </summary>
    private static readonly string[] TestProjectSuffixes = { ".Tests", ".TestSupport" };

    /// <summary>
    /// Walks up from the test assembly's output directory to the folder holding
    /// <c>ProcuLink.slnx</c>. Throws rather than returning null: a scanner that cannot find the
    /// source tree must fail loudly, not quietly scan nothing.
    /// </summary>
    public static string FindRepoRoot() => RepoSourceCorpus.FindRepoRoot();

    /// <summary>
    /// The top-level directories this scanner treats as test code, repo-root-relative.
    ///
    /// <para>Which top-level directories belong to this checkout is
    /// <see cref="RepoSourceCorpus.TopLevelDirectories"/>'s question — dot-directories and anything
    /// git does not track are not this repository, however plausibly they are named. A copy of the
    /// repo dropped beside the projects contains an <c>*.Tests</c> directory too.</para>
    /// </summary>
    public static IReadOnlyList<string> TestCodeRoots(string repoRoot) =>
        RepoSourceCorpus.TopLevelDirectories(repoRoot)
            .Where(IsTestProjectDirectory)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Every C# source file belonging to a test project of this checkout.</summary>
    public static IReadOnlyList<string> TestSourceFiles(string repoRoot)
    {
        var roots = TestCodeRoots(repoRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return RepoSourceCorpus.CsFiles(repoRoot)
            .Where(f => roots.Contains(TopLevelOf(f.Relative)))
            .Select(f => f.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsTestProjectDirectory(string name) =>
        TestProjectSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal));

    private static string TopLevelOf(string relative)
    {
        var slash = relative.IndexOf('/');
        return slash < 0 ? relative : relative[..slash];
    }

    /// <summary>Scans every test source file under <paramref name="repoRoot"/>.</summary>
    public static IReadOnlyList<VacuousPass> ScanRepository(string repoRoot) =>
        TestSourceFiles(repoRoot)
            .SelectMany(path => Scan(File.ReadAllText(path), RelativePath(repoRoot, path)))
            .ToList();

    /// <summary>Repo-root-relative, forward-slashed — the form used for both filtering and reporting.</summary>
    public static string RelativePath(string repoRoot, string absolutePath) =>
        Path.GetRelativePath(repoRoot, absolutePath).Replace('\\', '/');

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

            if (testAttributes.Any(HasExplicitSkip)) continue; // declared skip: the body never runs

            var report = new Reporter(tree, displayPath, method, found);

            // A helper's assertions ARE the test's assertions, so a catch that eats them is the
            // same defect wherever it lives — and moving the try into a helper would otherwise be
            // a way to walk past this rule. The other four rules do not transfer: a helper
            // legitimately returns early, and legitimately asserts only under a condition.
            ScanSwallowedFailures(body, method, report);

            if (testAttributes.Count == 0) continue;           // a helper, not a test

            ScanTautologies(body, method, report);
            ScanEarlyExits(body, method, report);
            ScanAssertionReach(body, method, report);
        }

        return found;
    }

    // ── rule: early-exit-before-assert ────────────────────────────────────────

    /// <summary>
    /// A <c>return</c> that reaches the runner before any assertion has run is the canonical
    /// offender. <c>goto</c> jumps past the assertions the same way. <c>break</c> and
    /// <c>continue</c> only skip the rest of a loop iteration, so they are reported solely when
    /// nothing outside that loop asserts either — otherwise the test still verifies something on
    /// every path and the report would be noise.
    /// </summary>
    private static void ScanEarlyExits(BlockSyntax body, MethodDeclarationSyntax method, Reporter report)
    {
        var assertions = RealAssertions(body).Where(a => BelongsDirectlyTo(a, method)).ToList();
        var hasUnconditionalAssertion = assertions.Any(a => !IsConditional(a, method));

        foreach (var statement in body.DescendantNodes())
        {
            var (isExit, requiresNoOtherAssertion) = statement switch
            {
                ReturnStatementSyntax ret => (IsValuelessReturn(ret), false),
                GotoStatementSyntax => (true, false),
                BreakStatementSyntax => (true, true),
                ContinueStatementSyntax => (true, true),
                _ => (false, false),
            };

            if (!isExit) continue;
            if (!BelongsDirectlyTo(statement, method)) continue; // a lambda's / local fn's exit
            if (assertions.Any(a => a.SpanStart < statement.SpanStart)) continue;
            if (requiresNoOtherAssertion && hasUnconditionalAssertion) continue;

            report.Add(statement, VacuousRule.EarlyExit, DescribeGuard(statement, method), Collapse(statement.ToString()));
        }
    }

    // ── rules: no-assertion-at-all / every-assertion-is-conditional ───────────

    /// <summary>
    /// Two halves of one question — <i>can this body verify anything on a green path?</i>
    ///
    /// <para>No assertion anywhere is the blunt case. The subtle one is a body whose every
    /// assertion sits inside an <c>if</c> that may be false, a <c>foreach</c> over a collection
    /// that may be empty, or a <c>catch</c> for an exception that may never be thrown. Both report
    /// Passed having checked nothing, and neither contains an early return, so the original
    /// single-rule scanner was blind to both.</para>
    ///
    /// <para>Assertions inside a lambda do not count: <c>Assert.All(items, x =&gt; …)</c> is fine
    /// because the outer <c>Assert.All</c> is itself the assertion, but a callback handed to
    /// production code may never be invoked.</para>
    /// </summary>
    private static void ScanAssertionReach(BlockSyntax body, MethodDeclarationSyntax method, Reporter report)
    {
        var assertions = RealAssertions(body).Where(a => BelongsDirectlyTo(a, method)).ToList();

        if (assertions.Count == 0)
        {
            report.Add(method.Identifier, VacuousRule.NoAssertion, "(no Assert / .Should() / .Verify() reaches the runner)", string.Empty);
            return;
        }

        if (assertions.Any(a => !IsConditional(a, method))) return;

        var first = assertions[0];
        report.Add(first, VacuousRule.ConditionalOnly, DescribeGuard(first, method), Collapse(TrimTo(first.ToString(), 80)));
    }

    // ── rule: tautological-assertion ──────────────────────────────────────────

    /// <summary>
    /// <c>Assert.True(true)</c> runs, reports Passed, and proves nothing about the system. It is
    /// worse than no assertion, because it also satisfies every "does this test assert?" check
    /// including this scanner's own <see cref="VacuousRule.NoAssertion"/> rule — which is why
    /// <see cref="RealAssertions"/> filters these out before the other rules count anything.
    /// </summary>
    private static void ScanTautologies(BlockSyntax body, MethodDeclarationSyntax method, Reporter report)
    {
        foreach (var anchor in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!IsAssertionInvocation(anchor)) continue;
            if (!IsTautology(anchor)) continue;
            if (!BelongsDirectlyTo(anchor, method)) continue;

            report.Add(anchor, VacuousRule.Tautology, "(cannot fail)",
                Collapse(TrimTo(WholeChain(anchor).ToString(), 80)));
        }
    }

    // ── rule: swallowed-assertion-failure ─────────────────────────────────────

    /// <summary>
    /// An assertion failure is an exception. A <c>catch</c> around assertions that neither
    /// re-throws nor asserts converts every failure into a green run — the loudest possible
    /// vacuous pass, since the test may have genuinely detected the bug and then eaten the report.
    /// A <c>catch</c> around setup or teardown that holds no assertion is left alone.
    ///
    /// <para><b>The collect-and-report idiom is not a swallow.</b> A matrix test that catches each
    /// cell's exception into a <c>failures</c> list and then asserts <c>failures.Should().BeEmpty()</c>
    /// after the sweep reports every failure — it just defers the report so the message can name all
    /// of them at once. That is better practice than failing on the first cell, and flagging it would
    /// have made this rule noise. So a non-empty catch is accepted when an unconditional assertion
    /// follows the <c>try</c>. An <i>empty</i> catch is never accepted: it cannot have recorded
    /// anything, so whatever it absorbed is gone regardless of what runs later.</para>
    ///
    /// <para>Known limit, stated rather than hidden: a non-empty catch followed by an assertion on
    /// something <i>unrelated</i> still passes this rule. Closing that needs dataflow, not syntax.</para>
    /// </summary>
    private static void ScanSwallowedFailures(BlockSyntax body, MethodDeclarationSyntax method, Reporter report)
    {
        foreach (var tryStatement in body.DescendantNodes().OfType<TryStatementSyntax>())
        {
            if (!BelongsDirectlyTo(tryStatement, method)) continue;
            if (!RealAssertions(tryStatement.Block).Any()) continue; // guarding cleanup, not assertions

            var reportedLater = ReportsAfter(tryStatement, body, method);

            foreach (var clause in tryStatement.Catches)
            {
                if (clause.Block.DescendantNodes().OfType<ThrowStatementSyntax>().Any()) continue;
                if (RealAssertions(clause.Block).Any()) continue;
                if (clause.Block.Statements.Count > 0 && reportedLater) continue;

                var caught = clause.Declaration?.Type.ToString() ?? "everything";
                var how = clause.Block.Statements.Count == 0
                    ? "is empty, so the failure it absorbs is gone"
                    : "absorbs the exception a failing assertion throws, and nothing downstream reports it";

                report.Add(clause, VacuousRule.SwallowedFailure, $"catch ({caught})", how);
            }
        }
    }

    /// <summary>Is there an unconditional assertion after this <c>try</c> that could report what it caught?</summary>
    private static bool ReportsAfter(TryStatementSyntax tryStatement, BlockSyntax body, MethodDeclarationSyntax method) =>
        RealAssertions(body).Any(assertion =>
            assertion.SpanStart > tryStatement.Span.End
            && BelongsDirectlyTo(assertion, method)
            && !IsConditional(assertion, method));

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

    /// <summary>
    /// <c>return;</c> and the async-shaped equivalents that also assert nothing.
    ///
    /// <para>Recognised structurally rather than by a literal list of spellings: the first version
    /// hardcoded four strings, so <c>return default(Task);</c> and <c>return new ValueTask();</c>
    /// walked straight through it. Anything that yields an already-completed, valueless task is an
    /// early exit no matter how it is spelled.</para>
    /// </summary>
    private static bool IsValuelessReturn(ReturnStatementSyntax ret)
    {
        if (ret.Expression is null) return true;

        var text = Collapse(ret.Expression.ToString()).Replace(" ", string.Empty);

        // default / default(Task) / default(ValueTask) / new ValueTask() / new ValueTask<T>()
        if (text == "default"
            || text.StartsWith("default(", StringComparison.Ordinal)
            || text.StartsWith("newValueTask(", StringComparison.Ordinal)
            || text.StartsWith("newTask(", StringComparison.Ordinal))
            return true;

        // Task.CompletedTask / ValueTask.CompletedTask / Task.Delay(0) / Task.Yield()
        if (text is "Task.CompletedTask" or "ValueTask.CompletedTask" or "Task.Yield()" or "Task.Delay(0)")
            return true;

        // Task.FromResult(…) / ValueTask.FromResult(…) with a constant, verified nothing.
        var fromResult = text.IndexOf(".FromResult", StringComparison.Ordinal);
        if (fromResult > 0)
        {
            var receiver = text[..fromResult];
            if (receiver is "Task" or "ValueTask")
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when the nearest enclosing function is <paramref name="method"/> itself — i.e. the
    /// node runs when the TEST runs, not when a callback the test handed to something is invoked.
    /// </summary>
    private static bool BelongsDirectlyTo(SyntaxNode node, MethodDeclarationSyntax method)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, method)) return true;

            if (current is SimpleLambdaExpressionSyntax
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
    /// Does this node sit behind a branch that may not be taken? <c>if</c>/<c>else</c>, every loop
    /// form, <c>catch</c>, a ternary and a <c>switch</c> section all qualify.
    ///
    /// <para>Three deliberate exceptions, each one a loop the compiler can already see will run.
    /// A <c>foreach</c> over a collection <i>literal</i> always iterates, so a table-driven test
    /// written as <c>foreach (var c in new[] { … })</c> is not conditional. A <c>for</c> counting
    /// between two integer literals — <c>for (var i = 0; i &lt; 3; i++)</c> — always iterates too.
    /// And a <c>switch</c> whose sections all assert or throw, <c>default</c> included, always
    /// verifies something whichever arm runs. Flagging any of those three would be noise with no
    /// available fix, and a guard people learn to wave through stops being a guard.</para>
    /// </summary>
    private static bool IsConditional(SyntaxNode node, MethodDeclarationSyntax method)
    {
        for (var current = node.Parent; current is not null && !ReferenceEquals(current, method); current = current.Parent)
        {
            switch (current)
            {
                case IfStatementSyntax:
                case ElseClauseSyntax:
                case WhileStatementSyntax:
                case DoStatementSyntax:
                case CatchClauseSyntax:
                case ConditionalExpressionSyntax:
                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.CoalesceExpression):
                    return true;

                case ForStatementSyntax counted when !IteratesAtLeastOnce(counted):
                    return true;

                // CommonForEachStatementSyntax, not ForEachStatementSyntax: a DECONSTRUCTING
                // foreach — `foreach (var (file, reason) in AcceptedExceptions)` — is a
                // ForEachVariableStatementSyntax, a different node type with the same meaning.
                // Matching only the plain form let two allowlist-sweeping guards scan clean.
                case CommonForEachStatementSyntax loop when !IteratesAtLeastOnce(loop):
                    return true;

                case SwitchSectionSyntax section when !AlwaysVerifies(section):
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A <c>foreach</c> over a non-empty collection literal cannot iterate zero times — including
    /// the overwhelmingly common table-driven spelling where the literal is one statement up:
    /// <c>var cases = new[] { … }; foreach (var (a, b) in cases)</c>.
    /// </summary>
    private static bool IteratesAtLeastOnce(CommonForEachStatementSyntax loop) =>
        IsNonEmptyLiteralCollection(ResolveLocalInitializer(loop.Expression));

    /// <summary>
    /// Follows a bare identifier back to its local declaration's initializer, so a literal assigned
    /// to a variable reads the same as a literal written inline. Deliberately conservative: it gives
    /// up on a name declared more than once, and on one that is ever reassigned — in both cases the
    /// initializer no longer tells us what the loop will actually read.
    /// </summary>
    private static ExpressionSyntax ResolveLocalInitializer(ExpressionSyntax expression)
    {
        if (expression is not IdentifierNameSyntax identifier) return expression;
        if (expression.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Body is not { } body)
            return expression;

        var name = identifier.Identifier.ValueText;

        if (body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Any(a => a.Left is IdentifierNameSyntax target && target.Identifier.ValueText == name))
            return expression;

        var declarations = body.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Identifier.ValueText == name)
            .ToList();

        return declarations is [{ Initializer.Value: { } value }] ? value : expression;
    }

    private static bool IsNonEmptyLiteralCollection(ExpressionSyntax expression) => expression switch
    {
        ArrayCreationExpressionSyntax array => array.Initializer?.Expressions.Count > 0,
        ImplicitArrayCreationExpressionSyntax implicitArray => implicitArray.Initializer.Expressions.Count > 0,
        CollectionExpressionSyntax collection => collection.Elements.Count > 0,
        ObjectCreationExpressionSyntax creation => creation.Initializer?.Expressions.Count > 0,
        ImplicitObjectCreationExpressionSyntax implicitCreation => implicitCreation.Initializer?.Expressions.Count > 0,
        _ => false,
    };

    /// <summary>
    /// A <c>for</c> counting from one integer literal to another — <c>for (var i = 0; i &lt; 3; i++)</c>
    /// — runs a known number of times. Anything whose bound is computed (<c>i &lt; Ladder.Length</c>)
    /// is not exempt: an empty <c>Ladder</c> is exactly the silent-zero-iterations case.
    /// </summary>
    private static bool IteratesAtLeastOnce(ForStatementSyntax loop)
    {
        if (loop.Declaration?.Variables is not [{ Initializer.Value: { } startExpression }]) return false;
        if (loop.Condition is not BinaryExpressionSyntax condition) return false;
        if (condition.Left is not IdentifierNameSyntax) return false;

        if (!TryReadInt(startExpression, out var start)) return false;
        if (!TryReadInt(condition.Right, out var bound)) return false;

        return condition.Kind() switch
        {
            SyntaxKind.LessThanExpression => start < bound,
            SyntaxKind.LessThanOrEqualExpression => start <= bound,
            SyntaxKind.GreaterThanExpression => start > bound,
            SyntaxKind.GreaterThanOrEqualExpression => start >= bound,
            _ => false,
        };
    }

    private static bool TryReadInt(ExpressionSyntax expression, out int value)
    {
        value = 0;
        return expression is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.NumericLiteralExpression)
            && int.TryParse(literal.Token.ValueText, out value);
    }

    /// <summary>
    /// True when EVERY arm of the enclosing switch — <c>default</c> present and included — either
    /// asserts or throws, so exactly one of them runs and it always verifies something.
    /// </summary>
    private static bool AlwaysVerifies(SwitchSectionSyntax section)
    {
        if (section.Parent is not SwitchStatementSyntax parent) return false;

        var hasDefault = parent.Sections.Any(s => s.Labels.Any(l => l.IsKind(SyntaxKind.DefaultSwitchLabel)));
        if (!hasDefault) return false;

        return parent.Sections.All(s =>
            RealAssertions(s).Any() || s.DescendantNodes().OfType<ThrowStatementSyntax>().Any());
    }

    /// <summary>
    /// Every assertion that could actually fail — <see cref="IsAssertionInvocation"/> minus the
    /// tautologies. <c>Assert.True(true)</c> must not satisfy "this test asserts something".
    /// </summary>
    private static IEnumerable<InvocationExpressionSyntax> RealAssertions(SyntaxNode scope) =>
        scope.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(inv => IsAssertionInvocation(inv) && !IsTautology(inv));

    /// <summary>
    /// The three assertion families this repo uses — xUnit's <c>Assert.*</c>, FluentAssertions'
    /// <c>.Should()</c> chains and Moq's <c>.Verify*</c> — plus locally-named assertion helpers
    /// (<c>AssertOrderIsDelivered(…)</c>, <c>VerifyDispatch(…)</c>, <c>ExpectRefusal(…)</c>).
    /// Helper recognition is deliberately generous: a false "this test asserts nothing" is a
    /// worse outcome here than a missed exotic helper, because it teaches people to distrust the guard.
    /// </summary>
    private static bool IsAssertionInvocation(InvocationExpressionSyntax invocation)
    {
        var name = InvokedName(invocation);
        if (name is null) return false;

        if (name is "Should" or "Verify" or "VerifyAll" or "VerifyNoOtherCalls") return true;

        if (name.StartsWith("Assert", StringComparison.Ordinal)
            || name.StartsWith("Verify", StringComparison.Ordinal)
            || name.StartsWith("Expect", StringComparison.Ordinal)
            || name.StartsWith("Should", StringComparison.Ordinal))
            return true;

        // Assert.Equal(…), Xunit.Assert.NotNull(…)
        return invocation.Expression is MemberAccessExpressionSyntax member
            && member.Expression.ToString() is "Assert" or "Xunit.Assert";
    }

    private static string? InvokedName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>
    /// An assertion whose outcome is fixed at compile time: <c>Assert.True(true)</c>,
    /// <c>Assert.Equal(x, x)</c>, <c>Assert.NotNull(new Thing())</c>, <c>x.Should().Be(x)</c>.
    ///
    /// <para>Takes the ANCHOR node — the invocation <see cref="IsAssertionInvocation"/> recognised.
    /// For xUnit that is the assertion call itself. For FluentAssertions it is <c>.Should()</c>,
    /// and the claim lives in the call chained onto it: <c>.Be(x)</c> alone is not recognisable as
    /// an assertion (its receiver is <c>x.Should()</c>, not <c>Assert</c>), so a check written
    /// against it would never run — which is exactly what an earlier draft did, silently reporting
    /// two of four planted tautologies and counting <c>x.Should().Be(x)</c> as a real assertion.</para>
    /// </summary>
    private static bool IsTautology(InvocationExpressionSyntax anchor)
    {
        if (anchor.Expression is MemberAccessExpressionSyntax member
            && member.Expression.ToString() is "Assert" or "Xunit.Assert")
        {
            var arguments = anchor.ArgumentList.Arguments;
            return InvokedName(anchor) switch
            {
                "True" when arguments.Count > 0 => Text(arguments[0]) == "true",
                "False" when arguments.Count > 0 => Text(arguments[0]) == "false",
                "Equal" or "StrictEqual" or "Same" when arguments.Count >= 2 =>
                    Text(arguments[0]) == Text(arguments[1]) && IsStableValue(arguments[0].Expression),
                "NotNull" when arguments.Count == 1 => IsFreshlyConstructed(arguments[0].Expression),
                _ => false,
            };
        }

        if (InvokedName(anchor) != "Should") return false;
        if (anchor.Expression is not MemberAccessExpressionSyntax shouldMember) return false;
        if (anchor.Parent is not MemberAccessExpressionSyntax chained) return false;
        if (chained.Parent is not InvocationExpressionSyntax claim) return false;

        var receiver = Collapse(shouldMember.Expression.ToString());
        var claimArguments = claim.ArgumentList.Arguments;

        return chained.Name.Identifier.ValueText switch
        {
            "BeTrue" => receiver == "true",
            "BeFalse" => receiver == "false",
            "Be" or "BeSameAs" or "BeEquivalentTo" when claimArguments.Count >= 1 =>
                receiver == Text(claimArguments[0]) && IsStableValue(shouldMember.Expression),
            "NotBeNull" => IsFreshlyConstructed(shouldMember.Expression),
            _ => false,
        };
    }

    /// <summary>
    /// Can this expression be evaluated twice and be guaranteed to give the same answer?
    ///
    /// <para>It is the difference between a tautology and a real test.
    /// <c>total.Should().Be(total)</c> compares a value to itself and cannot fail.
    /// <c>Hash(key).Should().Be(Hash(key))</c> is textually identical and is a <b>determinism</b>
    /// check — the whole point is that the function might not agree with itself. Two real tests
    /// (<c>ApiKeyHasherTests</c>, <c>DeliveryServiceIdempotencyTests</c>) were flagged by an earlier
    /// draft that compared only the text.</para>
    /// </summary>
    private static bool IsStableValue(ExpressionSyntax expression) =>
        !expression.DescendantNodesAndSelf().Any(node => node is
            InvocationExpressionSyntax
            or AwaitExpressionSyntax
            or ObjectCreationExpressionSyntax
            or ImplicitObjectCreationExpressionSyntax);

    /// <summary>The whole <c>x.Should().Be(y)</c> expression, so the report quotes the claim not the anchor.</summary>
    private static SyntaxNode WholeChain(InvocationExpressionSyntax anchor) =>
        anchor.Parent is MemberAccessExpressionSyntax chained
        && chained.Parent is InvocationExpressionSyntax claim
            ? claim
            : anchor;

    /// <summary>An object built one line earlier cannot be null, so asserting that proves nothing.</summary>
    private static bool IsFreshlyConstructed(ExpressionSyntax expression) => expression is
        ObjectCreationExpressionSyntax
        or ImplicitObjectCreationExpressionSyntax
        or AnonymousObjectCreationExpressionSyntax
        or ArrayCreationExpressionSyntax
        or ImplicitArrayCreationExpressionSyntax
        or CollectionExpressionSyntax;

    /// <summary>The <c>if</c>/loop/<c>catch</c> that lets this node be skipped — quoted verbatim.</summary>
    private static string DescribeGuard(SyntaxNode node, MethodDeclarationSyntax method)
    {
        for (var current = node.Parent; current is not null && !ReferenceEquals(current, method); current = current.Parent)
        {
            switch (current)
            {
                case IfStatementSyntax ifStatement:
                    return $"if ({Collapse(ifStatement.Condition.ToString())})";
                case ElseClauseSyntax:
                    return "else";
                case CommonForEachStatementSyntax loop:
                    return $"foreach (… in {Collapse(TrimTo(loop.Expression.ToString(), 40))})";
                case ForStatementSyntax or WhileStatementSyntax or DoStatementSyntax:
                    return "inside a loop that may not iterate";
                case CatchClauseSyntax catchClause:
                    return $"only inside catch ({catchClause.Declaration?.Type.ToString() ?? "…"})";
                case SwitchSectionSyntax:
                    return "only inside one switch arm";
            }
        }

        return "(unconditional)";
    }

    private static string Text(ArgumentSyntax argument) => Collapse(argument.Expression.ToString());

    private static string TrimTo(string text, int max) =>
        text.Length <= max ? text : text[..max] + " …";

    private static string Collapse(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Turns a syntax node into a <see cref="VacuousPass"/> with the right file and line.</summary>
    private sealed class Reporter(
        SyntaxTree tree,
        string displayPath,
        MethodDeclarationSyntax method,
        List<VacuousPass> sink)
    {
        public void Add(SyntaxNode node, string rule, string guard, string statement) =>
            Add(node.Span, rule, guard, statement);

        public void Add(SyntaxToken token, string rule, string guard, string statement) =>
            Add(token.Span, rule, guard, statement);

        private void Add(TextSpan span, string rule, string guard, string statement) =>
            sink.Add(new VacuousPass(
                displayPath,
                tree.GetLineSpan(span).StartLinePosition.Line + 1,
                method.Identifier.ValueText,
                rule,
                guard,
                statement));
    }
}
