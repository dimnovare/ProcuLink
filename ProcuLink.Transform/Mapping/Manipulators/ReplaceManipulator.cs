namespace ProcuLink.Transform.Mapping.Manipulators;

/// <summary>Params: [find, replacement]</summary>
public class ReplaceManipulator : IFieldManipulator
{
    private readonly string _find;
    private readonly string _with;

    public ReplaceManipulator(IReadOnlyList<string> @params)
    {
        if (@params.Count < 2)
            throw new ArgumentException("Replace requires 2 params: [find, with]", nameof(@params));
        _find = @params[0];
        _with = @params[1];
    }

    public string? Apply(string? value, IReadOnlyDictionary<string, string> row)
        => value?.Replace(_find, _with);
}
