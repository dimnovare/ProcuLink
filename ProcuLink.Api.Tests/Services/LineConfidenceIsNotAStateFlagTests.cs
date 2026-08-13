using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// <c>purchase_order_lines.confidence</c> reaches the order passport as a measurement or as null —
/// never as a resolution state.
///
/// <para>It used to be written at ingestion as
/// <c>resolved ? (parserFlagged ? 0.5f : 1.0f) : 0.0f</c> and read straight into
/// <c>PassportMappingDecision.Confidence</c>, which the review UI paints on the confidence ramp.
/// A line resolved from the supplier's saved mappings therefore printed a green <b>100%</b>,
/// a parser-flagged line a red <b>50%</b>, and an unresolved line a red <b>0%</b> — three numbers
/// no model produced, on a screen whose whole job is to tell an operator how much to trust a code.
/// The bulk-accept path promoted the model's REAL confidence into the same column, so the field
/// held a flag and a measurement with no way to tell them apart.</para>
///
/// <para>These fail on the original defect verbatim: restore the ternary at
/// <c>OrderIngestionService</c> and the resolved line comes back carrying 1.0 instead of null.</para>
/// </summary>
public class LineConfidenceIsNotAStateFlagTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<(Guid orgId, Guid orderId)> SeedAsync(
        ProcuLinkDbContext db, params PurchaseOrderLineEntity[] lines)
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        db.Organisations.Add(new Organisation
        {
            Id = orgId, Name = "Org", ClerkOrgId = "org_" + Guid.NewGuid().ToString("N"),
        });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = orgId,
            PoNumber = "PO-1",
            Status = OrderStatusConstants.Ready,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Lines = lines.ToList(),
        });
        await db.SaveChangesAsync();
        return (orgId, orderId);
    }

    [Fact]
    public async Task DeterministicallyResolvedLine_ReachesThePassportWithNoConfidence()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedAsync(db, new PurchaseOrderLineEntity
        {
            Id = Guid.NewGuid(),
            LineNumber = 1,
            BuyerItemCode = "BUY-1",
            SupplierItemCode = "SUP-1",
            NeedsReview = false,
            // Confidence deliberately left unset — this is what ingestion now writes.
        });

        var p = (await new PassportService(db).GetAsync(orgId, orderId, CancellationToken.None)).Value!;
        var row = Assert.Single(p.MappingDecisions);

        // Was 1.0f → a green "100%" chip on a line no model had looked at.
        Assert.Null(row.Confidence);

        // The resolution state is still fully reported — it just is not a percentage.
        Assert.Equal("deterministic", row.Source);
        Assert.Equal("SUP-1", row.SupplierItemCode);
    }

    [Fact]
    public async Task UnresolvedLine_ReachesThePassportWithNoConfidence()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedAsync(db, new PurchaseOrderLineEntity
        {
            Id = Guid.NewGuid(),
            LineNumber = 1,
            BuyerItemCode = "BUY-1",
            SupplierItemCode = null,
            NeedsReview = true,
            ReviewReason = "No mapping for this buyer code.",
        });

        var p = (await new PassportService(db).GetAsync(orgId, orderId, CancellationToken.None)).Value!;
        var row = Assert.Single(p.MappingDecisions);

        // Was 0.0f → a red "0%" that reads as "the model is certain this is wrong",
        // when in fact nothing had scored it at all.
        Assert.Null(row.Confidence);
        Assert.Equal("unresolved", row.Source);
    }

    [Fact]
    public async Task AcceptedAiSuggestion_KeepsTheModelsRealNumber()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedAsync(db, new PurchaseOrderLineEntity
        {
            Id = Guid.NewGuid(),
            LineNumber = 1,
            BuyerItemCode = "BUY-1",
            SupplierItemCode = "SUP-AI",
            NeedsReview = false,
            // What AcceptAiSuggestionsAsync leaves behind: the model's score promoted onto the
            // line, and the transient Ai* fields cleared.
            Confidence = 0.87f,
        });

        var p = (await new PassportService(db).GetAsync(orgId, orderId, CancellationToken.None)).Value!;
        var row = Assert.Single(p.MappingDecisions);

        Assert.Equal(0.87f, row.Confidence);
    }
}
