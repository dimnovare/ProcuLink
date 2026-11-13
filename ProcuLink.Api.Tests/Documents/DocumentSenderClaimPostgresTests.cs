using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Moq;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Api.Tests.Integration;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

/// <summary>The sender's claim runs as one conditional UPDATE on a relational provider; EF InMemory takes the other branch.</summary>
[Collection("postgres-container")]
public sealed class DocumentSenderClaimPostgresTests(PostgresContainerFixture postgres)
{
    [DockerRequiredFact]
    public async Task The_relational_claim_sends_a_current_document_and_refuses_one_edited_since_it_was_loaded()
    {
        var cs = await postgres.CreateDatabaseAsync("proculink_sendclaim");
        ProcuLinkDbContext NewDb() => new(new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs, n => n.CommandTimeout(180)).Options);
        var now = DateTime.UtcNow;
        var org = Guid.NewGuid();
        var flow = new Flow { Id = Guid.NewGuid(), OrgId = org, Name = "Invoices", CreatedAt = now, UpdatedAt = now };
        var template = new OutputNodeTemplate
        {
            Format = OutputFormat.Csv, CsvDialect = new CsvDialect { LineEnding = "\n" },
            Root = OutputNode.Obj("root", OutputNode.FieldOf("No", new OutputFieldRule { OutputPath = "No", CanonicalField = "invoice_no" })),
        };
        FlowDocument Doc() => new()
        {
            Id = Guid.NewGuid(), OrgId = org, FlowId = flow.Id, FileName = "a.pdf", ContentType = "application/pdf", SourceFileKey = "k",
            Sha256 = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), Status = DocumentStatus.Ready, CreatedAt = now, UpdatedAt = now,
            ReadJson = DocumentJson.Serialize(new ReadResult("Invoice",
                [new ReadField("invoice_no", "Invoice number", "INV-7", FieldTypes.Text, null, 1, null, false, true, false)], [])),
        };
        var (current, stale) = (Doc(), Doc());
        await using (var seed = NewDb())
        {
            seed.Organisations.Add(new Organisation { Id = org, Name = "Test Org A", Slug = "acme-" + org.ToString("N") });
            seed.Flows.Add(flow);
            seed.FlowDocuments.AddRange(current, stale);
            seed.Set<FlowOutput>().Add(new FlowOutput
            {
                Id = Guid.NewGuid(), OrgId = org, FlowId = flow.Id, Name = "Accounting CSV", Format = FlowOutputFormats.Csv,
                TemplateJson = DocumentJson.Serialize(template), CreatedAt = now, UpdatedAt = now,
            });
            await seed.SaveChangesAsync();
        }

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.CanProcessOrdersAsync(org, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var storage = new Mock<IFileStorageService>();

        await using var db = NewDb();
        var sender = new DocumentSender(db, billing.Object, storage.Object, Mock.Of<IBackgroundJobClient>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<DocumentSender>.Instance, new DocumentEngineGate(Microsoft.Extensions.Options.Options.Create(new DocumentEngineOptions())));
        var loaded = await db.FlowDocuments.SingleAsync(d => d.Id == current.Id);
        var staleCopy = await db.FlowDocuments.SingleAsync(d => d.Id == stale.Id);
        await using (var other = NewDb())
            await other.FlowDocuments.Where(d => d.Id == stale.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.UpdatedAt, DateTime.UtcNow.AddSeconds(5)));

        (await sender.SendAsync(loaded, SendMode.Person, null, CancellationToken.None)).Ok.Should().BeTrue();
        (await sender.SendAsync(staleCopy, SendMode.Person, null, CancellationToken.None)).Error.Should().Be("not_ready");

        await using var check = NewDb();
        (await check.FlowDocuments.Where(d => d.Id == current.Id).Select(d => d.Status).SingleAsync())
            .Should().BeOneOf(DocumentStatus.Sending, DocumentStatus.Sent);
        (await check.FlowDocuments.Where(d => d.Id == stale.Id).Select(d => d.Status).SingleAsync()).Should().Be(DocumentStatus.Ready);
    }
}
