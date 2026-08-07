using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Shares ONE Testcontainers-Postgres across every container test class, and still runs those
/// classes one at a time.
///
/// <para><b>Sharing</b> is <see cref="PostgresContainerFixture"/>'s job. Until 2026-08-06 this was
/// a bare <c>[CollectionDefinition]</c> with no fixture, so the collection bought serialisation
/// without buying sharing: 56 classes started 56 containers back to back, 538 s of the 563 s the
/// collection occupied on CI was container startup, and the collection was the job's critical
/// path. Each class now takes the fixture by constructor and asks it for its OWN database on the
/// shared container.</para>
///
/// <para><b>Serialisation</b> is still this definition's job, and is still deliberate. xUnit runs
/// test classes in parallel by default; the container classes are the expensive ones, and letting
/// them overlap is what made a container intermittently time out while opening its first
/// connection (Npgsql "Timeout during reading attempt" in <c>InitializeAsync</c>), failing the
/// suite flakily. The (cheap, InMemory) remainder of the assembly still parallelises freely.</para>
///
/// <para><b>Membership is wider than "needs Postgres".</b> This collection is the assembly's ONE
/// serialisation domain for <em>process-global</em> state, so it also holds every class that boots
/// a <c>WebApplicationFactory&lt;Program&gt;</c> or touches
/// <c>ProcuLink.Api.Controllers.MigrationReadiness</c> — even when that class never asks for a
/// database. <c>MigrationReadiness</c> is a <c>static volatile int</c>: it is per-PROCESS, while
/// xUnit gives each test class its own host. Booting a host runs Program.cs's
/// <c>ApplicationStarted</c> migration task, which writes that global; so does every explicit
/// <c>MarkPending</c>/<c>MarkSucceeded</c>/<c>MarkFailed</c> in a test. Two such classes in two
/// different collections run CONCURRENTLY, and one class's write lands inside another's
/// set-then-assert window.</para>
///
/// <para>That was not theoretical. <c>HealthEndpointTests</c> carried a header comment reasoning
/// that its tests "run SERIALLY within one class … so the shared static can't leak across tests" —
/// true, and beside the point, because the writer was a different CLASS. Measured on this branch,
/// 6 consecutive full-suite runs produced 2 failures in 2 different tests; one was
/// <c>Readiness_PendingThenSucceeded_TransitionsToReady</c> failing
/// "Expected: ServiceUnavailable / Actual: OK" — a <c>MarkSucceeded()</c> from a parallel class
/// landing between this test's <c>MarkPending()</c> and its request.</para>
///
/// <para>Membership is enforced, not remembered: <c>ProcessGlobalStateIsSerializedTests</c> fails
/// the build when a class that boots a host or names <c>MigrationReadiness</c> is missing the
/// attribute.</para>
///
/// <para>Joining costs a full-suite run nothing — measured, the four classes that joined got
/// FASTER (10 s less wall between them), because a serialised lane is not competing with the rest
/// of the assembly for CPU. It does mean a <c>--filter</c> run that selects only one of them
/// starts a Postgres container it never uses; see <see cref="PostgresContainerFixture.InitializeAsync"/>
/// for why that trade beat the alternative.</para>
/// </summary>
[CollectionDefinition("postgres-container")]
public sealed class PostgresContainerCollection : ICollectionFixture<PostgresContainerFixture>
{
}
