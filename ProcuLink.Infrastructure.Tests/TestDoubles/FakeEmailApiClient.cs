using ProcuLink.Core.Services.Email;

namespace ProcuLink.Infrastructure.Tests.TestDoubles;

/// <summary>
/// In-memory <see cref="IEmailApiClient"/> double. Captures the last <see cref="EmailApiMessage"/>
/// sent and returns a configurable result — lets dispatcher/sender tests assert the message they
/// build (recipients, subject, attachment, From/ReplyTo) and the result mapping, without Postmark.
/// </summary>
public sealed class FakeEmailApiClient : IEmailApiClient
{
    public bool IsConfigured { get; set; } = true;
    public string DefaultFrom { get; set; } = "orders@proculink.eu";
    public EmailApiMessage? LastMessage { get; private set; }
    public EmailApiResult ResultToReturn { get; set; } = new(true, null, 200);

    public Task<EmailApiResult> SendAsync(EmailApiMessage message, CancellationToken ct = default)
    {
        LastMessage = message;
        return Task.FromResult(ResultToReturn);
    }
}
