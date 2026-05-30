using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProcuLink.Api.Services.StarterTemplates;

/// <summary>
/// Loads PO mapping starter templates from embedded JSON fixtures on first call
/// and caches the result for the lifetime of the singleton. No DB access.
/// </summary>
public sealed class StarterTemplateService : IStarterTemplateService
{
    // Each embedded resource name in ProcuLink.Api.dll.
    // Note: .NET converts hyphens in directory names to underscores in resource names,
    // so "Fixtures/po-templates/" becomes "ProcuLink.Api.Fixtures.po_templates.".
    private static readonly string[] FixtureResourceNames =
    [
        "ProcuLink.Api.Fixtures.po_templates.erply.json",
        "ProcuLink.Api.Fixtures.po_templates.directo.json",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lazy<IReadOnlyList<StarterTemplateDto>> _templates;

    public StarterTemplateService()
    {
        _templates = new Lazy<IReadOnlyList<StarterTemplateDto>>(Load, isThreadSafe: true);
    }

    /// <inheritdoc />
    public IReadOnlyList<StarterTemplateDto> GetAll() => _templates.Value;

    private static IReadOnlyList<StarterTemplateDto> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var list = new List<StarterTemplateDto>(FixtureResourceNames.Length);

        foreach (var resourceName in FixtureResourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded starter-template fixture '{resourceName}' not found in ProcuLink.Api assembly. " +
                    "Ensure the file is included as an EmbeddedResource in ProcuLink.Api.csproj.");

            var dto = JsonSerializer.Deserialize<StarterTemplateDto>(stream, JsonOptions)
                ?? throw new InvalidOperationException(
                    $"Failed to deserialize starter-template fixture '{resourceName}' — JSON may be empty or malformed.");

            list.Add(dto);
        }

        return list.AsReadOnly();
    }
}
