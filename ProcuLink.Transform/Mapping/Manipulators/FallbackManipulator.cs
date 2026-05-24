namespace ProcuLink.Transform.Mapping.Manipulators;

/// <summary>Params: [col1, col2, ...] -- returns first non-empty value from the named columns.</summary>
public class FallbackManipulator : IFieldManipulator
{
    private readonly IReadOnlyList<string> _columns;

    public FallbackManipulator(IReadOnlyList<string> @params)
    {
        _columns = @params;
    }

    public string? Apply(string? value, IReadOnlyDictionary<string, string> row)
    {
        foreach (var col in _columns)
            if (row.TryGetValue(col, out var v) && !string.IsNullOrEmpty(v))
                return v;
        return null;
    }
}
