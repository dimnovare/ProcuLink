using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

public sealed class SupportContactService : ISupportContactService
{
    private const string DefaultSupportInbox = "support@proculink.eu";

    private readonly IEmailSender _mail;
    private readonly IAnalyticsService _analytics;
    private readonly IConfiguration _config;
    private readonly ILogger<SupportContactService> _log;

    public SupportContactService(
        IEmailSender mail,
        IAnalyticsService analytics,
        IConfiguration config,
        ILogger<SupportContactService> log)
    {
        _mail      = mail;
        _analytics = analytics;
        _config    = config;
        _log       = log;
    }

    public async Task<SupportContactResult> SubmitAsync(
        Guid? organisationId,
        string? userId,
        SupportContactRequest req,
        CancellationToken ct = default)
    {
        var subject = $"[support][{req.Category}] {req.Subject}".Trim();
        var body =
            $"Org:     {(organisationId?.ToString() ?? "(unauthenticated)")}{Environment.NewLine}" +
            $"User:    {(userId ?? "(none)")} {(req.UserEmail ?? string.Empty)}{Environment.NewLine}" +
            $"Route:   {(req.Route ?? "(none)")}{Environment.NewLine}" +
            $"Agent:   {(req.UserAgent ?? "(unknown)")}{Environment.NewLine}" +
            Environment.NewLine +
            "---" + Environment.NewLine + Environment.NewLine +
            req.Message;

        var supportInbox = _config["Smtp:SupportTo"] ?? _config["Support:Inbox"] ?? DefaultSupportInbox;

        // Snapshot before sending so the result is always accurate even if the sender swallows
        // exceptions (MailKitEmailSender does).
        bool delivered = _mail.CanDeliver;
        await _mail.SendAsync(supportInbox, subject, body, ct);

        if (organisationId.HasValue)
        {
            await _analytics.CaptureAsync(
                organisationId: organisationId.Value,
                userId: userId,
                eventName: "support_form_submitted",
                properties: new Dictionary<string, object?>
                {
                    ["category"]  = req.Category,
                    ["route"]     = req.Route ?? "(none)",
                    ["delivered"] = delivered,
                },
                ct: ct);
        }

        _log.LogInformation(
            "Support contact submitted: org={Org} user={User} category={Category} route={Route} delivered={Delivered}",
            organisationId?.ToString() ?? "(none)", userId ?? "(none)", req.Category, req.Route ?? "(none)", delivered);

        return new SupportContactResult(delivered, supportInbox);
    }
}
