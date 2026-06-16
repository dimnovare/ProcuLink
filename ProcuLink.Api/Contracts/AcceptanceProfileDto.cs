namespace ProcuLink.Api.Contracts;

public record AcceptanceRuleDto(
    Guid?   Id, string Scope, string FieldPath, string Operator,
    string? ExpectedValue, string Severity, bool BlockOnFail);

public record AcceptanceProfileDto(
    Guid    Id, Guid SupplierId, int VersionNo, string Status,
    string? Protocol, string? OutputFormat,
    DateTime? EffectiveFrom, DateTime? EffectiveTo,
    DateTime CreatedAt,
    IReadOnlyList<AcceptanceRuleDto> Rules);

public record CreateAcceptanceProfileRequest(
    string? Protocol, string? OutputFormat,
    IReadOnlyList<AcceptanceRuleDto>? Rules);

public record OrderValidationResultDto(
    int?    LineNumber, string Severity, string Status,
    string  Code, string Message,
    // Plain-language short title from the rule catalog (null for invariants / ad-hoc rules); the UI
    // shows it as the issue headline, with Message as the explanation + suggested fix.
    string? Title = null);
