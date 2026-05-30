using ProcuLink.Api.Contracts;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Services;

/// <summary>
/// Builds the read-only "PO Passport" evidence trail for a single order.
/// Lives in the Api project (not Core) because the passport shape is composed of
/// Api <see cref="PassportDto"/> contracts.
/// </summary>
public interface IPassportService
{
    /// <summary>
    /// Assembles the passport for <paramref name="orderId"/> scoped to
    /// <paramref name="organisationId"/>. Returns a failure result when the order
    /// does not exist for that org (including cross-tenant access attempts), so the
    /// controller can translate it to 404 without leaking existence.
    /// </summary>
    Task<Result<PassportDto>> GetAsync(
        Guid organisationId,
        Guid orderId,
        CancellationToken ct);
}
