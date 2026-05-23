namespace ProcuLink.Transform.Parsing;

/// <summary>
/// The full output of parsing a purchase order file.
/// Header fields may be null when the file format does not include them.
/// </summary>
public record ParsedOrder(
    string? PoNumber,
    DateTime? OrderDate,
    string? BuyerName,
    string? Currency,
    IReadOnlyList<ParsedOrderLine> Lines
);
