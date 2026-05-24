namespace ProcuLink.Transform.Mapping.Manipulators;
public class MultiplyManipulator : IFieldManipulator {
    public MultiplyManipulator(IReadOnlyList<string> _) { }
    public string? Apply(string? value, IReadOnlyDictionary<string, string> row) => value;
}
