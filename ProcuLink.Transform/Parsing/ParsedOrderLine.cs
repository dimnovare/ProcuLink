namespace ProcuLink.Transform.Parsing;

/// <summary>
/// A single line from a parsed purchase order file, before mapping or validation.
/// SupplierItemCode is intentionally absent — it is resolved from the mappings table,
/// not trusted from the upload file.
/// </summary>
public record ParsedOrderLine(
    int LineNumber,
    string BuyerItemCode,
    string? Description,
    decimal Quantity,
    string? Unit,
    decimal? UnitPrice
);
