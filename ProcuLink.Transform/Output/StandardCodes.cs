using System.Globalization;
using System.Text.RegularExpressions;

namespace ProcuLink.Transform.Output;

/// <summary>
/// The one code table the standard order writers share: a written unit to its UN/ECE Rec 20 code (UBL, cXML, EDIFACT)
/// and its X12 355 code, and a written country to its ISO 3166 alpha-2 code.
/// </summary>
public static partial class StandardCodes
{
    /// <summary>
    /// "Each": valid in both Rec 20 and X12 355, and already the blank-unit default of the UBL, Peppol, CII and X12
    /// writers, so one default serves every format and a blank unit writes what it always did.
    /// </summary>
    public const string DefaultUnit = "EA";

    // Box and pack use the Rec 21 package codes in the X-prefixed form the combined Rec 20/21 list (and Peppol's code
    // list) carries: plain BX / PK were removed from Rec 20. Piece words map to H87 ("piece"), which is what "pcs" says.
    private static readonly (string[] Words, string Rec20, string X12)[] Table =
    [
        (["pcs", "pc", "piece", "pieces"], "H87", "PC"),
        (["ea", "each"], "EA", "EA"),
        (["unit", "units"], "C62", "UN"),
        (["kg", "kgs", "kilogram", "kilograms"], "KGM", "KG"),
        (["g", "gram", "grams"], "GRM", "GR"),
        (["lb", "lbs", "pound", "pounds"], "LBR", "LB"),
        (["l", "litre", "liter", "litres", "liters"], "LTR", "LT"),
        (["ml"], "MLT", "ML"),
        (["m", "metre", "meter", "metres", "meters"], "MTR", "MR"),
        (["cm"], "CMT", "CM"),
        (["mm"], "MMT", "MM"),
        (["m2", "sqm"], "MTK", "SM"),
        (["box", "boxes"], "XBX", "BX"),
        (["pack", "packs"], "XPK", "PK"),
        (["pair", "pairs"], "PR", "PR"),
        (["dozen"], "DZN", "DZ"),
        (["h", "hr", "hour", "hours"], "HUR", "HR"),
        (["day", "days"], "DAY", "DA"),
    ];

    private static readonly Dictionary<string, (string Rec20, string X12)> Units = BuildUnits();
    private static readonly Dictionary<string, string> Countries = BuildCountries();

    /// <summary>The Rec 20 code: the table's, else a code-shaped value as written, else <see cref="DefaultUnit"/>.</summary>
    public static string Rec20Unit(string? unit) => Unit(unit, u => u.Rec20, Rec20Shape());

    /// <summary>The X12 355 code: the table's, else a 2-character code as written, else <see cref="DefaultUnit"/>.</summary>
    public static string X12Unit(string? unit) => Unit(unit, u => u.X12, X12Shape());

    /// <summary>ISO 3166 alpha-2 (upper case) from a code, a 3-letter code, or an English or native name; null when unknown.</summary>
    public static string? Country(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Countries.TryGetValue(value.Trim(), out var iso) ? iso : null;

    private static string Unit(string? unit, Func<(string Rec20, string X12), string> pick, Regex shape)
    {
        var written = unit?.Trim() ?? "";
        if (Units.TryGetValue(written.EndsWith('.') ? written[..^1] : written, out var codes)) return pick(codes);
        return shape.IsMatch(written) ? written : DefaultUnit;
    }

    private static Dictionary<string, (string, string)> BuildUnits()
    {
        var units = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (words, rec20, x12) in Table)
            foreach (var key in words.Append(rec20).Append(x12)) units.TryAdd(key, (rec20, x12));
        return units;
    }

    private static Dictionary<string, string> BuildCountries()
    {
        // Names people write that the platform's region names may not carry (they differ by ICU version, and the
        // Linux container's ICU is not the laptop's), so they are fixed here rather than left to RegionInfo.
        var countries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["UK"] = "GB", ["Great Britain"] = "GB", ["England"] = "GB", ["Scotland"] = "GB", ["Wales"] = "GB",
            ["United States of America"] = "US", ["The Netherlands"] = "NL", ["Holland"] = "NL",
            ["Czech Republic"] = "CZ", ["Czechia"] = "CZ", ["Turkey"] = "TR", ["Türkiye"] = "TR",
            ["South Korea"] = "KR", ["Korea"] = "KR", ["Russia"] = "RU", ["Russian Federation"] = "RU",
        };
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            RegionInfo region;
            try { region = new RegionInfo(culture.Name); } catch (ArgumentException) { continue; }
            var iso = region.TwoLetterISORegionName;
            // XK (Kosovo) is a user-assigned code, not ISO 3166: a receiver would reject it, so it stays unmapped.
            if (!Alpha2().IsMatch(iso) || iso == "XK") continue;
            foreach (var key in new[] { iso, region.ThreeLetterISORegionName, region.EnglishName, region.NativeName })
                if (!string.IsNullOrWhiteSpace(key)) countries.TryAdd(key, iso);
        }
        return countries;
    }

    [GeneratedRegex("^[A-Z0-9]{2,3}$")] private static partial Regex Rec20Shape();
    [GeneratedRegex("^[A-Z0-9]{2}$")] private static partial Regex X12Shape();
    [GeneratedRegex("^[A-Z]{2}$")] private static partial Regex Alpha2();
}
