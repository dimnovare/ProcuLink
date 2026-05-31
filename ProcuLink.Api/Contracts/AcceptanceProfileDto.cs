namespace ProcuLink.Api.Contracts;

public record AcceptanceRuleDto(
    Guid?   Id, string Scope, string FieldPath, string Operator,
    string? ExpectedValue, string Severity, bool BlockOnFail);

public record AcceptanceProfileDto(
    Guid    Id, int VersionNo, string Status,
    string? Protocol, string? OutputFormat,
    DateTime? EffectiveFrom, DateTime? EffectiveTo,
    DateTime CreatedAt,
    IReadOnlyList<AcceptanceRuleDto> Rules);

public record CreateAcceptanceProfileRequest(
    string? Protocol, string? OutputFormat,
    IReadOnlyList<AcceptanceRuleDto> Rules);

public record OrderValidationResultDto(
    int?    LineNumber, string Severity, string Status,
    string  Code, string Message);
