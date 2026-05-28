namespace ProcuLink.Core.Services.Ingress;

/// <summary>
/// Polls an SFTP server for a single organisation, imports newly-seen files,
/// and returns the number of files successfully imported.
/// </summary>
public interface ISftpIngressService
{
    /// <summary>
    /// Download all unimported files from the org's configured SFTP remote directory
    /// and create order stubs for each accepted file.
    /// </summary>
    /// <param name="organisationId">The organisation to poll.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of files imported in this run (0 if no config, config disabled, or no new files).</returns>
    Task<int> PollAsync(Guid organisationId, CancellationToken ct);
}
