namespace ProcuLink.Transform.Mapping.Manipulators;

/// <summary>Params: [factor] -- multiplies the numeric value by factor. Returns integer string when result is whole.</summary>
public class MultiplyManipulator : IFieldManipulator
{
    private readonly decimal _factor;

    public MultiplyManipulator(IReadOnlyList<string> @params)
    {
        if (@params.Count != 1)
            throw new ArgumentException("Multiply requires exactly 1 param: [factor]", nameof(@params));
        if (!decimal.TryParse(@params[0], System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out _factor))
            throw new ArgumentException("Multiply param 'factor' must be a valid decimal number.", nameof(@params));
    }

    public string? Apply(string? value, IReadOnlyDictionary<string, string> row)
    {
        if (!decimal.TryParse(value, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var n))
            return value;
        var result = n * _factor;
        return result == Math.Truncate(result)
            ? ((long)result).ToString()
            : result.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
