using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Tests.Integration;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public sealed class FlowDeliveryModelTests(PostgresContainerFixture postgres)
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private async Task<(ProcuLinkDbContext Db, FlowOutput Output, FlowDocument Doc)> SeedAsync()
    {
        var cs = await postgres.CreateDatabaseAsync("proculink_flowdelivery");
        var db = new ProcuLinkDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(cs, n => n.CommandTimeout(180)).Options);
        var orgId = Guid.NewGuid();
        var flow = new Flow { Id = Guid.NewGuid(), OrgId = orgId, Name = "Invoices", CreatedAt = Now, UpdatedAt = Now };
        var doc = new FlowDocument
        {
            Id = Guid.NewGuid(), OrgId = orgId, FlowId = flow.Id, FileName = "a.pdf", ContentType = "application/pdf",
            SourceFileKey = "k", Sha256 = new string('a', 64), Status = DocumentStatus.Ready, CreatedAt = Now, UpdatedAt = Now,
        };
        var output = new FlowOutput
        {
            Id = Guid.NewGuid(), OrgId = orgId, FlowId = flow.Id, Name = "Accounting CSV", Format = FlowOutputFormats.Csv,
            CreatedAt = Now, UpdatedAt = Now,
        };
        db.Organisations.Add(new Organisation { Id = orgId, Name = "Test Org A", Slug = "acme-" + orgId.ToString("N") });
        db.Flows.Add(flow);
        db.FlowDocuments.Add(doc);
        db.Set<FlowOutput>().Add(output);
        await db.SaveChangesAsync();
        return (db, output, doc);
    }

    private static FlowDelivery Delivery(FlowOutput o, FlowDocument d) => new()
    {
        Id = Guid.NewGuid(), OrgId = o.OrgId, DocumentId = d.Id, OutputId = o.Id, OutputName = o.Name,
        ContentType = "text/csv", FileName = "a.csv", CreatedAt = Now, UpdatedAt = Now,
    };

    [DockerRequiredFact]
    public async Task One_delivery_per_document_and_output()
    {
        var (db, output, doc) = await SeedAsync();
        await using var _ = db;
        db.Set<FlowDelivery>().Add(Delivery(output, doc));
        await db.SaveChangesAsync();

        db.Set<FlowDelivery>().Add(Delivery(output, doc));
        var act = () => db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [DockerRequiredFact]
    public async Task Deleting_an_output_keeps_its_delivery_history()
    {
        var (db, output, doc) = await SeedAsync();
        await using var _ = db;
        var delivery = Delivery(output, doc);
        db.Set<FlowDelivery>().Add(delivery);
        await db.SaveChangesAsync();

        await db.Set<FlowOutput>().Where(o => o.Id == output.Id).ExecuteDeleteAsync();
        await db.Entry(delivery).ReloadAsync();

        // No FK any more: a published version may still send through the deleted output.
        delivery.OutputId.Should().Be(output.Id);
        delivery.OutputName.Should().Be("Accounting CSV");
    }

    [Theory]
    [InlineData(new[] { "delivered", "stored" }, "sent")]
    [InlineData(new[] { "delivered", "dead_letter" }, "send_failed")]
    [InlineData(new[] { "dead_letter", "failed" }, "sending")]
    [InlineData(new[] { "queued" }, "sending")]
    [InlineData(new string[0], "send_failed")]
    public void Document_status_rolls_up(string[] d, string expected) =>
        FlowDeliveryStatus.DocumentStatusFor(d).Should().Be(expected);
}
