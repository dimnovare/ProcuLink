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
    private const int MaxHints = 40;
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
        IReadOnlyList<string> pageTexts = [];
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
                (words, text, pdfPages, pageTexts) = (pdf.Words, pdf.Text, pdf.PageCount, pdf.PageTexts);
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
            words = await ocr.ReadWordsAsync(pages, ct);
        }

        // A PDF with text is read ten pages per part, up to ReadLimits.MaxPages; a window with no word is skipped.
        var textPdf = isPdf && pages is null;
        var parts = new List<(string Text, IReadOnlyList<PageWord> Words)>();
        if (textPdf)
        {
            truncated = pdfPages > ReadLimits.MaxPages;
            var last = Math.Min(pdfPages, ReadLimits.MaxPages);
            for (var from = 1; from <= last; from += ReadLimits.PagesPerUnit)
            {
                var to = Math.Min(from + ReadLimits.PagesPerUnit - 1, last);
                var partWords = words.Where(w => w.Page >= from && w.Page <= to).ToList();
                if (partWords.Count == 0) continue;
                var partText = string.Concat(pageTexts.Skip(from - 1).Take(to - from + 1));
                if (partText.Length > MaxChars) (truncated, partText) = (true, partText[..MaxChars]);
                parts.Add((partText, partWords));
            }
        }

        if (text.Length > MaxChars)
        {
            truncated |= !textPdf;
            text = text[..MaxChars];
        }
        if (!textPdf) parts.Add((text, words));
        if (parts.Count == 0)
        {
            await FailAsync(doc, "empty_document", ct);
            return;
        }

        var config = await FlowConfigLoader.CurrentAsync(db, flow, ct);
        var expected = config.FieldsConfirmed ? config.Fields : null;
        var keys = expected?.Fields.Select(f => f.Key).ToHashSet();
        var hints = keys is null ? [] : DocumentJson.Deserialize<List<FieldHint>>(flow.HintsJson).Where(h => keys.Contains(h.FieldKey) && SenderIdentity.Matches(h.SenderKey, text, doc.Source, doc.SourceRef)).OrderByDescending(h => h.TaughtAt).Take(MaxHints).ToList();
        var onWords = isPdf || isImage;
        SourceLocation? Find(string? value, IReadOnlyList<PageWord> partWords) => onWords ? ValueLocator.Locate(value, partWords)
            : spans is not null ? ValueLocator.LocateInSpans(value, spans) : ValueLocator.LocateInCells(value, cells);

        var results = new List<ReadResult>();
        foreach (var (partText, partWords) in parts)
        {
            var part = results.Count + 1;
            var partExpected = expected ?? (results.Count == 0 ? null : ReadMerge.KeysOf(ReadMerge.Merge(results, null)));
            var outcome = await reader.ReadAsync(new DocumentReadInput(doc.FileName, doc.ContentType, partText, partExpected, pages,
                hints.Count > 0 ? hints : null, part, parts.Count), doc.OrgId, ct);
            if (outcome.Usage is { } usage)
            {
                // The cost row and the heartbeat in one save; an answered call is recorded even when the job is stopping.
                var now = DateTime.UtcNow;
                db.Set<FlowReadCall>().Add(new FlowReadCall
                {
                    Id = Guid.NewGuid(), OrgId = doc.OrgId, DocumentId = doc.Id, Part = part, Model = usage.Model,
                    TokensIn = usage.TokensIn, TokensOut = usage.TokensOut, CreatedAt = now,
                });
                doc.UpdatedAt = now;
                await db.SaveChangesAsync(CancellationToken.None);
            }
            if (!outcome.Success || outcome.Result is null)
            {
                var reason = outcome.FailureReason ?? "ai_error";
                // ai_error may be a blip: throw so Hangfire retries. The other reasons repeat for this document, so they are final.
                if (reason == "ai_error") throw new InvalidOperationException("The document reader failed (ai_error).");
                await FailAsync(doc, reason, ct);
                return;
            }

            var r = outcome.Result;
            results.Add(new ReadResult(r.KindLabel,
                r.Fields.Select(f => f with { Location = Find(f.Value, partWords) }).ToList(),
                r.Tables.Select(t => t with
                {
                    Rows = t.Rows.Select(row => new ReadRow(row.Cells.ToDictionary(c => c.Key, c => c.Value with { Location = Find(c.Value.Value, partWords) }))).ToList(),
                }).ToList(),
                r.Truncated));
        }

        // OCR words but nothing located (sideways photo, non-Latin scan, noise): flagging every value would be noise.
        var anyLocated = onWords && results.Any(r => r.Fields.Any(f => f.Location is not null)
            || r.Tables.Any(t => t.Rows.Any(row => row.Cells.Values.Any(c => c.Location is not null))));
        bool Miss(string? value, SourceLocation? loc) => anyLocated && !string.IsNullOrWhiteSpace(value) && loc is null;

        var merged = ReadMerge.Merge(results, expected);
        var read = new ReadResult(merged.KindLabel,
            merged.Fields.Select(f => f with { NotOnPage = Miss(f.Value, f.Location) }).ToList(),
            merged.Tables.Select(t => t with
            {
                Rows = t.Rows.Select(row => new ReadRow(row.Cells.ToDictionary(c => c.Key, c => c.Value with { NotOnPage = Miss(c.Value.Value, c.Value.Location) }))).ToList(),
            }).ToList(),
            truncated || merged.Truncated);

        await rechecker.ApplyAsync(doc, flow, read, ct, config);
        doc.ReadJson = DocumentJson.Serialize(read);
        doc.ReadAt = DateTime.UtcNow;
        // Bill the parts read, not the pages uploaded.
        doc.BillingUnits = Math.Max(1, results.Count);
        AddAudit(doc, "document.read", new
        {
            status = doc.Status, version = config.VersionNo,
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
