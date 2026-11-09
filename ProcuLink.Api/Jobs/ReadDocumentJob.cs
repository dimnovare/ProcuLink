using System.Text.Json;
using Hangfire;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ocr;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services.Ocr;
using ProcuLink.Transform.Documents;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Api.Jobs;

/// <summary>Hangfire job: reads a received flow document. Idempotent — a document past <c>reading</c> is skipped.</summary>
public class ReadDocumentJob(
    ProcuLinkDbContext db, IFileStorageService storage, IDocumentReader reader,
    DocumentRechecker rechecker, IBackgroundJobClient jobs, IPdfRasterizer rasterizer, IDocumentOcrService ocr,
    DocumentEngineGate gate, ProcuLink.Transform.Tokenizing.ISourceTokenizer tokenizer, ILogger<ReadDocumentJob> logger)
{
    private const int MaxChars = 60_000;
    private const int MinWordsPerTextPage = 5;
    private const int MaxScanPages = 10;
    private const int LastRetry = 3;

    [Queue("critical")]
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 5, 30, 120 })]
    public async Task ExecuteAsync(Guid documentId, Guid organisationId, PerformContext? context, CancellationToken ct)
    {
        var doc = await db.FlowDocuments.FirstOrDefaultAsync(d => d.Id == documentId && d.OrgId == organisationId, ct);
        if (doc is null || (doc.Status != DocumentStatus.Received && doc.Status != DocumentStatus.Reading)) return;

        try
        {
            doc.Status = DocumentStatus.Reading;
            doc.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
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
            {
                // Drop whatever the failed attempt tracked (ReadAt, ReadJson, the read audit row): an unread document is not billed.
                db.ChangeTracker.Clear();
                var failed = await db.FlowDocuments.FirstAsync(d => d.Id == documentId && d.OrgId == organisationId, ct);
                await FailAsync(failed, "ai_error", ct);
            }
            throw;
        }
    }

    private async Task ReadAsync(FlowDocument doc, CancellationToken ct)
    {
        var flow = await db.Flows.FirstAsync(f => f.Id == doc.FlowId && f.OrgId == doc.OrgId, ct);
        var isPdf = Path.GetExtension(doc.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        var isImage = DocumentFormats.IsImage(doc.FileName);
        var pdfPages = 0;
        IReadOnlyList<PageWord> words = [];
        IReadOnlyList<SheetCell> cells = [];
        IReadOnlyList<TextSpan>? spans = null;
        var text = "";

        byte[] bytes;
        await using (var stream = await storage.DownloadAsync(doc.SourceFileKey, ct))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }

        try
        {
            if (isPdf)
            {
                var pdf = PdfWordExtractor.Extract(bytes);
                (words, text, pdfPages) = (pdf.Words, pdf.Text, pdf.PageCount);
            }
            else if (DocumentFormats.IsMore(doc.FileName))
            {
                (text, spans) = await DocumentText.ReadAsync(doc.FileName, bytes, tokenizer, ct);
            }
            else if (!isImage)
            {
                cells = SheetCellReader.Read(new MemoryStream(bytes), Path.GetExtension(doc.FileName));
                text = SheetCellReader.ToText(cells, MaxChars);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A file we cannot open will not open on a retry either.
            logger.LogWarning(e, "Document {DocumentId} could not be opened for reading.", doc.Id);
            await FailAsync(doc, "unreadable_file", ct);
            return;
        }

        IReadOnlyList<byte[]>? pages = null;
        var truncated = false;
        if (isImage || (isPdf && !words.GroupBy(w => w.Page).Any(g => g.Count() >= MinWordsPerTextPage)))
        {
            if (!gate.ScansOn(doc.OrgId))
            {
                await FailAsync(doc, "scanned_not_supported", ct);
                return;
            }
            var upright = isImage ? ScanImages.Upright(bytes) : null;
            pages = isImage ? (upright is null ? [] : [upright]) : rasterizer.RenderPagesPng(bytes, MaxScanPages).Select(ScanImages.Upright).OfType<byte[]>().ToList();
            if (pages.Count == 0)
            {
                await FailAsync(doc, "unreadable_file", ct);
                return;
            }
            truncated = pdfPages > pages.Count;
            // Bill the pages we read, not the pages uploaded (intake billed one unit per ten).
            doc.BillingUnits = Math.Max(1, (int)Math.Ceiling(pages.Count / 10.0));
            words = await ocr.ReadWordsAsync(pages, ct);
        }

        if (text.Length > MaxChars)
        {
            truncated = true;
            text = text[..MaxChars];
        }

        var expected = flow.FieldsConfirmed ? DocumentJson.Deserialize<FlowFieldList>(flow.FieldsJson) : null;
        var outcome = await reader.ReadAsync(new DocumentReadInput(doc.FileName, doc.ContentType, text, expected, pages), doc.OrgId, ct);
        if (!outcome.Success || outcome.Result is null)
        {
            var reason = outcome.FailureReason ?? "ai_error";
            // ai_error may be a blip: throw so Hangfire retries. The other reasons repeat for this document, so they are final.
            if (reason == "ai_error") throw new InvalidOperationException("The document reader failed (ai_error).");
            await FailAsync(doc, reason, ct);
            return;
        }

        var first = outcome.Result;
        // OCR words but nothing located (sideways photo, non-Latin scan, noise): flagging every value would be noise.
        var anyLocated = first.Fields.Select(f => f.Value)
            .Concat(first.Tables.SelectMany(t => t.Rows.SelectMany(r => r.Cells.Values.Select(c => c.Value))))
            .Any(v => ValueLocator.Locate(v, words) is not null);

        (SourceLocation? Location, bool NotOnPage) Locate(string? value)
        {
            var onWords = isPdf || isImage;
            var loc = onWords ? ValueLocator.Locate(value, words)
                : spans is not null ? ValueLocator.LocateInSpans(value, spans) : ValueLocator.LocateInCells(value, cells);
            return (loc, onWords && anyLocated && !string.IsNullOrWhiteSpace(value) && loc is null);
        }

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
        SendDocumentJob.EnqueueIfAutomatic(jobs, doc, flow);
        // Committed: a shutdown must not skip the announcement or throw.
        await FlowWebhook.EnqueueAsync(db, jobs, doc, FlowWebhookEvent.Read, logger, CancellationToken.None);
    }

    private async Task FailAsync(FlowDocument doc, string reason, CancellationToken ct)
    {
        doc.Status = DocumentStatus.ReadFailed;
        doc.StatusReason = reason;
        doc.UpdatedAt = DateTime.UtcNow;
        AddAudit(doc, "document.read_failed", new { reason });
        await db.SaveChangesAsync(ct);
        await FlowWebhook.EnqueueAsync(db, jobs, doc, FlowWebhookEvent.ReadFailed, logger, CancellationToken.None);
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
