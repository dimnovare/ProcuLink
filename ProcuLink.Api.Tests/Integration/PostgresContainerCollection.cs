using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Serialises every Testcontainers-Postgres test class. xUnit runs test classes in
/// parallel by default, so each container class spins up its own postgres:16 at the
/// same moment — under that load a container intermittently times out while opening
/// its first connection (Npgsql "Timeout during reading attempt" in InitializeAsync),
/// failing the suite flakily. One shared collection runs the container classes one at
/// a time; the (cheap, InMemory) remainder of the assembly still parallelises freely.
/// </summary>
[CollectionDefinition("postgres-container")]
public sealed class PostgresContainerCollection
{
}
