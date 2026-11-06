using Hangfire;

namespace ProcuLink.Api.Jobs;

/// <summary>Hangfire job: delivers one queued flow delivery to its destination.</summary>
public class DeliverDocumentJob
{
    // Task 7 fills this in.
    public Task ExecuteAsync(Guid deliveryId, Guid organisationId, CancellationToken ct) => Task.CompletedTask;

    public static void Enqueue(IBackgroundJobClient jobs, Guid deliveryId, Guid organisationId) =>
        jobs.Enqueue<DeliverDocumentJob>(j => j.ExecuteAsync(deliveryId, organisationId, CancellationToken.None));
}
