using System.Globalization;
using System.Text;
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

    /// <summary>A unit price as written by every order writer: at least 2 and up to 6 decimals (a longer read price is rounded at the 6th).</summary>
    public static string Price(decimal price) => price.ToString("0.00####", CultureInfo.InvariantCulture);

    /// <summary>Free text split over the <paramref name="repeats"/> components a standard gives it, each up to <paramref name="max"/>, the rest cut.</summary>
    public static string[] Cut(string? text, int max, int repeats = 1) =>
        [.. (text ?? "").Chunk(max).Take(repeats).Select(c => new string(c))];

    /// <summary>An identifier as read, or refused when longer than <paramref name="max"/>: a cut identifier would send a wrong number.</summary>
    public static string Id(string? value, int max, string what) =>
        (value ?? "").Length <= max ? value ?? "" : throw new ValueTooLongException(what, max);

    /// <summary>A read Peppol ID split into its scheme and id, or null when it is not in that shape.</summary>
    public static (string Scheme, string Id)? Endpoint(string? value) =>
        PeppolId().Match(value?.Trim() ?? "") is { Success: true } m ? (m.Groups[1].Value, m.Groups[2].Value) : null;

    /// <summary>The GLN in a read Peppol ID (scheme 0088), or null: the one scheme EDIFACT and X12 have a code for.</summary>
    public static string? Gln(string? value) => Endpoint(value) is ("0088", var id) ? id : null;

    // Box and pack use the Rec 21 package codes in the X-prefixed form the combined Rec 20/21 list (and Peppol's code
    // list) carries: plain BX / PK were removed from Rec 20. Piece words map to H87 ("piece"), which is what "pcs" says.
    // Words in English, Estonian, German, Finnish, Latvian, Lithuanian and Russian. The piece row comes first, so its
    // "st" wins over the set row's X12 code ST.
    private static readonly (string[] Words, string Rec20, string X12)[] Table =
    [
        // "st" is the German St. (Stück); Latvian st. (hour) is left out.
        (["pcs", "pc", "piece", "pieces", "st", "stk", "stück", "tk", "tükk", "kpl", "gab", "vnt", "шт"], "H87", "PC"),
        (["ea", "each"], "EA", "EA"),
        (["unit", "units"], "C62", "UN"),
        (["kg", "kgs", "kilogram", "kilograms", "kilo", "кг"], "KGM", "KG"),
        (["g", "gram", "grams", "г"], "GRM", "GR"),
        (["lb", "lbs", "pound", "pounds"], "LBR", "LB"),
        (["l", "litre", "liter", "litres", "liters", "ltr", "liiter", "litra", "litrs", "litras", "л"], "LTR", "LT"),
        (["ml", "мл"], "MLT", "ML"),
        (["m", "metre", "meter", "metres", "meters", "jm", "lfm", "meeter", "metri", "metrs", "metras", "м"], "MTR", "MR"),
        (["cm", "см"], "CMT", "CM"),
        (["mm", "мм"], "MMT", "MM"),
        (["m2", "sqm", "m²", "qm", "м2", "м²"], "MTK", "SM"),
        (["m3", "m³", "cbm", "м3", "м³"], "MTQ", "CR"),
        (["box", "boxes", "karp", "kast", "laatikko", "kaste", "dėžė", "кор", "коробка"], "XBX", "BX"),
        (["pack", "packs", "pakk", "pak", "pck", "packung", "pkt", "paketti", "iepak", "уп", "упак"], "XPK", "PK"),
        (["pair", "pairs", "paar", "pari", "pāris", "pora", "пар", "пара"], "PR", "PR"),
        (["dozen"], "DZN", "DZ"),
        (["h", "hr", "hour", "hours", "tund", "std", "stunde", "tunti", "stunda", "val", "ч", "час"], "HUR", "HR"),
        (["day", "days", "päev", "tag", "tage", "pv", "päivä", "diena", "день", "дн"], "DAY", "DA"),
        (["set", "sets", "kmpl", "kompl", "komplekt", "satz", "sarja", "komplekts", "komplektas", "компл", "комплект"], "SET", "ST"),
        (["roll", "rolls", "rull", "rolle", "rulla", "rullis", "ritinys", "рул", "рулон"], "XRO", "RL"),
    ];

    private static readonly Dictionary<string, (string Rec20, string X12)> Units = BuildUnits();

    /// <summary>The Rec 20 code: the table's, else a code-shaped value as written, else <see cref="DefaultUnit"/>.</summary>
    public static string Rec20Unit(string? unit) => Unit(unit, u => u.Rec20, Rec20Shape());

    /// <summary>The X12 355 code: the table's, else a 2-character code as written, else <see cref="DefaultUnit"/>.</summary>
    public static string X12Unit(string? unit) => Unit(unit, u => u.X12, X12Shape());

    /// <summary>ISO 3166 alpha-2 (upper case) from a code, a 3-letter code, or an English or native name; null when unknown.</summary>
    public static string? Country(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Countries.TryGetValue(Fold(value), out var iso) ? iso : null;

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

    // Names fixed here rather than left to RegionInfo, whose names differ by ICU version (the Linux container's ICU is not the
    // laptop's): every EU/EEA country, Switzerland and the UK, in English and in its own languages, and names people write
    // that no region carries. Greece's EU VAT prefix EL is not ISO, so it is mapped to GR like any other alias.
    private static readonly (string Iso, string[] Names)[] Named =
    [
        ("AT", ["Austria", "Österreich"]), ("BE", ["Belgium", "België", "Belgique", "Belgien"]), ("BG", ["Bulgaria", "България"]),
        ("HR", ["Croatia", "Hrvatska"]), ("CY", ["Cyprus", "Κύπρος"]), ("CZ", ["Czechia", "Czech Republic", "Česko", "Česká republika"]),
        ("DK", ["Denmark", "Danmark"]), ("EE", ["Estonia", "Eesti"]), ("FI", ["Finland", "Suomi"]), ("FR", ["France"]),
        ("DE", ["Germany", "Deutschland"]), ("GR", ["Greece", "Ελλάδα", "Hellas", "EL"]), ("HU", ["Hungary", "Magyarország"]),
        ("IE", ["Ireland", "Éire"]), ("IT", ["Italy", "Italia"]), ("LV", ["Latvia", "Latvija"]), ("LT", ["Lithuania", "Lietuva"]),
        ("LU", ["Luxembourg", "Lëtzebuerg", "Luxemburg"]), ("MT", ["Malta"]), ("NL", ["Netherlands", "The Netherlands", "Nederland", "Holland"]),
        ("PL", ["Poland", "Polska"]), ("PT", ["Portugal"]), ("RO", ["Romania"]), ("SK", ["Slovakia", "Slovensko"]),
        ("SI", ["Slovenia", "Slovenija"]), ("ES", ["Spain", "España"]), ("SE", ["Sweden", "Sverige"]), ("IS", ["Iceland", "Ísland"]),
        ("LI", ["Liechtenstein"]), ("NO", ["Norway", "Norge", "Noreg"]), ("CH", ["Switzerland", "Schweiz", "Suisse", "Svizzera"]),
        ("GB", ["United Kingdom", "UK", "Great Britain", "England", "Scotland", "Wales"]),
        ("US", ["United States", "United States of America", "USA"]),
        ("TR", ["Turkey", "Türkiye"]), ("KR", ["South Korea", "Korea"]), ("RU", ["Russia", "Russian Federation"]),
    ];

    // After Named: static fields initialise in order.
    private static readonly Dictionary<string, string> Countries = BuildCountries();

    // Case and accents are ignored on both sides, so "POLSKA", "Osterreich" and "ΕΛΛΑΔΑ" resolve too.
    private static string Fold(string s) => string.Concat(s.Trim().Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark));

    private static Dictionary<string, string> BuildCountries()
    {
        var countries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (iso, names) in Named)
            foreach (var key in names.Prepend(iso)) countries.TryAdd(Fold(key), iso);
        // The rest of the world, and 3-letter codes, from the platform's regions.
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            RegionInfo region;
            try { region = new RegionInfo(culture.Name); } catch (ArgumentException) { continue; }
            var iso = region.TwoLetterISORegionName;
            // XK (Kosovo) is a user-assigned code, not ISO 3166: a receiver would reject it, so it stays unmapped.
            if (!Alpha2().IsMatch(iso) || iso == "XK") continue;
            foreach (var key in new[] { iso, region.ThreeLetterISORegionName, region.EnglishName, region.NativeName })
                if (!string.IsNullOrWhiteSpace(key)) countries.TryAdd(Fold(key), iso);
        }
        return countries;
    }

    [GeneratedRegex("^[A-Z0-9]{2,3}$")] private static partial Regex Rec20Shape();
    [GeneratedRegex("^[A-Z0-9]{2}$")] private static partial Regex X12Shape();
    [GeneratedRegex(@"^(\d{4}):(\S+)$")] private static partial Regex PeppolId();
    [GeneratedRegex("^[A-Z]{2}$")] private static partial Regex Alpha2();
}

/// <summary>An identifier too long for its standard, with a character it can't carry, or a country it can't code; the message is approved copy, shown after "The file … couldn't be made: ".</summary>
public sealed class ValueTooLongException(string message) : Exception(message)
{
    public ValueTooLongException(string what, int max) : this($"The {what} is longer than this standard allows ({max} characters).") { }

    /// <summary>An identifier with a character the standard can't carry: it is refused, never changed.</summary>
    public static ValueTooLongException NotCarried(string what) => new($"The {what} has a character this standard can't carry.");

    /// <summary>A country that is neither two letters nor a name we know: it is refused, never written as read.</summary>
    public static ValueTooLongException UnknownCountry(string party, string value) =>
        new($"We couldn't tell which country \"{value}\" is. Write the {party}'s country as a two-letter code, like DE.");
}
