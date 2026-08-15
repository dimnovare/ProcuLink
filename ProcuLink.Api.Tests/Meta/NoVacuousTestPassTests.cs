using FluentAssertions;
using ProcuLink.Api.Tests.Architecture;
using Xunit;

namespace ProcuLink.Api.Tests.Meta;

/// <summary>
/// The Wave 0 leaving gate: <b>no test may pass vacuously.</b>
///
/// <para>Every "the tests pass" claim in this repository is only worth the suite's ability to tell
/// "ran and asserted" apart from "silently did nothing". Before this guard it could not:
/// <c>Live_ImapIngress</c> was broken by <c>de4ea0e</c> and reported <b>Passed</b> on every run for
/// two and a half weeks, because its first statement was
/// <c>if (Environment.GetEnvironmentVariable("PROCULINK_LIVE_ENDPOINT_TESTS") != "1") return;</c>
/// and xUnit counts a method that returns without throwing as a pass.</para>
///
/// <para>Un-runnable tests must instead declare a skip that xUnit reports with a human reason —
/// <c>[DockerRequiredFact]</c>, <c>[EnvironmentGatedFact]</c>, <c>[LocalPostgresRequiredFact]</c>,
/// or plain <c>[Fact(Skip = "…")]</c>. The scanner's rules and their rationale live in
/// <see cref="VacuousTestPassScanner"/>.</para>
///
/// <para><b>A guard whose green is narrower than it reads is itself a vacuous pass.</b> The first
/// version of this file enforced one rule — a bare early <c>return</c> — and an adversarial pass
/// then walked eight other shapes straight through it: an <c>if</c> with no <c>return</c> in it, a
/// <c>foreach</c> over an empty collection, an assertion parked inside a lambda, <c>Assert.True(true)</c>,
/// <c>goto</c>, <c>continue</c>, two unlisted spellings of "return an already-completed task", and a
/// whole project outside the glob. Its green therefore meant "no bare early return", not "every test
/// asserts something" — which is what everyone read it as. Every one of those eight now has a caught
/// case below, and every shape with a legitimate twin has an allowed case beside it, because a guard
/// that cries wolf gets waved through and stops being a guard.</para>
/// </summary>
public class NoVacuousTestPassTests
{
    // ── the guard ────────────────────────────────────────────────────────────

    [Fact]
    public void NoTestMethodExitsBeforeAssertingAnything()
    {
        var repoRoot = VacuousTestPassScanner.FindRepoRoot();
        var offenders = VacuousTestPassScanner.ScanRepository(repoRoot);

        Assert.True(offenders.Count == 0, BuildReport(offenders));
    }

    /// <summary>
    /// The guard above must not itself pass vacuously. If <c>FindRepoRoot</c> resolved somewhere
    /// unexpected, or the glob stopped matching, the scan would return zero offenders and go green
    /// while checking nothing — precisely the failure mode this whole file exists to kill. So
    /// assert the scan actually reached the source tree, and reached files known to contain tests.
    /// </summary>
    [Fact]
    public void TheGuardActuallyReadsTheTestProjects()
    {
        var repoRoot = VacuousTestPassScanner.FindRepoRoot();
        var files = VacuousTestPassScanner.TestSourceFiles(repoRoot);

        files.Should().HaveCountGreaterThan(200,
            "the three test projects hold hundreds of source files — a near-empty scan means the "
            + "glob or the repo-root walk broke, and the guard is checking nothing");

        var relative = files
            .Select(p => Path.GetRelativePath(repoRoot, p).Replace('\\', '/'))
            .ToList();

        relative.Should().Contain("ProcuLink.Api.Tests/Meta/NoVacuousTestPassTests.cs");
        relative.Should().Contain("ProcuLink.Infrastructure.Tests/Services/Dispatchers/LiveEndpointDeliveryTests.cs");
        relative.Should().Contain("ProcuLink.Transform.Tests/Parsing/CxmlOrderParserTests.cs");

        // Sibling agents keep full clones of this repo under .claude/worktrees. Reading them would
        // report their in-progress code as ours.
        relative.Should().NotContain(p => p.Contains(".claude/worktrees", StringComparison.OrdinalIgnoreCase));
        relative.Should().NotContain(p => p.Contains("/obj/", StringComparison.Ordinal) || p.Contains("/bin/", StringComparison.Ordinal));
    }

    // ── proof that the guard catches offenders (not merely that it is green) ──

    [Fact]
    public void Catches_TheExactPatternThatHidLiveImapIngress()
    {
        const string offender = """
            public class LiveThingTests
            {
                [Fact]
                public async Task Live_Thing_RealEndpoint()
                {
                    if (Environment.GetEnvironmentVariable("PROCULINK_LIVE_ENDPOINT_TESTS") != "1") return;
                    var actual = await Call();
                    actual.Should().Be(1);
                }
            }
            """;

        var found = VacuousTestPassScanner.Scan(offender, "Synthetic.cs");

        found.Should().ContainSingle();
        found[0].TestName.Should().Be("Live_Thing_RealEndpoint");
        found[0].Line.Should().Be(6);
        found[0].Guard.Should().Contain("PROCULINK_LIVE_ENDPOINT_TESTS");
    }

    [Fact]
    public void Catches_FilesystemGatedEarlyReturn()
    {
        const string offender = """
            public class ParserTests
            {
                [Fact]
                public void Parses_Fixture()
                {
                    if (!File.Exists(path))
                        return;
                    Parse(path).Should().NotBeNull();
                }
            }
            """;

        VacuousTestPassScanner.Scan(offender, "Synthetic.cs")
            .Should().ContainSingle().Which.Guard.Should().Be("if (!File.Exists(path))");
    }

    [Fact]
    public void Catches_InfrastructureProbeGatedEarlyReturn()
    {
        // The lost-update test this scan actually found: gated on a live socket, not an env var.
        const string offender = """
            public class ReliabilityTests
            {
                [Fact]
                public async Task TwoConcurrentFailures_IncrementByExactlyTwo()
                {
                    var admin = await TryOpenPostgresAdminAsync();
                    if (admin is null)
                        return;
                    admin.Should().NotBeNull();
                }
            }
            """;

        VacuousTestPassScanner.Scan(offender, "Synthetic.cs").Should().ContainSingle();
    }

    [Fact]
    public void Catches_TaskCompletedTaskEscapeHatch()
    {
        const string offender = """
            public class SneakyTests
            {
                [Fact]
                public Task Does_Nothing_Quietly()
                {
                    if (!Enabled) return Task.CompletedTask;
                    return Verify();
                }
            }
            """;

        VacuousTestPassScanner.Scan(offender, "Synthetic.cs").Should().ContainSingle();
    }

    [Fact]
    public void Catches_TheoryEscapeHatch_AndProjectLocalFactSubclasses()
    {
        const string offender = """
            public class MixedTests
            {
                [Theory]
                [InlineData(1)]
                public void Theory_Bails(int n)
                {
                    if (!Enabled) return;
                    n.Should().Be(1);
                }

                [DockerRequiredFact]
                public void Docker_Test_Also_Bails()
                {
                    if (!Enabled) return;
                    Assert.Equal(1, Answer);
                }
            }
            """;

        VacuousTestPassScanner.Scan(offender, "Synthetic.cs").Should().HaveCount(2);
    }

    // ── proof that the guard does not cry wolf ────────────────────────────────

    [Fact]
    public void Allows_ReturnAfterTheTestHasAlreadyAsserted()
    {
        const string fine = """
            public class OkTests
            {
                [Fact]
                public void Asserts_Then_Returns()
                {
                    result.Should().NotBeNull();
                    if (result.IsTerminal)
                        return;
                    result.Next.Should().NotBeNull();
                }
            }
            """;

        VacuousTestPassScanner.Scan(fine, "Synthetic.cs").Should().BeEmpty();
    }

    [Fact]
    public void Allows_ReturnInsideALambdaOrLocalFunction()
    {
        const string fine = """
            public class OkTests
            {
                [Fact]
                public void Uses_Callbacks()
                {
                    handler.OnEvent(e => { if (e is null) return; seen.Add(e); });
                    void Local() { if (skip) return; }
                    Local();
                    seen.Should().NotBeEmpty();
                }
            }
            """;

        VacuousTestPassScanner.Scan(fine, "Synthetic.cs").Should().BeEmpty();
    }

    [Fact]
    public void Allows_EarlyReturnInANonTestHelperOrFixture()
    {
        // DockerProbe-gated fixtures are CORRECT: the [DockerRequiredFact] on each test declares
        // the skip, and InitializeAsync simply must not start a container. Only test BODIES matter.
        const string fine = """
            public class PostgresTests : IAsyncLifetime
            {
                public async Task InitializeAsync()
                {
                    if (DockerProbe.UnavailableReason is not null)
                        return;
                    await StartContainer();
                }

                private static string? Probe()
                {
                    if (!ready) return null;
                    return "ok";
                }
            }
            """;

        VacuousTestPassScanner.Scan(fine, "Synthetic.cs").Should().BeEmpty();
    }

    [Fact]
    public void Allows_ATestThatAlreadyDeclaresItsSkip()
    {
        const string fine = """
            public class DeclaredTests
            {
                [Fact(Skip = "needs a live OpenAI vision model")]
                public void Vision_Path()
                {
                    if (!Enabled) return;
                    Assert.True(true);
                }
            }
            """;

        VacuousTestPassScanner.Scan(fine, "Synthetic.cs").Should().BeEmpty();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The eight shapes that scanned CLEAN against the first version of this guard.
    //
    // Its single rule was "a valueless return before an assertion", so its green read
    // as "every test asserts something" when it only meant "no bare early return".
    // An adversarial pass ran nine shapes past it: one — the canonical offender above —
    // was caught, and eight walked through while verifying nothing. Each has a caught
    // case here and, where the shape has a legitimate twin, an allowed case beside it
    // so tightening the rule cannot quietly start crying wolf.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Shape 1 — the guard with no <c>return</c> in it. Same effect, invisible to a return-only rule.</summary>
    [Fact]
    public void Catches_AssertionsThatOnlyRunWhenAnEnvironmentVariableIsSet()
    {
        const string offender = """
            public class LiveTests
            {
                [Fact]
                public async Task Live_Delivery()
                {
                    if (Environment.GetEnvironmentVariable("PROCULINK_LIVE_ENDPOINT_TESTS") == "1")
                    {
                        var actual = await Deliver();
                        actual.Should().Be(202);
                    }
                }
            }
            """;

        var found = VacuousTestPassScanner.Scan(offender, "Synthetic.cs");

        found.Should().ContainSingle().Which.Rule.Should().Be(VacuousRule.ConditionalOnly);
        found[0].Guard.Should().Contain("PROCULINK_LIVE_ENDPOINT_TESTS");
    }

    /// <summary>Shape 2 — zero elements means zero assertions, and the runner sees a pass.</summary>
    [Fact]
    public void Catches_TheForeachThatMayIterateZeroTimes()
    {
        const string offender = """
            public class SweepTests
            {
                [Fact]
                public void EveryRow_IsValid()
                {
                    foreach (var row in await Query())
                        row.Total.Should().BePositive();
                }
            }
            """;

        VacuousTestPassScanner.Scan(offender, "Synthetic.cs")
            .Should().ContainSingle().Which.Rule.Should().Be(VacuousRule.ConditionalOnly);
    }

    /// <summary>…but a sweep that first proves it has something to sweep is exactly the fix, not an offence.</summary>
    [Fact]
    public void Allows_AForeachGuardedByANonEmptyAssertion()
    {
        const string fine = """
            public class SweepTests
            {
                [Fact]
                public void EveryRow_IsValid()
                {
                    var rows = await Query();
                    rows.Should().HaveCount(27);
                    foreach (var row in rows)
                        row.Total.Should().BePositive();
                }
            }
            """;

        VacuousTestPassScanner.Scan(fine, "Synthetic.cs").Should().BeEmpty();
    }

    /// <summary>A table-driven loop over a literal, and a counted loop between literals, always run.</summary>
    [Fact]
    public void Allows_LoopsThatCannotIterateZeroTimes()
    {
        const string fine = """
            public class TableTests
            {
                [Fact]
                public void EveryCase_Holds()
                {
                    foreach (var c in new[] { "a", "b" })
                        Map(c).Should().NotBeNull();
                }

                [Fact]
                public void ThreeRuns_AreHarmless()
                {
                    for (var i = 0; i < 3; i++)
                        Run().Should().Be(0);
                }
            }
            """;

        VacuousTestPassScanner.Scan(fine, "Synthetic.cs").Should().BeEmpty();
    }

    /// <summary>Shape 3 — a body with no assertion in it at all passes for any reason whatsoever.</summary>
    [Fact]
    public void Catches_ATestThatAssertsNothingAtAll()
    {
        const string offender = """
            public class TokenizerTests
            {
                [Fact]
                public void Garbage_ReturnsEmptyList()
                {
                    var tokens = Tokenize("!!!!");
                }
            }
            """;

        VacuousTestPassScanner.Scan(offender, "Synthetic.cs")
            .Should().ContainSingle().Which.Rule.Should().Be(VacuousRule.NoAssertion);
    }

    /// <summary>
    /// A test asserting through a named helper is not assertion-free. Helper recognition is
    /// deliberately generous — a false "this test asserts nothing" teaches people to wave the
    /// guard through, which costs more than the exotic helper it would otherwise catch.
    /// </summary>
    [Fact]
    public void Allows_ATestThatAssertsThroughANamedHelper()
    {
        const string fine = """
            public class DeliveryTests
            {
                [Fact]
                public void Delivered_OrderIsMarked()
                {
                    var order = Deliver();
                    AssertOrderIsDelivered(order);
                }
            }
            """;

        VacuousTestPassScanner.Scan(fine, "Synthetic.cs").Should().BeEmpty();
    }

    /// <summary>
    /// Shape 4 — <c>Assert.True(true)</c> runs, reports Passed, and proves nothing. Worse than no
    /// assertion, because it also satisfies every "does this test assert?" check including this
    /// scanner's own.
    /// </summary>
    [Fact]
    public void Catches_AssertionsThatCannotFail()
    {
        const string offender = """
            public class PlaceholderTests
            {
                [Fact]
                public void Still_TodoButGreen()
                {
                    Assert.True(true);
                }

                [Fact]
                public void Compares_AValueToItself()
                {
                    var actual = Compute();
                    Assert.Equal(actual, actual);
                }

                [Fact]
                public void Asserts_AFreshObjectIsNotNull()
                {
                    new Order().Should().NotBeNull();
                }

                [Fact]
                public void Fluently_ComparesAValueToItself()
                {
                    total.Should().Be(total);
                }
            }
            """;

        var found = VacuousTestPassScanner.Scan(offender, "Synthetic.cs");

        found.Where(f => f.Rule == VacuousRule.Tautology).Should().HaveCount(4);
    }

    /// <summary>
    /// Comparing two calls to the same function is a DETERMINISM check, not a tautology — the
    /// point is that the function might not agree with itself. Textually identical, semantically
    /// the opposite. An earlier draft compared only the text and flagged two real tests
    /// (<c>ApiKeyHasherTests.ComputeHash_SameInput_SameOutput</c> and
    /// <c>DeliveryServiceIdempotencyTests</c>), which would have been the guard crying wolf on
    /// exactly the kind of test it exists to protect.
    /// </summary>
    [Fact]
    public void Allows_ADeterminismCheckThatComparesTwoCallsOfTheSameFunction()
    {
        const string fine = """
            public class HasherTests
            {
                [Fact]
                public void ComputeHash_SameInput_SameOutput()
                {
                    var key = "plk_aaa";
                    Hasher.ComputeHash(key, Secret).Should().Be(Hasher.ComputeHash(key, Secret));
                }

                [Fact]
                public void Key_IsStable()
                {
                    Assert.Equal(BuildKey(orderId, artifactA), BuildKey(orderId, artifactA));
                }
            }
            """;

        VacuousTestPassScanner.Scan(fine, "Synthetic.cs").Should().BeEmpty();
    }

    /// <summary>
    /// The table-driven spelling this repo actually uses puts the literal one statement above the
    /// loop. A rule that only recognised an inline literal would flag every one of them.
    /// </summary>
    [Fact]
    public void Allows_AForeachOverALocalCollectionLiteral()
    {
        const string fine = """
            public class MatrixTests
            {
                [Fact]
                public async Task AllFormats_Survive()
                {
                    var cases = new[]
                    {
                        ("csv",  new CsvOrderParser()),
                        ("ubl",  new UblOrderParser()),
                    };

                    foreach (var (label, parser) in cases)
                        (await parser.ParseAsync()).Lines.Should().HaveCount(250, label);
                }
            }
            """;

        VacuousTestPassScanner.Scan(fine, "Synthetic.cs").Should().BeEmpty();
    }

    /// <summary>
    /// …but only while the variable still says what the loop reads. A name that is reassigned, or
    /// declared more than once, falls back to being treated as possibly-empty.
    /// </summary>
    [Fact]
    public void Catches_AForeachWhoseLocalIsReassignedAfterItsLiteral()
    {
        const string offender = """
            public class MatrixTests
            {
                [Fact]
                public void Filtered_Sweep()
                {
                    var cases = new[] { "csv", "ubl" };
                    cases = cases.Where(Enabled).ToArray();

                    foreach (var c in cases)
                        Map(c).Should().NotBeNull();
                }
            }
            """;

        VacuousTestPassScanner.Scan(offender, "Synthetic.cs")
            .Should().ContainSingle().Which.Rule.Should().Be(VacuousRule.ConditionalOnly);
    }

    /// <summary>A tautology must not count as "this test asserts something" for the other rules.</summary>
    [Fact]
    public void ATautologyDoesNotSatisfyTheAssertSomethingRule()
    {
        const string offender = """
            public class PlaceholderTests
            {
                [Fact]
                public void Looks_Asserted()
                {
                    Assert.True(true);
                }
            }
            """;

        VacuousTestPassScanner.Scan(offender, "Synthetic.cs")
            .Select(f => f.Rule)
            .Should().BeEquivalentTo(new[] { VacuousRule.Tautology, VacuousRule.NoAssertion });
    }

    /// <summary>Shape 5 — an assertion failure is an exception, and an empty catch eats it.</summary>
    [Fact]
    public void Catches_TheCatchThatEatsTheFailure()
    {
        const string offender = """
            public class OptimisticTests
            {
                [Fact]
                public void Delivers()
                {
                    try
                    {
                        Deliver().Status.Should().Be(202);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            """;

        VacuousTestPassScanner.Scan(offender, "Synthetic.cs")
            .Should().ContainSingle().Which.Rule.Should().Be(VacuousRule.SwallowedFailure);
    }

    /// <summary>
    /// …but the collect-and-report idiom defers the report rather than losing it, and naming every
    /// failing cell at once beats failing on the first. The live IN×OUT format matrix is written
    /// this way; flagging it would have made the rule noise on its most valuable test.
    /// </summary>
    [Fact]
    public void Allows_TheCollectAndReportIdiom()
    {
        const string fine = """
            public class MatrixTests
            {
                [Fact]
                public void Matrix_HasNoUnexpectedFailures()
                {
                    var failures = new List<string>();
                    foreach (var cell in Cells)
                    {
                        try
                        {
                            AssertStructurallyValid(Run(cell));
                        }
                        catch (Exception ex)
                        {
                            failures.Add($"{cell}: {ex.Message}");
                        }
                    }

                    failures.Should().BeEmpty("every cell must produce structurally valid output");
                }
            }
            """;

        VacuousTestPassScanner.Scan(fine, "Synthetic.cs").Should().BeEmpty();
    }

    /// <summary>
    /// A helper's assertions are the test's assertions. Moving the <c>try</c> one call deeper must
    /// not be a way past the rule.
    /// </summary>
    [Fact]
    public void Catches_ASwallowingCatchInAHelperTheTestCallsInto()
    {
        const string offender = """
            public class OptimisticTests
            {
                [Fact]
                public void Delivers() => AssertDelivered();

                private static void AssertDelivered()
                {
                    try
                    {
                        Deliver().Status.Should().Be(202);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            """;

        VacuousTestPassScanner.Scan(offender, "Synthetic.cs")
            .Should().ContainSingle().Which.Rule.Should().Be(VacuousRule.SwallowedFailure);
    }

    /// <summary>
    /// A DECONSTRUCTING foreach is <c>ForEachVariableStatementSyntax</c> — a different node type
    /// from the plain <c>ForEachStatementSyntax</c>, with identical meaning. Matching only the
    /// plain form let two allowlist-sweeping guards scan clean while asserting nothing on an
    /// empty allowlist, which is the state those allowlists are explicitly designed to reach.
    /// </summary>
    [Fact]
    public void Catches_TheDeconstructingForeachThatMayIterateZeroTimes()
    {
        const string offender = """
            public class AllowlistTests
            {
                [Fact]
                public void EveryAcceptedException_StillMatchesARealSite()
                {
                    foreach (var (file, fragment, _) in AcceptedExceptions)
                        Sites().Should().Contain(s => s.File == file);
                }
            }
            """;

        VacuousTestPassScanner.Scan(offender, "Synthetic.cs")
            .Should().ContainSingle().Which.Rule.Should().Be(VacuousRule.ConditionalOnly);
    }

    /// <summary>
    /// Shape 6 — the original <c>AssertionPrecedes</c> was purely positional while
    /// <c>BelongsDirectlyTo</c> was scope-aware, so an assertion parked inside a callback counted
    /// as "the test has already asserted". A callback nobody invokes asserts nothing.
    /// </summary>
    [Fact]
    public void Catches_AnEarlyReturnExcusedByAnAssertionInsideALambda()
    {
        const string offender = """
            public class CallbackTests
            {
                [Fact]
                public async Task Handles_TheEvent()
                {
                    handler.OnEvent(e => e.Should().NotBeNull());

                    if (Environment.GetEnvironmentVariable("LIVE") != "1") return;

                    await Fire();
                }
            }
            """;

        VacuousTestPassScanner.Scan(offender, "Synthetic.cs")
            .Should().Contain(f => f.Rule == VacuousRule.EarlyExit);
    }

    /// <summary>Shape 7 — <c>goto</c> and <c>continue</c> skip the assertions a <c>return</c> would have skipped.</summary>
    [Fact]
    public void Catches_TheNonReturnEscapeHatches()
    {
        const string offender = """
            public class JumpTests
            {
                [Fact]
                public void Gotos_PastTheAssertions()
                {
                    if (!Enabled) goto done;
                    Compute().Should().Be(1);
                    done: ;
                }

                [Fact]
                public void Continues_PastTheOnlyAssertion()
                {
                    foreach (var tier in Ladder())
                    {
                        if (tier.Used < 0) continue;
                        tier.Price.Should().BePositive();
                    }
                }
            }
            """;

        var found = VacuousTestPassScanner.Scan(offender, "Synthetic.cs");

        found.Where(f => f.Rule == VacuousRule.EarlyExit)
            .Select(f => f.TestName)
            .Should().BeEquivalentTo(new[] { "Gotos_PastTheAssertions", "Continues_PastTheOnlyAssertion" });
    }

    /// <summary>
    /// …and a <c>break</c> is not an offence when something outside the loop still asserts: the
    /// test verifies on every path, so reporting it would be noise.
    /// </summary>
    [Fact]
    public void Allows_ABreakWhenTheTestStillAssertsOutsideTheLoop()
    {
        const string fine = """
            public class ScanTests
            {
                [Fact]
                public void Finds_TheFirstMatch()
                {
                    string? hit = null;
                    foreach (var row in Rows())
                    {
                        if (row.IsMatch) { hit = row.Name; break; }
                    }

                    hit.Should().Be("expected");
                }
            }
            """;

        VacuousTestPassScanner.Scan(fine, "Synthetic.cs").Should().BeEmpty();
    }

    /// <summary>
    /// Shape 8 — the first scanner hardcoded four spellings of "an already-completed valueless
    /// task", so anything spelled a fifth way walked straight through. These four did.
    /// </summary>
    [Fact]
    public void Catches_TheTaskSpellingsAHardcodedListMissed()
    {
        const string offender = """
            public class SpellingTests
            {
                [Fact]
                public Task Returns_DefaultTask()
                {
                    if (!Enabled) return default(Task);
                    return Verify();
                }

                [Fact]
                public ValueTask Returns_ANewValueTask()
                {
                    if (!Enabled) return new ValueTask();
                    return VerifyCore();
                }

                [Fact]
                public Task Returns_FromResultTrue()
                {
                    if (!Enabled) return Task.FromResult(true);
                    return Verify();
                }

                [Fact]
                public Task Returns_ADelayOfZero()
                {
                    if (!Enabled) return Task.Delay(0);
                    return Verify();
                }
            }
            """;

        VacuousTestPassScanner.Scan(offender, "Synthetic.cs")
            .Where(f => f.Rule == VacuousRule.EarlyExit)
            .Should().HaveCount(4);
    }

    /// <summary>A switch whose every arm verifies, default included, always verifies something.</summary>
    [Fact]
    public void Allows_ASwitchWhereEveryArmVerifies()
    {
        const string fine = """
            public class SwitchTests
            {
                [Fact]
                public void Every_ChannelIsHandled()
                {
                    switch (channel)
                    {
                        case "http":  Result.Should().Be(202); break;
                        case "sftp":  Result.Should().Be(0);   break;
                        default:      throw new NotSupportedException(channel);
                    }
                }
            }
            """;

        VacuousTestPassScanner.Scan(fine, "Synthetic.cs").Should().BeEmpty();
    }

    // ── the scan's reach ──────────────────────────────────────────────────────

    /// <summary>
    /// <c>ProcuLink.TestSupport</c> is compiled into two of the three test assemblies through a
    /// linked <c>&lt;Compile Include&gt;</c> item, but its directory does not end in <c>.Tests</c>
    /// — so the original glob never read it. It held no <c>[Fact]</c> when that was found, making
    /// the hole latent rather than live, and a shared file compiled into every test assembly is
    /// the last place a vacuous test should be able to hide.
    /// </summary>
    [Fact]
    public void TheGuardScansTheSharedTestSupportProject()
    {
        var repoRoot = VacuousTestPassScanner.FindRepoRoot();

        VacuousTestPassScanner.TestSourceFiles(repoRoot)
            .Select(p => VacuousTestPassScanner.RelativePath(repoRoot, p))
            .Should().Contain("ProcuLink.TestSupport/EnvironmentGatedFacts.cs");
    }

    /// <summary>
    /// The glob is a list, and a list goes stale. If someone adds <c>ProcuLink.Testing</c> or
    /// <c>ProcuLink.IntegrationTests</c> tomorrow, this fails until the scan is widened to include
    /// it — rather than silently scanning one project fewer and still reporting green.
    /// </summary>
    [Fact]
    public void TheGuardCoversEveryTopLevelDirectoryNamedForTests()
    {
        var repoRoot = VacuousTestPassScanner.FindRepoRoot();
        var scanned = VacuousTestPassScanner.TestCodeRoots(repoRoot);

        // Directories holding C# — `TestResults` and friends carry no source and are not test code.
        // The candidate list comes from RepoSourceCorpus, so an untracked copy of the repository
        // sitting beside the projects (`.git-audit-e/`, `audit-2026-08/`) does not get to nominate
        // its own `*.Tests` directory as one this guard has failed to scan.
        var looksLikeTestCode = RepoSourceCorpus.TopLevelDirectories(repoRoot)
            .Where(name => name.Contains("Test", StringComparison.OrdinalIgnoreCase))
            .Where(name => Directory
                .EnumerateFiles(Path.Combine(repoRoot, name), "*.cs", SearchOption.AllDirectories).Any())
            .ToList();

        looksLikeTestCode.Should().NotBeEmpty("the repo has test projects, so this check must have input");
        scanned.Should().BeEquivalentTo(looksLikeTestCode,
            "every top-level directory named for tests must be scanned — one left out is a place a "
            + "vacuous test can live while the guard reports green");
    }

    // ── reporting ────────────────────────────────────────────────────────────

    private static string BuildReport(IReadOnlyList<VacuousPass> offenders)
    {
        if (offenders.Count == 0) return string.Empty;

        var lines = offenders
            .Select((o, i) => $"  {i + 1,2}. {o}")
            .Prepend(
                $"{offenders.Count} test method(s) can report Passed without verifying anything.\n"
                + "A green run proves nothing about any of them. Fix by rule:\n\n"
                + $"  [{VacuousRule.EarlyExit}]\n"
                + "      The test bails before it asserts. If the reason is a missing environment,\n"
                + "      replace the exit with a DECLARED skip that names what to set:\n"
                + "        [EnvironmentGatedFact(\"requires a live IMAP mailbox\", LiveTestEnvironment.EndpointOptIn, \"PROCULINK_LIVE_IMAP_HOST\")]\n"
                + "        [DockerRequiredFact] / [LocalPostgresRequiredFact] / [Fact(Skip = \"…\")]\n\n"
                + $"  [{VacuousRule.ConditionalOnly}]\n"
                + "      Every assertion sits behind an if / a loop / a catch that may not run. Add ONE\n"
                + "      unconditional assertion proving the input set is what the test believes it is —\n"
                + "      an exact count where the size is knowable, so the set cannot quietly shrink either.\n\n"
                + $"  [{VacuousRule.NoAssertion}]\n"
                + "      Nothing is checked. Turn the claim in the test's NAME into an assertion; where the\n"
                + "      claim really is \"this does not throw\", say so: act.Should().NotThrow(\"…\").\n\n"
                + $"  [{VacuousRule.Tautology}]\n"
                + "      The assertion cannot fail. Assert the value against what the code should produce.\n\n"
                + $"  [{VacuousRule.SwallowedFailure}]\n"
                + "      A catch absorbs the exception a failing assertion throws. Either let it propagate,\n"
                + "      or record it and assert on the record after the sweep (the collect-and-report idiom).\n");

        return string.Join('\n', lines);
    }
}
