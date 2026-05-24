namespace ProcuLink.Transform.Mapping;

public interface IFieldManipulator
{
    string? Apply(string? value, IReadOnlyDictionary<string, string> row);
}
