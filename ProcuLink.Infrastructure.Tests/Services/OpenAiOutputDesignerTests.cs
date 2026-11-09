using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure.Services.Ai;

namespace ProcuLink.Infrastructure.Tests.Services;

public class OpenAiOutputDesignerTests
{
    // A no-egress org must never reach OpenAI. The check runs BEFORE the cap check, so a strict
    // tracker (no setups) throws if the cap is consulted, which proves the call was never prepared.
    [Fact]
    public async Task NoEgressOrg_SendsNothingAndNeverChecksTheCap()
    {
        var orgId = Guid.NewGuid();
        var tracker = new Mock<IAiUsageTracker>(MockBehavior.Strict);
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection()
            .AddDbContext<ProcuLinkDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddSingleton(tracker.Object)
            .BuildServiceProvider();
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.Organisations.Add(new Organisation { Id = orgId, ClerkOrgId = "org_x", Name = "Synthetic", Slug = "synthetic", SelfHostedOcr = true });
            await db.SaveChangesAsync();
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Provider"] = "openai",
            ["Ai:OpenAI:ApiKey"] = "sk-test-key",
        }).Build();
        var designer = new OpenAiOutputDesigner(config, NullLogger<OpenAiOutputDesigner>.Instance, services.GetRequiredService<IServiceScopeFactory>());

        var result = await designer.BindAsync(orgId, [new OutputSlot("Ship to", "Ship to", ["ship_address"])],
            new FlowFieldList([new FlowField("ship_address", "Delivery address", FieldTypes.Text, null, false)]), CancellationToken.None);

        result.Should().BeEmpty();
        tracker.Verify(t => t.IsAtOrOverLimitAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

        var proposal = await designer.ProposeAsync(orgId, "Synthetic documentation", new FlowFieldList([]), CancellationToken.None);

        proposal.Should().Be(new ProposalResult(null, "no_egress"));
        tracker.Verify(t => t.IsAtOrOverLimitAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
