using FluentAssertions;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services;

// ════════════════════════════════════════════════════════════════════════════
//  Audit item 10 (Batch D): header-injection guard on the SMTP sender.
//
//  The anonymous support contact form's Category + Subject are user input and
//  end up in the outbound message's Subject HEADER (SupportContactService
//  builds "[support][{Category}] {Subject}"). A value containing CR/LF could
//  attempt to smuggle additional headers (Bcc:, a second To:, etc.) into the
//  message. MailKitEmailSender.SanitizeHeaderValue strips CR/LF before the
//  value reaches MimeMessage.Subject — these tests pin that guard.
// ════════════════════════════════════════════════════════════════════════════
public class MailKitEmailSenderHeaderSanitizationTests
{
    [Theory]
    [InlineData("hello\r\nBcc: attacker@evil.example", "hello  Bcc: attacker@evil.example")]
    [InlineData("line1\nline2", "line1 line2")]
    [InlineData("line1\rline2", "line1 line2")]
    [InlineData("\r\n\r\n", "    ")]
    public void SanitizeHeaderValue_StripsCrLf(string input, string expected)
    {
        var result = MailKitEmailSender.SanitizeHeaderValue(input);

        result.Should().Be(expected);
        result.Should().NotContain("\r").And.NotContain("\n");
    }

    [Fact]
    public void SanitizeHeaderValue_LeavesCleanValuesUntouched()
    {
        const string clean = "[support][question] Need help with EDIFACT mapping";

        MailKitEmailSender.SanitizeHeaderValue(clean).Should().BeSameAs(clean);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SanitizeHeaderValue_NullOrEmpty_ReturnsEmpty(string? input)
    {
        MailKitEmailSender.SanitizeHeaderValue(input).Should().BeEmpty();
    }

    [Fact]
    public void SanitizeHeaderValue_InjectionPayload_CannotTerminateHeaderLine()
    {
        // A classic SMTP header-injection payload: terminate the Subject header
        // and start new ones. After sanitisation no CR or LF survives, so the
        // payload stays inside the single Subject header value.
        const string payload = "innocent\r\nTo: victim@example.com\r\nBcc: list@evil.example\r\n\r\nbody";

        var result = MailKitEmailSender.SanitizeHeaderValue(payload);

        result.Should().NotContainAny("\r", "\n");
        result.Should().Contain("innocent");
    }
}
