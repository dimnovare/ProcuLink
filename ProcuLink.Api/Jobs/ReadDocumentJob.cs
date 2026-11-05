using System.Text.Json;
using Hangfire;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Documents;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Api.Jobs;

/// <summary>Hangfire job: reads a received flow document. Idempotent — a document past <c>reading</c> is skipped.</summary>
public class ReadDocumentJob(
    ProcuLinkDbContext db, IFileStorageService storage, IDocumentReader reader,
    DocumentRechecker rechecker, ILogger<ReadDocumentJob> logger)
{
    private const int MaxChars = 60_000;
    private const int MinWordsPerTextPage = 5;
    private const int LastRetry = 3;

    [Queue("critical")]
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 5, 30, 120 })]
    public async Task ExecuteAsync(Guid documentId, Guid organisationId, PerformContext? context, CancellationToken ct)
    {
        var doc = await db.FlowDocuments.FirstOrDefaultAsync(d => d.Id == documentId && d.OrgId == organisationId, ct);
        if (doc is null || (doc.Status != DocumentStatus.Received && doc.Status != DocumentStatus.Reading)) return;

        doc.Status = DocumentStatus.Reading;
        doc.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        try
        {
            await ReadAsync(doc, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ReadDocumentJob failed for document {DocumentId}", documentId);
            if (context is null || context.GetJobParameter<int>("RetryCount") >= LastRetry)
                await FailAsync(doc, "ai_error", ct);
            throw;
        }
    }

    private async Task ReadAsync(FlowDocument doc, CancellationToken ct)
    {
        var flow = await db.Flows.FirstAsync(f => f.Id == doc.FlowId && f.OrgId == doc.OrgId, ct);
        var isPdf = Path.GetExtension(doc.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<PageWord> words = [];
        IReadOnlyList<SheetCell> cells = [];
        string text;

        await using (var stream = await storage.DownloadAsync(doc.SourceFileKey, ct))
        {
            if (isPdf)
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct);
                var pdf = PdfWordExtractor.Extract(buffer.ToArray());
                if (!pdf.Words.GroupBy(w => w.Page).Any(g => g.Count() >= MinWordsPerTextPage))
                {
                    await FailAsync(doc, "scanned_not_supported", ct);
                    return;
                }
                (words, text) = (pdf.Words, pdf.Text);
            }
            else
            {
                cells = SheetCellReader.Read(stream, Path.GetExtension(doc.FileName));
                text = SheetCellReader.ToText(cells);
            }
        }

        var truncated = text.Length > MaxChars;
        if (truncated) text = text[..MaxChars];

        var expected = flow.FieldsConfirmed ? DocumentJson.Deserialize<FlowFieldList>(flow.FieldsJson) : null;
        var outcome = await reader.ReadAsync(new DocumentReadInput(doc.FileName, doc.ContentType, text, expected), doc.OrgId, ct);
        if (!outcome.Success || outcome.Result is null)
        {
            await FailAsync(doc, outcome.FailureReason ?? "ai_error", ct);
            return;
        }

        (SourceLocation? Location, bool NotOnPage) Locate(string? value)
        {
            var loc = isPdf ? ValueLocator.Locate(value, words) : ValueLocator.LocateInCells(value, cells);
            return (loc, isPdf && value is not null && loc is null);
        }

        var first = outcome.Result;
        var read = new ReadResult(first.KindLabel,
            first.Fields.Select(f =>
            {
                var (loc, miss) = Locate(f.Value);
                return f with { Location = loc, NotOnPage = miss };
            }).ToList(),
            first.Tables.Select(t => t with
            {
                Rows = t.Rows.Select(r => new ReadRow(r.Cells.ToDictionary(c => c.Key, c =>
                {
                    var (loc, miss) = Locate(c.Value.Value);
                    return c.Value with { Location = loc, NotOnPage = miss };
                }))).ToList(),
            }).ToList(),
            truncated);

        await rechecker.ApplyAsync(doc, flow, read, ct);
        doc.ReadJson = DocumentJson.Serialize(read);
        doc.ReadAt = DateTime.UtcNow;
        AddAudit(doc, "document.read", new
        {
            status = doc.Status,
            failedChecks = DocumentJson.Deserialize<List<DocumentCheck>>(doc.ChecksJson).Select(c => c.Code).ToList(),
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task FailAsync(FlowDocument doc, string reason, CancellationToken ct)
    {
        doc.Status = DocumentStatus.ReadFailed;
        doc.StatusReason = reason;
        doc.UpdatedAt = DateTime.UtcNow;
        AddAudit(doc, "document.read_failed", new { reason });
        await db.SaveChangesAsync(ct);
    }

    private void AddAudit(FlowDocument doc, string action, object payload) =>
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(), OrgId = doc.OrgId, EntityType = "FlowDocument", EntityId = doc.Id,
            Action = action, CreatedAt = DateTime.UtcNow, Payload = JsonSerializer.SerializeToDocument(payload),
        });

    public static void Enqueue(IBackgroundJobClient jobs, Guid documentId, Guid organisationId) =>
        jobs.Enqueue<ReadDocumentJob>(j => j.ExecuteAsync(documentId, organisationId, null, CancellationToken.None));
}
