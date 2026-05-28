namespace ProcuLink.Core.Services.Ingress;

/// <summary>
/// Polls a configured S3/R2 bucket for new purchase-order files and feeds them
/// into the order-ingestion pipeline.
/// </summary>
public interface IS3IngressService
{
    /// <summary>
    /// Scans the bucket configured for <paramref name="organisationId"/>,
    /// downloads new objects with accepted extensions, creates order stubs,
    /// and records each imported object so subsequent polls skip it.
    /// </summary>
    /// <returns>Count of files successfully imported in this run.</returns>
    Task<int> PollAsync(Guid organisationId, CancellationToken ct);
}
