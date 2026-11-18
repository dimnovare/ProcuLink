using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Api.Services.PhoneAlerts;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public class PhoneAlertJobTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private static readonly Guid OrgA = DocumentEngineTestFactory.OrgA, OrgB = DocumentEngineTestFactory.OrgB;

    private sealed class CapturingAlertSender(bool canSend = true) : IPhoneAlertSender
    {
        public Dictionary<string, PushResult> Results { get; } = [];
        public List<(string Endpoint, string Json, string Topic)> Sent { get; } = [];
        public bool CanSend => canSend;
        public Task<PushResult> SendAsync(PushDevice device, string json, string topic, CancellationToken ct)
        {
            Sent.Add((device.Endpoint, json, topic));
            return Task.FromResult(Results.GetValueOrDefault(device.Endpoint, PushResult.Sent));
        }
    }

    private static string Endpoint(string name) => $"https://fcm.googleapis.com/fcm/send/{name}";

    private async Task Seed(Action<ProcuLinkDbContext> seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        db.RemoveRange(db.Set<PushDevice>());
        db.RemoveRange(db.FlowDocuments);
        db.RemoveRange(db.AuditEvents);
        await db.SaveChangesAsync();
        seed(db);
        await db.SaveChangesAsync();
    }

    private static void Doc(ProcuLinkDbContext db, Guid org, string status, string? action, DateTime at, string checks = "[]", bool sample = false)
    {
        var flow = new Flow { Id = Guid.NewGuid(), OrgId = org, Name = "Invoices" };
        var doc = new FlowDocument { Id = Guid.NewGuid(), OrgId = org, FlowId = flow.Id, Status = status, ChecksJson = checks, IsSample = sample, CreatedAt = at };
        db.AddRange(flow, doc);
        if (action is not null)
            db.Add(new AuditEvent { Id = Guid.NewGuid(), OrgId = org, EntityType = "FlowDocument", EntityId = doc.Id, Action = action, CreatedAt = at });
    }

    private static PushDevice Device(Guid org, string name, DateTime lastChecked, DateTime? created = null) => new()
    {
        Id = Guid.NewGuid(), OrgId = org, ClerkUserId = $"user_{name}", Endpoint = Endpoint(name), P256dh = "BPlan20", Auth = "plan20auth",
        CreatedAt = created ?? lastChecked, LastCheckedAt = lastChecked,
    };

    private async Task Run(IPhoneAlertSender sender, DateTime now)
    {
        using var scope = factory.Services.CreateScope();
        await new PhoneAlertJob(scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>(), sender,
            scope.ServiceProvider.GetRequiredService<DocumentEngineGate>(), NullLogger<PhoneAlertJob>.Instance).RunAtAsync(now, CancellationToken.None);
    }

    private async Task<List<PushDevice>> Devices()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().Set<PushDevice>().AsNoTracking().ToListAsync();
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Alerts_carry_only_counts_and_reach_each_device_once()
    {
        var now = DateTime.UtcNow.Date.AddHours(10);
        var newSender = DocumentJson.Serialize(new List<DocumentCheck> { new(DocumentChecks.NewSender, false, null, "First document from this address") });
        await Seed(db =>
        {
            Doc(db, OrgA, DocumentStatus.NeedsReview, "document.read", now.AddMinutes(-10));
            Doc(db, OrgA, DocumentStatus.NeedsReview, "document.read", now.AddMinutes(-10), newSender);
            Doc(db, OrgA, DocumentStatus.SendFailed, "document.delivery_failed", now.AddMinutes(-10));
            Doc(db, OrgA, DocumentStatus.NeedsReview, "document.read", now.AddMinutes(-10), sample: true);
            Doc(db, OrgB, DocumentStatus.NeedsReview, "document.read", now.AddMinutes(-10));
            db.AddRange(Device(OrgA, "a1", now.AddHours(-1)), Device(OrgA, "a2", now.AddMinutes(-5)), Device(OrgB, "b1", now.AddHours(-1)));
        });

        var sender = new CapturingAlertSender();
        await Run(sender, now);
        await Run(sender, now);

        var a1 = sender.Sent.Where(s => s.Endpoint == Endpoint("a1")).ToList();
        a1.Select(s => s.Topic).Should().BeEquivalentTo("needs-you", "new-address", "send-failed");
        a1.Select(s => Parse(s.Json).GetProperty("body").GetString())
            .Should().BeEquivalentTo("2 documents need you", "A first document from a new email address waits for you.", "1 document couldn't be sent");
        foreach (var (_, json, _) in sender.Sent)
        {
            var alert = Parse(json);
            alert.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("tag", "title", "body", "path", "count");
            alert.GetProperty("title").GetString().Should().Be("ProcuLink");
            alert.GetProperty("path").GetString().Should().StartWith("/").And.NotStartWith("//");
            json.Should().NotContain("Invoices").And.NotContain(OrgA.ToString()).And.NotContain("example.test");
        }
        a1.Should().OnlyContain(s => Parse(s.Json).GetProperty("count").GetInt32() == 2);
        sender.Sent.Should().NotContain(s => s.Endpoint == Endpoint("a2"));
        var b1 = sender.Sent.Should().ContainSingle(s => s.Endpoint == Endpoint("b1")).Subject;
        Parse(b1.Json).GetProperty("body").GetString().Should().Be("1 document needs you");
        Parse(b1.Json).GetProperty("count").GetInt32().Should().Be(1);
        sender.Sent.Should().HaveCount(4); // the second run added nothing
    }

    [Fact]
    public async Task A_gone_device_is_deleted_and_a_failing_one_does_not_stop_the_others()
    {
        var now = DateTime.UtcNow.Date.AddHours(10);
        var before = now.AddHours(-1);
        await Seed(db =>
        {
            Doc(db, OrgA, DocumentStatus.NeedsReview, "document.read", now.AddMinutes(-10));
            Doc(db, OrgB, DocumentStatus.NeedsReview, "document.read", now.AddMinutes(-10));
            Doc(db, OrgB, DocumentStatus.NeedsReview, "document.read", now.AddMinutes(-10));
            db.AddRange(Device(OrgA, "d1", before), Device(OrgA, "d2", before), Device(OrgA, "d3", before),
                Device(OrgA, "shared", before, before.AddMinutes(-30)), Device(OrgB, "shared", before));
        });

        // Without keys nothing is sent and no watermark moves.
        var off = new CapturingAlertSender(canSend: false);
        await Run(off, now);
        off.Sent.Should().BeEmpty();
        (await Devices()).Should().HaveCount(5).And.OnlyContain(d => d.LastCheckedAt == before);

        var sender = new CapturingAlertSender();
        sender.Results[Endpoint("d1")] = PushResult.Gone;
        sender.Results[Endpoint("d2")] = PushResult.Failed;
        await Run(sender, now);

        var devices = await Devices();
        devices.Should().NotContain(d => d.Endpoint == Endpoint("d1"));
        devices.Single(d => d.Endpoint == Endpoint("d2")).LastCheckedAt.Should().Be(now);
        sender.Sent.Should().Contain(s => s.Endpoint == Endpoint("d3"));
        devices.Should().ContainSingle(d => d.Endpoint == Endpoint("shared")).Which.OrgId.Should().Be(OrgB);
        sender.Sent.Where(s => s.Endpoint == Endpoint("shared"))
            .Should().NotBeEmpty().And.OnlyContain(s => Parse(s.Json).GetProperty("count").GetInt32() == 2);
    }

    [Fact]
    public async Task Daily_summary_sends_once_per_day_and_only_after_a_document()
    {
        var today = DateTime.UtcNow.Date;
        await Seed(db =>
        {
            Doc(db, OrgA, DocumentStatus.NeedsReview, null, today.AddHours(8));
            Doc(db, OrgA, DocumentStatus.NeedsReview, null, today.AddHours(9));
            Doc(db, OrgA, DocumentStatus.Sent, null, today.AddHours(9));
            db.AddRange(Device(OrgA, "sa", today), Device(OrgB, "sb", today));
        });

        var sender = new CapturingAlertSender();
        await Run(sender, today.AddHours(15).AddMinutes(55));
        sender.Sent.Should().BeEmpty();

        await Run(sender, today.AddHours(16).AddMinutes(5));
        var summary = sender.Sent.Should().ContainSingle().Subject;
        summary.Endpoint.Should().Be(Endpoint("sa"));
        summary.Topic.Should().Be("summary");
        Parse(summary.Json).GetProperty("body").GetString().Should().Be("3 documents today, 2 need you");
        Parse(summary.Json).GetProperty("path").GetString().Should().Be("/documents");
        (await Devices()).Single(d => d.OrgId == OrgB).SummaryOn.Should().Be(DateOnly.FromDateTime(today));

        await Run(sender, today.AddHours(16).AddMinutes(10));
        sender.Sent.Should().HaveCount(1);
    }
}
