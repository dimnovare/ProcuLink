namespace ProcuLink.Api.Contracts;

/// <summary>Group V4 — a reusable rule definition (template) in the org's catalog.</summary>
public record RuleDefinitionDto(
    Guid    Id,
    string  Code,
    string  Title,
    string? Description,
    string  Scope,
    string  FieldPath,
    string  Operator,
    string  DefaultSeverity,
    string? DefaultExpectedValue,
    string? ParamHint,
    string? UblRef,
    string? EdifactRef,
    string? X12Ref,
    string? CxmlRef,
    bool    IsSystem,
    DateTime CreatedAt);

/// <summary>
/// Group V4 — a supplier's executable acceptance rule (the per-binding values the executor reads)
/// joined to the reusable definition it binds to. <see cref="Definition"/> is null for an unlinked
/// legacy rule.
/// </summary>
public record SupplierRuleBindingDto(
    Guid    RuleId,
    Guid?   RuleDefinitionId,
    string? RuleCode,
    string  Scope,
    string  FieldPath,
    string  Operator,
    string? ExpectedValue,
    string  Severity,
    bool    BlockOnFail,
    RuleDefinitionDto? Definition);
