using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services.Dispatchers;

namespace ProcuLink.Infrastructure.Services.Delivery;

public static class DeliveryRepair
{
    /// <summary>
    /// Channels whose destination is a directory of files, where the filename alone decides whether
    /// two documents collide.
    /// </summary>
    internal static bool IsFileDropProtocol(string protocol) =>
        protocol.Equals(DeliveryProtocolConstants.Sftp, StringComparison.OrdinalIgnoreCase)
        || protocol.Equals(DeliveryProtocolConstants.Ftps, StringComparison.OrdinalIgnoreCase)
        || protocol.Equals(DeliveryProtocolConstants.Ftp, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a crash-recovery re-drive on this connection would be unable to replace the file
    /// its own predecessor may have written — a file-drop channel whose operator turned
    /// <c>overwriteExisting</c> off.
    ///
    /// <para>
    /// The re-send cannot duplicate (the path is per-order), which is why the dispatcher still
    /// declares <see cref="ResendSafety.Safe"/>; what it cannot do is finish. Re-driving it produces
    /// a refusal, then a retry, then a dead-lettered order — while the supplier may already hold the
    /// complete document, or a truncated one that every subsequent retry declines to repair. Neither
    /// "delivered" nor "failed" is a claim this process can make, so it parks for a human.
    /// </para>
    /// </summary>
    public static bool CannotRepairItsOwnFile(SupplierDeliveryConfig config) =>
        IsFileDropProtocol(config.Protocol)
        && !SftpDeliveryDispatcher.OverwriteExistingFromConfig(config.ConfigJson);
}
