#pragma warning disable OPENAI001 // OpenAI Responses API is marked experimental in 2.10.0.
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// T4 — external web/product-code grounding. Like every other AI service, the searcher
/// MUST be a safe no-op (return null, no network, no egress) unless THREE things are true:
/// provider is openai, an API key is configured, AND the per-feature flag
/// <c>Ai:OpenAI:ProductSearch:Enabled</c> is on. The default deploy (flag absent) is therefore
/// byte-identical. The JSON-parsing seam is exercised purely, without a network call.
/// </summary>
public class OpenAiProductCodeSearchTests
{
    private static OpenAiProductCodeSearch CreateService(Dictionary<string, string?> values) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            NullLogger<OpenAiProductCodeSearch>.Instance);

    [Fact]
    public async Task FindPartNumberAsync_NonOpenAiProvider_ReturnsNull()
    {
        var service = CreateService(new()
        {
            ["Ai:Provider"] = "none",
            ["Ai:OpenAI:ApiKey"] = "sk-test-key",
            ["Ai:OpenAI:ProductSearch:Enabled"] = "true",
        });

        var result = await service.FindPartNumberAsync("Apple iPhone 15 case", null, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task FindPartNumberAsync_NoApiKey_ReturnsNull()
    {
        var service = CreateService(new()
        {
            ["Ai:Provider"] = "openai",
            ["Ai:OpenAI:ProductSearch:Enabled"] = "true",
            // no ApiKey
        });

        var result = await service.FindPartNumberAsync("Apple iPhone 15 case", null, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task FindPartNumberAsync_FlagOff_ReturnsNull_EvenWithKey()
    {
        // Default deploy: provider + key present but the per-feature flag is absent → no-op.
        var service = CreateService(new()
        {
            ["Ai:Provider"] = "openai",
            ["Ai:OpenAI:ApiKey"] = "sk-test-key",
            // Ai:OpenAI:ProductSearch:Enabled absent
        });

        var result = await service.FindPartNumberAsync("Apple iPhone 15 case", "Apple", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task FindPartNumberAsync_FlagExplicitlyFalse_ReturnsNull()
    {
        var service = CreateService(new()
        {
            ["Ai:Provider"] = "openai",
            ["Ai:OpenAI:ApiKey"] = "sk-test-key",
            ["Ai:OpenAI:ProductSearch:Enabled"] = "false",
        });

        var result = await service.FindPartNumberAsync("Apple iPhone 15 case", null, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task FindPartNumberAsync_BlankDescription_ReturnsNull()
    {
        // Even fully configured, an empty description has nothing to search for.
        var service = CreateService(new()
        {
            ["Ai:Provider"] = "openai",
            ["Ai:OpenAI:ApiKey"] = "sk-test-key",
            ["Ai:OpenAI:ProductSearch:Enabled"] = "true",
        });

        var result = await service.FindPartNumberAsync("   ", null, CancellationToken.None);

        result.Should().BeNull();
    }

    // ── Request-shape seam (no network) ─────────────────────────────────────────

    [Fact]
    public void BuildOptions_DisablesServerSideStorage()
    {
        // The Responses API stores request/response payloads by default (unlike Chat
        // Completions). PO line descriptions are customer data, so the request must opt out
        // explicitly: StoredOutputEnabled maps to the "store" property in the JSON payload.
        var options = OpenAiProductCodeSearch.BuildOptions("gpt-5-mini", "Apple iPhone 15 case", null);

        options.StoredOutputEnabled.Should().BeFalse();
    }

    [Fact]
    public void BuildOptions_CarriesModelWebSearchToolAndOutputCap()
    {
        var options = OpenAiProductCodeSearch.BuildOptions("gpt-5-mini", "Apple iPhone 15 case", "Apple");

        options.Model.Should().Be("gpt-5-mini");
        options.Tools.Should().ContainSingle();
        options.MaxOutputTokenCount.Should().Be(600);
        options.InputItems.Should().ContainSingle();
    }

    // ── Pure JSON parse seam (no network) ───────────────────────────────────────

    [Fact]
    public void ParseMatch_ValidJson_MapsAllFields()
    {
        var match = OpenAiProductCodeSearch.ParseMatch(
            """{"partNumber":"REDACTED-ORDER-DATA","title":"Apple iPhone 15 Silicone Case","sourceUrl":"https://example.invalid/redacted","confidence":0.7}""");

        match.Should().NotBeNull();
        match!.PartNumber.Should().Be("REDACTED-ORDER-DATA");
        match.Title.Should().Be("Apple iPhone 15 Silicone Case");
        match.SourceUrl.Should().Be("https://example.invalid/redacted");
        match.Confidence.Should().BeApproximately(0.7f, 0.0001f);
    }

    [Fact]
    public void ParseMatch_EmptyPartNumber_ReturnsNull()
    {
        OpenAiProductCodeSearch.ParseMatch("""{"partNumber":"","confidence":0.9}""").Should().BeNull();
        OpenAiProductCodeSearch.ParseMatch("""{"partNumber":"   ","confidence":0.9}""").Should().BeNull();
    }

    [Fact]
    public void ParseMatch_ConfidenceClampedTo01()
    {
        OpenAiProductCodeSearch.ParseMatch("""{"partNumber":"X","confidence":2.5}""")!
            .Confidence.Should().Be(1f);
        OpenAiProductCodeSearch.ParseMatch("""{"partNumber":"X","confidence":-3}""")!
            .Confidence.Should().Be(0f);
    }

    [Fact]
    public void ParseMatch_TolueratesCodeFencesAndSurroundingProse()
    {
        // Web-search models often wrap JSON in a ```json fence or add a sentence around it.
        var raw = "Here is what I found:\n```json\n{\"partNumber\":\"A2848\",\"confidence\":0.6}\n```\nHope that helps!";
        var match = OpenAiProductCodeSearch.ParseMatch(raw);

        match.Should().NotBeNull();
        match!.PartNumber.Should().Be("A2848");
    }

    [Fact]
    public void ParseMatch_GarbageOrEmpty_ReturnsNull()
    {
        OpenAiProductCodeSearch.ParseMatch(null).Should().BeNull();
        OpenAiProductCodeSearch.ParseMatch("").Should().BeNull();
        OpenAiProductCodeSearch.ParseMatch("not json at all").Should().BeNull();
    }
}
