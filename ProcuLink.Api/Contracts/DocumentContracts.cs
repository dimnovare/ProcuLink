using System.Text.Json;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;

namespace ProcuLink.Api.Contracts;

public record FlowDto(Guid Id, string Name, bool FieldsConfirmed, FlowFieldList Fields, int DocumentCount, DateTime CreatedAt, string Automation = "review", bool ScansOn = false, bool FormatsOn = false, bool DocsOutputsOn = false, bool PeppolOn = false,
    int? PublishedVersion = null, bool HasChanges = false, int? NoCorrectionStreak = null);

public record FlowVersionDto(int VersionNo, DateTime PublishedAt, string? PublishedBy, bool Live);

public record CreateFlowRequest(string Name);

public record RenameFlowRequest(string? Name = null, string? Automation = null);

public record DocumentSummaryDto(
    Guid Id, Guid FlowId, string FlowName, string FileName, string Status, string? StatusReason,
    int? PageCount, int BillingUnits, int FailedChecks, DateTime CreatedAt,
    string Source = "upload", string? SourceRef = null, int? FlowVersion = null)
{
    public static DocumentSummaryDto From(FlowDocument d, string flowName) =>
        new(d.Id, d.FlowId, flowName, d.FileName, d.Status, d.StatusReason, d.PageCount, d.BillingUnits,
            DocumentJson.Deserialize<List<DocumentCheck>>(d.ChecksJson).Count, d.CreatedAt, d.Source, d.SourceRef, d.FlowVersion);
}

public record DocumentDto(DocumentSummaryDto Summary, string ContentType, ReadResult Read, IReadOnlyList<DocumentCheck> Checks,
    string? SenderLabel = null);

/// <summary>What the file API returns for a document: the read result and the sends made from it.</summary>
public record FlowFileResultDto(DocumentDto Document, IReadOnlyList<object> Deliveries);

public record FieldEdit(string Key, string? Value);

/// <summary><paramref name="Row"/> is a 0-based index.</summary>
public record CellEdit(string Table, int Row, string Column, string? Value);

public record RowRef(string Table, int Row);

public record EditValuesRequest(
    IReadOnlyList<FieldEdit>? Fields, IReadOnlyList<CellEdit>? Cells,
    IReadOnlyList<string>? AddRowToTables, IReadOnlyList<RowRef>? RemoveRows);

/// <summary>One of <paramref name="Key"/> (a flow field) or <paramref name="NewLabel"/>, and one of <paramref name="Page"/> + <paramref name="Box"/> or <paramref name="Address"/>.</summary>
public record TeachRequest(string? Key, string? NewLabel, int? Page, Box? Box, string? Address, string? Value);

public record HistoryEntryDto(string Action, DateTime At, string? UserId, JsonElement Payload);
