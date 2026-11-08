using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Mapping;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

public class LookupSuggesterTests
{
    [Fact]
    public async Task At_the_ai_allowance_nothing_is_sent_and_the_cap_is_reported()
    {
        var org = Guid.NewGuid();
        var db = new ProcuLinkDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var lookup = new Lookup
        {
            Id = Guid.NewGuid(), OrgId = org, Name = "Codes",
            CandidatesJson = DocumentJson.Serialize(new[] { new LookupCandidate("SUP-1", "Pilsner bottle 0.5L") }),
        };
        db.Set<Lookup>().Add(lookup);
        await db.SaveChangesAsync();
        var ai = new Mock<IAiMappingService>();
        var usage = new Mock<IAiUsageTracker>();
        usage.Setup(u => u.IsAtOrOverLimitAsync(org, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await new LookupSuggester(db, ai.Object, usage.Object)
            .SuggestAsync(org, [new LookupMiss(lookup.Id, "A1", new Dictionary<string, string> { ["name"] = "Pilsner 0.5" })], default);

        result.CapReached.Should().BeTrue();
        result.Suggestions.Should().BeEmpty();
        ai.Verify(a => a.SuggestSupplierItemCodesAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<AiMappingLineContext>>(), It.IsAny<IReadOnlyList<AiMappingCandidate>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
