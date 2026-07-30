using Xunit;

namespace ProcuLink.TestSupport;

/// <summary>
/// Shared gating attributes for tests that cannot run without external infrastructure
/// (a live mailbox, a real SFTP server, an OpenAI key, a local Postgres…).
///
/// <para><b>Why this exists.</b> The historical idiom was a bare early <c>return</c> at the top of
/// the test body:</para>
/// <code>
/// [Fact]
/// public async Task Live_Something()
/// {
///     if (Environment.GetEnvironmentVariable("PROCULINK_LIVE_ENDPOINT_TESTS") != "1") return;
///     …
/// }
/// </code>
/// <para>The runner counts that as <b>Passed</b>. A suite that cannot tell "ran and passed" from
/// "silently did nothing" is not an instrument: <c>Live_ImapIngress</c> was broken for two and a
/// half weeks while reporting green, and only a hand-run negative control found it. These
/// attributes make the same condition a <b>declared, statically-reported skip carrying a human
/// reason</b> — xUnit v2 has no dynamic <c>Assert.Skip</c>, so the decision must be made in the
/// attribute constructor, exactly as <c>DockerRequiredFactAttribute</c> already does for
/// Testcontainers.</para>
///
/// <para>This file is compiled into every test project that needs it via a linked
/// <c>&lt;Compile Include="..\ProcuLink.TestSupport\*.cs" /&gt;</c> item, so there is exactly ONE
/// copy on disk to keep correct. Do not hand-copy it.</para>
///
/// <para>Enforced by <c>ProcuLink.Api.Tests/Meta/NoVacuousTestPassTests.cs</c>, which fails the
/// build if any test body reintroduces a bare environment-gated early return.</para>
/// </summary>
public static class LiveTestEnvironment
{
    /// <summary>Master opt-in for the real-endpoint (HTTP/SMTP/SFTP/IMAP/S3) test-fires.</summary>
    public const string EndpointOptIn = "PROCULINK_LIVE_ENDPOINT_TESTS";

    /// <summary>Master opt-in for the live OpenAI extraction smokes.</summary>
    public const string AiOptIn = "PROCULINK_LIVE_AI_TESTS";

    /// <summary>Master opt-in for the live distributor catalog-feed fetches.</summary>
    public const string FeedOptIn = "PROCULINK_LIVE_FEED_TESTS";

    /// <summary>
    /// Prefix marking a requirement as "must be set AND must point at an existing directory".
    /// </summary>
    public const string DirectoryPrefix = "dir:";

    private static bool IsSet(string name) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));

    /// <summary>
    /// Returns <c>null</c> when every requirement is satisfied (so the test RUNS), otherwise a
    /// one-sentence human reason naming exactly what to set — the string xUnit prints as the
    /// skip reason.
    /// </summary>
    /// <param name="humanRequirement">
    /// Plain-language description of the external thing needed, e.g.
    /// "requires a live IMAP mailbox". No jargon, no variable names — those are appended.
    /// </param>
    /// <param name="optInSwitch">The master opt-in variable, which must be exactly "1".</param>
    /// <param name="requirements">
    /// Further variables that must be non-empty. Prefix a name with <see cref="DirectoryPrefix"/>
    /// to additionally require that its value is an existing directory.
    /// </param>
    public static string? SkipReason(string humanRequirement, string optInSwitch, params string[] requirements)
    {
        var missing = new List<string>();

        if (Environment.GetEnvironmentVariable(optInSwitch) != "1")
            missing.Add($"{optInSwitch}=1");

        foreach (var requirement in requirements)
        {
            if (requirement.StartsWith(DirectoryPrefix, StringComparison.Ordinal))
            {
                var name = requirement[DirectoryPrefix.Length..];
                if (!IsSet(name))
                    missing.Add($"{name} (an existing directory)");
                else if (!Directory.Exists(Environment.GetEnvironmentVariable(name)))
                    missing.Add($"{name} → '{Environment.GetEnvironmentVariable(name)}' does not exist");
            }
            else if (!IsSet(requirement))
            {
                missing.Add(requirement);
            }
        }

        return missing.Count == 0
            ? null
            : $"{humanRequirement} — set {string.Join(" + ", missing)}.";
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that statically skips — with a human reason — when the external
/// environment the test needs is not configured. Never silently passes.
/// </summary>
/// <example>
/// <code>
/// [EnvironmentGatedFact(
///     "requires a live IMAP mailbox to poll",
///     LiveTestEnvironment.EndpointOptIn,
///     "PROCULINK_LIVE_IMAP_HOST", "PROCULINK_LIVE_IMAP_USER")]
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method)]
public sealed class EnvironmentGatedFactAttribute : FactAttribute
{
    public EnvironmentGatedFactAttribute(string humanRequirement, string optInSwitch, params string[] requirements)
    {
        if (LiveTestEnvironment.SkipReason(humanRequirement, optInSwitch, requirements) is { } reason)
            Skip = reason;
    }
}

/// <summary><see cref="EnvironmentGatedFactAttribute"/>'s theory twin — statically skips every case.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class EnvironmentGatedTheoryAttribute : TheoryAttribute
{
    public EnvironmentGatedTheoryAttribute(string humanRequirement, string optInSwitch, params string[] requirements)
    {
        if (LiveTestEnvironment.SkipReason(humanRequirement, optInSwitch, requirements) is { } reason)
            Skip = reason;
    }
}

/// <summary>
/// One-time probe for the local development Postgres on :5435. Mirrors <c>DockerProbe</c>:
/// tests that need a REAL relational engine (relative <c>ExecuteUpdate</c>, row locks, real
/// concurrency) cannot be faked by the InMemory provider, so when no local Postgres answers they
/// must report a declared skip rather than a green no-op.
/// </summary>
public static class LocalPostgresProbe
{
    /// <summary>The dev database the repo standardises on (see CLAUDE.md).</summary>
    public const string AdminConnectionString =
        "Host=localhost;Port=5435;Username=postgres;Password=postgres;Database=postgres;Timeout=3";

    public static readonly string? UnavailableReason = Detect();

    private static string? Detect()
    {
        try
        {
            using var conn = new Npgsql.NpgsqlConnection(AdminConnectionString);
            conn.Open();
            return null;
        }
        catch (Exception ex)
        {
            return $"no Postgres answered on localhost:5435 ({ex.GetType().Name}: {ex.Message})";
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that statically skips — with a human reason — when the local
/// development Postgres on :5435 is unreachable.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LocalPostgresRequiredFactAttribute : FactAttribute
{
    public LocalPostgresRequiredFactAttribute()
    {
        if (LocalPostgresProbe.UnavailableReason is { } reason)
            Skip = $"Requires the local dev Postgres on :5435 — {reason}.";
    }
}
