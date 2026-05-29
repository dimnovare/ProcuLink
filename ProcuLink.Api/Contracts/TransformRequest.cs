namespace ProcuLink.Api.Contracts;

/// <summary>HTTP request body for POST /api/orders/{id}/transform.</summary>
public record TransformRequest(string? Format);

/// <summary>HTTP request body for POST /api/orders/{id}/mark-rejected.</summary>
public record MarkRejectedRequest(string? Reason);
