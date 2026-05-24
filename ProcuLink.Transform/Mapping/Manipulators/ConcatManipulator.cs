namespace ProcuLink.Transform.Mapping.Manipulators;

/// <summary>Params: [separator, col1, col2, ...] -- reads named columns from the row and joins them.</summary>
public class ConcatManipulator : IFieldManipulator
{
    private readonly string _separator;
    private readonly IReadOnlyList<string> _columns;

    public ConcatManipulator(IReadOnlyList<string> @params)
    {
        if (@params.Count < 2)
            throw new ArgumentException("Concat requires at least 2 params: [separator, col1, ...]", nameof(@params));
        _separator = @params[0];
        _columns = @params.Skip(1).ToList();
    }

    public string? Apply(string? value, IReadOnlyDictionary<string, string> row)
    {
        var parts = _columns.Select(c => row.TryGetValue(c, out var v) ? v : string.Empty);
        return string.Join(_separator, parts);
    }
}
