namespace ProcuLink.Transform.Mapping.Manipulators;

/// <summary>
/// Params: [lookupId, ifMissing]. Swaps the value for the lookup's stored To, verbatim. A value the lookup does not
/// hold is kept (<c>ifMissing</c> "keep", the default) or blanked ("blank"), and reported as a miss — never a failure.
/// The tables come from <see cref="LookupScope"/>, which only a document render sets.
/// </summary>
public class LookupManipulator(IReadOnlyList<string> @params, LookupTables? tables) : IFieldManipulator
{
    private readonly bool _blank = @params.ElementAtOrDefault(1) == "blank";

    public string? Apply(string? value, IReadOnlyDictionary<string, string> row)
    {
        // Checked before the empty-value shortcut: outside a document render (the order product) the step is refused
        // outright, so an order save's dry run still rejects it as it did before Lookup existed.
        if (tables is null) throw new InvalidOperationException("lookup_unavailable");
        if (string.IsNullOrWhiteSpace(value)) return value;
        if (!Guid.TryParse(@params.ElementAtOrDefault(0), out var id) || !tables.Has(id))
            throw new InvalidOperationException("lookup_unavailable");
        return tables.TryMap(id, value, out var mapped) ? mapped : _blank ? "" : value;
    }
}
