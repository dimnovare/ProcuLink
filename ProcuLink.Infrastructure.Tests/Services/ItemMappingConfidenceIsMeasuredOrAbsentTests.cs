using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// <c>item_mappings.confidence</c> holds a measurement or nothing.
///
/// <para>It used to hold <c>source == MappingSource.Manual ? 1.0f : 0.8f</c> — a two-valued
/// literal under a column the supplier screen heads <b>Confidence</b>. A code an operator typed by
/// hand rendered a green <b>100%</b>; a code loaded from their CSV rendered a flat amber
/// <b>80%</b>. No model produced either number, and because the column was non-nullable the
/// screen's own "Not scored" branch was unreachable for every live row.</para>
///
/// <para>These tests fail on the original defect verbatim: the two-valued ternary makes
/// <c>Confidence</c> non-null on both paths, so every "carries no score" assertion below goes red.</para>
/// </summary>
public class ItemMappingConfidenceIsMeasuredOrAbsentTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task HandTypedMapping_CarriesNoScore_AndSaysAHumanEnteredIt()
    {
        await using var db = MakeDb();
        var svc = new ItemMappingService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await svc.UpsertAsync(orgId, supplierId, "BUYER-1", "SUP-1",
            MappingSource.Manual, confidence: null, CancellationToken.None);

        var mapping = await db.ItemMappings.SingleAsync();

        // Was 1.0f — a green "100%" against a code a human had just typed.
        mapping.Confidence.Should().BeNull("nothing scored a hand-typed code");

        // What the row DOES know, and what the screen reads to say a human entered it.
        mapping.Source.Should().Be("manual");
    }

    [Fact]
    public async Task ImportedMapping_CarriesNoScore()
    {
        await using var db = MakeDb();
        var svc = new ItemMappingService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await svc.CreateAsync(orgId, supplierId, "BUYER-2", "SUP-2",
            MappingSource.Imported, confidence: null, CancellationToken.None);

        var mapping = await db.ItemMappings.SingleAsync();

        // Was 0.8f — a flat amber "80%" on every row of every bulk CSV import.
        mapping.Confidence.Should().BeNull("nothing scored a bulk-imported code");
        mapping.Source.Should().Be("imported");
    }

    [Fact]
    public async Task AcceptedModelSuggestion_KeepsTheModelsRealNumber()
    {
        await using var db = MakeDb();
        var svc = new ItemMappingService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        // The reviewer took the model's code verbatim; OrderResolutionService passes the model's
        // own confidence through rather than discarding it.
        await svc.UpsertAsync(orgId, supplierId, "BUYER-3", "SUP-3",
            MappingSource.Suggested, confidence: 0.83f, CancellationToken.None);

        var mapping = await db.ItemMappings.SingleAsync();

        mapping.Confidence.Should().Be(0.83f, "a real model score must survive being saved");
        mapping.Source.Should().Be("suggested");
    }

    [Fact]
    public async Task HandTypingOverAScoredMapping_ClearsTheStaleScore()
    {
        await using var db = MakeDb();
        var svc = new ItemMappingService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await svc.UpsertAsync(orgId, supplierId, "BUYER-4", "SUP-OLD",
            MappingSource.Suggested, confidence: 0.91f, CancellationToken.None);

        // An operator corrects it by hand. The 0.91 described the code they just overwrote.
        await svc.UpsertAsync(orgId, supplierId, "BUYER-4", "SUP-NEW",
            MappingSource.Manual, confidence: null, CancellationToken.None);

        var mapping = await db.ItemMappings.SingleAsync();

        mapping.SupplierItemCode.Should().Be("SUP-NEW");
        mapping.Confidence.Should().BeNull(
            "a score describing the previous code must not be left sitting against the new one");
        mapping.Source.Should().Be("manual");
    }

    [Fact]
    public async Task EditingAMappingByHand_ClearsTheScore()
    {
        await using var db = MakeDb();
        var svc = new ItemMappingService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        var created = await svc.CreateAsync(orgId, supplierId, "BUYER-5", "SUP-5",
            MappingSource.Suggested, confidence: 0.77f, CancellationToken.None);

        var updated = await svc.UpdateByIdAsync(orgId, created.Id, "BUYER-5", "SUP-5-CORRECTED",
            MappingSource.Manual, CancellationToken.None);

        updated.Should().NotBeNull();
        // Was `source == Manual ? 1.0f : mapping.Confidence` — a hand-correction stamped 1.0f.
        updated!.Confidence.Should().BeNull("an operator retyping the code did not score it");
    }
}
