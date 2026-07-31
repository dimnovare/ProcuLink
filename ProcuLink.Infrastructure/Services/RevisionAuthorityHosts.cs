namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// One deployed host that resolves an effective connection config, and therefore MUST have
/// <c>Connections__RevisionAuthority</c> set on its deployment.
/// </summary>
/// <param name="ProjectName">The project directory / assembly name, e.g. <c>ProcuLink.Api</c>.</param>
/// <param name="DeployedServiceName">
/// The Railway service whose variables carry the flag in production. Named explicitly because the
/// service name does NOT match the project name for the Worker — it is <c>aware-amazement</c>,
/// which is exactly the kind of detail a deployer cannot guess.
/// </param>
/// <param name="DevelopmentConfigFile">The host's local development settings file.</param>
public sealed record RevisionAuthorityHost(
    string ProjectName,
    string DeployedServiceName,
    string DevelopmentConfigFile);

/// <summary>
/// The enforced roster of hosts that resolve an effective connection config.
///
/// <para><b>Why this list exists at all.</b> <c>Connections:RevisionAuthority</c> decides whether a
/// pinned order is processed under the contract it was ingested with or under whatever the live
/// tables say right now. Every host that resolves an effective config answers that question
/// independently, from its OWN environment. A host missing the variable does not fail, warn, or
/// differ visibly — it quietly reads the live tables while its siblings honour the pin, so one
/// order can be transformed under a pinned revision by the Worker and previewed under live config
/// by the API in the same second.</para>
///
/// <para><b>What enforces it.</b> <c>RevisionAuthorityHostCoverageTests</c> scans every
/// <c>Program.cs</c> in the solution for an <c>IEffectiveConnectionConfigResolver</c> registration
/// and asserts the resulting set equals this list. A new host cannot merge without being added
/// here, and adding an entry forces its author to name the deployed service — the step that would
/// otherwise be forgotten. The same test asserts the production runbook names every entry.</para>
///
/// <para><b>Deployed state — an observation with a date, not a standing fact.</b>
/// <c>Connections__RevisionAuthority = true</c> on both Railway services (<c>ProcuLink</c> and
/// <c>aware-amazement</c>) when last read, 2026-07-27 and 2026-07-31. This sentence is exactly the
/// kind of claim that rots: nothing in this repository can assert it, and removing the variable
/// tomorrow would leave it silently false. So it is NOT the authority — the authority is
/// <c>GET /health/ready</c> (<c>revisionAuthority</c>) for the API, and the startup log for the
/// Worker, both read live. <c>.github/workflows/uptime.yml</c> fails on the API's value not being
/// true, so the flag going off pages rather than rotting. The 2026-07-27 audit finding that called
/// the versioning subsystem "inert in production" read <c>appsettings.Development.json</c> and
/// never read the deployed environment. See
/// <c>docs/ops/revision-authority-production-smoke.md</c>.</para>
/// </summary>
public static class RevisionAuthorityHosts
{
    /// <summary>
    /// The environment-variable spelling a deployer sets (ASP.NET's <c>__</c> section separator).
    /// Derived from the configuration key so the two can never drift.
    /// </summary>
    public const string EnvironmentVariableName = EffectiveConnectionConfigResolver.EnvironmentVariableName;

    /// <summary>
    /// Every host that resolves an effective connection config. Keep in lock-step with the DI
    /// registrations — <c>RevisionAuthorityHostCoverageTests</c> fails the build otherwise.
    /// </summary>
    public static readonly IReadOnlyList<RevisionAuthorityHost> All = new[]
    {
        // Serves HTTP: preview, conformance, replay, manual send. Railway service "ProcuLink".
        new RevisionAuthorityHost("ProcuLink.Api", "ProcuLink", "appsettings.Development.json"),
        // Runs the Hangfire jobs that parse, transform and deliver. Railway service
        // "aware-amazement" — the generated name, deliberately recorded because it is unguessable.
        new RevisionAuthorityHost("ProcuLink.Worker", "aware-amazement", "appsettings.Development.json"),
    };
}
