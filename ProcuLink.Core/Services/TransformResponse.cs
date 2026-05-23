namespace ProcuLink.Core.Services;

/// <summary>
/// Returned by <see cref="IOrderService.TransformAsync"/> on success.
/// Contains the minimum information the frontend needs to show a download link.
/// </summary>
public record TransformResponse(
    Guid     ArtifactId,
    string   Format,
    DateTime CreatedAt
);
