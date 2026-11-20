namespace ProcuLink.Transform.Mapping.Manipulators;

/// <summary>Params: [delimiter, zeroBasedIndex] -- splits on delimiter and returns the token at index.</summary>
public class SplitManipulator : IFieldManipulator
{
    private readonly string _delimiter;
    private readonly int _index;

    public SplitManipulator(IReadOnlyList<string> @params)
    {
        if (@params.Count != 2)
            throw new ArgumentException("“Split & take one part” needs a separator and which part to take.", nameof(@params));
        _delimiter = @params[0];
        if (!int.TryParse(@params[1], out _index))
            throw new ArgumentException("“Split & take one part” needs the part as a whole number: 0 for the first part, 1 for the second.", nameof(@params));
    }

    public string? Apply(string? value, IReadOnlyDictionary<string, string> row)
    {
        if (value is null) return null;
        var parts = value.Split(_delimiter);
        return _index >= 0 && _index < parts.Length ? parts[_index] : value;
    }
}
