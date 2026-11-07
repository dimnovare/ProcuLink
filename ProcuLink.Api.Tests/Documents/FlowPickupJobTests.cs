using System.Text.Json;
using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Api.Services.Documents.Intake;
using ProcuLink.Api.Tests.Integration;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Security;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public sealed class FlowPickupJobTests(PostgresContainerFixture postgres)
{
    private sealed class MemoryStorage : IFileStorageService
    {
        public Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct) => Task.FromResult(key);
        public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) => Task.FromResult(key);
        public Task<Stream> DownloadAsync(string key, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream());
        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Harness
    {
        public required string ConnectionString { get; init; }
        public required Guid Org { get; init; }
        public required Guid IntakeId { get; init; }
        public Mock<IPickupSource> Source { get; } = new();
        public Mock<IPickupSession> Session { get; } = new();
        public Mock<IBillingService> Billing { get; } = new();

        public Harness()
        {
            Source.SetupGet(s => s.Kind).Returns(IntakeKind.Sftp);
            Source.Setup(s => s.OpenAsync(It.IsAny<FlowIntake>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Session.Object);
            Session.Setup(s => s.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
            {
                new PickupFile("path:/in/a.csv", "a.csv", "/in/a.csv"),
                new PickupFile("path:/in/b.docx", "b.docx", "/in/b.docx"),
            });
            Session.Setup(s => s.DownloadAsync(It.IsAny<PickupFile>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream("a,b\r\n1,2\r\n"u8.ToArray()));
            Billing.Setup(b => b.CheckOrderLimitAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new LimitCheckResult(true, false, "growth", 150));
        }

        public ProcuLinkDbContext NewDb() =>
            new(new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(ConnectionString, n => n.CommandTimeout(180)).Options);

        public async Task RunAsync()
        {
            await using var db = NewDb();
            var gate = new DocumentEngineGate(Options.Create(new DocumentEngineOptions { EnabledForAll = true }));
            var submitter = new DocumentIntakeService(db, new MemoryStorage(), new Mock<IBackgroundJobClient>().Object, gate, Billing.Object);
            var job = new FlowPickupJob(db, [Source.Object], Encryption, submitter, gate, Billing.Object, NullLogger<FlowPickupJob>.Instance);
            await job.ExecuteAsync(Org, IntakeId, CancellationToken.None);
        }

        public static DeliveryEncryptionService Encryption => new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]) }).Build());

        public async Task<List<FlowDocument>> DocumentsAsync() { await using var db = NewDb(); return await db.FlowDocuments.AsNoTracking().ToListAsync(); }
        public async Task<int> LedgerAsync() { await using var db = NewDb(); return await db.FlowIntakeItems.CountAsync(); }
        public async Task<FlowIntake> IntakeAsync() { await using var db = NewDb(); return await db.FlowIntakes.AsNoTracking().SingleAsync(); }
    }

    private async Task<Harness> SeedAsync()
    {
        var cs = await postgres.CreateDatabaseAsync("proculink_flowpickup");
        var now = DateTime.UtcNow;
        var org = Guid.NewGuid();
        var flow = new Flow { Id = Guid.NewGuid(), OrgId = org, Name = "Invoices", CreatedAt = now, UpdatedAt = now };
        var intake = new FlowIntake
        {
            Id = Guid.NewGuid(), OrgId = org, FlowId = flow.Id, Kind = IntakeKind.Sftp, CreatedAt = now, UpdatedAt = now,
            ConfigJson = """{"host":"sftp.example.test","port":22,"username":"u","directory":"/in"}""",
        };
        intake.EncryptedCredentials = Harness.Encryption.Encrypt("""{"password":"pw"}""",
            new CredentialScope(org, CredentialPurpose.FlowIntakeCredentials, intake.Id));
        await using var db = new ProcuLinkDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs, n => n.CommandTimeout(180)).Options);
        db.Organisations.Add(new Organisation { Id = org, Name = "Test Org A", Slug = "acme-" + org.ToString("N") });
        db.Flows.Add(flow);
        db.FlowIntakes.Add(intake);
        await db.SaveChangesAsync();
        return new Harness { ConnectionString = cs, Org = org, IntakeId = intake.Id };
    }

    [DockerRequiredFact]
    public async Task A_file_is_picked_up_once()
    {
        var h = await SeedAsync();

        await h.RunAsync();
        await h.RunAsync();

        var docs = await h.DocumentsAsync();
        docs.Should().ContainSingle().Which.Source.Should().Be("sftp");
        h.Session.Verify(s => s.DownloadAsync(It.Is<PickupFile>(f => f.FileName == "b.docx"), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        (await h.IntakeAsync()).LastMessage.Should().Be("No new files.");
        // Run 1 marks it after taking it; run 2 marks it again because it was already ingested.
        h.Session.Verify(s => s.MarkDoneAsync(It.Is<PickupFile>(f => f.FileName == "a.csv"), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [DockerRequiredFact]
    public async Task A_paused_plan_takes_nothing_and_says_why()
    {
        var h = await SeedAsync();
        h.Billing.Setup(b => b.CheckOrderLimitAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LimitCheckResult(false, true, "pilot", 20));

        await h.RunAsync();

        h.Source.Verify(s => s.OpenAsync(It.IsAny<FlowIntake>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()), Times.Never);
        (await h.LedgerAsync()).Should().Be(0);
        var intake = await h.IntakeAsync();
        intake.LastStatus.Should().Be("paused");
        intake.LastMessage.Should().Be("Paused: your trial has ended, so new documents aren't being accepted.");
    }

    [DockerRequiredFact]
    public async Task A_claim_left_by_a_crash_is_retaken_after_30_minutes()
    {
        var h = await SeedAsync();
        await using (var db = h.NewDb())
        {
            db.FlowIntakeItems.Add(new FlowIntakeItem
            {
                Id = Guid.NewGuid(), OrgId = h.Org, ScopeId = h.IntakeId, ItemKey = "path:/in/a.csv",
                DocumentId = null, CreatedAt = DateTime.UtcNow.AddMinutes(-31),
            });
            await db.SaveChangesAsync();
        }

        await h.RunAsync();

        (await h.DocumentsAsync()).Should().ContainSingle();
        (await h.IntakeAsync()).LastMessage.Should().Be("Picked up 1 new file(s).");
    }
}
