using Hangfire;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using ProcuLink.Infrastructure.Jobs;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

public sealed class IntegrationTriggerService : IIntegrationTriggerService
{
    private readonly ProcuLinkDbContext   _db;
    private readonly IBackgroundJobClient _jobs;

    public IntegrationTriggerService(ProcuLinkDbContext db, IBackgroundJobClient jobs)
    {
        _db   = db;
        _jobs = jobs;
    }

    public async Task EnqueueAsync(
        Guid organisationId, string eventType, object payload, CancellationToken ct)
    {
        var subs = await _db.IntegrationSubscriptions
                            .Where(s => s.OrganisationId == organisationId
                                     && s.EventType == eventType
                                     && s.IsActive)
                            .ToListAsync(ct);

        if (subs.Count == 0) return;

        var payloadJson = JsonSerializer.Serialize(payload);
        foreach (var sub in subs)
        {
            FireIntegrationTriggerJob.Enqueue(_jobs, sub.Id, payloadJson);
        }
    }
}
