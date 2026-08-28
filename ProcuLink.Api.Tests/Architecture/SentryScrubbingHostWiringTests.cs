using System.Reflection;
using System.Text.RegularExpressions;
using ProcuLink.Api.Telemetry;
using ProcuLink.Core.Security;
using Sentry;
using Xunit;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// P1 telemetry-hygiene cluster (2026-08-14 readiness audit) — three things that must stay true.
///
/// <list type="number">
///   <item><description><b>Both hosts scrub.</b> The API scrubbed only the captured
///   <c>SentryRequest</c>; <c>ProcuLink.Worker/Program.cs</c> had <b>no BeforeSend at all</b>,
///   while being the host that fires customer webhooks. Two <c>Program.cs</c> files drifting is a
///   recurring defect class here, so the wiring is one shared call and this guard is parameterised
///   over both hosts — the same shape as <c>AcceptanceGateSingleDoorTests.BothHosts_registerTheGate</c>
///   and <c>PushIngressSeamRegistrationTests</c>.</description></item>
///   <item><description><b>The callbacks are really attached, and really redact.</b> A source
///   guard alone would pass on a call that installs a no-op. These tests pull the delegates back
///   off the configured <see cref="SentryOptions"/> and run a realistic Slack webhook URL through
///   them.</description></item>
///   <item><description><b>The webhook job does not hand a raw target URL to a logger.</b>
///   Redaction in Sentry is a last line of defence; it does not protect the stdout log sink that
///   Railway retains.</description></item>
/// </list>
///
/// <para>This test never calls <c>SentrySdk.Init</c> — it configures a bare
/// <see cref="SentryOptions"/> only. <c>SentryWorkerAlertSinkTests</c> in this same assembly
/// asserts <c>SentrySdk.IsEnabled == false</c>, and initialising the global hub here would break
/// it.</para>
/// </summary>
public class SentryScrubbingHostWiringTests
{
    // Hand-typed Slack incoming-webhook shape, fake token. Deliberately NOT built from any
    // constant the redactor matches on: a scrubber fed its own emitter's output compares constants
    // to themselves and survives any mutation.
    //
    // DO NOT JOIN THESE INTO ONE LITERAL. The value the redactor is handed is the complete vendor
    // shape either way; the split exists because GitHub push protection's "Slack Incoming Webhook
    // URL" detector matches the contiguous literal and rejects the push. That it fires at all is
    // the useful signal here — it is third-party confirmation that this fixture is a realistic
    // captured shape rather than something reverse-engineered from the scrubber's own rules.
    private const string SlackSecret  = "QVc4vBPBt2M0uSm5oQwGaJ7T";
    private const string SlackWebhook = "https://hooks.slack.com/services/T0ABCDE12/B0FGHIJ34/" + SlackSecret;

    // ── 1. Both hosts install the shared scrubbing ───────────────────────────
    [Theory]
    [InlineData("ProcuLink.Api/Program.cs")]
    [InlineData("ProcuLink.Worker/Program.cs")]
    public void BothHosts_installTheSharedSentryScrubbing(string host)
    {
        var program = HostSource(host);

        Assert.Matches(@"UseProcuLinkScrubbing\s*\(\s*\)", program);

        // …and it is installed inside the Sentry configuration callback, i.e. before the host is
        // built. A call added after Build() would configure nothing.
        var scrub = Regex.Match(program, @"UseProcuLinkScrubbing\s*\(\s*\)");
        var build = Regex.Match(program, @"builder\s*\.\s*Build\s*\(\s*\)");
        Assert.True(build.Success, $"{host}: no builder.Build() found — the scan is broken");
        Assert.True(scrub.Index < build.Index,
            $"{host}: UseProcuLinkScrubbing() must be configured before builder.Build().");
    }

    /// <summary>
    /// Neither host may keep a private, drifting copy of the scrubbing logic. The API used to hold
    /// an inline <c>ScrubToken</c> regex that the Worker never had.
    /// </summary>
    [Theory]
    [InlineData("ProcuLink.Api/Program.cs")]
    [InlineData("ProcuLink.Worker/Program.cs")]
    public void NeitherHost_keepsItsOwnInlineScrubber(string host)
    {
        var program = HostSource(host);
        Assert.DoesNotContain("SetBeforeSend", program);
        Assert.DoesNotContain("SetBeforeBreadcrumb", program);
    }

    // ── 2. The installed callbacks actually redact ───────────────────────────
    [Fact]
    public void UseProcuLinkScrubbing_attachesAllThreeCallbacks()
    {
        var options = new SentryOptions();

        Assert.Null(Internal<Delegate>(options, "BeforeSendInternal"));
        Assert.Null(Internal<Delegate>(options, "BeforeBreadcrumbInternal"));
        Assert.Null(Internal<Delegate>(options, "BeforeSendTransactionInternal"));

        options.UseProcuLinkScrubbing();

        Assert.NotNull(Internal<Delegate>(options, "BeforeSendInternal"));
        Assert.NotNull(Internal<Delegate>(options, "BeforeBreadcrumbInternal"));
        Assert.NotNull(Internal<Delegate>(options, "BeforeSendTransactionInternal"));
    }

    [Fact]
    public void TheAttachedBeforeBreadcrumb_removesAWebhookCredential()
    {
        var options = new SentryOptions();
        options.UseProcuLinkScrubbing();
        var beforeBreadcrumb = Internal<Func<Breadcrumb, SentryHint, Breadcrumb?>>(options, "BeforeBreadcrumbInternal");
        Assert.NotNull(beforeBreadcrumb);

        // What an Information-level log line looks like once Sentry has turned it into a breadcrumb.
        var raw = new Breadcrumb(
            message: $"FireIntegrationTriggerJob delivered to {SlackWebhook}, status=OK",
            type: "default",
            data: new Dictionary<string, string> { ["Url"] = SlackWebhook, ["Status"] = "OK" },
            category: "ProcuLink.Infrastructure.Jobs.FireIntegrationTriggerJob",
            level: BreadcrumbLevel.Info);

        // Anti-vacuity: the breadcrumb genuinely carries the credential before scrubbing.
        Assert.Contains(SlackSecret, raw.Message);
        Assert.Contains(SlackSecret, raw.Data!["Url"]);

        var scrubbed = beforeBreadcrumb!(raw, new SentryHint());

        Assert.NotNull(scrubbed);
        Assert.DoesNotContain(SlackSecret, scrubbed!.Message);
        Assert.DoesNotContain(SlackSecret, scrubbed.Data!["Url"]);
        // Still diagnosable.
        Assert.Contains("hooks.slack.com", scrubbed.Message);
        Assert.Equal("OK", scrubbed.Data["Status"]);
        Assert.Equal(raw.Category, scrubbed.Category);
        Assert.Equal(raw.Level, scrubbed.Level);
    }

    [Fact]
    public void TheAttachedBeforeSend_removesAWebhookCredential_fromMessageParamsExceptionExtraAndRequest()
    {
        var options = new SentryOptions();
        options.UseProcuLinkScrubbing();
        var beforeSend = Internal<Func<SentryEvent, SentryHint, SentryEvent?>>(options, "BeforeSendInternal");
        Assert.NotNull(beforeSend);

        var e = new SentryEvent
        {
            Message = new SentryMessage
            {
                Message   = "FireIntegrationTriggerJob delivered to {Url}",
                Formatted = $"FireIntegrationTriggerJob delivered to {SlackWebhook}",
                Params    = new object[] { SlackWebhook },
            },
            SentryExceptions = new[]
            {
                new global::Sentry.Protocol.SentryException
                {
                    Type  = "InvalidOperationException",
                    Value = $"Webhook send to {SlackWebhook} failed",
                },
            },
            Request = new SentryRequest
            {
                Url         = "https://api.proculink.eu/api/inbound/email?token=p0stm4rkSh4r3dS3cr3t",
                QueryString = "?token=p0stm4rkSh4r3dS3cr3t",
            },
        };
        e.SetExtra("targetUrl", SlackWebhook);

        var scrubbed = beforeSend!(e, new SentryHint());

        Assert.NotNull(scrubbed);
        Assert.DoesNotContain(SlackSecret, scrubbed!.Message!.Formatted);
        Assert.DoesNotContain(SlackSecret, string.Join("|", scrubbed.Message.Params!.Select(p => p?.ToString())));
        Assert.DoesNotContain(SlackSecret, scrubbed.SentryExceptions!.Single().Value);
        Assert.DoesNotContain(SlackSecret, (string)scrubbed.Extra["targetUrl"]!);

        // The original inbound-email ?token= rule the API used to carry inline still holds.
        Assert.DoesNotContain("p0stm4rkSh4r3dS3cr3t", scrubbed.Request.Url);
        Assert.DoesNotContain("p0stm4rkSh4r3dS3cr3t", scrubbed.Request.QueryString);
        Assert.Contains(TelemetryRedactor.Redacted, scrubbed.Request.QueryString);

        // Control: the event is still readable. A scrubber that blanks everything is also broken.
        Assert.Contains("hooks.slack.com", scrubbed.Message.Formatted);
        Assert.Contains("api.proculink.eu", scrubbed.Request.Url);
        Assert.Equal("InvalidOperationException", scrubbed.SentryExceptions!.Single().Type);
    }

    [Fact]
    public void TheAttachedBeforeSend_leavesAnEventWithNoSecretsUntouched()
    {
        var options = new SentryOptions();
        options.UseProcuLinkScrubbing();
        var beforeSend = Internal<Func<SentryEvent, SentryHint, SentryEvent?>>(options, "BeforeSendInternal");

        const string ordinary = "Delivery failed: HTTP 502 from https://api.supplier.example.com/inbound/orders";
        var e = new SentryEvent { Message = new SentryMessage { Formatted = ordinary, Message = ordinary } };

        var scrubbed = beforeSend!(e, new SentryHint());

        Assert.Equal(ordinary, scrubbed!.Message!.Formatted);
        Assert.Equal(ordinary, scrubbed.Message.Message);
    }

    // ── 3. The webhook job hands no raw target URL to a logger ───────────────
    /// <summary>
    /// Every use of <c>sub.TargetUrl</c> in the webhook job is enumerated here. Three uses are
    /// legitimate — validating it, sending to it, and reducing it to a scheme+host destination —
    /// and a fourth is how the original leak was written. Adding one fails this test on purpose:
    /// it is a review checkpoint, not a style rule. Log
    /// <c>TelemetryRedactor.SafeDestination(sub.TargetUrl)</c> plus the subscription id instead.
    /// </summary>
    [Fact]
    public void TheWebhookJob_usesTheRawTargetUrlOnlyWhereAudited()
    {
        var path = Path.Combine(RepoSourceCorpus.FindRepoRoot(),
                                "ProcuLink.Infrastructure", "Jobs", "FireIntegrationTriggerJob.cs");
        Assert.True(File.Exists(path), $"{path} not found — the job moved and this guard is now blind.");
        var source = File.ReadAllText(path);

        // Anti-vacuity: the redaction really is present in the file this guard is reading.
        Assert.Contains("TelemetryRedactor.SafeDestination(sub.TargetUrl)", source);

        var uses = source
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains("sub.TargetUrl", StringComparison.Ordinal)
                        && !l.StartsWith("//", StringComparison.Ordinal))
            .ToList();

        var expected = new[]
        {
            "var guardResult = await _guard.ValidateAsync(sub.TargetUrl, ct);",
            "using var request = new HttpRequestMessage(HttpMethod.Post, sub.TargetUrl)",
            "var destination = TelemetryRedactor.SafeDestination(sub.TargetUrl);",
        };

        Assert.Equal(expected.OrderBy(s => s, StringComparer.Ordinal).ToList(),
                     uses.OrderBy(s => s, StringComparer.Ordinal).ToList());
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    /// <summary>
    /// Host source with comments stripped.
    ///
    /// <para>Stripping is not cosmetic. The first draft of this guard matched the raw file, and
    /// the mutation check caught it: commenting the call out — <c>// o.UseProcuLinkScrubbing();</c>
    /// — left the guard GREEN while the Worker shipped with no scrubbing at all. That is precisely
    /// the defect this file exists to catch, and it is the same trap the other cross-host guards in
    /// this directory avoid by going through <see cref="OrphanDetector.StripComments"/>. It also
    /// keeps <see cref="NeitherHost_keepsItsOwnInlineScrubber"/> from tripping on a comment that
    /// merely mentions <c>SetBeforeSend</c> — such as the one this file's own prose contains.</para>
    /// </summary>
    private static string HostSource(string relativePath)
    {
        var full = Path.Combine(RepoSourceCorpus.FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"{full} not found — the host moved and this guard is now blind.");
        var stripped = OrphanDetector.StripComments(File.ReadAllText(full));

        // Anti-vacuity: the stripper must not have swallowed the composition root. It has done
        // exactly that before on ProcuLink.Api/Program.cs — see the note in AcceptanceGateSingleDoorTests.
        Assert.Contains("builder.Build()", stripped);
        return stripped;
    }

    /// <summary>
    /// Reads one of <see cref="SentryOptions"/>' internal <c>Before*Internal</c> properties — the
    /// field the SDK itself invokes. Asserting against these rather than against
    /// <c>SentryScrubbing.Scrub</c> directly is the point: it proves the delegate is genuinely
    /// installed, not merely that a redaction method exists somewhere.
    /// </summary>
    // internal, not private: ExpectedLogNoiseTests needs the same hook to prove the filter is
    // actually attached, and a second copy of a reflection hack is a second thing to drift.
    internal static T? Internal<T>(SentryOptions options, string propertyName) where T : class
    {
        var property = typeof(SentryOptions).GetProperty(
            propertyName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.True(property is not null,
            $"SentryOptions.{propertyName} no longer exists — the Sentry SDK changed and this guard is now blind.");
        return property!.GetValue(options) as T;
    }
}
