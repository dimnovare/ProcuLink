using System.Text;
using System.Text.Json;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Generates a canonical JSON payload from a fully-resolved order.
/// Suitable for delivery to REST-webhook suppliers.
/// <para>The optional <c>shipTo</c> / <c>billTo</c> / <c>contact</c> blocks carry the canonical
/// address + ordering-contact fields. They are OMITTED entirely when the order has no such data —
/// the conditional-object strategy (build the key only when present) rather than
/// <c>JsonIgnoreCondition.WhenWritingNull</c>, so existing always-emitted keys (e.g. a null
/// <c>currency</c>) are NOT dropped. An address-less order is therefore byte-identical to the
/// pre-feature payload. The buyer stays name-only (the canonical model has no buyer postal address).</para>
/// </summary>
public sealed class JsonTransformService : ITransformService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public bool CanTransform(OutputFormat format) => format == OutputFormat.Json;

    public Task<TransformResult> TransformAsync(
        PurchaseOrderEntity order,
        OutputFormat format,
        CancellationToken ct,
        CxmlCredentialConfig? cxmlCredentials = null) // not used: JSON has no cXML Header
    {
        ValidateOrder(order);
        // B1: same price>0 / qty>0 output invariant as the fixed CSV/XML transforms — for a fixed
        // JSON transform the entity's canonical columns ARE the emitted bytes (override/template/
        // OutputNode paths emit elsewhere and are intentionally not guarded here).
        OutputFieldValidator.ValidateEntity(order, format);

        var lines = order.Lines
            .OrderBy(l => l.LineNumber)
            .Select(l => new
            {
                l.LineNumber,
                supplierItemCode = l.SupplierItemCode ?? string.Empty,
                buyerItemCode    = l.BuyerItemCode    ?? string.Empty,
                description      = l.Description      ?? string.Empty,
                quantity         = l.Quantity,
                unit             = l.Unit             ?? string.Empty,
                unitPrice        = l.UnitPrice,
                lineTotal        = l.Quantity * l.UnitPrice,
            })
            .ToList();

        var totalValue = lines.Sum(l => l.lineTotal);

        var buyerName = OrderHeaderReader.ExtractBuyerName(order);

        // Ordered payload. Top-level keys are written verbatim (already camelCase) into a
        // Dictionary — PropertyNamingPolicy applies to anonymous-object members (lines/shipTo/…) but
        // NOT to dictionary keys, so the existing keys serialize byte-identically. The optional
        // address/contact blocks are inserted (between supplierName and lines) ONLY when present, so
        // an address-less order keeps the exact pre-feature key set and bytes.
        var payload = new Dictionary<string, object?>
        {
            ["poNumber"]     = order.PoNumber    ?? string.Empty,
            ["orderDate"]    = order.OrderDate.ToString("yyyy-MM-dd"),
            ["currency"]     = order.Currency,
            ["buyerName"]    = buyerName,
            ["supplierName"] = order.Supplier?.Name ?? string.Empty,
        };

        var shipTo = BuildAddress(
            order.ShipToName, order.ShipToDeliverTo, order.ShipToStreet, order.ShipToCity,
            order.ShipToPostalCode, order.ShipToCountry, order.ShipToEmail, order.ShipToPhone);
        if (shipTo is not null) payload["shipTo"] = shipTo;

        var billTo = BuildAddress(
            order.BillToName, order.BillToDeliverTo, order.BillToStreet, order.BillToCity,
            order.BillToPostalCode, order.BillToCountry, order.BillToEmail, order.BillToPhone);
        if (billTo is not null) payload["billTo"] = billTo;

        var contact = BuildContact(order);
        if (contact is not null) payload["contact"] = contact;

        payload["lines"]       = lines;
        payload["totalValue"]  = totalValue;
        payload["generatedAt"] = DateTime.UtcNow.ToString("O");

        var json  = JsonSerializer.Serialize(payload, SerializerOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        return Task.FromResult(new TransformResult(
            Content:       new MemoryStream(bytes),
            ContentType:   "application/json",
            FileExtension: ".json"
        ));
    }

    private static void ValidateOrder(PurchaseOrderEntity order)
    {
        var unresolved = order.Lines
            .Where(l => l.NeedsReview || string.IsNullOrWhiteSpace(l.SupplierItemCode))
            .Select(l => l.LineNumber)
            .OrderBy(n => n)
            .ToList();

        if (unresolved.Count > 0)
            throw new TransformValidationException(unresolved);
    }

    // ── Address / contact blocks ───────────────────────────────────────────────

    private static bool IsBlank(string? s) => string.IsNullOrWhiteSpace(s);

    /// <summary>
    /// Builds a nested shipTo/billTo object with only the populated leaf fields, or null when the
    /// NAME is blank (→ the parent key is omitted entirely → byte-identical for orders without that
    /// address). Keys are written verbatim (already camelCase) so they don't depend on the dictionary
    /// key policy; absent leaves are simply not added.
    /// </summary>
    private static Dictionary<string, object?>? BuildAddress(
        string? name, string? deliverTo, string? street, string? city,
        string? postal, string? country, string? email, string? phone)
    {
        if (IsBlank(name))
            return null;

        var addr = new Dictionary<string, object?> { ["name"] = name };
        if (!IsBlank(deliverTo)) addr["deliverTo"]  = deliverTo;
        if (!IsBlank(street))    addr["street"]     = street;
        if (!IsBlank(city))      addr["city"]       = city;
        if (!IsBlank(postal))    addr["postalCode"] = postal;
        if (!IsBlank(country))   addr["country"]    = country;
        if (!IsBlank(email))     addr["email"]      = email;
        if (!IsBlank(phone))     addr["phone"]      = phone;
        return addr;
    }

    /// <summary>
    /// Builds the nested contact object (name/email/phone), or null when all three are blank
    /// (→ the <c>contact</c> key is omitted → byte-identical for orders with no contact).
    /// </summary>
    private static Dictionary<string, object?>? BuildContact(PurchaseOrderEntity o)
    {
        if (IsBlank(o.ContactName) && IsBlank(o.ContactEmail) && IsBlank(o.ContactPhone))
            return null;

        var contact = new Dictionary<string, object?>();
        if (!IsBlank(o.ContactName))  contact["name"]  = o.ContactName;
        if (!IsBlank(o.ContactEmail)) contact["email"] = o.ContactEmail;
        if (!IsBlank(o.ContactPhone)) contact["phone"] = o.ContactPhone;
        return contact;
    }
}
