using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Tests.TestDoubles;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services;

public class SupportContactServiceTests
{
    // ── helper ───────────────────────────────────────────────────────────────

    private static SupportContactService Make(FakeEmailSender mail, FakeAnalyticsService analytics, IConfiguration? config = null)
        => new(mail, analytics, config ?? EmptyConfig(), NullLogger<SupportContactService>.Instance);

    // ── existing behaviour (still passing) ───────────────────────────────────

    [Fact]
    public async Task SubmitAsync_SendsEmail_WithExpectedSubjectAndCategory()
    {
        var mail      = new FakeEmailSender(canDeliver: true);
        var analytics = new FakeAnalyticsService();
        var svc       = Make(mail, analytics);

        var orgId = Guid.NewGuid();
        var result = await svc.SubmitAsync(
            orgId,
            "user_abc",
            new SupportContactRequest("bug", "Cannot upload PDF", "Stack trace …", "u@example.com", "Mozilla/5", "/upload"),
            CancellationToken.None);

        mail.Sent.Should().HaveCount(1);
        var sent = mail.Sent[0];
        sent.Subject.Should().Contain("[support][bug]");
        sent.Subject.Should().Contain("Cannot upload PDF");
        sent.To.Should().Be("support@proculink.eu");
        sent.Body.Should().Contain("u@example.com");
        sent.Body.Should().Contain("/upload");
        sent.Body.Should().Contain("Stack trace");

        analytics.CapturedEvents.Should().ContainSingle(e => e.EventName == "support_form_submitted");
        var ev = analytics.CapturedEvents.Single(e => e.EventName == "support_form_submitted");
        ev.OrgId.Should().Be(orgId);
        ev.UserId.Should().Be("user_abc");
        ev.Properties["category"].Should().Be("bug");
        ev.Properties["route"].Should().Be("/upload");
    }

    [Fact]
    public async Task SubmitAsync_DoesNotEmitAnalytics_WhenAnonymous()
    {
        var mail      = new FakeEmailSender(canDeliver: true);
        var analytics = new FakeAnalyticsService();
        var svc       = Make(mail, analytics);

        await svc.SubmitAsync(
            organisationId: null,
            userId: null,
            new SupportContactRequest("general", "Question", "How does it work?", "anon@example.com", null, "/pricing"),
            CancellationToken.None);

        mail.Sent.Should().HaveCount(1);
        mail.Sent[0].Subject.Should().Contain("[support][general]");
        mail.Sent[0].Body.Should().Contain("anon@example.com");

        analytics.CapturedEvents.Should().BeEmpty(
            "anonymous support submissions should not be tied to an organisation in PostHog");
    }

    // ── new: delivered flag truthfulness ─────────────────────────────────────

    [Fact]
    public async Task SubmitAsync_ReturnsDelivered_True_WhenSmtpSenderConfigured()
    {
        // Simulate MailKitEmailSender (CanDeliver = true)
        var mail      = new FakeEmailSender(canDeliver: true);
        var analytics = new FakeAnalyticsService();
        var svc       = Make(mail, analytics);

        var result = await svc.SubmitAsync(
            Guid.NewGuid(), "user_1",
            new SupportContactRequest("billing", "Invoice question", "Body.", null, null, null),
            CancellationToken.None);

        result.Delivered.Should().BeTrue("MailKitEmailSender has SMTP configured");
        result.ContactEmail.Should().Be("support@proculink.eu");
    }

    [Fact]
    public async Task SubmitAsync_ReturnsDelivered_False_WhenConsoleOnlySender()
    {
        // Simulate ConsoleEmailSender (CanDeliver = false — no SMTP host)
        var mail      = new FakeEmailSender(canDeliver: false);
        var analytics = new FakeAnalyticsService();
        var svc       = Make(mail, analytics);

        var result = await svc.SubmitAsync(
            Guid.NewGuid(), "user_2",
            new SupportContactRequest("general", "Dev question", "Body.", null, null, null),
            CancellationToken.None);

        result.Delivered.Should().BeFalse("ConsoleEmailSender only logs — no SMTP configured");
        result.ContactEmail.Should().Be("support@proculink.eu",
            "caller still needs the fallback address to show the user");

        // The email body was still captured (logged), just not truly sent
        mail.Sent.Should().HaveCount(1, "ConsoleEmailSender still records the call in the fake");
    }

    [Fact]
    public async Task SubmitAsync_AnalyticsEvent_IncludesDeliveredFlag()
    {
        var mail      = new FakeEmailSender(canDeliver: false);
        var analytics = new FakeAnalyticsService();
        var svc       = Make(mail, analytics);

        var orgId = Guid.NewGuid();
        await svc.SubmitAsync(orgId, "user_3",
            new SupportContactRequest("feature", "Request", "Body.", null, null, "/dashboard"),
            CancellationToken.None);

        var ev = analytics.CapturedEvents.Single(e => e.EventName == "support_form_submitted");
        ev.Properties["delivered"].Should().Be(false);
    }

    // ── helper ───────────────────────────────────────────────────────────────

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().Build();
}
