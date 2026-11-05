using ProcuLink.Core.Documents;

namespace ProcuLink.Api.Contracts;

public record FlowDto(Guid Id, string Name, bool FieldsConfirmed, FlowFieldList Fields, int DocumentCount, DateTime CreatedAt);

public record CreateFlowRequest(string Name);

public record RenameFlowRequest(string Name);
