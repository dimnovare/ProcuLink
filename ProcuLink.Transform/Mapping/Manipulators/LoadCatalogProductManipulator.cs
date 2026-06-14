namespace ProcuLink.Transform.Mapping.Manipulators;

/// <summary>
/// Phase 2 catalog manipulator. Params: <c>[field]</c> where field ∈ price|code|unit|barcode.
/// The catalog row is pre-injected into the value bag by the caller under reserved keys
/// (<c>__catalog_price</c>, <c>__catalog_code</c>, <c>__catalog_unit</c>, <c>__catalog_barcode</c>)
/// — the manipulator contract only sees the row, never the DB, so the lookup stays centralised
/// and the engine stays sandboxed. Returns the catalog field's RAW string (a suggestion); the
/// caller decides whether to use it. Missing → empty string (never throws). Any arithmetic on the
/// returned price must use the EU-aware parse (see <c>PriceVarianceGuard.ParseEuAware</c>) — this
/// manipulator does NOT reformat numbers, and it NEVER overwrites the PO value silently.
/// </summary>
public class LoadCatalogProductManipulator : IFieldManipulator
{
    private readonly string _key;

    public LoadCatalogProductManipulator(IReadOnlyList<string> @params)
    {
        if (@params.Count != 1)
            throw new ArgumentException("LoadCatalogProduct requires exactly 1 param: [field] (price|code|unit|barcode)", nameof(@params));
        _key = @params[0].Trim().ToLowerInvariant() switch
        {
            "price"   => "__catalog_price",
            "code"    => "__catalog_code",
            "unit"    => "__catalog_unit",
            "barcode" => "__catalog_barcode",
            var other => throw new ArgumentException($"LoadCatalogProduct: unknown field '{other}' (expected price|code|unit|barcode)", nameof(@params)),
        };
    }

    public string? Apply(string? value, IReadOnlyDictionary<string, string> row)
        => row.TryGetValue(_key, out var v) ? v : string.Empty;
}
