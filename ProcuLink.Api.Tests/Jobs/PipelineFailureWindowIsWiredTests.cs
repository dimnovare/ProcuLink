using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Worker;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// The pipeline-failure window has to reach the query, not just exist as a property.
///
/// <para><c>OpsHealthService</c> takes <c>WorkerHealthAlertOptions</c> as an OPTIONAL constructor
/// parameter, so a container that does not supply it silently falls back to the code defaults. That
/// is deliberate for the API — which never runs the sweep — but on the Worker it would be a silent
/// misconfiguration: an operator who set <c>WorkerHealthAlert__PipelineFailureWindowMinutes</c>
/// would get a green boot, no error anywhere, and the default 24 h anyway. Every unit test of the
/// windowing passes the options in by hand, so none of them can see that.</para>
///
/// <para>These resolve through the REAL Worker registration seam
/// (<c>WorkerAlertingRegistration.AddWorkerAlerting</c>) and read the window back off a snapshot
/// the service actually produced.</para>
/// </summary>
public class PipelineFailureWindowIsWiredTests
{
    [Fact]
    public async Task ConfiguredWindow_ReachesTheHealthSnapshot()
    {
        await using var provider = BuildWorkerGraph(windowMinutes: "180");
        using var scope = provider.CreateScope();

        var snapshot = await scope.ServiceProvider
            .GetRequiredService<IOpsHealthService>()
            .GetWorkerHealthSnapshotAsync(default);

        Assert.Equal(180, snapshot.PipelineFailureWindowMinutes);
    }

    /// <summary>
    /// The control. Without it the test above would pass on a service that ignored the container
    /// entirely and happened to be asked for the number it was already going to return.
    /// </summary>
    [Fact]
    public async Task UnconfiguredWindow_FallsBackToTheDocumentedDefault()
    {
        await using var provider = BuildWorkerGraph(windowMinutes: null);
        using var scope = provider.CreateScope();

        var snapshot = await scope.ServiceProvider
            .GetRequiredService<IOpsHealthService>()
            .GetWorkerHealthSnapshotAsync(default);

        Assert.Equal(1440, snapshot.PipelineFailureWindowMinutes);
    }

    /// <summary>
    /// The Worker's own <c>Program.cs</c> registers <see cref="IOpsHealthService"/> next to
    /// <c>AddWorkerAlerting</c>, which is what makes the wiring above the production shape rather
    /// than a shape this test invented.
    /// </summary>
    private static ServiceProvider BuildWorkerGraph(string? windowMinutes)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ProcuLinkDbContext>(o =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped<IOpsHealthService, OpsHealthService>();

        var settings = new Dictionary<string, string?>();
        if (windowMinutes is not null)
            settings["WorkerHealthAlert:PipelineFailureWindowMinutes"] = windowMinutes;

        services.AddWorkerAlerting(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

        return services.BuildServiceProvider();
    }
}
