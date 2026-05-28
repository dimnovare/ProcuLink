namespace ProcuLink.Core.Services;

public sealed record SupportContactRequest(
    string Category,
    string Subject,
    string Message,
    string? UserEmail,
    string? UserAgent,
    string? Route);

public interface ISupportContactService
{
    Task SubmitAsync(Guid? organisationId, string? userId, SupportContactRequest req, CancellationToken ct = default);
}
