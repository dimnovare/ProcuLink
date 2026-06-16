using System.Globalization;

namespace ProcuLink.Transform.Mapping.Manipulators;

/// <summary>
/// Formats a numeric value with a .NET numeric format string and an optional culture, so the output
/// designer can offer plain "Number" / "Currency" presets without hand-written Scriban. The input is
/// parsed as a culture-invariant decimal (the machine value the row bag always carries), then rendered
/// with <c>ToString(format, culture)</c>.
///
/// <para>Params: <c>[format]</c> or <c>[format, culture]</c>.</para>
/// <list type="bullet">
///   <item><c>["N2"]</c> → <c>1,234.50</c> (invariant grouping, 2 decimals)</item>
///   <item><c>["N2","de-DE"]</c> → <c>1.234,50</c> (EU grouping)</item>
///   <item><c>["C2","en-US"]</c> → <c>$1,234.50</c></item>
///   <item><c>["0.00"]</c> → <c>1234.50</c> (no grouping)</item>
/// </list>
///
/// <para>Non-numeric input is returned UNCHANGED (never throws — same contract as
/// <see cref="DateFormatManipulator"/>): a blank or already-formatted value passes through, so an
/// authoring mistake degrades gracefully instead of corrupting the document.</para>
/// </summary>
public class NumberFormatManipulator : IFieldManipulator
{
    private readonly string _format;
    private readonly CultureInfo _culture;

    public NumberFormatManipulator(IReadOnlyList<string> @params)
    {
        if (@params.Count is < 1 or > 2)
            throw new ArgumentException(
                "NumberFormat requires 1 or 2 params: [format] or [format, culture].", nameof(@params));

        _format = @params[0];
        _culture = @params.Count == 2 && !string.IsNullOrWhiteSpace(@params[1])
            ? ResolveCulture(@params[1])
            : CultureInfo.InvariantCulture;
    }

    public string? Apply(string? value, IReadOnlyDictionary<string, string> row)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;

        // Parse the incoming value as the invariant machine decimal the row bag carries.
        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var n)
            ? n.ToString(_format, _culture)
            : value; // not a number → leave it untouched
    }

    private static CultureInfo ResolveCulture(string name)
    {
        try { return CultureInfo.GetCultureInfo(name); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }
}
