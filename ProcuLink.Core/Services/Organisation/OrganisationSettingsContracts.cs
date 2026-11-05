using System.Text.Json.Serialization;
using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services.Organisation;

/// <summary>
/// Partial update of the org settings: a field left out of the body is left unchanged. The enum is
/// exchanged as its string name ("Outbound"/"Inbound", case-insensitive on input) so the frontend
/// works with readable values rather than magic integers.
/// <para>The invoice destination can be CLEARED, so JSON <c>null</c> and "absent" mean different
/// things; <see cref="InvoiceDestinationSet"/> records that the field was sent at all.</para>
/// </summary>
public sealed record UpdateOrgSettingsRequest(
    [property: JsonConverter(typeof(JsonStringEnumConverter<OrderDirection>))]
    OrderDirection? Direction = null)
{
    private readonly Guid? _invoiceDestinationSupplierId;

    public Guid? InvoiceDestinationSupplierId
    {
        get => _invoiceDestinationSupplierId;
        init { _invoiceDestinationSupplierId = value; InvoiceDestinationSet = true; }
    }

    [JsonIgnore] public bool InvoiceDestinationSet { get; private init; }
}

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
    string Slug,
    Guid? InvoiceDestinationSupplierId);
