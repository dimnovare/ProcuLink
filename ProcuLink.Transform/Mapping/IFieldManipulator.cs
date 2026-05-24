namespace ProcuLink.Transform.Mapping;

/// <summary>
/// Applies a transformation to a field value.
/// </summary>
/// <remarks>
/// Null contract: implementations may return null when <paramref name="value"/> is null.
/// Callers should treat null and empty-string returns as "no value resolved."
/// <c>TrimManipulator</c> is an exception — it returns <see cref="string.Empty"/> for null input.
/// </remarks>
public interface IFieldManipulator
{
    string? Apply(string? value, IReadOnlyDictionary<string, string> row);
}
