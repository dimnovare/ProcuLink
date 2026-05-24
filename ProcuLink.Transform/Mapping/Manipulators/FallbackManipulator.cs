namespace ProcuLink.Transform.Mapping.Manipulators;
public class FallbackManipulator : IFieldManipulator {
    public FallbackManipulator(IReadOnlyList<string> _) { }
    public string? Apply(string? value, IReadOnlyDictionary<string, string> row) => value;
}
