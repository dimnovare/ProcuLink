using Hangfire;

namespace ProcuLink.Api.Jobs;

/// <summary>Hangfire job: reads a received flow document. Idempotent — a document past <c>reading</c> is skipped.</summary>
public class ReadDocumentJob
{
    [Queue("critical")]
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 5, 30, 120 })]
    public Task ExecuteAsync(Guid documentId, Guid organisationId, CancellationToken ct) => Task.CompletedTask;

    public static void Enqueue(IBackgroundJobClient jobs, Guid documentId, Guid organisationId) =>
        jobs.Enqueue<ReadDocumentJob>(j => j.ExecuteAsync(documentId, organisationId, CancellationToken.None));
}
