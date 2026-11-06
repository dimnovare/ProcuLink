using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public class DocumentUsageCountTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    [Fact]
    public async Task Read_documents_count_units_failed_and_unread_do_not()
    {
        var org = DocumentEngineTestFactory.OrgA;
        factory.ClientFor(org);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 2; i++)
            db.PurchaseOrders.Add(new PurchaseOrderEntity { Id = Guid.NewGuid(), OrgId = org, PoNumber = $"PO-{i}", CreatedAt = now });
        FlowDocument Doc(int units, DateTime? readAt, bool sample = false) => new()
        {
            Id = Guid.NewGuid(), OrgId = org, FlowId = Guid.NewGuid(), FileName = "synthetic.pdf",
            BillingUnits = units, ReadAt = readAt, IsSample = sample, CreatedAt = now, UpdatedAt = now,
        };
        db.FlowDocuments.AddRange(Doc(4, now), Doc(1, null), Doc(3, now, sample: true));
        await db.SaveChangesAsync();

        var status = await scope.ServiceProvider.GetRequiredService<IBillingService>().GetStatusAsync(org);

        Assert.Equal(6, status.OrdersThisMonth);
    }
}
