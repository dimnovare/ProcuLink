using ProcuLink.Core.Documents;

namespace ProcuLink.Api.Contracts;

public record FlowDto(Guid Id, string Name, bool FieldsConfirmed, FlowFieldList Fields, int DocumentCount, DateTime CreatedAt);

public record CreateFlowRequest(string Name);

public record RenameFlowRequest(string Name);

public record DocumentSummaryDto(
    Guid Id, Guid FlowId, string FlowName, string FileName, string Status, string? StatusReason,
    int? PageCount, int BillingUnits, int FailedChecks, DateTime CreatedAt);
