using System.Text.Json;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Api.Tests.Integration;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public sealed class DeliverDocumentJobTests(PostgresContainerFixture postgres)
{
    private sealed record Call(string IdempotencyKey, string FileName);

    private sealed class FakeDispatcher : IDeliveryDispatcher
    {
        public string Protocol { get; set; } = "http";
        public ResendSafety ResendSafety { get; set; } = ResendSafety.Safe;
        public List<Call> Calls { get; } = [];
        public DeliveryResult Result { get; set; } = new(true, null, 200);

        public Task<DeliveryResult> DispatchAsync(byte[] content, string fileName, string contentType, SupplierDeliveryConfig config,
            string decryptedCredentials, CancellationToken ct, string? idempotencyKey = null, bool isTestFire = false)
        {
            Calls.Add(new(idempotencyKey!, fileName));
            return Task.FromResult(Result);
        }
    }

    private sealed class MemoryStorage(Dictionary<string, byte[]> stored) : IFileStorageService
    {
        public async Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            stored[key] = ms.ToArray();
            return key;
        }
        public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) => Task.FromResult(key);
        public Task<Stream> DownloadAsync(string key, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream(stored.GetValueOrDefault(key) ?? "a,b\r\n1,2\r\n"u8.ToArray()));
        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Harness
    {
        public required string ConnectionString { get; init; }
        public required Guid Org { get; init; }
        public required Guid DocumentId { get; init; }
        public required Guid DeliveryId { get; init; }
        public required Guid FlowId { get; init; }
        public FakeDispatcher Dispatcher { get; } = new();
        public Dictionary<string, byte[]> Stored { get; } = [];
        public Mock<IBackgroundJobClient> Jobs { get; } = new();
        public Mock<ILogger<DeliverDocumentJob>> Log { get; } = new();

        public ProcuLinkDbContext NewDb() =>
            new(new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(ConnectionString, n => n.CommandTimeout(180)).Options);

        /// <summary>A fresh context and job per activation, as Hangfire gives each run its own scope.</summary>
        public async Task RunAsync()
        {
            await using var db = NewDb();
            var encryption = new DeliveryEncryptionService(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]) }).Build());
            var job = new DeliverDocumentJob(db, new MemoryStorage(Stored), new FlowDestinationService(db, encryption, [], new ProcuLink.Api.Services.Google.LinkedAccountAvailability(new DocumentEngineGate(Microsoft.Extensions.Options.Options.Create(new DocumentEngineOptions())), [], Microsoft.Extensions.Options.Options.Create(new ProcuLink.Infrastructure.Services.Google.GoogleOptions())), null!), [Dispatcher],
                Jobs.Object, new DeliveryReliabilityOptions { MaxAttempts = 3 }, Log.Object);
            await job.ExecuteAsync(DeliveryId, Org, CancellationToken.None);
        }

        public async Task<FlowDelivery> DeliveryAsync() { await using var db = NewDb(); return await db.Set<FlowDelivery>().AsNoTracking().SingleAsync(d => d.Id == DeliveryId); }
        public async Task<string> DocStatusAsync() { await using var db = NewDb(); return await db.FlowDocuments.Where(d => d.Id == DocumentId).Select(d => d.Status).SingleAsync(); }

        public async Task AddWebhookAsync()
        {
            await using var db = NewDb();
            db.FlowIntakes.Add(new FlowIntake { Id = Guid.NewGuid(), OrgId = Org, FlowId = FlowId, Kind = IntakeKind.Api, ConfigJson = """{"webhookUrl":"https://hooks.example.test/in"}""" });
            await db.SaveChangesAsync();
        }

        public async Task<DeliveryBounceResult?> BounceAsync(string key)
        {
            await using var db = NewDb();
            return await new FlowDeliveryBounceHandler(db, Jobs.Object, NullLogger<FlowDeliveryBounceHandler>.Instance)
                .TryHandleAsync(new DeliveryBounceNotification(key, DeliveryBounceKind.Hard, "supplier@example.test", null, null), CancellationToken.None);
        }

        public async Task<List<JsonElement>> FailurePayloadsAsync()
        {
            await using var db = NewDb();
            return (await db.AuditEvents.Where(a => a.EntityId == DocumentId && a.Action == "document.delivery_failed").OrderBy(a => a.CreatedAt).ToListAsync())
                .Select(a => a.Payload!.RootElement.Clone()).ToList();
        }

        public async Task<int> AuditCountAsync(string action) { await using var db = NewDb(); return await db.AuditEvents.CountAsync(a => a.EntityId == DocumentId && a.Action == action); }

        public IEnumerable<string> WebhookEvents() => Jobs.Invocations
            .Where(i => i.Method.Name == nameof(IBackgroundJobClient.Create) && i.Arguments[0] is Job j && j.Type == typeof(FlowWebhookJob))
            .Select(i => (string)((Job)i.Arguments[0]).Args[2]);

        /// <summary>Turns the seeded delivery into a KSeF one queued for <paramref name="queuedFor"/>, its destination now on <paramref name="destinationEnvironment"/>.</summary>
        public async Task KsefAsync(string destinationEnvironment, string queuedFor)
        {
            Dispatcher.Protocol = "ksef";
            await using var db = NewDb();
            var d = await db.Set<FlowDelivery>().SingleAsync(x => x.Id == DeliveryId);
            (d.Protocol, d.RemoteEnvironment) = ("ksef", queuedFor);
            var dest = await db.Set<FlowDestination>().SingleAsync(x => x.Id == d.DestinationId);
            (dest.Protocol, dest.ConfigJson) = ("ksef", "{\"environment\":\"" + destinationEnvironment + "\",\"nip\":\"1111111111\"}");
            await db.SaveChangesAsync();
        }

        /// <summary>Makes a failed delivery's backoff elapse.</summary>
        public async Task DueAsync() { await using var db = NewDb(); await db.Set<FlowDelivery>().Where(d => d.Id == DeliveryId).ExecuteUpdateAsync(s => s.SetProperty(d => d.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1))); }

        public async Task SetAsync(string status, DateTime? claimedAt, int attempts)
        {
            await using var db = NewDb();
            await db.Set<FlowDelivery>().Where(d => d.Id == DeliveryId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, status).SetProperty(d => d.ClaimedAt, claimedAt).SetProperty(d => d.Attempts, attempts));
        }
    }

    [DockerRequiredFact]
    public async Task Ksef_number_and_receipt_are_kept_on_the_delivery()
    {
        var h = await SeedAsync();
        h.Dispatcher.Result = new DeliveryResult(true, null, 200, RemoteReference: "1111111111-20261126-ABCDEF123456-7A",
            RemoteEnvironment: "test", Receipt: "<UPO/>"u8.ToArray());

        await h.RunAsync();

        var d = await h.DeliveryAsync();
        (d.Status, d.RemoteReference, d.RemoteEnvironment).Should().Be((FlowDeliveryStatus.Delivered, "1111111111-20261126-ABCDEF123456-7A", "test"));
        d.ReceiptKey.Should().NotBeNull();
        System.Text.Encoding.UTF8.GetString(h.Stored[d.ReceiptKey!]).Should().Be("<UPO/>");
    }

    [DockerRequiredFact]
    public async Task A_ksef_delivery_is_not_sent_to_an_environment_it_was_not_queued_for()
    {
        var h = await SeedAsync();
        await h.KsefAsync(destinationEnvironment: "production", queuedFor: "test");

        await h.RunAsync();

        h.Dispatcher.Calls.Should().BeEmpty();
        var d = await h.DeliveryAsync();
        (d.Status, d.FailureKind, d.LastError).Should().Be((FlowDeliveryStatus.DeadLetter, FlowDeliveryFailureKind.Setup,
            "This KSeF destination was switched to another environment after this invoice was queued. Nothing was sent. Send the document again to check and confirm it."));
    }

    [DockerRequiredFact]
    public async Task A_ksef_reference_is_kept_when_the_outcome_is_unknown()
    {
        var h = await SeedAsync();
        await h.KsefAsync(destinationEnvironment: "production", queuedFor: "production");
        h.Dispatcher.Result = new DeliveryResult(false, "unconfirmed", Outcome: DeliveryOutcome.NotRetryable, RemoteReference: "INV-1");

        await h.RunAsync();

        var d = await h.DeliveryAsync();
        // An unconfirmed outcome is ambiguous, so it is ours (it pages), never the customer's.
        (d.Status, d.FailureKind, d.RemoteReference, d.RemoteEnvironment).Should().Be((FlowDeliveryStatus.DeadLetter, FlowDeliveryFailureKind.Internal, "INV-1", "production"));
    }

    [DockerRequiredFact]
    public async Task A_dead_letter_logs_one_warning_and_no_error()
    {
        var h = await SeedAsync();
        h.Dispatcher.Result = new DeliveryResult(false, "rejected", 422, Outcome: DeliveryOutcome.NotRetryable);

        await h.RunAsync();

        (await h.DeliveryAsync()).Status.Should().Be(FlowDeliveryStatus.DeadLetter);
        h.Log.Verify(l => l.Log(LogLevel.Warning, It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains(h.DeliveryId.ToString()) && v.ToString()!.Contains(h.Org.ToString())
                                          && v.ToString()!.Contains("http") && v.ToString()!.Contains("refused")),
            null, It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        h.Log.Verify(l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Never);
    }

    [DockerRequiredFact]
    public async Task A_hard_bounce_dead_letters_a_delivered_delivery_once_and_announces_it()
    {
        var h = await SeedAsync();
        await h.AddWebhookAsync();
        await h.RunAsync();
        var key = (await h.DeliveryAsync()).DispatchKey.ToString("N");

        (await h.BounceAsync(key))!.Outcome.Should().Be(DeliveryBounceOutcome.FlowDeliveryMarkedFailed);
        (await h.BounceAsync(key))!.Outcome.Should().Be(DeliveryBounceOutcome.AlreadyResolved);

        var delivery = await h.DeliveryAsync();
        delivery.Status.Should().Be(FlowDeliveryStatus.DeadLetter);
        delivery.LastError.Should().Be(FlowDeliveryBounceHandler.HardBounceError);
        (await h.DocStatusAsync()).Should().Be(DocumentStatus.SendFailed);
        (await h.AuditCountAsync("document.delivery_failed")).Should().Be(1);
        h.WebhookEvents().Should().Equal(FlowWebhookEvent.Sent, FlowWebhookEvent.SendFailed);
        (await h.FailurePayloadsAsync()).Single().GetProperty("destination").GetString().Should().Be("Accounting webhook");

        // A destination id naming another workspace's destination never lends that workspace's name.
        await using (var db = h.NewDb())
        {
            var other = Guid.NewGuid();
            var foreign = new FlowDestination { Id = Guid.NewGuid(), OrgId = other, Name = "Foreign webhook", Protocol = "http", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Organisations.Add(new Organisation { Id = other, ClerkOrgId = "org_" + other.ToString("N"), Name = "Test Org B", Slug = "other-" + other.ToString("N") });
            db.Set<FlowDestination>().Add(foreign);
            await db.SaveChangesAsync();
            await db.Set<FlowDelivery>().Where(d => d.Id == h.DeliveryId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, FlowDeliveryStatus.Delivered).SetProperty(d => d.DestinationId, foreign.Id));
        }
        (await h.BounceAsync(key))!.Outcome.Should().Be(DeliveryBounceOutcome.FlowDeliveryMarkedFailed);
        (await h.FailurePayloadsAsync())[1].GetProperty("destination").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [DockerRequiredFact]
    public async Task A_bounce_leaves_an_undelivered_row_alone_and_ignores_keys_that_are_not_flow_keys()
    {
        var h = await SeedAsync();
        var key = (await h.DeliveryAsync()).DispatchKey.ToString("N");
        await h.SetAsync(FlowDeliveryStatus.Failed, null, 1); // being retried under the same key

        (await h.BounceAsync(key))!.Outcome.Should().Be(DeliveryBounceOutcome.AlreadyResolved);
        (await h.BounceAsync(Guid.NewGuid().ToString("N"))).Should().BeNull();
        (await h.BounceAsync("po-" + Guid.NewGuid() + "-" + Guid.NewGuid())).Should().BeNull();

        (await h.DeliveryAsync()).Status.Should().Be(FlowDeliveryStatus.Failed);
        (await h.DocStatusAsync()).Should().Be(DocumentStatus.Sending);
        (await h.AuditCountAsync("document.delivery_failed")).Should().Be(0);
    }

    private async Task<Harness> SeedAsync()
    {
        var cs = await postgres.CreateDatabaseAsync("proculink_deliverdocument");
        var now = DateTime.UtcNow;
        var org = Guid.NewGuid();
        var flow = new Flow { Id = Guid.NewGuid(), OrgId = org, Name = "Invoices", CreatedAt = now, UpdatedAt = now };
        var doc = new FlowDocument
        {
            Id = Guid.NewGuid(), OrgId = org, FlowId = flow.Id, FileName = "a.pdf", ContentType = "application/pdf",
            SourceFileKey = "k", Sha256 = new string('a', 64), Status = DocumentStatus.Sending, CreatedAt = now, UpdatedAt = now,
        };
        var output = new FlowOutput { Id = Guid.NewGuid(), OrgId = org, FlowId = flow.Id, Name = "Accounting CSV", Format = FlowOutputFormats.Csv, CreatedAt = now, UpdatedAt = now };
        var destination = new FlowDestination { Id = Guid.NewGuid(), OrgId = org, Name = "Accounting webhook", Protocol = "http", CreatedAt = now, UpdatedAt = now };
        var delivery = new FlowDelivery
        {
            Id = Guid.NewGuid(), OrgId = org, DocumentId = doc.Id, OutputId = output.Id, OutputName = output.Name, DestinationId = destination.Id,
            Protocol = "http", DispatchKey = Guid.NewGuid(), ArtifactKey = "key.csv", ContentType = "text/csv", FileName = "a.csv", CreatedAt = now, UpdatedAt = now,
        };
        await using var db = new ProcuLinkDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs, n => n.CommandTimeout(180)).Options);
        db.Organisations.Add(new Organisation { Id = org, Name = "Test Org A", Slug = "acme-" + org.ToString("N") });
        db.Flows.Add(flow);
        db.FlowDocuments.Add(doc);
        db.Set<FlowOutput>().Add(output);
        db.Set<FlowDestination>().Add(destination);
        db.Set<FlowDelivery>().Add(delivery);
        await db.SaveChangesAsync();
        return new Harness { ConnectionString = cs, Org = org, DocumentId = doc.Id, DeliveryId = delivery.Id, FlowId = flow.Id };
    }

    [DockerRequiredFact]
    public async Task A_delivery_is_dispatched_once_and_carries_its_id()
    {
        var h = await SeedAsync();

        await h.RunAsync();
        await h.RunAsync();

        h.Dispatcher.Calls.Should().HaveCount(1);
        var delivery = await h.DeliveryAsync();
        h.Dispatcher.Calls[0].IdempotencyKey.Should().Be(delivery.DispatchKey.ToString("N"));
        delivery.Status.Should().Be(FlowDeliveryStatus.Delivered);
        delivery.DeliveredAt.Should().NotBeNull();
        delivery.ResponseCode.Should().Be(200);
        (await h.DocStatusAsync()).Should().Be(DocumentStatus.Sent);

        // A delivery claimed 30s ago is in flight elsewhere and left alone; one claimed 3 minutes ago is reclaimed.
        await h.SetAsync(FlowDeliveryStatus.Delivering, DateTime.UtcNow.AddSeconds(-30), 1);
        await h.RunAsync();
        h.Dispatcher.Calls.Should().HaveCount(1);
        await h.SetAsync(FlowDeliveryStatus.Delivering, DateTime.UtcNow.AddMinutes(-3), 1);
        await h.RunAsync();
        h.Dispatcher.Calls.Should().HaveCount(2);
        (await h.DeliveryAsync()).Status.Should().Be(FlowDeliveryStatus.Delivered);
    }

    [DockerRequiredFact]
    public async Task Failures_retry_then_dead_letter_and_the_document_shows_it()
    {
        var h = await SeedAsync();
        h.Dispatcher.Result = new DeliveryResult(false, "503", 503);

        await using (var db = h.NewDb())
            await db.FlowDocuments.Where(d => d.Id == h.DocumentId).ExecuteUpdateAsync(x => x.SetProperty(d => d.FlowVersion, 4));

        await h.RunAsync();
        (await h.DeliveryAsync()).Status.Should().Be(FlowDeliveryStatus.Failed);
        (await h.DeliveryAsync()).NextAttemptAt.Should().NotBeNull();
        await h.RunAsync(); // not due yet: left alone
        h.Dispatcher.Calls.Should().HaveCount(1);
        await h.DueAsync();
        await h.RunAsync();
        await h.DueAsync();
        await h.RunAsync();

        var delivery = await h.DeliveryAsync();
        delivery.Status.Should().Be(FlowDeliveryStatus.DeadLetter);
        delivery.Attempts.Should().Be(3);
        (await h.DocStatusAsync()).Should().Be(DocumentStatus.SendFailed);
        h.Jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<ScheduledState>()), Times.Exactly(2));
        var failures = await h.FailurePayloadsAsync();
        failures.Should().ContainSingle();
        failures[0].GetProperty("attempts").GetInt32().Should().Be(3);
        failures[0].GetProperty("destinationId").GetGuid().Should().Be(delivery.DestinationId!.Value);
        failures[0].GetProperty("file").GetString().Should().Be("a.csv");
        failures[0].GetProperty("destination").GetString().Should().Be("Accounting webhook");   // the seeded destination's name, kept in the event itself
        failures[0].GetProperty("version").GetInt32().Should().Be(4);

        // A refusal that retrying cannot fix dead-letters on the first attempt.
        h.Dispatcher.Result = new DeliveryResult(false, "rejected", 422, Outcome: DeliveryOutcome.NotRetryable);
        await h.SetAsync(FlowDeliveryStatus.Queued, null, 0);
        await h.RunAsync();
        var refused = await h.DeliveryAsync();
        refused.Status.Should().Be(FlowDeliveryStatus.DeadLetter);
        refused.Attempts.Should().Be(1);
        (await h.FailurePayloadsAsync()).Select(p => p.GetProperty("attempts").GetInt32()).Should().Equal(3, 1);   // the first failure keeps its own count
    }

    [DockerRequiredFact]
    public async Task A_reclaimed_delivery_on_a_channel_that_cannot_deduplicate_is_not_sent_again()
    {
        var h = await SeedAsync();
        h.Dispatcher.ResendSafety = ResendSafety.Unsafe;
        await h.SetAsync(FlowDeliveryStatus.Delivering, DateTime.UtcNow.AddMinutes(-3), 1);

        await h.RunAsync();

        h.Dispatcher.Calls.Should().BeEmpty();
        var delivery = await h.DeliveryAsync();
        delivery.Status.Should().Be(FlowDeliveryStatus.DeadLetter);
        delivery.LastError.Should().Be("This may already have been sent. Check with the recipient before sending again.");
        (await h.DocStatusAsync()).Should().Be(DocumentStatus.SendFailed);
    }

    [Theory]
    [InlineData("b2brouter", null, 422, true)]   // the file is wrong (a seller not on the account, a number taken): no destination to fix
    [InlineData("b2brouter", "7", null, true)]   // B2Brouter holds the invoice
    [InlineData("b2brouter", null, 401, false)]  // the key is wrong: fix the destination
    [InlineData("http", null, 422, false)]
    public void Fix_the_destination_is_not_advised_when_the_file_or_the_network_is_the_problem(string protocol, string? reference, int? code, bool holds) =>
        DeliverDocumentJob.RemoteMayHold(protocol, FlowDeliveryStatus.DeadLetter, reference, code).Should().Be(holds);
}
