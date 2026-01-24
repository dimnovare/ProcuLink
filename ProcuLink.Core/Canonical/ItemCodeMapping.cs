namespace ProcuLink.Core.Canonical;

/// <summary>
/// Represents a mapping from buyer item code to supplier item code
/// </summary>
public record ItemCodeMapping(string BuyerItemCode, string SupplierItemCode, DateTime CreatedAtUtc);
