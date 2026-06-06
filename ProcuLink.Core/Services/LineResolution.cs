namespace ProcuLink.Core.Services;

/// <summary>
/// A single buyer→supplier code resolution submitted by the user in the review UI.
/// </summary>
public record LineResolution(int LineNumber, string SupplierItemCode);

/// <summary>
/// Optional header-field corrections submitted alongside line resolutions from the
/// review screen. Every field is nullable and means "no change" when null —
/// each is applied independently. PO number and supplier are intentionally absent
/// (they stay read-only).
/// </summary>
/// <param name="OrderDate">
/// Corrected order date, or null for no change. The controller validates/parses the
/// raw ISO string and passes a parsed <see cref="DateOnly"/> through.
/// </param>
/// <param name="BuyerName">
/// Corrected, already-trimmed buyer name, or null for no change. The controller
/// treats whitespace-only input as no-change (passes null).
/// </param>
/// <param name="Currency">
/// Corrected currency as a validated, upper-cased 3-letter alpha code, or null for
/// no change.
/// </param>
public record ResolveHeaderFields(
    DateOnly? OrderDate = null,
    string?   BuyerName = null,
    string?   Currency  = null)
{
    /// <summary>True when at least one header field carries a change.</summary>
    public bool HasAnyChange => OrderDate.HasValue
        || !string.IsNullOrWhiteSpace(BuyerName)
        || !string.IsNullOrWhiteSpace(Currency);
}
