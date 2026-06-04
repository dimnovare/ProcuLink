namespace ProcuLink.Core.Services;

public sealed record SupportContactRequest(
    string Category,
    string Subject,
    string Message,
    string? UserEmail,
    string? UserAgent,
    string? Route);

/// <summary>
/// Result returned by <see cref="ISupportContactService.SubmitAsync"/>.
/// <para>
/// <see cref="Delivered"/> is <c>true</c> only when an SMTP-backed sender was used and the
/// message was dispatched. When <c>false</c> (dev / no SMTP configured) the submission was
/// logged but NOT emailed — the caller must surface this honestly to the user.
/// </para>
/// </summary>
public sealed record SupportContactResult(bool Delivered, string ContactEmail);

public interface ISupportContactService
{
    Task<SupportContactResult> SubmitAsync(Guid? organisationId, string? userId, SupportContactRequest req, CancellationToken ct = default);
}
