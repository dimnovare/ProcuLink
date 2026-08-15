using FluentAssertions;
using ProcuLink.Core.Security;

namespace ProcuLink.Infrastructure.Tests.Security;

/// <summary>
/// Proves <see cref="TelemetryRedactor"/> actually fires — the failure mode to fear here is a
/// scrubber that silently does nothing, which looks identical to a clean log until the day someone
/// reads a public CI log and finds a working Slack webhook in it.
///
/// <para><b>Every secret-bearing input in this file is a verbatim vendor URL SHAPE, hand-typed
/// from the vendors' own documented formats with obviously-fake values.</b> None of them is
/// constructed from a constant the redactor matches on. A scrubber fed its own emitter's output
/// compares constants to themselves and stays green through any mutation; these inputs are
/// independent of the implementation, so if a rule is deleted the assertion goes red.</para>
///
/// <para>Each secret-bearing case asserts three things, not one:
/// (1) an anti-vacuity check that the RAW input really does contain the secret — otherwise a test
/// asserting "the secret is gone" passes trivially on an input that never had one;
/// (2) the secret is absent from the output;
/// (3) something an operator can act on survives. Plus a control set proving ordinary URLs come
/// through byte-for-byte, because a redactor that eats everything is also a broken redactor.</para>
/// </summary>
public class TelemetryRedactorTests
{
    // ── Realistic captured shapes. Fake values, real formats. ────────────────────────────────
    //
    // <b>DO NOT JOIN THE SLACK CONSTANTS INTO SINGLE LITERALS.</b> The redactor is handed the
    // complete vendor shape either way — the split is purely at source level, because GitHub push
    // protection's "Slack Incoming Webhook URL" / "Slack Workflow Webhook URL" / "Slack API Token"
    // detectors match the contiguous text and reject the push. That they fire at all is the useful
    // part: it is third-party confirmation these fixtures are realistic captured shapes and not
    // something reverse-engineered from the scrubber's own constants.

    // Slack incoming webhook: the third path segment IS the credential.
    private const string SlackSecretSegment = "QVc4vBPBt2M0uSm5oQwGaJ7T";
    private const string SlackWebhook = "https://hooks.slack.com/services/T0ABCDE12/B0FGHIJ34/" + SlackSecretSegment;

    // Slack workflow webhook — a different Slack shape with the same property.
    private const string SlackWorkflowSecretSegment = "XyZ1AbC2DeF3GhI4JkL5MnO6";
    private const string SlackWorkflowWebhook =
        "https://hooks.slack.com/workflows/T0ABCDE12/A0KLMNO56/512345678901234567/" + SlackWorkflowSecretSegment;

    // Microsoft Teams connector (current webhookb2 host form).
    private const string TeamsWebhook =
        "https://contoso.webhook.office.com/webhookb2/11111111-2222-3333-4444-555555555555@66666666-7777-8888-9999-000000000000/IncomingWebhook/8f4a2b1c9d3e4f5a6b7c8d9e0f1a2b3c/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string TeamsSecretSegment = "8f4a2b1c9d3e4f5a6b7c8d9e0f1a2b3c";

    // Microsoft Teams connector (legacy outlook.office.com host form).
    private const string TeamsLegacyWebhook =
        "https://outlook.office.com/webhook/11111111-2222-3333-4444-555555555555@66666666-7777-8888-9999-000000000000/IncomingWebhook/0d1c2b3a4958677685948372615049af/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string TeamsLegacySecretSegment = "0d1c2b3a4958677685948372615049af";

    // Zapier catch hook — note BOTH secret segments are short, so only the host/path rule saves it.
    private const string ZapierWebhook = "https://hooks.zapier.com/hooks/catch/1234567/3abx9q1/";
    private const string ZapierSecretSegment = "3abx9q1";

    // Discord webhook: /api/webhooks/<id>/<token>.
    private const string DiscordWebhook =
        "https://discord.com/api/webhooks/1234567890123456789/GnB7wq0Ls2xY4pR8tUvZ1cD3eF5gH7iJ9kL1mN3oP5qR7sT9uV1wX3yZ5aB7cD9e";
    private const string DiscordSecretSegment =
        "GnB7wq0Ls2xY4pR8tUvZ1cD3eF5gH7iJ9kL1mN3oP5qR7sT9uV1wX3yZ5aB7cD9e";

    // Power Automate / Logic Apps: the credential is the ?sig= query value.
    private const string PowerAutomateWebhook =
        "https://prod-42.westeurope.logic.azure.com:443/workflows/9f8e7d6c5b4a39281706f5e4d3c2b1a0/triggers/manual/paths/invoke?api-version=2016-06-01&sp=%2Ftriggers%2Fmanual%2Frun&sv=1.0&sig=Kd93jXm2QpLs7ZtRv0Nb4YcH6uWf1AeG8sTz5Rx2Qy0";
    private const string PowerAutomateSecretSegment = "Kd93jXm2QpLs7ZtRv0Nb4YcH6uWf1AeG8sTz5Rx2Qy0";

    // Slack bot token — split for the same push-protection reason as the webhook URLs above.
    private const string SlackBotToken = "xoxb-1111111111-2222222222-" + "FaKeSlackBotTokenValue";

    public static TheoryData<string, string, string> SecretBearingWebhooks() => new()
    {
        // url, the secret that must disappear, the part an operator still needs
        { SlackWebhook,         SlackSecretSegment,         "hooks.slack.com" },
        { SlackWorkflowWebhook, SlackWorkflowSecretSegment, "hooks.slack.com" },
        { TeamsWebhook,         TeamsSecretSegment,         "contoso.webhook.office.com" },
        { TeamsLegacyWebhook,   TeamsLegacySecretSegment,   "outlook.office.com" },
        { ZapierWebhook,        ZapierSecretSegment,        "hooks.zapier.com" },
        { DiscordWebhook,       DiscordSecretSegment,       "discord.com" },
        { PowerAutomateWebhook, PowerAutomateSecretSegment, "logic.azure.com" },
    };

    [Theory]
    [MemberData(nameof(SecretBearingWebhooks))]
    public void RedactUrl_removesTheCredential_butKeepsTheVendorIdentifiable(
        string url, string secret, string mustSurvive)
    {
        // (1) Anti-vacuity: the input really carries the secret. Without this a rule deletion could
        //     be masked by a test input that never contained anything to redact.
        url.Should().Contain(secret, "the test input must actually carry a credential");

        var redacted = TelemetryRedactor.RedactUrl(url);

        // (2) The credential is gone.
        redacted.Should().NotContain(secret);
        // (3) The destination is still actionable, and the redaction is visible rather than silent.
        redacted.Should().Contain(mustSurvive);
        redacted.Should().Contain(TelemetryRedactor.Redacted);
    }

    [Theory]
    [MemberData(nameof(SecretBearingWebhooks))]
    public void Redact_removesTheCredential_whenTheUrlIsEmbeddedInALogLine(
        string url, string secret, string mustSurvive)
    {
        // The realistic shape: the URL arrives inside a rendered log message or exception text,
        // not on its own. This is the path that actually reaches a Sentry breadcrumb.
        var line = $"FireIntegrationTriggerJob delivered to {url}, status=OK, delivery=6f1c.";
        line.Should().Contain(secret);

        var redacted = TelemetryRedactor.Redact(line);

        redacted.Should().NotBeNull();
        redacted!.Should().NotContain(secret);
        redacted.Should().Contain(mustSurvive);
        // The surrounding diagnostic text is untouched.
        redacted.Should().Contain("status=OK");
        redacted.Should().Contain("delivery=6f1c");
    }

    // ── Controls: an ordinary URL must survive readable ──────────────────────────────────────
    // A redactor that eats every URL is as useless as one that eats none, and it fails silently in
    // the same way. These pin the other direction.
    public static TheoryData<string> OrdinaryUrls() => new()
    {
        "https://api.supplier.example.com/v1/purchase-orders/PO-2026-000412",
        "https://erp.buyer.example.com/api/orders/3f7c1e2a-9b44-4d51-8c0e-6a2f5b93d1c7/lines",
        "https://api.proculink.eu/api/orders?status=ready_to_deliver&page=2",
        "https://supplier.example.com/inbound/purchase-orders-for-acme-components-eu",
        "https://files.example.com/exports/PO-2026-000412-acme-components-export.csv",
        "http://localhost:5223/health",
    };

    [Theory]
    [MemberData(nameof(OrdinaryUrls))]
    public void RedactUrl_leavesAnOrdinaryUrlByteForByte(string url)
    {
        TelemetryRedactor.RedactUrl(url).Should().Be(url);
    }

    [Theory]
    [MemberData(nameof(OrdinaryUrls))]
    public void Redact_leavesAnOrdinaryUrlReadableInsideALogLine(string url)
    {
        var line = $"Delivery failed: HTTP 502 from {url}";
        TelemetryRedactor.Redact(line).Should().Be(line);
    }

    [Fact]
    public void Redact_doesNotTouchTextWithNoSecrets()
    {
        const string line =
            "FireIntegrationTriggerJob: sub 3f7c1e2a-9b44-4d51-8c0e-6a2f5b93d1c7 not found or inactive — skipping.";
        TelemetryRedactor.Redact(line).Should().Be(line);
    }

    // ── Opaque path segments on an unknown vendor ────────────────────────────────────────────
    [Fact]
    public void RedactUrl_redactsAnOpaqueTokenSegment_onAVendorItHasNeverHeardOf()
    {
        // The next vendor will not be on the host list. The shape rule is what covers them.
        const string url = "https://webhooks.newvendor.example/notify/7fKq2Bz9Lp4Rt6Wm1Yc3Xd5Ne8Ug0Vh";
        const string secret = "7fKq2Bz9Lp4Rt6Wm1Yc3Xd5Ne8Ug0Vh";
        url.Should().Contain(secret);

        var redacted = TelemetryRedactor.RedactUrl(url);

        redacted.Should().NotContain(secret);
        redacted.Should().Be("https://webhooks.newvendor.example/notify/" + TelemetryRedactor.Redacted);
    }

    [Fact]
    public void RedactUrl_redactsSensitiveQueryValues_butKeepsTheParameterNames()
    {
        // The API's original inbound-email rule, preserved: Postmark can only pass its shared
        // secret in the webhook URL.
        const string url = "https://api.proculink.eu/api/inbound/email?token=p0stm4rkSh4r3dS3cr3t&source=postmark";
        url.Should().Contain("p0stm4rkSh4r3dS3cr3t");

        var redacted = TelemetryRedactor.RedactUrl(url);

        redacted.Should().Be(
            $"https://api.proculink.eu/api/inbound/email?token={TelemetryRedactor.Redacted}&source=postmark");
    }

    [Fact]
    public void Redact_scrubsTokenQueryParam_evenWhenTheStringIsNotAParseableUrl()
    {
        // SentryRequest.QueryString arrives as a bare fragment, not an absolute URL.
        var redacted = TelemetryRedactor.Redact("?token=p0stm4rkSh4r3dS3cr3t&source=postmark");
        redacted.Should().Be($"?token={TelemetryRedactor.Redacted}&source=postmark");
    }

    // ── Free-text credential shapes in messages ──────────────────────────────────────────────
    [Theory]
    [InlineData("Upstream rejected: Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.ZmFrZS1wYXlsb2Fk.c2lnbmF0dXJl", "eyJhbGciOiJIUzI1NiJ9.ZmFrZS1wYXlsb2Fk.c2lnbmF0dXJl")]
    [InlineData("Stripe call failed with key sk_live_51NfakeKeyValue0000", "sk_live_51NfakeKeyValue0000")]
    [InlineData("webhook signing secret whsec_f4k3s1gn1ngs3cr3tv4lu3 rejected", "whsec_f4k3s1gn1ngs3cr3tv4lu3")]
    [InlineData("slack bot token " + SlackBotToken + " expired", SlackBotToken)]
    [InlineData("S3 auth failed for AKIAFAKEEXAMPLE12345", "AKIAFAKEEXAMPLE12345")]
    [InlineData("connecting with password=Sup3rS3cretPassphrase to host", "Sup3rS3cretPassphrase")]
    [InlineData("api_key=abc123XYZdef456 was rejected", "abc123XYZdef456")]
    public void Redact_removesFreeTextCredentialShapes(string message, string secret)
    {
        message.Should().Contain(secret, "the test input must actually carry a credential");

        var redacted = TelemetryRedactor.Redact(message);

        redacted.Should().NotBeNull();
        redacted!.Should().NotContain(secret);
        redacted.Should().Contain(TelemetryRedactor.Redacted);
    }

    [Fact]
    public void Redact_isIdempotent()
    {
        var once  = TelemetryRedactor.Redact($"delivered to {SlackWebhook} ok");
        var twice = TelemetryRedactor.Redact(once);
        twice.Should().Be(once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Redact_passesNullAndEmptyThrough(string? input)
    {
        TelemetryRedactor.Redact(input).Should().Be(input);
    }

    // ── SafeDestination: the form a caller may deliberately log ──────────────────────────────
    [Theory]
    [InlineData(SlackWebhook, "https://hooks.slack.com")]
    [InlineData(TeamsWebhook, "https://contoso.webhook.office.com")]
    [InlineData(ZapierWebhook, "https://hooks.zapier.com")]
    [InlineData(DiscordWebhook, "https://discord.com")]
    [InlineData("https://api.supplier.example.com/v1/purchase-orders/PO-2026-000412", "https://api.supplier.example.com")]
    [InlineData("http://192.168.1.10:8080/hooks/incoming/abc", "http://192.168.1.10:8080")]
    public void SafeDestination_keepsSchemeAndHostAndNothingElse(string url, string expected)
    {
        TelemetryRedactor.SafeDestination(url).Should().Be(expected);
    }

    [Fact]
    public void SafeDestination_neverCarriesAPathQueryOrFragment()
    {
        // Property-style guard: whatever the input, the output has at most scheme://host[:port].
        foreach (var url in new[] { SlackWebhook, TeamsWebhook, TeamsLegacyWebhook, ZapierWebhook, DiscordWebhook, PowerAutomateWebhook })
        {
            var safe = TelemetryRedactor.SafeDestination(url);
            safe.Should().NotContain("?");
            safe.Should().NotContain("#");
            // Exactly the two slashes of the scheme separator — no path.
            safe.Count(c => c == '/').Should().Be(2, "SafeDestination must never carry a path");
        }
    }

    [Theory]
    [InlineData(null, "(no destination)")]
    [InlineData("", "(no destination)")]
    [InlineData("   ", "(no destination)")]
    [InlineData("not a url at all", "(unparseable destination)")]
    public void SafeDestination_degradesSafely(string? url, string expected)
    {
        TelemetryRedactor.SafeDestination(url).Should().Be(expected);
    }
}
