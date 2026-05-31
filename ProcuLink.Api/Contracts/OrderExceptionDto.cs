namespace ProcuLink.Api.Contracts;

public record OrderExceptionDto(
    Guid     Id,
    Guid     OrderId,
    Guid?    LineId,
    string   Stage,
    string   Code,
    string   Severity,
    string   State,
    string   Message,
    DateTime CreatedAt,
    DateTime? ResolvedAt
);
