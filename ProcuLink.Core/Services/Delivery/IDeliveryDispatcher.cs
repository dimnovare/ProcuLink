using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// Protocol-specific delivery dispatcher. One implementation per protocol (http / sftp / ftp).
/// Registered as IEnumerable&lt;IDeliveryDispatcher&gt; in DI; DeliveryService resolves by Protocol.
/// </summary>
public interface IDeliveryDispatcher
{
    /// <summary>Protocol name this dispatcher handles: "http" | "sftp" | "ftp".</summary>
    string Protocol { get; }

    /// <summary>
    /// Dispatches the artifact payload to the configured destination.
    /// Must not throw — return DeliveryResult(false, message) on failure.
    /// </summary>
    Task<DeliveryResult> DispatchAsync(
        byte[] content,
        string fileName,
        string contentType,
        SupplierDeliveryConfig config,
        string decryptedCredentials,
        CancellationToken ct);
}
