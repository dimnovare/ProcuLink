using System.Text.Json;

namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// Reads optional boolean switches out of a supplier's non-secret delivery
/// <see cref="Entities.SupplierDeliveryConfig.ConfigJson"/>.
///
/// <para>
/// <c>ConfigJson</c> is free-form (<c>DeliveryConfigService.ValidateConfigJson</c> only checks that
/// it parses), so every reader must tolerate a missing key, a wrong-typed value and unparseable
/// JSON without throwing — a malformed config must never take down a dispatch. Matching is
/// case-insensitive, mirroring the dispatchers' <c>PropertyNameCaseInsensitive</c> deserialization.
/// </para>
/// </summary>
public static class DeliveryConfigFlags
{
    /// <summary>Delivery config key: keep the pre-WP-20 bare-PO-number file-drop filename.</summary>
    public const string LegacyFileName = "legacyFileName";

    /// <summary>Delivery config key: allow a file-drop upload to replace an existing remote file.</summary>
    public const string OverwriteExisting = "overwriteExisting";

    /// <summary>
    /// Returns the boolean value of <paramref name="property"/>, or <paramref name="fallback"/>
    /// when the config is blank/unparseable, is not a JSON object, omits the key, or carries a
    /// non-boolean value there.
    /// </summary>
    public static bool ReadBool(string? configJson, string property, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return fallback;

        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return fallback;

            foreach (var element in doc.RootElement.EnumerateObject())
            {
                if (!element.Name.Equals(property, StringComparison.OrdinalIgnoreCase)) continue;

                return element.Value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => fallback,
                };
            }

            return fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}
