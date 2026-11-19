namespace ProcuLink.Transform.Output;

/// <summary>
/// The interchange control number in an X12 (ISA13/IEA02, GS06/GE02) or EDIFACT (UNB/UNZ 0020) envelope.
/// Null keeps the legacy constant (000000001 / 1), so preview and rule checks write today's bytes.
/// </summary>
public static class InterchangeControl
{
    /// <summary>Wraps a sequence id into 1…999,999,999: never 0, fits ISA13's 9 digits.</summary>
    public static long Wrap(long id) => ((id - 1) % 999_999_999) + 1;

    /// <summary>ISA13 / IEA02 / UNB 0020 / UNZ 0020: 9 digits, zero-padded.</summary>
    public static string Padded(long? n) => (n ?? 1).ToString("D9", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>GS06 / GE02: unpadded (X12 N0, 1–9 digits).</summary>
    public static string Group(long? n) => (n ?? 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
