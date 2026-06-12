namespace ProcuLink.Core.Services;

/// <summary>
/// Returned by <see cref="IOrderService.TransformAsync"/> on success.
/// Contains the minimum information the frontend needs to show a download link.
/// </summary>
/// <param name="Skipped">
/// True when the transform did NOT run because the order was already in flight or already
/// transformed (idempotency guard). <see cref="ArtifactId"/> then refers to the latest
/// EXISTING artifact (or <see cref="Guid.Empty"/> when none exists). Callers must not
/// enqueue delivery for a skipped response — the run that produced the artifact already did.
/// </param>
public record TransformResponse(
    Guid     ArtifactId,
    string   Format,
    DateTime CreatedAt,
    bool     Skipped = false
);
