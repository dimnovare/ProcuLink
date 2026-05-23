namespace ProcuLink.Api.Contracts;

/// <summary>HTTP request body for POST /api/orders/{id}/transform.</summary>
public record TransformRequest(string? Format);
