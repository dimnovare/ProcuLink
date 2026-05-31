namespace ProcuLink.Core.Entities;

/// <summary>One structured acceptance rule inside a profile version.</summary>
public class SupplierAcceptanceRule
{
    public Guid    Id            { get; set; }
    public Guid    ProfileId     { get; set; }
    /// <summary>order | line</summary>
    public string  Scope         { get; set; } = "line";
    /// <summary>e.g. supplierItemCode, quantity, unitPrice, currency, buyerName</summary>
    public string  FieldPath     { get; set; } = string.Empty;
    /// <summary>required | equals | in | min | max</summary>
    public string  Operator      { get; set; } = "required";
    public string? ExpectedValue { get; set; }
    /// <summary>warning | error</summary>
    public string  Severity      { get; set; } = "error";
    public bool    BlockOnFail   { get; set; }

    public SupplierAcceptanceProfile Profile { get; set; } = null!;
}
