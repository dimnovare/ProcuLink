using System.Text.Json;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Security;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

public sealed class FlowWebhookTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly DocumentEngineGate Gate = new(Microsoft.Extensions.Options.Options.Create(new DocumentEngineOptions { EnabledForAll = true }));

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>().UseInMemoryDatabase($"flow-webhook-{Guid.NewGuid()}").Options);

    private static DeliveryEncryptionService Encryption() =>
        new(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]) }).Build());

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public byte[] Body { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = await request.Content!.ReadAsByteArrayAsync(ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task A_read_on_a_flow_with_a_webhook_enqueues_one_event()
    {
        var db = NewDb();
        var withHook = new Flow { Id = Guid.NewGuid(), OrgId = Org, Name = "A", FieldsJson = DocumentJson.Serialize(new FlowFieldList([])) };
        var without = new Flow { Id = Guid.NewGuid(), OrgId = Org, Name = "B", FieldsJson = DocumentJson.Serialize(new FlowFieldList([])) };
        db.Flows.AddRange(withHook, without);
        db.FlowIntakes.Add(new FlowIntake
        {
            Id = Guid.NewGuid(), OrgId = Org, FlowId = withHook.Id, Kind = IntakeKind.Api,
            ConfigJson = """{"webhookUrl":"https://hooks.example.test/in"}""",
        });
        var docA = new FlowDocument { Id = Guid.NewGuid(), OrgId = Org, FlowId = withHook.Id, FileName = "a.csv", SourceFileKey = "k", Status = DocumentStatus.Reading };
        var docB = new FlowDocument { Id = Guid.NewGuid(), OrgId = Org, FlowId = without.Id, FileName = "b.csv", SourceFileKey = "k", Status = DocumentStatus.Reading };
        db.FlowDocuments.AddRange(docA, docB);
        await db.SaveChangesAsync();

        var storage = new Mock<IFileStorageService>();
        storage.Setup(s => s.DownloadAsync("k", It.IsAny<CancellationToken>())).ReturnsAsync(() => new MemoryStream("a,b\r\n1,2\r\n"u8.ToArray()));
        var reader = new Mock<IDocumentReader>();
        reader.Setup(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), Org, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentReadOutcome(true, new ReadResult("Invoice", []), null));
        var jobs = new Mock<IBackgroundJobClient>();
        var job = new ReadDocumentJob(db, storage.Object, reader.Object, new DocumentRechecker(db), jobs.Object, Mock.Of<ProcuLink.Core.Services.Ocr.IPdfRasterizer>(), Mock.Of<ProcuLink.Core.Services.Ocr.IDocumentOcrService>(), new DocumentEngineGate(Microsoft.Extensions.Options.Options.Create(new DocumentEngineOptions())), NullLogger<ReadDocumentJob>.Instance);

        await job.ExecuteAsync(docA.Id, Org, null, CancellationToken.None);
        await job.ExecuteAsync(docB.Id, Org, null, CancellationToken.None);

        var webhooks = jobs.Invocations.Where(i => i.Method.Name == nameof(IBackgroundJobClient.Create))
            .Select(i => (Job)((Job)i.Arguments[0])).Where(j => j.Type == typeof(FlowWebhookJob)).ToList();
        webhooks.Should().ContainSingle();
        webhooks[0].Args[1].Should().Be(docA.Id);
        webhooks[0].Args[2].Should().Be("document.read");
    }

    [Fact]
    public async Task The_webhook_body_is_signed()
    {
        var db = NewDb();
        var enc = Encryption();
        var intake = new FlowIntake
        {
            Id = Guid.NewGuid(), OrgId = Org, FlowId = Guid.NewGuid(), Kind = IntakeKind.Api,
            ConfigJson = """{"webhookUrl":"https://hooks.example.test/in"}""",
        };
        intake.EncryptedCredentials = enc.Encrypt("""{"webhookSecret":"s3cret"}""",
            new CredentialScope(Org, CredentialPurpose.FlowIntakeCredentials, intake.Id));
        db.FlowIntakes.Add(intake);
        var doc = new FlowDocument { Id = Guid.NewGuid(), OrgId = Org, FlowId = intake.FlowId, FileName = "a.csv", Status = DocumentStatus.NeedsReview };
        db.FlowDocuments.Add(doc);
        await db.SaveChangesAsync();

        var handler = new CapturingHandler();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("delivery")).Returns(() => new HttpClient(handler));
        var guard = new OutboundRequestGuard(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Delivery:AllowPrivateNetworkTargets"] = "true" }).Build(), NullLogger<OutboundRequestGuard>.Instance);
        var job = new FlowWebhookJob(db, factory.Object, enc, guard, Gate, NullLogger<FlowWebhookJob>.Instance);
        var eventId = Guid.NewGuid();

        await job.ExecuteAsync(Org, doc.Id, "document.read", eventId, CancellationToken.None);

        handler.Request!.Headers.GetValues("X-ProcuLink-Signature").Single()
            .Should().Be(WebhookSignature.Header(handler.Body, "s3cret")).And.StartWith("sha256=");
        handler.Request.Headers.GetValues("X-ProcuLink-Event-Id").Single().Should().Be(eventId.ToString());
        JsonDocument.Parse(handler.Body).RootElement.GetProperty("document").GetProperty("id").GetGuid().Should().Be(doc.Id);
    }

    [Fact]
    public async Task A_secret_that_will_not_decrypt_sends_nothing()
    {
        var db = NewDb();
        var enc = Encryption();
        var intake = new FlowIntake
        {
            Id = Guid.NewGuid(), OrgId = Org, FlowId = Guid.NewGuid(), Kind = IntakeKind.Api,
            ConfigJson = """{"webhookUrl":"https://hooks.example.test/in"}""",
        };
        // Bound to another intake, so it will not open for this one.
        intake.EncryptedCredentials = enc.Encrypt("""{"webhookSecret":"s3cret"}""",
            new CredentialScope(Org, CredentialPurpose.FlowIntakeCredentials, Guid.NewGuid()));
        db.FlowIntakes.Add(intake);
        var doc = new FlowDocument { Id = Guid.NewGuid(), OrgId = Org, FlowId = intake.FlowId, FileName = "a.csv", Status = DocumentStatus.NeedsReview };
        db.FlowDocuments.Add(doc);
        await db.SaveChangesAsync();

        var handler = new CapturingHandler();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("delivery")).Returns(() => new HttpClient(handler));
        var guard = new OutboundRequestGuard(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Delivery:AllowPrivateNetworkTargets"] = "true" }).Build(), NullLogger<OutboundRequestGuard>.Instance);
        var job = new FlowWebhookJob(db, factory.Object, enc, guard, Gate, NullLogger<FlowWebhookJob>.Instance);

        var act = () => job.ExecuteAsync(Org, doc.Id, "document.read", Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Request.Should().BeNull();
    }
}
