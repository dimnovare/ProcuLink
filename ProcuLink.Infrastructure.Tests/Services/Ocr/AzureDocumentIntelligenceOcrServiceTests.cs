using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Infrastructure.Services.Ocr;

namespace ProcuLink.Infrastructure.Tests.Services.Ocr;

/// <summary>
/// Tests for <see cref="AzureDocumentIntelligenceOcrService"/>.
///
/// All tests exercise the no-op / IsAvailable paths only — no live Azure calls
/// are made.  Mirrors the pattern from <c>OpenAiSchemaInferencerTests</c>:
/// when provider keys are absent, the service short-circuits cleanly and the
/// public contract remains correct.
/// </summary>
public class AzureDocumentIntelligenceOcrServiceTests
{
    // ── No-op / config-absent paths ──────────────────────────────────────────

    [Fact]
    public async Task ExtractTextAsync_NoEndpoint_ReturnsEmptyString()
    {
        // Neither Ocr:Azure:Endpoint nor Ocr:Azure:ApiKey are set.
        // Service must return empty immediately without attempting a network call.
        var service = CreateService(new Dictionary<string, string?>());

        await using var stream = StreamOf("%PDF fake content");
        var result = await service.ExtractTextAsync(stream, "application/pdf", CancellationToken.None);

        result.Should().BeEmpty("no endpoint configured → no-op path");
    }

    [Fact]
    public async Task ExtractTextAsync_NoApiKey_ReturnsEmptyString()
    {
        // Endpoint is present but API key is absent — service must still be no-op.
        var service = CreateService(new Dictionary<string, string?>
        {
            ["Ocr:Azure:Endpoint"] = "https://my-doc-intel.cognitiveservices.azure.com/"
        });

        await using var stream = StreamOf("%PDF fake content");
        var result = await service.ExtractTextAsync(stream, "application/pdf", CancellationToken.None);

        result.Should().BeEmpty("API key missing → no-op path");
    }

    // ── IsAvailable ──────────────────────────────────────────────────────────

    [Fact]
    public void IsAvailable_BothKeysPresent_ReturnsTrue()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["Ocr:Azure:Endpoint"] = "https://my-doc-intel.cognitiveservices.azure.com/",
            ["Ocr:Azure:ApiKey"]   = "00000000000000000000000000000000"
        });

        service.IsAvailable.Should().BeTrue(
            "both Ocr:Azure:Endpoint and Ocr:Azure:ApiKey are present");
    }

    [Fact]
    public void IsAvailable_MissingKey_ReturnsFalse()
    {
        // Only the endpoint — no key.
        var serviceNoKey = CreateService(new Dictionary<string, string?>
        {
            ["Ocr:Azure:Endpoint"] = "https://my-doc-intel.cognitiveservices.azure.com/"
        });

        serviceNoKey.IsAvailable.Should().BeFalse(
            "Ocr:Azure:ApiKey is missing");

        // Only the key — no endpoint.
        var serviceNoEndpoint = CreateService(new Dictionary<string, string?>
        {
            ["Ocr:Azure:ApiKey"] = "00000000000000000000000000000000"
        });

        serviceNoEndpoint.IsAvailable.Should().BeFalse(
            "Ocr:Azure:Endpoint is missing");

        // Neither key — definitely false.
        var serviceNeither = CreateService(new Dictionary<string, string?>());

        serviceNeither.IsAvailable.Should().BeFalse(
            "neither config key is present");
    }

    // ── NoOpOcrService parity ────────────────────────────────────────────────

    [Fact]
    public async Task NoOpOcrService_IsAvailableFalseAndAlwaysReturnsEmpty()
    {
        var noop = new NoOpOcrService();

        noop.IsAvailable.Should().BeFalse();

        await using var stream = StreamOf("anything");
        var result = await noop.ExtractTextAsync(stream, "application/pdf", CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static AzureDocumentIntelligenceOcrService CreateService(
        Dictionary<string, string?> configValues)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        return new AzureDocumentIntelligenceOcrService(
            config,
            NullLogger<AzureDocumentIntelligenceOcrService>.Instance);
    }

    private static MemoryStream StreamOf(string text)
        => new(Encoding.UTF8.GetBytes(text));
}
