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
    DocumentEngineGate gate, ProcuLink.Transform.Tokenizing.ISourceTokenizer tokenizer, ILogger<ReadDocumentJob> logger,
    DocumentIntakeService? intake = null)
{
    private const int MaxChars = 60_000;
    private const int MinWordsPerTextPage = 5;
    private const int MaxHints = 40;
    private const int LastRetry = 3;
    private const int TriesPerPart = 2;
    private static readonly TimeSpan MaxReadTime = TimeSpan.FromMinutes(20);

    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;
    internal TimeSpan ReadBudget { get; init; } = MaxReadTime;
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(10);
    private int _partsRead;
    private Guid _runFlowId;

    [Queue("critical")]
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 5, 30, 120 })]
    public async Task ExecuteAsync(Guid documentId, Guid organisationId, PerformContext? context, CancellationToken ct)
    {
        var doc = await db.FlowDocuments.FirstOrDefaultAsync(d => d.Id == documentId && d.OrgId == organisationId, ct);
        if (doc is null || (doc.Status != DocumentStatus.Received && doc.Status != DocumentStatus.Reading)) return;

        // One time budget for this run of the read: the download, every part and its second try. A call still running when it ends is cancelled.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runFlowId = doc.FlowId;
        try
        {
            doc.Status = DocumentStatus.Reading;
            doc.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            deadline.CancelAfter(ReadBudget);
            await ReadAsync(doc, deadline.Token, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // Out of time during a call: final, as when the time runs out between parts.
            logger.LogWarning("Read of document {DocumentId} stopped at the time limit after {Parts} part(s).", documentId, _partsRead);
            await ReloadAndFailAsync(documentId, organisationId, "document_too_large", ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ReadDocumentJob failed for document {DocumentId}", documentId);
            if (context is null || context.GetJobParameter<int>("RetryCount") >= LastRetry)
            {
                // Drop whatever the failed attempt tracked (ReadAt, ReadJson, the read audit row): an unread document is not billed.
                await ReloadAndFailAsync(documentId, organisationId, "ai_error", ct);
            }
            throw;
        }
    }

    /// <summary>Fails the reloaded document, dropping whatever the failed attempt tracked.</summary>
    private async Task ReloadAndFailAsync(Guid documentId, Guid organisationId, string reason, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await FailAsync(await db.FlowDocuments.FirstAsync(d => d.Id == documentId && d.OrgId == organisationId, ct), reason, ct);
    }

    /// <param name="budget">Ends with the read's time budget; given to the download, OCR and the reader. Saves use <paramref name="ct"/>.</param>
    private async Task ReadAsync(FlowDocument doc, CancellationToken budget, CancellationToken ct)
    {
        var started = UtcNow();
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
        await using (var stream = await storage.DownloadAsync(doc.SourceFileKey, budget))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, budget);
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
                (text, spans) = await DocumentText.ReadAsync(doc.FileName, bytes, tokenizer, budget);
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

        var scan = isImage || (isPdf && !words.GroupBy(w => w.Page).Any(g => g.Count() >= MinWordsPerTextPage));
        if (scan && !gate.ScansOn(doc.OrgId))
        {
            await FailAsync(doc, "scanned_not_supported", ct);
            return;
        }

        var truncated = isPdf && pdfPages > ReadLimits.MaxPages;
        string PagesText(int from, int to)
        {
            var t = string.Concat(pageTexts.Skip(from - 1).Take(to - from + 1));
            if (t.Length > MaxChars) (truncated, t) = (true, t[..MaxChars]);
            return t;
        }

        // A PDF with text is read ten pages per part, up to ReadLimits.MaxPages; a window with no word is skipped.
        // A scanned PDF's parts are rendered one window at a time in the loop below; any other file is one part.
        var scanPdf = isPdf && scan;
        var parts = new List<(string Text, IReadOnlyList<PageWord> Words, IReadOnlyList<byte[]>? Pages)>();
        var textWindows = new List<(int From, int To)>();
        if (isPdf && !scan)
        {
            var last = Math.Min(pdfPages, ReadLimits.MaxPages);
            for (var from = 1; from <= last; from += ReadLimits.PagesPerUnit)
            {
                var to = Math.Min(from + ReadLimits.PagesPerUnit - 1, last);
                var partWords = words.Where(w => w.Page >= from && w.Page <= to).ToList();
                if (partWords.Count == 0) continue;
                parts.Add((PagesText(from, to), partWords, null));
                textWindows.Add((from, to));
            }
        }

        if (text.Length > MaxChars)
        {
            truncated |= !isPdf;
            text = text[..MaxChars];
        }
        if (isImage)
        {
            var upright = ScanImages.Upright(bytes);
            if (upright is null)
            {
                await FailAsync(doc, "unreadable_file", ct);
                return;
            }
            parts.Add((text, await ocr.ReadWordsAsync([upright], budget), [upright]));
        }
        else if (!isPdf) parts.Add((text, words, null));
        if (!scanPdf && parts.Count == 0)
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
        ReadResult Located(ReadResult r, IReadOnlyList<PageWord> partWords) => new(r.KindLabel,
            r.Fields.Select(f => f with { Location = Find(f.Value, partWords) }).ToList(),
            r.Tables.Select(t => t with
            {
                Rows = t.Rows.Select(row => new ReadRow(row.Cells.ToDictionary(c => c.Key, c => c.Value with { Location = Find(c.Value.Value, partWords) }))).ToList(),
            }).ToList(),
            r.Truncated);

        var results = new List<ReadResult>();
        var starts = new List<DocumentSpan>();
        // A ZUGFeRD / Factur-X / XRechnung PDF carries the invoice as data: that is the read, exact, with no AI call. Data can differ from
        // what is printed (an IBAN, a total, a seller), so a header value the page does not show is not taken: the page is read instead.
        var embedded = isPdf && EmbeddedInvoice.Read(bytes, expected) is { } data ? Located(data, words) : null;
        if (embedded?.Fields.Any(f => !string.IsNullOrWhiteSpace(f.Value) && f.Location is null && EmbeddedInvoice.PrintedAsIs(f.Meaning)) == true)
        {
            logger.LogWarning("Document {DocumentId} carries invoice data the page does not show; the page is read instead.", doc.Id);
            embedded = null;
        }
        if (embedded is not null)
        {
            logger.LogInformation("Document {DocumentId} read from its embedded invoice data.", doc.Id);
            results.Add(embedded);
        }
        var partCount = embedded is not null ? 0 : scanPdf ? ReadLimits.UnitsFor(pdfPages) : parts.Count;
        static string? Failure(DocumentReadOutcome o) => o.Success && o.Result is not null ? null : o.FailureReason ?? "ai_error";
        for (var part = 1; part <= partCount; part++)
        {
            if (part > 1 && UtcNow() - started > ReadBudget)
            {
                logger.LogWarning("Read of document {DocumentId} stopped at the time limit after {Parts} part(s).", doc.Id, results.Count);
                await FailAsync(doc, "document_too_large", ct);
                return;
            }
            // The heartbeat: a read still working is not taken for stranded.
            doc.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            if (scanPdf)
            {
                // A window is rendered when its turn comes and stops at its first page that cannot be decoded, so every value keeps its page.
                // One that yields no image ends the reading: its pages and the rest were not read.
                var skip = (part - 1) * ReadLimits.PagesPerUnit;
                var images = rasterizer.RenderPagesPng(bytes, ReadLimits.PagesPerUnit, skip).Select(ScanImages.Upright).TakeWhile(i => i is not null).OfType<byte[]>().ToList();
                truncated |= images.Count < Math.Min(ReadLimits.PagesPerUnit, pdfPages - skip);
                if (images.Count == 0)
                {
                    if (part > 1) break;
                    await FailAsync(doc, "unreadable_file", ct);
                    return;
                }
                var ocrWords = (await ocr.ReadWordsAsync(images, budget)).Select(w => w with { Page = w.Page + skip }).ToList();
                parts.Add((PagesText(skip + 1, Math.Min(skip + ReadLimits.PagesPerUnit, pdfPages)), ocrWords, images));
            }
            var (partText, partWords, partPages) = parts[part - 1];
            if (textWindows.Count > 0 && gate.PageImagesWithText)
            {
                // A text PDF's part also carries its own pages' images, rendered when its turn comes; none rendered → text alone.
                var (from, to) = textWindows[part - 1];
                partPages = rasterizer.RenderPagesPng(bytes, to - from + 1, from - 1) is { Count: > 0 } images ? images : null;
            }
            var partExpected = expected ?? (results.Count == 0 ? null : ReadMerge.KeysOf(ReadMerge.Merge(results, null)));
            var input = new DocumentReadInput(doc.FileName, doc.ContentType, partText, partExpected, partPages,
                hints.Count > 0 ? hints : null, part, partCount, scan);
            DocumentReadOutcome outcome;
            var tries = 0;
            do
            {
                if (tries > 0) await Task.Delay(RetryDelay, budget);
                outcome = await reader.ReadAsync(input, doc.OrgId, budget);
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
            }
            // ai_error may be a blip: the part is called once more after RetryDelay. The other reasons repeat for this document.
            while (Failure(outcome) == "ai_error" && ++tries < TriesPerPart);
            if (Failure(outcome) is { } reason)
            {
                // Part 1 failing twice is thrown so Hangfire retries the read; on a later part it is final, so the parts already paid for are not read again.
                if (reason == "ai_error" && part == 1) throw new InvalidOperationException("The document reader failed (ai_error).");
                await FailAsync(doc, reason, ct);
                return;
            }

            var r = outcome.Result!;
            // A start counts only inside the pages this part holds, so the documents never outnumber the pages read.
            var (winFrom, winTo) = textWindows.Count > 0 ? textWindows[part - 1]
                : ((part - 1) * ReadLimits.PagesPerUnit + 1, Math.Min(part * ReadLimits.PagesPerUnit, pdfPages));
            starts.AddRange((outcome.Documents ?? []).Where(s => s.FirstPage >= winFrom && s.FirstPage <= winTo));
            results.Add(Located(r, partWords));
            _partsRead = results.Count;
        }

        // OCR words but nothing located (sideways photo, non-Latin scan, noise): flagging every value would be noise.
        var anyLocated = onWords && results.Any(r => r.Fields.Any(f => f.Location is not null)
            || r.Tables.Any(t => t.Rows.Any(row => row.Cells.Values.Any(c => c.Location is not null))));
        // Embedded invoice data writes dates and codes in its own form, which the page need not print.
        bool Miss(string? value, SourceLocation? loc, string? meaning) => anyLocated && !string.IsNullOrWhiteSpace(value) && loc is null
            && (embedded is null || EmbeddedInvoice.PrintedAsIs(meaning));

        var merged = ReadMerge.Merge(results, expected);
        // One number style per document: "1.500" reads as its other numbers do, or is marked to check.
        var read = ReadNumbers.Settle(new ReadResult(merged.KindLabel,
            merged.Fields.Select(f => f with { NotOnPage = Miss(f.Value, f.Location, f.Meaning) }).ToList(),
            merged.Tables.Select(t => t with
            {
                Rows = t.Rows.Select(row => new ReadRow(row.Cells.ToDictionary(c => c.Key, c => c.Value with { NotOnPage = Miss(c.Value.Value, c.Value.Location, t.Columns.FirstOrDefault(x => x.Key == c.Key)?.Meaning) }))).ToList(),
            }).ToList(),
            truncated || merged.Truncated));

        // Another run may have finished this document meanwhile, or it was sorted into another flow: then this run writes nothing.
        var current = await db.FlowDocuments.AsNoTracking().Where(d => d.Id == doc.Id && d.OrgId == doc.OrgId).Select(d => new { d.Status, d.FlowId }).FirstAsync(ct);
        if (current.Status != DocumentStatus.Reading || current.FlowId != flow.Id) return;
        // A file of several documents becomes one document each, on its first read only; a part is never split again,
        // nor a file not read to its end (a text PDF's every window read).
        if (isPdf && doc.ReadAt is null && !doc.IsSample && doc.PartOfId is null && intake is not null && !read.Truncated
            && (scanPdf || textWindows.Count == ReadLimits.UnitsFor(pdfPages)))
        {
            var ranges = DocumentSplit.Ranges(starts, pdfPages);
            if (!ranges.SequenceEqual([new PageRange(1, pdfPages)])
                && await intake.HasRoomForAsync(doc.OrgId, ranges.Sum(r => ReadLimits.UnitsFor(r.LastPage - r.FirstPage + 1)), ct)
                && Cut(doc, bytes, ranges) is { } files)
            {
                await SplitAsync(doc, files, ranges, ct);
                return;
            }
        }
        await rechecker.ApplyAsync(doc, flow, read, ct, config);
        doc.ReadJson = DocumentJson.Serialize(read);
        // The first successful read is the billed one: a re-read after sorting keeps its time and units.
        if (doc.ReadAt is null)
        {
            doc.ReadAt = DateTime.UtcNow;
            // Bill the parts read, not the pages uploaded.
            doc.BillingUnits = Math.Max(1, results.Count);
        }
        doc.Kind ??= DocumentKind.Normalize(read.KindLabel);
        doc.SenderKey ??= DocumentKind.SenderKeyOf(read, doc.Source, doc.SourceRef);
        AddAudit(doc, "document.read", new
        {
            status = doc.Status, version = config.VersionNo,
            failedChecks = DocumentJson.Deserialize<List<DocumentCheck>>(doc.ChecksJson).Select(c => c.Code).ToList(),
        });
        var sorted = flow.IsInbox && !doc.IsSample && await DocumentSort.SortAsync(db, doc, ct);
        await db.SaveChangesAsync(ct);
        if (flow.IsInbox)
        {
            // A sorted document is read again in its new flow; one left in the inbox waits for a person.
            if (!doc.IsSample) logger.LogInformation("Inbox document {DocumentId} outcome {Outcome}.", doc.Id, sorted ? "sorted" : doc.StatusReason);
            if (sorted) Enqueue(jobs, doc.Id, doc.OrgId);
            return;
        }
        SendDocumentJob.EnqueueIfAutomatic(jobs, doc, flow);
        // A sort that is not certain announces nothing until a person confirms it.
        if (doc.SortUnconfirmed) return;
        // Committed: a shutdown must not skip the announcement or throw.
        await FlowWebhook.EnqueueAsync(db, jobs, doc, FlowWebhookEvent.Read, logger, CancellationToken.None);
    }

    /// <summary>Every document's own PDF, cut before anything is written; null (read whole) when the file cannot be cut.</summary>
    private byte[][]? Cut(FlowDocument doc, byte[] bytes, IReadOnlyList<PageRange> ranges)
    {
        try { return ranges.Select(r => PdfPages.Take(bytes, r.FirstPage, r.LastPage)).ToArray(); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Document {DocumentId} could not be cut into its documents; it is read as one.", doc.Id);
            return null;
        }
    }

    /// <summary>
    /// Documents 2..N go in through intake first, then the original keeps range 1 and is read again; a retry after a crash
    /// in between re-creates no part (by name). The whole-file hash stays on the original, so the same file is still held at intake.
    /// </summary>
    private async Task SplitAsync(FlowDocument doc, byte[][] files, IReadOnlyList<PageRange> ranges, CancellationToken ct)
    {
        string Name(PageRange r) => ProcuLink.Api.Helpers.FileNameSanitiser.Sanitise(DocumentSplit.PartName(doc.FileName, r));
        var done = await db.FlowDocuments.Where(d => d.OrgId == doc.OrgId && d.PartOfId == doc.Id).Select(d => d.FileName).ToListAsync(ct);
        for (var i = 1; i < ranges.Count; i++)
            if (!done.Contains(Name(ranges[i])))
                await intake!.AcceptAsync(doc.OrgId, doc.FlowId, new MemoryStream(files[i]), Name(ranges[i]),
                    doc.ContentType, doc.Source, doc.SourceRef, null, ct, partOf: doc.Id, splitFileKey: doc.SourceFileKey, senderUnconfirmed: doc.SenderUnconfirmed);

        var (first, file) = (ranges[0], doc.FileName);
        var key = $"{doc.OrgId}/documents/{doc.Id}/{Name(first)}";
        // A file already named like its first part keeps the whole file: the part goes under its own key.
        if (key == doc.SourceFileKey) key = $"{doc.OrgId}/documents/{doc.Id}/1-{Name(first)}";
        await storage.UploadAsync(new MemoryStream(files[0]), key, doc.ContentType, ct);
        var pages = first.LastPage - first.FirstPage + 1;
        (doc.SourceFileKey, doc.FileName, doc.PageCount, doc.BillingUnits) = (key, Name(first), pages, ReadLimits.UnitsFor(pages));
        (doc.PartOfId, doc.Status, doc.UpdatedAt) = (doc.Id, DocumentStatus.Received, DateTime.UtcNow);
        AddAudit(doc, "document.split", new { documents = ranges.Count, file });
        await db.SaveChangesAsync(ct);
        Enqueue(jobs, doc.Id, doc.OrgId);
    }

    /// <summary>
    /// Fails the document only while this run still owns it: in the flow the run started in, and still reading or received.
    /// A stale run never fails a document already moved on; a read that failed in the document's own flow always does.
    /// </summary>
    private async Task FailAsync(FlowDocument doc, string reason, CancellationToken ct)
    {
        var current = await db.FlowDocuments.AsNoTracking().Where(d => d.Id == doc.Id && d.OrgId == doc.OrgId).Select(d => new { d.Status, d.FlowId }).FirstAsync(ct);
        if (current.FlowId != _runFlowId || current.Status is not (DocumentStatus.Reading or DocumentStatus.Received)) return;
        doc.Status = DocumentStatus.ReadFailed;
        doc.StatusReason = reason;
        doc.UpdatedAt = DateTime.UtcNow;
        AddAudit(doc, "document.read_failed", new { reason });
        await db.SaveChangesAsync(ct);
        // A sort that is not certain announces nothing, a failed read included, until a person confirms it.
        if (!doc.SortUnconfirmed) await FlowWebhook.EnqueueAsync(db, jobs, doc, FlowWebhookEvent.ReadFailed, logger, CancellationToken.None);
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
