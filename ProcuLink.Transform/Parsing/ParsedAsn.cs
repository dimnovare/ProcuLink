namespace ProcuLink.Transform.Parsing;

public sealed record ParsedAsn(
    string ShipmentId,
    DateOnly DespatchDate,
    DateOnly? EstimatedDeliveryDate,
    string? BuyerOrderRef,
    string? SupplierRef,
    IReadOnlyList<ParsedAsnPackage> Packages);

public sealed record ParsedAsnPackage(
    string PackageId,
    string? Sscc,
    IReadOnlyList<ParsedAsnLine> Lines);

public sealed record ParsedAsnLine(
    string? BuyerItemCode,
    string? SupplierItemCode,
    decimal Quantity,
    string UnitCode);
