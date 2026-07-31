using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// WP-21 (c), host-side half — the effective value of <c>Connections:RevisionAuthority</c> is
/// announced at startup by EVERY host, not just the one that serves HTTP.
///
/// <para><b>Why a startup line and not only <c>/health/ready</c>.</b> The Worker
/// (<c>aware-amazement</c> on Railway) serves no HTTP at all, so a readiness endpoint can never
/// answer "is the flag on for the process that actually runs parse, transform and delivery jobs".
/// A log line at boot is the only surface that host has. Both hosts already call
/// <see cref="StartupConfigurationValidator.Validate"/> before <c>Run()</c>, so the announcement
/// rides a seam that exists rather than adding a new one to each <c>Program.cs</c>.</para>
///
/// <para><b>What it must say.</b> The PARSED effective value — not the raw string, not "the key is
/// present". "Present" was never the question: the 2026-07-27 audit read a raw
/// <c>appsettings.Development.json</c> and concluded production was off. A line that says
/// <c>enabled=True</c> or <c>enabled=False</c> is the only one that ends that argument.</para>
///
/// <para>Rule R1 — these tests are the announcement's consumer, and they assert both polarities so
/// it cannot degrade into a constant.</para>
/// </summary>
public class RevisionAuthorityStartupAnnouncementTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => p.Value))
            .Build();

    private static (CapturingLogger Logger, IConfiguration Cfg) Arrange(string? flagValue)
    {
        var pairs = new List<(string, string?)>();
        if (flagValue is not null)
            pairs.Add((EffectiveConnectionConfigResolver.FlagKey, flagValue));

        return (new CapturingLogger(), Config(pairs.ToArray()));
    }

    private static void RunValidate(IConfiguration cfg, ILogger logger, string component) =>
        StartupConfigurationValidator.Validate(
            configuration:   cfg,
            logger:          logger,
            environmentName: "Development",   // never throws — we are testing the announcement
            requiredKeys:    Array.Empty<string>(),
            optionalKeys:    Array.Empty<string>(),
            componentName:   component);

    [Fact]
    public void Startup_AnnouncesRevisionAuthorityEnabled_WhenTheFlagIsTrue()
    {
        var (logger, cfg) = Arrange("true");

        RunValidate(cfg, logger, "ProcuLink.Api");

        var line = logger.Lines.SingleOrDefault(l => l.Contains("revision authority", StringComparison.OrdinalIgnoreCase));
        Assert.False(line is null,
            "every host must announce the EFFECTIVE revision-authority value at startup — the Worker "
            + "serves no HTTP, so this log line is the only place its value is observable. Lines seen: "
            + string.Join(" | ", logger.Lines));
        Assert.Contains("True", line!, StringComparison.Ordinal);
        Assert.Contains(EffectiveConnectionConfigResolver.FlagKey, line!, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_AnnouncesRevisionAuthorityDisabled_WhenTheKeyIsAbsent()
    {
        var (logger, cfg) = Arrange(flagValue: null);

        RunValidate(cfg, logger, "ProcuLink.Worker");

        var line = logger.Lines.SingleOrDefault(l => l.Contains("revision authority", StringComparison.OrdinalIgnoreCase));
        Assert.False(line is null,
            "the announcement must fire when the key is ABSENT too — that is the configuration the "
            + "2026-07-27 audit assumed production had, and its invisibility is what made the false "
            + "P0 possible. Lines seen: " + string.Join(" | ", logger.Lines));
        Assert.Contains("False", line!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The flag reader is ONE implementation shared by every consumer. Two hand-rolled
    /// <c>bool.TryParse</c> copies already existed (the resolver and SupplierConnectionService);
    /// a third that treated "1"/"yes" differently would route two consumers of the same order at
    /// the same instant to different configs.
    /// </summary>
    [Theory]
    [InlineData("true",  true)]
    [InlineData("True",  true)]
    [InlineData("TRUE",  true)]
    [InlineData("false", false)]
    [InlineData("1",     false)]   // NOT truthy — bool.TryParse semantics, deliberately strict
    [InlineData("yes",   false)]
    [InlineData("",      false)]
    [InlineData(null,    false)]
    public void IsEnabled_IsTheSingleStrictReaderOfTheFlag(string? raw, bool expected)
    {
        var cfg = raw is null
            ? Config()
            : Config((EffectiveConnectionConfigResolver.FlagKey, raw));

        Assert.Equal(expected, EffectiveConnectionConfigResolver.IsEnabled(cfg));
    }

    /// <summary>
    /// The environment-variable spelling is what a deployer types into Railway. It is derived from
    /// the configuration key by ASP.NET's <c>__</c> section separator, so it must never be
    /// hand-maintained out of sync with it.
    /// </summary>
    [Fact]
    public void EnvironmentVariableName_MatchesTheConfigurationKey()
    {
        Assert.Equal(
            EffectiveConnectionConfigResolver.EnvironmentVariableName,
            EffectiveConnectionConfigResolver.FlagKey.Replace(":", "__"));
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
