using FluentAssertions;
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
/// or plain <c>[Fact(Skip = "…")]</c>. The scanner's rule and its rationale live in
/// <see cref="VacuousTestPassScanner"/>.</para>
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
                    Assert.True(true);
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

    // ── reporting ────────────────────────────────────────────────────────────

    private static string BuildReport(IReadOnlyList<VacuousPass> offenders)
    {
        if (offenders.Count == 0) return string.Empty;

        var lines = offenders
            .Select((o, i) => $"  {i + 1,2}. {o}")
            .Prepend(
                $"{offenders.Count} test method(s) can exit reporting Passed without asserting anything.\n"
                + "Each one below returns before its first assertion, so a green run proves nothing about it.\n"
                + "Replace the early return with a DECLARED skip that names the reason a human can act on:\n"
                + "  [EnvironmentGatedFact(\"requires a live IMAP mailbox\", LiveTestEnvironment.EndpointOptIn, \"PROCULINK_LIVE_IMAP_HOST\")]\n"
                + "  [DockerRequiredFact] / [LocalPostgresRequiredFact] / [Fact(Skip = \"…\")]\n");

        return string.Join('\n', lines);
    }
}
