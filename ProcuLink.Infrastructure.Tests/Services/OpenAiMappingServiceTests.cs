using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

public class OpenAiMappingServiceTests
{
    [Fact]
    public async Task SuggestSupplierItemCodeAsync_NoApiKey_ReturnsNull()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["Ai:Provider"] = "openai",
            ["Ai:OpenAI:MappingModel"] = "gpt-5-mini"
        });

        var result = await service.SuggestSupplierItemCodeAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Acme Components",
            new AiMappingLineContext(1, "HEI-PLT-09", "Mounting plate 90mm", 4, "PCS"),
            Array.Empty<AiMappingCandidate>());

        result.Should().BeNull();
    }

    [Fact]
    public async Task SuggestSupplierItemCodeAsync_NonOpenAiProvider_ReturnsNull()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["Ai:Provider"] = "none",
            ["Ai:OpenAI:ApiKey"] = "test-key",
            ["Ai:OpenAI:MappingModel"] = "gpt-5-mini"
        });

        var result = await service.SuggestSupplierItemCodeAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Acme Components",
            new AiMappingLineContext(1, "HEI-PLT-09", "Mounting plate 90mm", 4, "PCS"),
            new[]
            {
                new AiMappingCandidate("HEI-PLT-08", "ACM-PLT-080", "existing mapping")
            });

        result.Should().BeNull();
    }

    // ── Batch variant: same no-op guarantees as the single-line method ──────────

    [Fact]
    public async Task SuggestSupplierItemCodesAsync_NoApiKey_ReturnsEmpty()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["Ai:Provider"] = "openai",
            ["Ai:OpenAI:MappingModel"] = "gpt-5-mini"
        });

        var result = await service.SuggestSupplierItemCodesAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Acme Components",
            new[]
            {
                new AiMappingLineContext(1, "HEI-PLT-09", "Mounting plate 90mm", 4, "PCS"),
                new AiMappingLineContext(2, "HEI-PLT-10", "Mounting plate 100mm", 2, "PCS"),
            },
            Array.Empty<AiMappingCandidate>());

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SuggestSupplierItemCodesAsync_NonOpenAiProvider_ReturnsEmpty()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["Ai:Provider"] = "none",
            ["Ai:OpenAI:ApiKey"] = "test-key",
            ["Ai:OpenAI:MappingModel"] = "gpt-5-mini"
        });

        var result = await service.SuggestSupplierItemCodesAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Acme Components",
            new[]
            {
                new AiMappingLineContext(1, "HEI-PLT-09", "Mounting plate 90mm", 4, "PCS"),
            },
            new[]
            {
                new AiMappingCandidate("HEI-PLT-08", "ACM-PLT-080", "existing mapping")
            });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SuggestSupplierItemCodesAsync_EmptyLineList_ReturnsEmpty()
    {
        // Even with a configured key the call must short-circuit (and never hit the
        // network) when there are no lines to suggest for.
        var service = CreateService(new Dictionary<string, string?>
        {
            ["Ai:Provider"] = "openai",
            ["Ai:OpenAI:ApiKey"] = "test-key",
            ["Ai:OpenAI:MappingModel"] = "gpt-5-mini"
        });

        var result = await service.SuggestSupplierItemCodesAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Acme Components",
            Array.Empty<AiMappingLineContext>(),
            Array.Empty<AiMappingCandidate>());

        result.Should().BeEmpty();
    }

    private static OpenAiMappingService CreateService(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return new OpenAiMappingService(
            configuration,
            NullLogger<OpenAiMappingService>.Instance);
    }
}
