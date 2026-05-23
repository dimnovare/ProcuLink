namespace ProcuLink.Core.Services;

/// <summary>
/// A single buyer→supplier code resolution submitted by the user in the review UI.
/// </summary>
public record LineResolution(int LineNumber, string SupplierItemCode);
