using System.Text.Json;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;

namespace ProcuLink.Api.Contracts;

public record FlowDto(Guid Id, string Name, bool FieldsConfirmed, FlowFieldList Fields, int DocumentCount, DateTime CreatedAt, string Automation = "review", bool ScansOn = false, bool FormatsOn = false, bool DocsOutputsOn = false, bool PeppolOn = false,
    int? PublishedVersion = null, bool HasChanges = false, int? NoCorrectionStreak = null, FlowSummaryDto? Summary = null, bool CanDelete = false, bool DownloadOnly = false);

/// <summary>The workspace inbox: its flow, its email address and the address's intake id, and which file kinds it reads.</summary>
public sealed record InboxDto(Guid FlowId, string? Address, Guid? IntakeId, bool ScansOn, bool FormatsOn);

/// <summary>One file a flow sends: Destination is the destination's name, null for a download-only output or a destination that is gone.</summary>
public sealed record FlowSummaryOutputDto(string Name, string Format, string? Standard, string? Destination);

/// <summary>What a flow does and what needs a person, for the flows list. NeedsYou counts needs_review plus ready.</summary>
public sealed record FlowSummaryDto(int NeedsYou, int CouldNotSend, int CouldNotRead, DateTime? LastDocumentAt,
    IReadOnlyList<string> WaysIn, IReadOnlyList<FlowSummaryOutputDto> Outputs);

public record FlowVersionDto(int VersionNo, DateTime PublishedAt, string? PublishedBy, bool Live);

public record CreateFlowRequest(string Name);

public sealed record MoveDocumentRequest(Guid FlowId);

public sealed record SetUpDocumentRequest(string? Name = null, string? CsvSeparator = null);

public sealed record DownloadDocumentRequest(string? Format, string? CsvSeparator = null);

public sealed record SetUpResultDto(Guid FlowId, string FlowName, DocumentSummaryDto Document);

/// <summary>A moved document, plus how many waiting documents like it the move took to the same flow.</summary>
public sealed record MovedDocumentDto : DocumentSummaryDto
{
    public MovedDocumentDto(DocumentSummaryDto summary) : base(summary) { }
    public int AlsoMoved { get; init; }
}

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

/// <summary>One file a sent document made. Destination is the destination's name, null when stored for download or the destination is gone.</summary>
public sealed record DocumentFileDto(Guid DeliveryId, string FileName, bool Delivered, string? Destination);

/// <summary>Where sending stopped. Destination is null when it has no name left to give (deleted, or not this workspace's).</summary>
public sealed record DocumentStopDto(Guid? DestinationId, string? Destination, string? Error);

/// <summary>The copy that makes a document's number a repeat: <paramref name="Id"/> is null when only a removed copy is left,
/// <paramref name="ReceivedAt"/> is when that copy arrived, and <paramref name="Number"/> is the number as read on this document.</summary>
public sealed record DuplicateOfDto(Guid? Id, DateTime ReceivedAt, string? Number);

/// <summary>A row of the app's paged documents list: the summary plus what only the app shows. Never returned by the file API.</summary>
public sealed record DocumentRowDto : DocumentSummaryDto
{
    public DocumentRowDto(DocumentSummaryDto summary) : base(summary) { }
    public string? Number { get; init; }
    public string? Sender { get; init; }
    public IReadOnlyList<string> Checks { get; init; } = [];
    public DateTime? SentAt { get; init; }
    public DateTime? FailedAt { get; init; }
    public DocumentStopDto? Stop { get; init; }
    public bool IsSample { get; init; }
    /// <summary>What the read says the document is, as written: trimmed, at most 80 characters.</summary>
    public string? Kind { get; init; }
    public bool InInbox { get; init; }
    /// <summary>A sent document's files that can be downloaded (delivered or stored, with a made file). Empty for any other status.</summary>
    public IReadOnlyList<DocumentFileDto> Files { get; init; } = [];
    /// <summary>For a repeated file (<c>duplicate_file</c>): when the newest earlier copy arrived.</summary>
    public DateTime? DuplicateOfAt { get; init; }
    /// <summary>For a repeated number (a failed <c>duplicate</c> check): the earlier copy.</summary>
    public DuplicateOfDto? DuplicateOf { get; init; }
    /// <summary>The flow's current answer: no file it makes goes to a destination.</summary>
    public bool DownloadOnly { get; init; }
    /// <summary>Whether the caller may remove it: an org admin always; a member only when nothing was made or sent from it.</summary>
    public bool CanRemove { get; init; }
    /// <summary>It counts toward the plan's current usage window, so removing it still leaves it counted.</summary>
    public bool CountsThisMonth { get; init; }
}

public record DocumentDto(DocumentSummaryDto Summary, string ContentType, ReadResult Read, IReadOnlyList<DocumentCheck> Checks,
    string? SenderLabel = null);

/// <summary>The app's document detail: the file API's, plus what only the app shows. Never returned by the file API.</summary>
public sealed record DocumentDetailDto : DocumentDto
{
    public DocumentDetailDto(DocumentDto detail) : base(detail) { }
    /// <summary>Whether the caller may remove it: an org admin always; a member only when nothing was made or sent from it.</summary>
    public bool CanRemove { get; init; }
    /// <summary>It counts toward the plan's current usage window, so removing it still leaves it counted.</summary>
    public bool CountsThisMonth { get; init; }
    /// <summary>Our built-in sample: the screen offers no Send for it.</summary>
    public bool IsSample { get; init; }
    /// <summary>For a repeated number (a failed <c>duplicate</c> check): the earlier copy.</summary>
    public DuplicateOfDto? DuplicateOf { get; init; }
}

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
