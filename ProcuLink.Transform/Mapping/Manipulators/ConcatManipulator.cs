namespace ProcuLink.Transform.Mapping.Manipulators;
public class ConcatManipulator : IFieldManipulator {
    public ConcatManipulator(IReadOnlyList<string> _) { }
    public string? Apply(string? value, IReadOnlyDictionary<string, string> row) => value;
}
