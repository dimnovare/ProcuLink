namespace ProcuLink.Transform.Mapping.Manipulators;

/// <summary>Params: [delimiter, zeroBasedIndex] -- splits on delimiter and returns the token at index.</summary>
public class SplitManipulator : IFieldManipulator
{
    private readonly string _delimiter;
    private readonly int _index;

    public SplitManipulator(IReadOnlyList<string> @params)
    {
        if (@params.Count != 2)
            throw new ArgumentException("Split requires exactly 2 params: [delimiter, index]", nameof(@params));
        _delimiter = @params[0];
        if (!int.TryParse(@params[1], out _index))
            throw new ArgumentException("Split param 'index' must be a valid integer.", nameof(@params));
    }

    public string? Apply(string? value, IReadOnlyDictionary<string, string> row)
    {
        if (value is null) return null;
        var parts = value.Split(_delimiter);
        return _index >= 0 && _index < parts.Length ? parts[_index] : value;
    }
}
