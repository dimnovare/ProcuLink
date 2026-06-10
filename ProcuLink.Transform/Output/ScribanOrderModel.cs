using System.Globalization;
using System.Text.Json;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Mapping;
using Scriban.Runtime;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Builds the STABLE Scriban model namespace exposed to a WHOLE-DOCUMENT output template
/// (<see cref="ScribanTemplateTransformService"/>). This is the contract the founder/FE template
/// editor renders as the "proposed structure"; the canonical reference is
/// <c>docs/qa/2026-06-09-scriban-template-namespace.md</c>. Keep the two in sync.
///
/// <para><b>Top-level globals</b> (header scope, available everywhere in the template):</para>
/// <list type="bullet">
///   <item><c>OrderNr</c> — the PO number (alias of the canonical <c>PoNumber</c>; both work).</item>
///   <item><c>PoNumber</c> — same value, canonical name.</item>
///   <item><c>OrderDate</c> — ISO date <c>yyyy-MM-dd</c>.</item>
///   <item><c>Currency</c>, <c>BuyerName</c>, <c>SupplierName</c>.</item>
///   <item><c>SubTotal</c>, <c>TaxTotal</c>, <c>GrandTotal</c>, <c>PaymentTerms</c> (Phase 4 enrichment;
///         empty when absent).</item>
///   <item><c>ShippingAddress</c> — object: <c>Company, FirstName, LastName, Address1, Address2, City,
///         ProvinceCode, State, PostalCode, CountryCode, Phone, Email</c> (each "" when absent).</item>
///   <item><c>CustomFields</c> — object keyed by the override's header custom-field keys.</item>
///   <item><c>Lines</c> — list of line objects (see below).</item>
/// </list>
///
/// <para><b>Each item of <c>Lines</c></b> exposes: <c>LineNr</c> / <c>LineNumber</c>, <c>SupplierItemCode</c>
/// (alias <c>DistributorPid</c>), <c>BuyerItemCode</c>, <c>Description</c>, <c>Qty</c> / <c>Quantity</c>
/// (numbers), <c>Unit</c>, <c>UnitPrice</c> / <c>OrderedPrice</c> (numbers), <c>LineTotal</c> (number),
/// plus each line-scoped custom field by its key, and a nested <c>CustomFields</c> object for the same.</para>
///
/// <para>Numeric fields (<c>Qty</c>, <c>UnitPrice</c>, <c>LineTotal</c>, totals) are exposed as real
/// numbers so a template can emit them unquoted (<c>"quantity":{{ Line.Qty }}</c>) and do arithmetic.
/// Missing/absent values are empty strings (never null), so a relaxed template never renders "null".</para>
/// </summary>
internal static class ScribanOrderModel
{
    /// <summary>Sub-keys exposed on the <c>ShippingAddress</c> object, in display order.</summary>
    internal static readonly string[] ShippingAddressKeys =
    {
        "Company", "FirstName", "LastName", "Address1", "Address2",
        "City", "ProvinceCode", "State", "PostalCode", "CountryCode", "Phone", "Email",
    };

    /// <summary>
    /// Build the global <see cref="ScriptObject"/> for <paramref name="order"/> using its (optional)
    /// per-order <paramref name="override"/> for custom fields. The override may be null (no custom
    /// fields / no shipping overrides) — the model is still fully populated from the canonical entity
    /// and its <c>CanonicalJson</c>.
    /// </summary>
    internal static ScriptObject Build(PurchaseOrderEntity order, OrderMappingOverride? @override)
    {
        var root = new ScriptObject();

        var poNumber     = order.PoNumber ?? string.Empty;
        var orderDate    = order.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var buyerName    = ExtractBuyerName(order);
        var currency     = order.Currency ?? string.Empty;
        var supplierName = order.Supplier?.Name ?? order.SupplierName ?? string.Empty;

        // ── Header globals (canonical + intuitive aliases) ──
        root["PoNumber"]     = poNumber;
        root["OrderNr"]      = poNumber;          // alias the founder's example uses
        root["OrderDate"]    = orderDate;
        root["BuyerName"]    = buyerName;
        root["Currency"]     = currency;
        root["SupplierName"] = supplierName;

        // Phase-4 enrichment numbers (empty string when absent — never the literal "null").
        root["SubTotal"]     = NumberOrEmpty(order.SubTotal);
        root["TaxTotal"]     = NumberOrEmpty(order.TaxTotal);
        root["GrandTotal"]   = NumberOrEmpty(order.GrandTotal);
        root["PaymentTerms"] = order.PaymentTerms ?? string.Empty;

        // ── ShippingAddress object ──
        root["ShippingAddress"] = BuildShippingAddress(order, @override);

        // ── CustomFields (header-scoped) ──
        var headerCustom = new ScriptObject();
        if (@override is not null)
        {
            foreach (var cf in @override.CustomFields)
            {
                if (!string.Equals(cf.Scope, "header", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(cf.Key)) continue;
                var val = cf.Value ?? string.Empty;
                headerCustom[cf.Key] = val;
                // Also expose at top level for convenience IF it does not shadow a built-in global.
                if (!root.ContainsKey(cf.Key)) root[cf.Key] = val;
            }
        }
        root["CustomFields"] = headerCustom;

        // ── Lines ──
        var lines = new List<ScriptObject>();
        foreach (var line in order.Lines.OrderBy(l => l.LineNumber))
            lines.Add(BuildLine(line, @override));
        root["Lines"] = lines;

        return root;
    }

    private static ScriptObject BuildLine(PurchaseOrderLineEntity line, OrderMappingOverride? @override)
    {
        var obj = new ScriptObject();

        var lineNr   = (decimal)line.LineNumber;
        var qty      = line.Quantity;
        var price    = line.UnitPrice;
        var total    = line.Quantity * line.UnitPrice;
        var supplier = line.SupplierItemCode ?? string.Empty;

        obj["LineNumber"]       = lineNr;
        obj["LineNr"]           = lineNr;            // alias
        obj["SupplierItemCode"] = supplier;
        obj["DistributorPid"]   = supplier;          // alias the founder's example uses
        obj["BuyerItemCode"]    = line.BuyerItemCode ?? string.Empty;
        obj["Description"]      = line.Description ?? string.Empty;
        obj["Quantity"]         = qty;
        obj["Qty"]              = qty;                // alias
        obj["Unit"]             = line.Unit ?? string.Empty;
        obj["UnitPrice"]        = price;
        obj["OrderedPrice"]     = price;             // alias the founder's example uses
        obj["LineTotal"]        = total;
        // LineAmount: the printed extended total when stated, else the computed line total.
        obj["LineAmount"]       = line.LineAmount ?? total;

        // Line-scoped custom fields for THIS line number.
        var lineCustom = new ScriptObject();
        if (@override is not null)
        {
            foreach (var cf in @override.CustomFields)
            {
                if (!string.Equals(cf.Scope, "line", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(cf.Key)) continue;
                var val = cf.LineValues is not null && cf.LineValues.TryGetValue(line.LineNumber, out var lv)
                    ? lv
                    : string.Empty;
                lineCustom[cf.Key] = val;
                if (!obj.ContainsKey(cf.Key)) obj[cf.Key] = val;
            }
        }
        obj["CustomFields"] = lineCustom;

        return obj;
    }

    /// <summary>
    /// Build the <c>ShippingAddress</c> object. Every documented sub-key is present and defaults to "".
    /// Population precedence per key (later wins): a nested <c>shippingAddress</c>/<c>shipTo</c> object
    /// in <c>CanonicalJson</c>, then a header custom field whose key matches (e.g. <c>City</c> or
    /// <c>ShipToCity</c>). This is intentionally forgiving so common templates "just work" whether the
    /// data arrived from a parser (CanonicalJson) or was hand-added as a custom field.
    /// </summary>
    private static ScriptObject BuildShippingAddress(PurchaseOrderEntity order, OrderMappingOverride? @override)
    {
        var addr = new ScriptObject();
        foreach (var key in ShippingAddressKeys) addr[key] = string.Empty;

        // 1) Pull from a nested object in CanonicalJson, if present.
        var node = FindShippingNode(order.CanonicalJson);
        if (node is { ValueKind: JsonValueKind.Object })
        {
            foreach (var key in ShippingAddressKeys)
            {
                var v = ReadStringProperty(node.Value, key);
                if (v is not null) addr[key] = v;
            }
        }

        // 2) Header custom fields can fill or override (exact key, or "ShipTo"+key).
        if (@override is not null)
        {
            foreach (var cf in @override.CustomFields)
            {
                if (!string.Equals(cf.Scope, "header", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(cf.Key) || cf.Value is null) continue;

                foreach (var key in ShippingAddressKeys)
                {
                    if (string.Equals(cf.Key, key, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(cf.Key, "ShipTo" + key, StringComparison.OrdinalIgnoreCase))
                    {
                        addr[key] = cf.Value;
                    }
                }
            }
        }

        return addr;
    }

    private static JsonElement? FindShippingNode(JsonDocument? canonical)
    {
        if (canonical is null) return null;
        try
        {
            if (canonical.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in new[] { "shippingAddress", "ShippingAddress", "shipTo", "ShipTo", "deliveryAddress" })
            {
                if (canonical.RootElement.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Object)
                    return el;
            }
        }
        catch { /* malformed JSON — ignore */ }
        return null;
    }

    /// <summary>Case-insensitive string-property read from a JSON object element. Null when absent.</summary>
    private static string? ReadStringProperty(JsonElement obj, string name)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            return prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                JsonValueKind.Null   => string.Empty,
                JsonValueKind.Number => prop.Value.GetRawText(),
                JsonValueKind.True   => "true",
                JsonValueKind.False  => "false",
                _                    => prop.Value.GetRawText(),
            };
        }
        return null;
    }

    /// <summary>A decimal exposed as a real number, or "" when null (so the template can test/branch).</summary>
    private static object NumberOrEmpty(decimal? value) => value.HasValue ? value.Value : string.Empty;

    private static string ExtractBuyerName(PurchaseOrderEntity order)
    {
        if (!string.IsNullOrEmpty(order.BuyerName)) return order.BuyerName;
        if (order.CanonicalJson is null) return string.Empty;
        try
        {
            if (order.CanonicalJson.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (order.CanonicalJson.RootElement.TryGetProperty("buyerName", out var el))
                    return el.GetString() ?? string.Empty;
                if (order.CanonicalJson.RootElement.TryGetProperty("BuyerName", out var el2))
                    return el2.GetString() ?? string.Empty;
            }
        }
        catch { /* malformed JSON — ignore */ }
        return string.Empty;
    }
}
