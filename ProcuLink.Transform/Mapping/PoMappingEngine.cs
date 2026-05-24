using ProcuLink.Core.Services.Mapping;

namespace ProcuLink.Transform.Mapping;

public static class PoMappingEngine
{
    public static MappedOrder Apply(
        IReadOnlyDictionary<string, string> headerRow,
        IReadOnlyList<IReadOnlyDictionary<string, string>> lineRows,
        PoMappingConfig config)
    {
        return new MappedOrder
        {
            PoNumber  = ResolveField("PoNumber",  config.Header, headerRow),
            OrderDate = ResolveField("OrderDate", config.Header, headerRow),
            BuyerName = ResolveField("BuyerName", config.Header, headerRow),
            Currency  = ResolveField("Currency",  config.Header, headerRow),
            Lines = lineRows.Select(row => new MappedOrderLine
            {
                LineNumber    = ResolveField("LineNumber",    config.Lines, row),
                BuyerItemCode = ResolveField("BuyerItemCode", config.Lines, row),
                Description   = ResolveField("Description",  config.Lines, row),
                Quantity      = ResolveField("Quantity",      config.Lines, row),
                Unit          = ResolveField("Unit",          config.Lines, row),
                UnitPrice     = ResolveField("UnitPrice",     config.Lines, row),
            }).ToList()
        };
    }

    private static string? ResolveField(
        string canonicalField,
        Dictionary<string, FieldMappingEntry> mapping,
        IReadOnlyDictionary<string, string> row)
    {
        if (!mapping.TryGetValue(canonicalField, out var entry)) return null;

        string? value = entry.FixedValue
            ?? (entry.ExternalField is not null && row.TryGetValue(entry.ExternalField, out var v) ? v : null);

        foreach (var m in entry.FieldManipulators)
        {
            var manipulator = ManipulatorRegistry.Resolve(m.Type, m.Params);
            value = manipulator.Apply(value, row);
        }

        return value;
    }
}
