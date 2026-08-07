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
/// </summary>
[CollectionDefinition("postgres-container")]
public sealed class PostgresContainerCollection : ICollectionFixture<PostgresContainerFixture>
{
}
