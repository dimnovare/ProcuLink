using FluentAssertions;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Tests.TestDoubles;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services;

// ════════════════════════════════════════════════════════════════════════════
//  PostmarkEmailSender — the transactional IEmailSender (support form, notices)
//  that delegates to IEmailApiClient. These tests pin: CanDeliver mirrors the
//  client's IsConfigured; the EmailApiMessage built from (to, subject, body)
//  uses the client's DefaultFrom; and the Subject is run through the same CR/LF
//  header-injection sanitiser as MailKitEmailSender (the support form's Category +
//  Subject are anonymous user input). SanitizeHeaderValue is reachable via the
//  Infrastructure→Tests InternalsVisibleTo already used by the MailKit test.
// ════════════════════════════════════════════════════════════════════════════
public class PostmarkEmailSenderTests
{
    // 1. CanDeliver tracks the underlying client's configured state.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CanDeliver_MirrorsClientIsConfigured(bool configured)
    {
        var sender = new PostmarkEmailSender(new FakeEmailApiClient { IsConfigured = configured });

        sender.CanDeliver.Should().Be(configured);
    }

    // 2. SendAsync builds the message from (to, subject, body) using the client's DefaultFrom,
    //    and the subject is sanitised so no CR/LF survives into the header.
    [Fact]
    public async Task SendAsync_BuildsMessageWithSanitizedSubjectAndClientDefaultFrom()
    {
        var fake = new FakeEmailApiClient { DefaultFrom = "platform@proculink.eu" };
        var sender = new PostmarkEmailSender(fake);

        await sender.SendAsync("supplier@example.com", "a\r\nBcc: attacker@evil.example", "the body");

        fake.LastMessage.Should().NotBeNull();
        fake.LastMessage!.To.Should().Equal("supplier@example.com");
        fake.LastMessage.From.Should().Be("platform@proculink.eu");
        fake.LastMessage.TextBody.Should().Be("the body");
        fake.LastMessage.Subject.Should().NotContainAny("\r", "\n");
        fake.LastMessage.Subject.Should().Contain("Bcc: attacker@evil.example"); // payload neutralised, not dropped
    }

    // 3. SanitizeHeaderValue: CR/LF → space; null/empty → "". Mirrors MailKitEmailSender's guard.
    [Theory]
    [InlineData("a\r\nb", "a  b")]
    [InlineData("line1\nline2", "line1 line2")]
    [InlineData("line1\rline2", "line1 line2")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void SanitizeHeaderValue_StripsCrLf_AndHandlesNullOrEmpty(string? input, string expected)
    {
        var result = PostmarkEmailSender.SanitizeHeaderValue(input);

        result.Should().Be(expected);
        result.Should().NotContainAny("\r", "\n");
    }
}
