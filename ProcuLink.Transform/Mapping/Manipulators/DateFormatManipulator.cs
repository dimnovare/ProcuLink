namespace ProcuLink.Transform.Mapping.Manipulators;

/// <summary>Params: [inputFormat, outputFormat]</summary>
public class DateFormatManipulator : IFieldManipulator
{
    private readonly string _inputFormat;
    private readonly string _outputFormat;

    public DateFormatManipulator(IReadOnlyList<string> @params)
    {
        if (@params.Count != 2)
            throw new ArgumentException("DateFormat requires exactly 2 params: [inputFormat, outputFormat]", nameof(@params));
        _inputFormat = @params[0];
        _outputFormat = @params[1];
    }

    public string? Apply(string? value, IReadOnlyDictionary<string, string> row)
    {
        if (value is null) return null;
        return DateTime.TryParseExact(value, _inputFormat,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dt)
            ? dt.ToString(_outputFormat, System.Globalization.CultureInfo.InvariantCulture)
            : value;
    }
}
