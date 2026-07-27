namespace ProcuLink.Api.Contracts;

/// <summary>HTTP request body for POST /api/orders/{id}/transform.</summary>
public record TransformRequest(string? Format);

/// <summary>HTTP request body for POST /api/orders/{id}/mark-rejected.</summary>
public record MarkRejectedRequest(string? Reason);

/// <summary>HTTP request body for POST /api/orders/{id}/assign-supplier (routing Phase 1).</summary>
/// <param name="SupplierId">The supplier to route this order to. Required — this is the operator's actual intent.</param>
/// <param name="SuggestionId">
/// Optional: the <c>order_supplier_suggestions</c> row the operator clicked, so the acceptance is
/// attributable to a specific offered candidate. Purely attribution — whether the decision is
/// recorded as <c>accepted</c> or <c>manual</c> turns on whether <see cref="SupplierId"/> was
/// among the suggestions, not on whether this was sent.
/// </param>
public record AssignSupplierRequest(Guid SupplierId, Guid? SuggestionId = null);
