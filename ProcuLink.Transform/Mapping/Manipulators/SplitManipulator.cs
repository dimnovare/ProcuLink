namespace ProcuLink.Transform.Mapping.Manipulators;
public class SplitManipulator : IFieldManipulator {
    public SplitManipulator(IReadOnlyList<string> _) { }
    public string? Apply(string? value, IReadOnlyDictionary<string, string> row) => value;
}
