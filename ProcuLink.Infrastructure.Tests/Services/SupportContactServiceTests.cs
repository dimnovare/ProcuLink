using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Tests.TestDoubles;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services;

public class SupportContactServiceTests
{
    [Fact]
    public async Task SubmitAsync_SendsEmail_WithExpectedSubjectAndCategory()
    {
        var mail = new FakeEmailSender();
        var analytics = new FakeAnalyticsService();
        var svc = new SupportContactService(mail, analytics, NullLogger<SupportContactService>.Instance);

        var orgId = Guid.NewGuid();
        await svc.SubmitAsync(
            orgId,
            "user_abc",
            new SupportContactRequest("bug", "Cannot upload PDF", "Stack trace …", "u@example.com", "Mozilla/5", "/upload"),
            CancellationToken.None);

        mail.Sent.Should().HaveCount(1);
        var sent = mail.Sent[0];
        sent.Subject.Should().Contain("[support][bug]");
        sent.Subject.Should().Contain("Cannot upload PDF");
        sent.To.Should().Be("support@proculink.com");
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
        var mail = new FakeEmailSender();
        var analytics = new FakeAnalyticsService();
        var svc = new SupportContactService(mail, analytics, NullLogger<SupportContactService>.Instance);

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
}
