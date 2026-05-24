namespace ProcuLink.Transform.Mapping.Manipulators;

/// <summary>Params: [divisor] -- divides the numeric value by divisor. Returns integer string when result is whole.</summary>
public class DivideManipulator : IFieldManipulator
{
    private readonly decimal _divisor;

    public DivideManipulator(IReadOnlyList<string> @params)
    {
        if (@params.Count != 1)
            throw new ArgumentException("Divide requires exactly 1 param: [divisor]", nameof(@params));
        if (!decimal.TryParse(@params[0], System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out _divisor))
            throw new ArgumentException("Divide param 'divisor' must be a valid decimal number.", nameof(@params));
    }

    public string? Apply(string? value, IReadOnlyDictionary<string, string> row)
    {
        if (_divisor == 0) return value;
        if (!decimal.TryParse(value, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var n))
            return value;
        var result = n / _divisor;
        return result == Math.Truncate(result)
            ? ((long)result).ToString()
            : result.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
