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

    [DockerRequiredFact]
    public async Task Two_sends_of_an_x12_output_carry_different_interchange_control_numbers()
    {
        var cs = await postgres.CreateDatabaseAsync("proculink_sendcontrol");
        ProcuLinkDbContext NewDb() => new(new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs, n => n.CommandTimeout(180)).Options);
        var now = DateTime.UtcNow;
        var org = Guid.NewGuid();
        (string Key, string Meaning, string Value)[] header = [("po", FieldMeanings.DocumentNumber, "PO-1"), ("date", FieldMeanings.IssueDate, "2026-11-13"), ("cur", FieldMeanings.Currency, "EUR")];
        (string Key, string Meaning, string Value)[] line = [("code", FieldMeanings.LineItemCode, "A-1"), ("qty", FieldMeanings.LineQuantity, "3"), ("price", FieldMeanings.LineUnitPrice, "9.99")];
        var flow = new Flow
        {
            Id = Guid.NewGuid(), OrgId = org, Name = "Orders", FieldsConfirmed = true, CreatedAt = now, UpdatedAt = now,
            FieldsJson = DocumentJson.Serialize(new FlowFieldList(
                header.Select(f => new FlowField(f.Key, f.Key, FieldTypes.Text, f.Meaning, false)).ToList(),
                [new FlowTable("lines", "Lines", line.Select(f => new FlowField(f.Key, f.Key, FieldTypes.Text, f.Meaning, false)).ToList())])),
        };
        var read = DocumentJson.Serialize(new ReadResult("Purchase order",
            header.Select(f => new ReadField(f.Key, f.Key, f.Value, FieldTypes.Text, null, 1, null, false, true, false)).ToList(),
            [new ReadTable("lines", "Lines", line.Select(f => new ReadColumn(f.Key, f.Key, FieldTypes.Text, null)).ToList(),
                [new ReadRow(line.ToDictionary(f => f.Key, f => new ReadCell(f.Value, 1, null, false, false)))], true)]));
        FlowDocument Doc() => new()
        {
            Id = Guid.NewGuid(), OrgId = org, FlowId = flow.Id, FileName = "a.pdf", ContentType = "application/pdf", SourceFileKey = "k",
            Sha256 = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), Status = DocumentStatus.Ready, CreatedAt = now, UpdatedAt = now, ReadJson = read,
        };
        var docs = new[] { Doc(), Doc() };
        await using (var seed = NewDb())
        {
            seed.Organisations.Add(new Organisation { Id = org, Name = "Test Org A", Slug = "acme-" + org.ToString("N") });
            seed.Flows.Add(flow);
            seed.FlowDocuments.AddRange(docs);
            seed.Set<FlowOutput>().Add(new FlowOutput
            {
                Id = Guid.NewGuid(), OrgId = org, FlowId = flow.Id, Name = "X12", Format = FlowOutputFormats.Text, Standard = "x12-850",
                TableKey = "lines", TemplateJson = "{}", CreatedAt = now, UpdatedAt = now,
            });
            await seed.SaveChangesAsync();
        }

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.CanProcessOrdersAsync(org, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var isa13 = new List<string>();
        var storage = new Mock<IFileStorageService>();
        storage.Setup(s => s.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, string, string, CancellationToken>((body, _, _, _) => isa13.Add(new StreamReader(body).ReadToEnd().Split('~')[0].Split('*')[13]));

        await using var db = NewDb();
        var sender = new DocumentSender(db, billing.Object, storage.Object, Mock.Of<IBackgroundJobClient>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<DocumentSender>.Instance, new DocumentEngineGate(Microsoft.Extensions.Options.Options.Create(new DocumentEngineOptions())));
        foreach (var d in docs)
            (await sender.SendAsync(await db.FlowDocuments.SingleAsync(x => x.Id == d.Id), SendMode.Person, null, CancellationToken.None)).Ok.Should().BeTrue();

        isa13.Should().HaveCount(2).And.OnlyHaveUniqueItems().And.NotContain("000000001");
    }
}
