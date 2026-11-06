using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProcuLink.Core.Documents;

/// <summary>The one serializer for jsonb columns holding read results and flow field lists.</summary>
public static class DocumentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;
}
