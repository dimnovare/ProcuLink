namespace ProcuLink.Transform.Mapping.Manipulators;
public class DivideManipulator : IFieldManipulator {
    public DivideManipulator(IReadOnlyList<string> _) { }
    public string? Apply(string? value, IReadOnlyDictionary<string, string> row) => value;
}
