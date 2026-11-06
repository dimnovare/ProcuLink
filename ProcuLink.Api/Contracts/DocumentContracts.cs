using System.Text.Json;
using ProcuLink.Core.Documents;

namespace ProcuLink.Api.Contracts;

public record FlowDto(Guid Id, string Name, bool FieldsConfirmed, FlowFieldList Fields, int DocumentCount, DateTime CreatedAt, string Automation = "review");

public record CreateFlowRequest(string Name);

public record RenameFlowRequest(string? Name = null, string? Automation = null);

public record DocumentSummaryDto(
    Guid Id, Guid FlowId, string FlowName, string FileName, string Status, string? StatusReason,
    int? PageCount, int BillingUnits, int FailedChecks, DateTime CreatedAt);

public record DocumentDto(DocumentSummaryDto Summary, string ContentType, ReadResult Read, IReadOnlyList<DocumentCheck> Checks);

public record FieldEdit(string Key, string? Value);

/// <summary><paramref name="Row"/> is a 0-based index.</summary>
public record CellEdit(string Table, int Row, string Column, string? Value);

public record RowRef(string Table, int Row);

public record EditValuesRequest(
    IReadOnlyList<FieldEdit>? Fields, IReadOnlyList<CellEdit>? Cells,
    IReadOnlyList<string>? AddRowToTables, IReadOnlyList<RowRef>? RemoveRows);

public record HistoryEntryDto(string Action, DateTime At, string? UserId, JsonElement Payload);
