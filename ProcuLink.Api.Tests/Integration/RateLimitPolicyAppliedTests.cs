using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ProcuLink.Api.Controllers;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

// ════════════════════════════════════════════════════════════════════════════
//  P2-4 follow-up: the named rate-limit policies ("transform", "ai",
//  "signed-url") were DEFINED in Program.cs but never APPLIED — only the
//  "upload" policy was wired to an action, so they were dead config. These
//  tests guard that each expensive / cost-bearing / abusable action now carries
//  the correct [EnableRateLimiting("<policy>")] attribute, AND that the policy
//  is actually enforced end-to-end (a live 429 once the window is exhausted).
//
//  The attribute-presence checks are the primary regression guard: they prove
//  the exact action↔policy wiring that the concern was about. The live 429 test
//  proves the wiring is honoured by the middleware pipeline (UseRateLimiter is
//  registered before UseAuthorization, so an over-limit caller is rejected with
//  429 before any 401).
// ════════════════════════════════════════════════════════════════════════════

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
    [InlineData(typeof(OrdersController), nameof(OrdersController.GetMappingPreview), "ai")]
    [InlineData(typeof(SchemaInferenceController), nameof(SchemaInferenceController.Infer), "ai")]
    [InlineData(typeof(SchemaInferenceController), nameof(SchemaInferenceController.ProposeMapping), "ai")]
    [InlineData(typeof(MappingSuggestionsController), nameof(MappingSuggestionsController.SuggestFields), "ai")]
    // Signed-URL / file-download surfaces → "signed-url" (60/min).
    [InlineData(typeof(OrdersController), nameof(OrdersController.Download), "signed-url")]
    [InlineData(typeof(InvoiceController), nameof(InvoiceController.Download), "signed-url")]
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
}
