namespace ProcuLink.Transform.Mapping.Manipulators;

/// <summary>No params required.</summary>
public class TrimManipulator : IFieldManipulator
{
    public TrimManipulator(IReadOnlyList<string> _) { }

    public string? Apply(string? value, IReadOnlyDictionary<string, string> row)
        => value?.Trim() ?? string.Empty;
}
