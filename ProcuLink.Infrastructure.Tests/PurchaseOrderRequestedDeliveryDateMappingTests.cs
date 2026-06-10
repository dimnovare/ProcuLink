using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Infrastructure.Tests;

/// <summary>
/// V5 regression guard: <see cref="PurchaseOrderEntity.RequestedDeliveryDate"/> must be a REAL
/// mapped column (<c>requested_delivery_date</c>), NOT EF-Ignored.
///
/// The original V5 bug: the header field was <c>b.Ignore(x =&gt; x.RequestedDeliveryDate)</c> and
/// "rode canonical_json". In production it was therefore ALWAYS null at transform time — the async
/// ingest persisted typed columns only via <c>ExecuteUpdateAsync</c> (no canonical rewrite) and the
/// transform reloaded the entity fresh, so the Ignored property came back null. The 19 V5 unit tests
/// never caught it because they only exercised the parser + a hand-built in-memory entity, never the
/// save → reload round-trip.
///
/// These two tests pin the fix without needing a relational provider:
///   1. The EF model exposes a mapped property for RequestedDeliveryDate (FindProperty != null and
///      IsImplicitlyCreated/Ignore is false) bound to column <c>requested_delivery_date</c>.
///      If anyone re-adds <c>b.Ignore(...)</c>, FindProperty returns null and this fails.
///   2. A value assigned before SaveChanges survives a reload from a FRESH context — proving it is
///      persisted, not discarded. Under EF-Ignore the reloaded value would be null (the exact prod bug).
///
/// The full ingest → ExecuteUpdateAsync → reload → transform round-trip (which the InMemory provider
/// cannot translate) is covered against real Postgres in
/// <c>ProcuLink.Api.Tests/Integration/EndToEndPipelineTests.ParseStoredFileAsync_Idoc_PersistsHeaderRequestedDeliveryDate</c>.
/// </summary>
public class PurchaseOrderRequestedDeliveryDateMappingTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public void RequestedDeliveryDate_IsMappedColumn_NotIgnored()
    {
        using var db = NewDb();

        var entityType = db.Model.FindEntityType(typeof(PurchaseOrderEntity));
        entityType.Should().NotBeNull();

        // FindProperty returns null for an EF-Ignored member, so a non-null result here is the
        // definitive proof the field is mapped (the original V5 bug was b.Ignore(...)).
        var property = entityType!.FindProperty(nameof(PurchaseOrderEntity.RequestedDeliveryDate));
        property.Should().NotBeNull(
            "RequestedDeliveryDate must be a mapped property — if it were b.Ignore()'d, FindProperty would return null");
        // Definitive proof it's the real column: an EF-Ignored member has no property at all,
        // so the non-null FindProperty above already rules out b.Ignore(...). Pin the column name too.
        property!.GetColumnName().Should().Be("requested_delivery_date");
    }

    [Fact]
    public async Task RequestedDeliveryDate_SurvivesSaveAndReload()
    {
        var orderId = Guid.NewGuid();
        var orgId   = Guid.NewGuid();
        var expected = new DateOnly(2026, 5, 25);

        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(dbName).Options;

        // Write in one context...
        await using (var writeDb = new ProcuLinkDbContext(options))
        {
            writeDb.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id                    = orderId,
                OrgId                 = orgId,
                SupplierId            = Guid.NewGuid(),
                PoNumber              = "PO-RDD-001",
                Currency              = "EUR",
                OrderDate             = new DateOnly(2026, 1, 1),
                Status                = "ready",
                CreatedAt             = DateTime.UtcNow,
                UpdatedAt             = DateTime.UtcNow,
                RequestedDeliveryDate = expected,
            });
            await writeDb.SaveChangesAsync();
        }

        // ...and read it back through a FRESH context (no tracking carry-over).
        await using (var readDb = new ProcuLinkDbContext(options))
        {
            var reloaded = await readDb.PurchaseOrders.AsNoTracking()
                .SingleAsync(o => o.Id == orderId && o.OrgId == orgId);

            reloaded.RequestedDeliveryDate.Should().Be(expected,
                "the value must persist through save+reload — an EF-Ignored property would read back null (the original V5 prod bug)");
        }
    }
}
