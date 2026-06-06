using System.Text.Json.Serialization;
using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services.Organisation;

/// <summary>
/// Request to change the org's order-direction presentation flag. The enum is
/// exchanged as its string name ("Outbound"/"Inbound", case-insensitive on input)
/// so the frontend works with readable values rather than magic integers.
/// </summary>
public sealed record UpdateOrderDirectionRequest(
    [property: JsonConverter(typeof(JsonStringEnumConverter<OrderDirection>))]
    OrderDirection Direction);

/// <summary>
/// Org-level settings response. New free org-wide flags accrete here as additional
/// properties over time.
/// <para>
/// <c>Slug</c> is the org's unique kebab-case identifier; the frontend renders the
/// customer's ingress endpoint as <c>{API_BASE}/api/ingress/{slug}/orders</c> from it.
/// </para>
/// </summary>
public sealed record OrgSettingsResponse(
    [property: JsonConverter(typeof(JsonStringEnumConverter<OrderDirection>))]
    OrderDirection Direction,
    string Slug);
