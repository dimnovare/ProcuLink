using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Core.Constants;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public class ActivityApiTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private static readonly Guid Org = DocumentEngineTestFactory.OrgA;

    [Theory]
    [InlineData(PlanConstants.Pilot, AccountStatusConstants.Trialing)]
    [InlineData(PlanConstants.Pilot, AccountStatusConstants.TrialExpired)]
    [InlineData(PlanConstants.Growth, AccountStatusConstants.PastDue)]
    public async Task The_list_is_open_on_every_plan_and_stays_readable_when_processing_is_paused(string plan, string status)
    {
        var http = factory.ClientFor(Org);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            var org = await db.Organisations.SingleAsync(o => o.Id == Org);
            (org.Plan, org.AccountStatus) = (plan, status);
            await db.SaveChangesAsync();
        }

        var res = await http.GetAsync("/api/audit");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
