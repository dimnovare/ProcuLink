using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services.Email;
using ProcuLink.TestSupport;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Mail to a flow's own address becomes a document in that flow and never a purchase order. Real
/// Postgres, because "the same message twice is one document" rests on the ledger's unique index.
/// </summary>
[Collection("postgres-container")]
public sealed class InboundEmailFlowPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _connectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;
        _connectionString = await postgres.CreateDatabaseAsync("proculink_inbound_flow");
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(new Npgsql.NpgsqlConnectionStringBuilder(_connectionString) { Pooling = false }.ConnectionString)
            .Options;
    }

    public async Task DisposeAsync() => await postgres.DropDatabaseAsync(_connectionString);

    [DockerRequiredFact]
    public async Task Email_to_a_flow_address_lands_a_document_in_that_flow()
    {
        var (orgId, flowId, to) = await SeedAsync(enabled: true);
        var result = await RouteAsync(Payload(to, "msg-1"));

        Assert.True(result.Success);
        await using var db = new ProcuLinkDbContext(_options!);
        var doc = await db.FlowDocuments.AsNoTracking().SingleAsync(d => d.OrgId == orgId);
        Assert.Equal(flowId, doc.FlowId);
        Assert.Equal("email", doc.Source);
        Assert.Equal("buyer@example.test", doc.SourceRef);
        Assert.Empty(db.PurchaseOrders);
    }

    [DockerRequiredFact]
    public async Task The_same_postmark_message_twice_makes_one_document()
    {
        var (orgId, _, to) = await SeedAsync(enabled: true);
        await RouteAsync(Payload(to, "msg-1"));
        await RouteAsync(Payload(to, "msg-1"));

        await using var db = new ProcuLinkDbContext(_options!);
        Assert.Equal(1, await db.FlowDocuments.CountAsync(d => d.OrgId == orgId));
    }

    [DockerRequiredFact]
    public async Task A_delivery_whose_submit_threw_is_taken_by_the_redelivery()
    {
        var (orgId, _, to) = await SeedAsync(enabled: true);
        await Assert.ThrowsAsync<IOException>(() => RouteAsync(Payload(to, "msg-1"), new ThrowingSubmitter()));
        var redelivered = await RouteAsync(Payload(to, "msg-1"));

        Assert.True(redelivered.Success);
        await using var db = new ProcuLinkDbContext(_options!);
        Assert.Equal(1, await db.FlowDocuments.CountAsync(d => d.OrgId == orgId));
    }

    [DockerRequiredFact]
    public async Task A_disabled_email_intake_makes_no_document_and_no_order()
    {
        var (_, _, to) = await SeedAsync(enabled: false);
        var result = await RouteAsync(Payload(to, "msg-1"));

        Assert.False(result.Success);
        Assert.Equal(InboundEmailRejectionKind.Permanent, result.RejectionKind);
        await using var db = new ProcuLinkDbContext(_options!);
        Assert.Empty(db.FlowDocuments);
        Assert.Empty(db.PurchaseOrders);
    }

    private static InboundEmailPayload Payload(string to, string messageId) => new(
        "buyer@example.test", to, "Docs",
        [new InboundAttachment("a.pdf", "application/pdf", Encoding.UTF8.GetBytes("%PDF-1.4 test"))],
        ProviderMessageId: messageId);

    private async Task<(Guid OrgId, Guid FlowId, string To)> SeedAsync(bool enabled)
    {
        var config = InboundAddressTestHarness.Configuration();
        await using var db = new ProcuLinkDbContext(_options!);
        var orgId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_{orgId:N}", Name = "Example Org", Slug = $"org-{orgId:N}", AccountStatus = AccountStatusConstants.Active,
            CreatedAt = now, EmailConfigJson = "{}",
        });
        db.Flows.Add(new Flow { Id = flowId, OrgId = orgId, Name = "Orders", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        var minted = await InboundAddressTestHarness.Create(db, config).MintForFlowAsync(orgId, flowId, CancellationToken.None);
        db.FlowIntakes.Add(new FlowIntake
        {
            Id = Guid.NewGuid(), OrgId = orgId, FlowId = flowId, Kind = IntakeKind.Email, IsEnabled = enabled,
            CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return (orgId, flowId, $"orders@{minted.Token}.proculink.eu");
    }

    private async Task<InboundEmailResult> RouteAsync(InboundEmailPayload payload, IFlowDocumentSubmitter? submitter = null)
    {
        var config = InboundAddressTestHarness.Configuration();
        await using var db = new ProcuLinkDbContext(_options!);
        var router = new InboundEmailRouter(
            db, Mock.Of<IClaimedOrderCreator>(), Mock.Of<IParseJobEnqueuer>(), Mock.Of<IEmailBodyOrderExtractor>(),
            InboundAddressTestHarness.Create(db, config), PermissiveBilling.Mock().Object, config,
            NullLogger<InboundEmailRouter>.Instance, submitter ?? new PersistingSubmitter(_options!));
        return await router.RouteAsync(payload, CancellationToken.None);
    }

    /// <summary>A submit that fails before anything commits, as a storage outage would.</summary>
    private sealed class ThrowingSubmitter : IFlowDocumentSubmitter
    {
        public Task<FlowSubmitResult> SubmitAsync(
            Guid orgId, Guid flowId, Stream content, string fileName, string contentType,
            string source, string? sourceRef, CancellationToken ct) => throw new IOException("storage down");

        public IReadOnlyList<string> AcceptedFormats(Guid orgId) => DocumentFormats.Base;
    }

    /// <summary>Stands in for the Api submitter: persists the document the way AcceptAsync would.</summary>
    private sealed class PersistingSubmitter(DbContextOptions<ProcuLinkDbContext> options) : IFlowDocumentSubmitter
    {
        public IReadOnlyList<string> AcceptedFormats(Guid orgId) => DocumentFormats.Base;

        public async Task<FlowSubmitResult> SubmitAsync(
            Guid orgId, Guid flowId, Stream content, string fileName, string contentType,
            string source, string? sourceRef, CancellationToken ct)
        {
            await using var db = new ProcuLinkDbContext(options);
            var now = DateTime.UtcNow;
            var doc = new FlowDocument
            {
                Id = Guid.NewGuid(), OrgId = orgId, FlowId = flowId, FileName = fileName, ContentType = contentType,
                SourceFileKey = $"{orgId}/{fileName}", Sha256 = Guid.NewGuid().ToString("N"), Source = source,
                SourceRef = sourceRef, CreatedAt = now, UpdatedAt = now,
            };
            db.FlowDocuments.Add(doc);
            await db.SaveChangesAsync(ct);
            return new FlowSubmitResult(doc, null, null);
        }
    }
}
