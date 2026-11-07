using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public class DocumentSubmitTests : IClassFixture<DocumentEngineTestFactory>
{
    private readonly DocumentEngineTestFactory _factory;

    public DocumentSubmitTests(DocumentEngineTestFactory factory) => _factory = factory;

    private async Task<(IServiceScope Scope, Guid FlowId)> ScopeWithFlow(LimitCheckResult limit)
    {
        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.CheckOrderLimitAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(limit);
        var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddScoped(_ => billing.Object);
        }));
        _factory.ClientFor(DocumentEngineTestFactory.OrgA);
        var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var flow = new Flow { Id = Guid.NewGuid(), OrgId = DocumentEngineTestFactory.OrgA, Name = "Synthetic flow", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Flows.Add(flow);
        await db.SaveChangesAsync();
        return (scope, flow.Id);
    }

    private static Task<FlowSubmitResult> Submit(IServiceScope scope, Guid flowId, string source, string? sourceRef) =>
        scope.ServiceProvider.GetRequiredService<IFlowDocumentSubmitter>().SubmitAsync(
            DocumentEngineTestFactory.OrgA, flowId, new MemoryStream(Encoding.UTF8.GetBytes("a,b\n1,2\n")),
            "rows.csv", "text/csv", source, sourceRef, CancellationToken.None);

    [Fact]
    public async Task Submit_over_the_plan_limit_refuses_and_stores_nothing()
    {
        var (scope, flowId) = await ScopeWithFlow(new LimitCheckResult(false, true, "pilot", 20));
        using var _ = scope;
        _factory.Storage.Invocations.Clear();

        var result = await Submit(scope, flowId, "email", "a@example.test");

        Assert.Equal("pilot_expired", result.Error);
        _factory.Storage.Verify(s => s.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        Assert.False(await db.FlowDocuments.AnyAsync(d => d.FlowId == flowId));
    }

    [Fact]
    public async Task Submit_records_the_source()
    {
        var (scope, flowId) = await ScopeWithFlow(new LimitCheckResult(true, false, "growth", 150));
        using var _ = scope;

        var result = await Submit(scope, flowId, "email", "a@example.test");

        Assert.Null(result.Error);
        Assert.Equal("email", result.Document!.Source);
        Assert.Equal("a@example.test", result.Document.SourceRef);
    }
}
