using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Infrastructure.Services.Ai;

namespace ProcuLink.Infrastructure.Tests.Services.Ai;

/// <summary>
/// Tests for the email body NLP order extractor scaffold.
/// All tests use the no-op path (no OpenAI key) or a mocked
/// <see cref="IAiUsageTracker"/> — none of them hit the live OpenAI API.
///
/// Mirrors the structure of <see cref="OpenAiSchemaInferencerTests"/>.
/// </summary>
public class OpenAiEmailBodyOrderExtractorTests
{
    // ── No-op / provider-check path ──────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_NoApiKey_ReturnsFailure()
    {
        // No Ai:OpenAI:ApiKey configured → extractor is in no-op mode.
        // The tracker must not be touched at all.
        var extractor = CreateExtractor(
            config: new Dictionary<string, string?>
            {
                ["Ai:Provider"] = "openai"
                // Ai:OpenAI:ApiKey intentionally absent
            },
            tracker: null,
            orgId: Guid.NewGuid());

        var result = await extractor.ExtractAsync(
            "Please send us 5 units of SKU-001 at €10 each. PO: PO-9901.",
            CancellationToken.None);

        result.Success.Should().BeFalse("no-op when Ai:OpenAI:ApiKey is missing");
        result.Order.Should().BeNull();
        result.FailureReason.Should().NotBeNullOrWhiteSpace();
        result.Confidence.Should().Be(0.0);
    }

    [Fact]
    public async Task ExtractAsync_NonOpenAiProvider_ReturnsFailure()
    {
        // Provider != "openai" → still in no-op mode even with an API key configured.
        var extractor = CreateExtractor(
            config: new Dictionary<string, string?>
            {
                ["Ai:Provider"]      = "anthropic",
                ["Ai:OpenAI:ApiKey"] = "sk-test-key",
            },
            tracker: null,
            orgId: Guid.NewGuid());

        var result = await extractor.ExtractAsync(
            "Dear supplier, attached please find PO-1234 for 10x Widget A.",
            CancellationToken.None);

        result.Success.Should().BeFalse("provider is not openai");
        result.Order.Should().BeNull();
        result.FailureReason.Should().NotBeNullOrWhiteSpace();
    }

    // ── Cap enforcement ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_AtOrOverCap_DoesNotCallOpenAiAndReturnsFailure()
    {
        // Provider configured + key configured, but the tracker says the org
        // is over its monthly cap → extractor must short-circuit BEFORE
        // dispatching any OpenAI call. IncrementAsync must never be called.
        var orgId = Guid.NewGuid();
        var tracker = new Mock<IAiUsageTracker>(MockBehavior.Strict);
        tracker.Setup(t => t.IsAtOrOverLimitAsync(orgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);
        // The blocked-path log line resolves the org's limit via the snapshot.
        tracker.Setup(t => t.GetCurrentAsync(orgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new AiUsageSnapshot(orgId, 2026, 6, 1000, 1000));

        // Pass overrideClient=null so no real HTTP call can sneak through —
        // the test-ctor still blocks creation of a ChatClient because no
        // real API key is in config.
        var extractor = CreateExtractor(
            config: new Dictionary<string, string?>
            {
                ["Ai:Provider"]      = "openai",
                ["Ai:OpenAI:ApiKey"] = "sk-test-key",
            },
            tracker: tracker.Object,
            orgId: orgId);

        var result = await extractor.ExtractAsync(
            "Order 50 units of part ABC at $5 each. PO# 7700.",
            CancellationToken.None);

        result.Success.Should().BeFalse("the per-org cap blocks the extraction call");
        result.Order.Should().BeNull();
        tracker.Verify(
            t => t.IsAtOrOverLimitAsync(orgId, It.IsAny<CancellationToken>()),
            Times.Once);
        // Strict mock ensures IncrementAsync was never called.
        tracker.Verify(
            t => t.IncrementAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExtractAsync_CapCheckFailure_FailsSafeWithFailure()
    {
        // If the cap check throws, the extractor must treat the org as
        // "blocked" rather than silently bypass the cap.
        var orgId = Guid.NewGuid();
        var tracker = new Mock<IAiUsageTracker>(MockBehavior.Strict);
        tracker.Setup(t => t.IsAtOrOverLimitAsync(orgId, It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("simulated DB outage"));

        var extractor = CreateExtractor(
            config: new Dictionary<string, string?>
            {
                ["Ai:Provider"]      = "openai",
                ["Ai:OpenAI:ApiKey"] = "sk-test-key",
            },
            tracker: tracker.Object,
            orgId: orgId);

        var result = await extractor.ExtractAsync(
            "Could you please despatch PO-5000, 100 units of XYZ at £3 each?",
            CancellationToken.None);

        result.Success.Should().BeFalse("cap-check failure must fail safe, not bypass the cap");
        result.Order.Should().BeNull();
        result.FailureReason.Should().NotBeNullOrWhiteSpace();
    }

    // ── Short-circuit on empty body ──────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_EmptyBody_ReturnsFailureWithoutCallingTracker()
    {
        // An empty body should be detected before reaching the AI provider check
        // or the tracker. Using MockBehavior.Strict ensures any unexpected tracker
        // method call will fail the test.
        var tracker = new Mock<IAiUsageTracker>(MockBehavior.Strict);
        // No setups — Strict mode will fail if any method is invoked.

        var extractor = CreateExtractor(
            config: new Dictionary<string, string?>
            {
                ["Ai:Provider"]      = "openai",
                ["Ai:OpenAI:ApiKey"] = "sk-test-key",
            },
            tracker: tracker.Object,
            orgId: Guid.NewGuid());

        var result = await extractor.ExtractAsync(string.Empty, CancellationToken.None);

        result.Success.Should().BeFalse("empty body cannot contain a purchase order");
        result.Order.Should().BeNull();
        result.FailureReason.Should().NotBeNullOrWhiteSpace();
        // Strict mock would throw if tracker was touched.
    }

    // ── Whitespace-only body (edge case variant of empty) ────────────────────

    [Fact]
    public async Task ExtractAsync_WhitespaceOnlyBody_ReturnsFailureWithoutCallingTracker()
    {
        var tracker = new Mock<IAiUsageTracker>(MockBehavior.Strict);

        var extractor = CreateExtractor(
            config: new Dictionary<string, string?>
            {
                ["Ai:Provider"]      = "openai",
                ["Ai:OpenAI:ApiKey"] = "sk-test-key",
            },
            tracker: tracker.Object,
            orgId: Guid.NewGuid());

        var result = await extractor.ExtractAsync("   \t\n  ", CancellationToken.None);

        result.Success.Should().BeFalse("whitespace-only body is effectively empty");
        result.Order.Should().BeNull();
        result.FailureReason.Should().NotBeNullOrWhiteSpace();
    }

    // ── DTO snake_case binding (regression guard) ────────────────────────────

    [Fact]
    public void ExtractionDto_BindsSnakeCaseJson_UnderWebDefaults()
    {
        // The OpenAI structured-output schema emits snake_case keys, but the DTO is
        // deserialized with JsonSerializerDefaults.Web (camelCase). Without explicit
        // [JsonPropertyName] attributes every multi-word field binds to null/default,
        // silently dropping the PO number, buyer name, item codes, and unit prices.
        const string json = """
            {
              "confidence": 0.9,
              "po_number": "PO-1",
              "order_date": "2026-05-20",
              "currency": "EUR",
              "buyer_name": "Acme",
              "lines": [
                { "line_number": 2, "buyer_item_code": "ABC", "description": "Widget",
                  "quantity": 4, "unit": "PCS", "unit_price": 12.5 }
              ]
            }
            """;

        var dto = JsonSerializer.Deserialize<OpenAiEmailBodyOrderExtractor.ExtractionDto>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        dto.Should().NotBeNull();
        dto!.PoNumber.Should().Be("PO-1");
        dto.OrderDate.Should().Be("2026-05-20");
        dto.BuyerName.Should().Be("Acme");
        dto.Currency.Should().Be("EUR");
        dto.Lines.Should().ContainSingle();
        dto.Lines![0].LineNumber.Should().Be(2);
        dto.Lines[0].BuyerItemCode.Should().Be("ABC");
        dto.Lines[0].UnitPrice.Should().Be(12.5);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static OpenAiEmailBodyOrderExtractor CreateExtractor(
        Dictionary<string, string?> config,
        IAiUsageTracker?            tracker,
        Guid                        orgId)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(config).Build();

        // Internal test ctor: bypasses ICurrentTenantService and lets us inject
        // a Func<Guid> for the org id. Pass overrideClient=null so the API-key
        // presence check still gates whether a ChatClient is constructed.
        return new OpenAiEmailBodyOrderExtractor(
            cfg,
            NullLogger<OpenAiEmailBodyOrderExtractor>.Instance,
            tracker,
            () => orgId,
            overrideClient: null);
    }
}
