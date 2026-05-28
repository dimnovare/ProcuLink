using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Tests.TestDoubles;

/// <summary>
/// Test double for <see cref="IEmailSender"/>. Captures every email so support
/// service tests can assert subject, body, and recipient.
/// </summary>
public sealed class FakeEmailSender : IEmailSender
{
    public List<SentEmail> Sent { get; } = new();

    public Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        Sent.Add(new SentEmail(to, subject, body));
        return Task.CompletedTask;
    }

    public sealed record SentEmail(string To, string Subject, string Body);
}
