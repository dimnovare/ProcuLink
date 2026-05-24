namespace ProcuLink.Transform.Mapping.Manipulators;
public class DateFormatManipulator : IFieldManipulator {
    public DateFormatManipulator(IReadOnlyList<string> _) { }
    public string? Apply(string? value, IReadOnlyDictionary<string, string> row) => value;
}
