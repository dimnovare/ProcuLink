using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProcuLink.Core.Services.Mapping;

/// <summary>
/// A <see cref="JsonStringEnumConverter"/> fixed to camelCase member names, usable as a
/// <c>[JsonConverter(typeof(...))]</c> ATTRIBUTE (the attribute form cannot pass a naming policy).
///
/// Applied to <see cref="OutputNodeType"/> (and the <see cref="OutputNodeTemplate.Format"/> property),
/// it makes those values round-trip as camelCase strings ("object"/"array"/"field"/"attribute",
/// "json"/"csv"/…) EVERYWHERE — including ASP.NET Core's <c>[FromBody]</c> model binding, which uses
/// the global web JSON options (no string-enum converter). Without this, a frontend tree posting
/// <c>"nodeType":"object"</c> fails to bind and the endpoint returns HTTP 400.
/// </summary>
public sealed class CamelCaseJsonStringEnumConverter : JsonStringEnumConverter
{
    public CamelCaseJsonStringEnumConverter() : base(JsonNamingPolicy.CamelCase) { }
}
