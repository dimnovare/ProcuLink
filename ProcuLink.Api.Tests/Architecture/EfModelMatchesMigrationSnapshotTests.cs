using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// The EF model the code builds must be the model the migrations snapshot records.
///
/// <para>A DbSet property is not schema: removing one is safe only while the entity stays configured
/// (with an explicit <c>ToTable</c>) in <c>OnModelCreating</c>. If a removal, a rename or a deleted
/// config line ever changes the model, the next generated migration would rename or drop a table
/// and nothing else would fail. EF Core 8 has no <c>HasPendingModelChanges</c>, so this asks the
/// migrations differ directly. Nothing connects: only models are built.</para>
/// </summary>
public sealed class EfModelMatchesMigrationSnapshotTests
{
    [Fact]
    public void TheModelTheCodeBuilds_HasNoDifferencesFromTheMigrationsSnapshot()
    {
        using var db = new ProcuLinkDbContext(
            new DbContextOptionsBuilder<ProcuLinkDbContext>()
                .UseNpgsql("Host=model-only;Database=model-only")
                .Options);

        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot;
        snapshot.Should().NotBeNull("the migrations assembly must carry a model snapshot to compare against");

        // The snapshot's model is not finalized; the initializer makes it relational-readable.
        var snapshotModel = db.GetService<IModelRuntimeInitializer>()
            .Initialize(snapshot!.Model, designTime: true, validationLogger: null);
        var currentModel = db.GetService<IDesignTimeModel>().Model;

        var differences = db.GetService<IMigrationsModelDiffer>().GetDifferences(
            snapshotModel.GetRelationalModel(),
            currentModel.GetRelationalModel());

        differences.Should().BeEmpty(
            "the code's model drifted from the migrations snapshot — a generated migration would "
          + "change the schema. Differences: "
          + string.Join("; ", differences.Select(d => d.GetType().Name)));
    }
}
