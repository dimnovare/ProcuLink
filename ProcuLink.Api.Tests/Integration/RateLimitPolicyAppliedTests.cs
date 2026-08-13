using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Controllers;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

// ════════════════════════════════════════════════════════════════════════════
//  P2-4 follow-up: the named rate-limit policies ("transform", "ai",
//  "signed-url", "preview") were DEFINED in Program.cs but (originally) never
//  APPLIED — only the "upload" policy was wired to an action, so they were dead
//  config. These tests guard that each expensive / cost-bearing / abusable
//  action now carries the correct [EnableRateLimiting("<policy>")] attribute,
//  AND that the policy is actually enforced end-to-end (a live 429 once the
//  window is exhausted). The deterministic mapping-preview reads use the
//  generous "preview" policy (they do no LLM work — the editor polls them).
//
//  The attribute-presence checks are the primary regression guard: they prove
//  the exact action↔policy wiring that the concern was about. The live 429 test
//  proves the wiring is honoured by the middleware pipeline (UseRateLimiter is
//  registered before UseAuthorization, so an over-limit caller is rejected with
//  429 before any 401).
// ════════════════════════════════════════════════════════════════════════════

[Collection("postgres-container")]
public sealed class RateLimitPolicyAppliedTests : IClassFixture<HardeningTestFactory>
{
    private readonly HardeningTestFactory _factory;

    public RateLimitPolicyAppliedTests(HardeningTestFactory factory) => _factory = factory;

    // ── Attribute-presence guards (the core fix for the dead-policy concern) ──

    [Theory]
    // Transform is CPU/IO-heavy → "transform" (30/min).
    [InlineData(typeof(OrdersController), nameof(OrdersController.Transform), "transform")]
    // AI-invoking endpoints → "ai" (15/min — protects the OpenAI bill).
    [InlineData(typeof(OrdersController), nameof(OrdersController.AcceptAiSuggestions), "ai")]
    // Deterministic mapping-preview reads do NO LLM work but the mapping editor polls them
    // (debounced) → generous "preview" policy (120/min), NOT the tight "ai" cap.
    [InlineData(typeof(OrdersController), nameof(OrdersController.GetMappingPreview), "preview")]
    [InlineData(typeof(OrdersController), nameof(OrdersController.PreviewMappingOverride), "preview")]
    [InlineData(typeof(SchemaInferenceController), nameof(SchemaInferenceController.Infer), "ai")]
    [InlineData(typeof(SchemaInferenceController), nameof(SchemaInferenceController.ProposeMapping), "ai")]
    [InlineData(typeof(MappingSuggestionsController), nameof(MappingSuggestionsController.SuggestFields), "ai")]
    // Signed-URL / file-download surfaces → "signed-url" (60/min).
    [InlineData(typeof(OrdersController), nameof(OrdersController.Download), "signed-url")]
    [InlineData(typeof(InvoiceController), nameof(InvoiceController.Download), "signed-url")]
    // The source document is STREAMED rather than signed, but it is the same surface — a
    // document download — and shares the same budget deliberately, so one cap governs document
    // egress instead of two that drift apart.
    [InlineData(typeof(OrderSourceDocumentController), nameof(OrderSourceDocumentController.GetSource), "signed-url")]
    // Anonymous support form feeds an outbound email sender → "support" (5/min).
    [InlineData(typeof(SupportController), nameof(SupportController.Contact), "support")]
    // The practice-order endpoint persists a CALLER-SUPPLIED recipient address that a
    // subsequent send mails from ProcuLink's verified Postmark sender → "sample-order"
    // (5/min, its own partition). It previously fell back to the 300/60s global limiter,
    // which is far too loose for a surface that seeds outbound mail recipients.
    [InlineData(typeof(SampleOrderController), nameof(SampleOrderController.Create), "sample-order")]
    public void Action_HasExpectedRateLimitPolicy(Type controller, string actionName, string expectedPolicy)
    {
        var method = controller.GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Action '{actionName}' not found on {controller.Name}.");

        var attr = method.GetCustomAttribute<EnableRateLimitingAttribute>();

        Assert.True(attr is not null,
            $"{controller.Name}.{actionName} must carry [EnableRateLimiting] — the named policies are otherwise dead config.");
        Assert.Equal(expectedPolicy, attr!.PolicyName);
    }

    // The Stripe webhook must NOT be rate-limited beyond the global backstop:
    // a too-tight cap could drop legitimate retried billing events. It relies on
    // signature verification + the global 300/min limiter instead.
    [Fact]
    public void StripeWebhook_HasNoNamedRateLimitPolicy()
    {
        var method = typeof(BillingController).GetMethod(nameof(BillingController.Webhook),
            BindingFlags.Public | BindingFlags.Instance);

        Assert.True(method is not null, "BillingController.Webhook action not found.");

        var attr = method!.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.Null(attr); // no named policy — global backstop + Stripe signature only
    }

    // ── Live enforcement: the "transform" policy actually returns 429 ─────────
    //
    // Uses the anonymous HardeningTestFactory client (no auth). UseRateLimiter
    // runs before UseAuthorization, so an over-limit caller is rejected with 429
    // BEFORE the 401 it would otherwise get. The "transform" window is 30/min;
    // firing > 30 requests synchronously within the same minute guarantees a 429.
    [Fact]
    public async Task Transform_Returns429_AfterExceedingPolicyLimit()
    {
        var client = _factory.CreateClient();

        // Comfortably above the 30/min "transform" cap. The partition is shared
        // by IP across the process, so prior tests can only make 429 arrive
        // SOONER — never later — which keeps this assertion robust.
        const int requests = 45;
        var orderId = Guid.NewGuid();

        var sawTooManyRequests = false;
        for (var i = 0; i < requests; i++)
        {
            var resp = await client.PostAsync(
                $"/api/orders/{orderId}/transform",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                sawTooManyRequests = true;
                break;
            }
        }

        Assert.True(sawTooManyRequests,
            "The 'transform' rate-limit policy must reject with 429 once its per-window limit is exceeded.");
    }

    // ── Live enforcement: the "support" policy actually returns 429 ───────────
    //
    // The support contact form is [AllowAnonymous] and feeds an outbound email
    // sender, so its 5/60s window is the abuse cap on that surface. In the test
    // host no Smtp:Host is configured → ConsoleEmailSender (log-only), so the
    // first requests 200 cheaply; the policy must reject within 12 attempts.
    [Fact]
    public async Task SupportContact_Returns429_AfterExceedingPolicyLimit()
    {
        var client = _factory.CreateClient();

        const int requests = 12; // comfortably above the 5/min "support" cap
        var sawTooManyRequests = false;

        for (var i = 0; i < requests; i++)
        {
            var resp = await client.PostAsync(
                "/api/support/contact",
                new StringContent(
                    """{"category":"question","subject":"rate limit probe","message":"probe"}""",
                    System.Text.Encoding.UTF8, "application/json"));

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                sawTooManyRequests = true;
                break;
            }
        }

        Assert.True(sawTooManyRequests,
            "The 'support' rate-limit policy must reject with 429 once its 5/60s window is exceeded.");
    }

    // ── Live enforcement: the "sample-order" policy actually returns 429 ──────
    //
    // WP-27 gave POST /api/onboarding/sample-order a caller-supplied `deliverTo`
    // that is persisted as a delivery recipient; the user's next send then mails a
    // CSV from ProcuLink's verified Postmark sender to that address. Bounces and
    // complaints on caller-chosen recipients are charged to OUR sender reputation —
    // the same amplification shape as the support form, minus the fixed inbox — so
    // the cap is the same 5/60s, in its own partition.
    //
    // The attribute-presence row above is the deterministic guard; this one proves
    // the middleware honours it. UseRateLimiter runs before UseAuthorization, so the
    // anonymous client is rejected with 429 before the 401 it would otherwise get.
    [Fact]
    public async Task SampleOrder_Returns429_AfterExceedingPolicyLimit()
    {
        var client = _factory.CreateClient();

        const int requests = 12; // comfortably above the 5/min "sample-order" cap
        var sawTooManyRequests = false;

        for (var i = 0; i < requests; i++)
        {
            var resp = await client.PostAsync(
                "/api/onboarding/sample-order",
                new StringContent(
                    """{"deliverTo":"probe@example.com"}""",
                    System.Text.Encoding.UTF8, "application/json"));

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                sawTooManyRequests = true;
                break;
            }
        }

        Assert.True(sawTooManyRequests,
            "The 'sample-order' rate-limit policy must reject with 429 once its 5/60s window is exceeded.");
    }

    // ── WP-19: a 429 must say HOW LONG, not just "shortly" ───────────────────
    //
    // The rejection used to carry a body of "Rate limit exceeded. Please slow
    // down and retry shortly." and nothing else — no Retry-After header, no
    // number anywhere. A client could not act on it, so every caller guessed:
    // the upload workbench hard-codes [15s, 35s, 61s] because 61s is the longest
    // window defined here, and every other surface treated the 429 as permanent.
    // It is the ONE 4xx on this API that is guaranteed to clear on its own.
    [Fact]
    public async Task RateLimitRejection_SaysHowLongToWait()
    {
        var client = _factory.CreateClient();
        var orderId = Guid.NewGuid();

        HttpResponseMessage? rejection = null;
        for (var i = 0; i < 45 && rejection is null; i++)
        {
            var resp = await client.PostAsync(
                $"/api/orders/{orderId}/transform",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            if (resp.StatusCode == HttpStatusCode.TooManyRequests) rejection = resp;
        }

        Assert.True(rejection is not null, "No 429 was produced, so this test proved nothing.");

        // The standard header — what proxies and non-browser clients honour.
        Assert.True(rejection!.Headers.TryGetValues("Retry-After", out var headerValues),
            "A 429 must carry Retry-After; without it the wait is unknowable to any client.");
        var header = headerValues!.Single();
        Assert.True(int.TryParse(header, out var headerSeconds), $"Retry-After must be delay-seconds, got '{header}'.");
        // Never zero: advertising an instant retry invites the tight loop the limiter exists to stop.
        Assert.True(headerSeconds >= 1, $"Retry-After must be at least 1 second, got {headerSeconds}.");
        Assert.True(headerSeconds <= 60, $"No window here is longer than 60s, so {headerSeconds} is wrong.");

        using var body = System.Text.Json.JsonDocument.Parse(await rejection.Content.ReadAsStringAsync());

        // The same number in the BODY. Retry-After is not CORS-safelisted, and the
        // browser app is a different origin from this API in every deployed
        // environment, so the body is the carrier that always survives.
        Assert.True(body.RootElement.TryGetProperty("retryAfterSeconds", out var bodySeconds),
            "The 429 body must carry retryAfterSeconds — a cross-origin client cannot rely on the header.");
        Assert.Equal(headerSeconds, bodySeconds.GetInt32());

        // The wording is load-bearing and must NOT drift: UploadWorkbench.getLimitCode
        // string-sniffs it ("rate limit" / "slow down" / "too many") to tell a speed
        // throttle apart from a plan cap. Relabelling a throttle as "Plan limit
        // reached" is alarming and wrong — their plan is fine.
        Assert.Equal(
            "Rate limit exceeded. Please slow down and retry shortly.",
            body.RootElement.GetProperty("error").GetString());
    }

    // The header above is only readable by the browser app if CORS exposes it.
    // AllowAnyHeader governs REQUEST headers and says nothing about this; without
    // an explicit exposed list a cross-origin fetch sees only the CORS-safelisted
    // response headers, and Retry-After is not one of them.
    [Fact]
    public void CorsPolicy_ExposesRetryAfter_SoTheBrowserCanReadIt()
    {
        var options = _factory.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>>()
            .Value;

        var policy = options.GetPolicy("AllowFrontend");
        Assert.True(policy is not null, "The AllowFrontend CORS policy must exist.");
        Assert.Contains("Retry-After", policy!.ExposedHeaders);
    }
}
