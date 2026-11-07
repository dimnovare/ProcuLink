using System.Text.Json;
using ProcuLink.Core.Entities;

namespace ProcuLink.Api.Services.Documents.Intake;

/// <param name="Key">The ledger key; unique within the intake.</param>
/// <param name="SourceRef">The remote path (SFTP, FTP, FTPS), the object key (S3) or the sender (IMAP).</param>
public sealed record PickupFile(string Key, string FileName, string? SourceRef);

/// <summary>A server or mailbox a flow intake picks files up from.</summary>
public interface IPickupSource
{
    /// <summary>The <c>IntakeKind</c> this source serves.</summary>
    string Kind { get; }

    /// <summary>Runs the SSRF guard, then connects. SFTP writes a first-use host key fingerprint into <c>intake.ConfigJson</c>.</summary>
    Task<IPickupSession> OpenAsync(FlowIntake intake, JsonElement credentials, CancellationToken ct);
}

public interface IPickupSession : IDisposable
{
    Task<IReadOnlyList<PickupFile>> ListAsync(CancellationToken ct);

    /// <exception cref="ProcuLink.Core.Services.Catalog.CatalogFileTooLargeException">The file is over <paramref name="maxBytes"/>.</exception>
    Task<MemoryStream> DownloadAsync(PickupFile file, long maxBytes, CancellationToken ct);

    /// <summary>Does nothing except on IMAP, which marks the message seen.</summary>
    Task MarkDoneAsync(PickupFile file, CancellationToken ct);

    /// <summary>Called once after a real run. IMAP marks matching messages with nothing readable seen and returns a note for each; nothing else has any.</summary>
    Task<IReadOnlyList<string>> SettleAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
}
