using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Helpers;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Documents;
using ProcuLink.Transform.Output.Ksef;

namespace ProcuLink.Api.Services.Documents;

public enum SendMode { Person, Automatic }

/// <summary>Which output could not be rendered, and why.</summary>
public sealed record OutputFailure(string Output, string? Detail);

/// <summary>A KSeF Production send waits for a person: what the files say, and the hash to confirm them by.</summary>
public sealed record KsefConfirmDetail(IReadOnlyList<KsefSummary> Summaries, string Confirm);

/// <summary>The document already has this production KSeF number, so it is never issued again.</summary>
public sealed record KsefIssuedDetail(string? KsefNumber);

public sealed record SendOutcome(string? Error, object? Detail, IReadOnlyList<FlowDelivery> Deliveries)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Renders a read document through every output of its flow, stores the artifacts, and writes one delivery per output:
/// <c>stored</c> for download-only outputs, <c>queued</c> for those with a destination (delivered by <see cref="DeliverDocumentJob"/>).
/// </summary>
public sealed class DocumentSender(ProcuLinkDbContext db, IBillingService billing, IFileStorageService storage, IBackgroundJobClient jobs, ILogger<DocumentSender> logger, DocumentEngineGate gate, IAnalyticsService analytics)
{
    private sealed record Rendered(FlowOutput Output, byte[] Bytes, string ContentType, string Extension);

    private const string NoOutputMatches = "no_output_matches";

    private async Task HoldAsync(FlowDocument doc, string reason, CancellationToken ct)
    {
        doc.Status = DocumentStatus.NeedsReview;
        doc.StatusReason = reason;
        doc.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static SendOutcome Refuse(string error, object? detail = null) => new(error, detail, []);

    /// <param name="ksefConfirm">
    /// The confirmation a person gives for an invoice going to KSeF Production: the hash of exactly the file they were shown
    /// (<see cref="Fa3Summary.ConfirmHash"/>). Without it such a send is refused with the preview; an automatic send is held.
    /// </param>
    public async Task<SendOutcome> SendAsync(FlowDocument doc, SendMode mode, string? userId, CancellationToken ct, string? ksefConfirm = null)
    {
        var (observed, observedAt) = (doc.Status, Micros(doc.UpdatedAt));
        if (!DocumentStatus.IsEditable(observed, doc.StatusReason)) return Refuse("not_ready");
        if (!await billing.CanProcessOrdersAsync(doc.OrgId, ct)) return Refuse("processing_paused");

        var flow = await db.Flows.FirstAsync(f => f.OrgId == doc.OrgId && f.Id == doc.FlowId, ct);
        var config = await FlowConfigLoader.CurrentAsync(db, flow, ct);
        var outputs = config.Outputs.ToList();
        if (outputs.Count == 0) return Refuse("no_outputs");

        // Render everything first: one bad output means nothing is stored or sent.
        var read = DocumentJson.Deserialize<ReadResult>(doc.ReadJson);
        var header = DocumentRows.Build(read, null).Header;
        outputs = outputs.Where(o => SendWhenOps.Matches(o.SendWhenJson is null ? null : DocumentJson.Deserialize<SendWhen>(o.SendWhenJson), header)).ToList();
        if (outputs.Count == 0)
        {
            if (mode == SendMode.Automatic) await HoldAsync(doc, NoOutputMatches, ct);
            return Refuse(NoOutputMatches);
        }

        var templates = outputs.Select(o => DocumentJson.Deserialize<OutputNodeTemplate>(o.TemplateJson)).ToList();
        var lookups = await DocumentLookups.LoadAsync(db, doc.OrgId, templates, ct);
        var rendered = new List<Rendered>();
        foreach (var (o, template) in outputs.Zip(templates))
        {
            // A standard whose switch (Peppol, KSeF) has since gone off is refused like an unknown one, never rendered.
            // An X12 / EDIFACT file gets a fresh interchange number; nothing is modified yet, so allocating it saves nothing else.
            var known = StandardOutputs.Find(o.Standard);
            var render = o.Standard is not null
                ? known is not null && !gate.StandardOn(doc.OrgId, known)
                    ? new DocumentRender(null, "output_invalid", null)
                    : await StandardOutputs.RenderAsync(o, read, config.Fields, doc.OrgId, ct,
                        known?.NeedsControlNumber == true ? await InterchangeNumbers.NextAsync(db, ct) : null)
                : DocumentOutputRenderer.Render(o.Format, template, read, o.TableKey, lookups,
                    o.LayoutJson is null ? null : DocumentJson.Deserialize<OutputLayout>(o.LayoutJson), o.Logo, config.Fields.Tables);
            if (!render.Ok)
            {
                if (mode == SendMode.Automatic) await HoldAsync(doc, render.Error!, ct);
                return Refuse(render.Error!, new OutputFailure(o.Name, render.Detail));
            }
            using var buffer = new MemoryStream();
            await render.Result!.Content.CopyToAsync(buffer, ct);
            rendered.Add(new(o, buffer.ToArray(), render.Result.ContentType, render.Result.FileExtension));
        }

        var issue = await KsefProductionAsync(doc, rendered, ct);
        // KSeF Production: never twice, and never without a person who saw exactly this file. An automatic send is held for one.
        if (issue is not null)
        {
            var refusal = issue.Refusal
                ?? (mode == SendMode.Automatic || ksefConfirm is null ? Refuse(KsefConfirm, new KsefConfirmDetail(issue.Summaries, issue.Confirm!))
                    : ksefConfirm != issue.Confirm ? Refuse("ksef_changed") : null);
            if (refusal is not null)
            {
                if (mode == SendMode.Automatic) await HoldAsync(doc, refusal.Error!, ct);
                return refusal;
            }
        }

        // Claim: a double Send, a person racing an automatic send, or an edit since we rendered finds zero rows here.
        if (!await ClaimAsync(doc, observed, observedAt, DocumentStatus.Sending, ct)) return Refuse("not_ready");
        // Saved with the send itself (StoreAsync's SaveChanges), so a confirmation is recorded exactly when it was acted on.
        if (issue is { Confirm: { } confirmed })
            DocumentAudit.Add(db, doc, "document.ksef_issue_confirmed", new { confirm = confirmed, environment = "production" }, userId);
        IReadOnlyList<FlowDelivery> queued;
        SendOutcome outcome;
        try
        {
            (outcome, queued) = await StoreAsync(doc, mode, userId, rendered, config.VersionNo, ct);
        }
        catch
        {
            db.ChangeTracker.Clear();
            await ClaimAsync(doc, DocumentStatus.Sending, null, observed, CancellationToken.None);
            throw;
        }

        // The send is committed. An enqueue failure leaves the rows queued for the sweep; it must not undo the send.
        foreach (var d in queued) DeliverDocumentJob.Enqueue(jobs, d.Id, d.OrgId);
        // Sent here only when nothing is left to deliver; otherwise DeliverDocumentJob announces the end, so a send is announced once.
        if (doc.Status == DocumentStatus.Sent) await FlowWebhook.EnqueueAsync(db, jobs, doc, FlowWebhookEvent.Sent, logger, CancellationToken.None);
        // Every committed send, not only the first (a funnel takes the first). Mode only: never content, names or addresses.
        await analytics.CaptureAsync(doc.OrgId, userId, "document_sent",
            new Dictionary<string, object?> { ["mode"] = mode.ToString().ToLowerInvariant() }, CancellationToken.None);
        return outcome;
    }

    private async Task<(SendOutcome, IReadOnlyList<FlowDelivery>)> StoreAsync(FlowDocument doc, SendMode mode, string? userId, List<Rendered> rendered, int? version, CancellationToken ct)
    {
        var existing = await db.Set<FlowDelivery>().Where(d => d.OrgId == doc.OrgId && d.DocumentId == doc.Id).ToListAsync(ct);
        var destinationIds = rendered.Select(r => r.Output.DestinationId).OfType<Guid>().Distinct().ToList();
        var destinations = await db.Set<FlowDestination>().Where(d => d.OrgId == doc.OrgId && destinationIds.Contains(d.Id)).ToListAsync(ct);
        var stem = FileNameSanitiser.Sanitise(Path.GetFileNameWithoutExtension(doc.FileName));
        var now = DateTime.UtcNow;
        var sent = new List<FlowDelivery>();

        // A dead letter from an output this send no longer routes to is history, not a failure: detach it like a deleted output.
        var routed = rendered.Select(r => r.Output.Id).ToHashSet();
        foreach (var stale in existing.Where(d => d.Status == FlowDeliveryStatus.DeadLetter && d.OutputId is { } id && !routed.Contains(id)))
            stale.OutputId = null;

        foreach (var r in rendered)
        {
            var delivery = existing.FirstOrDefault(d => d.OutputId == r.Output.Id);
            if (delivery?.Status is FlowDeliveryStatus.Delivered or FlowDeliveryStatus.Stored or FlowDeliveryStatus.Queued or FlowDeliveryStatus.Delivering)
                continue;
            // A row KSeF Production may already hold is kept as history, never reused: its reference keeps the document from Production.
            if (delivery is { RemoteEnvironment: "production", RemoteReference: not null })
                (delivery.OutputId, delivery) = (null, null);
            if (delivery is null)
            {
                delivery = new FlowDelivery
                {
                    Id = Guid.NewGuid(), OrgId = doc.OrgId, DocumentId = doc.Id, OutputId = r.Output.Id, CreatedAt = now,
                };
                db.Set<FlowDelivery>().Add(delivery);
                existing.Add(delivery);
            }

            var destination = destinations.FirstOrDefault(d => d.Id == r.Output.DestinationId);
            delivery.OutputName = r.Output.Name;
            delivery.DestinationId = destination?.Id;
            delivery.Protocol = destination?.Protocol ?? "";
            // A KSeF delivery is bound to the environment it was checked (and, for Production, confirmed) for.
            (delivery.RemoteEnvironment, delivery.RemoteReference, delivery.ReceiptKey) =
                (destination?.Protocol == Core.Constants.DeliveryProtocolConstants.Ksef ? FlowDestinationService.KsefEnvironment(destination) : null, null, null);
            delivery.DispatchKey = Guid.NewGuid();
            delivery.ArtifactKey = $"{doc.OrgId}/documents/{doc.Id}/outputs/{delivery.DispatchKey}{r.Extension}";
            delivery.ContentType = r.ContentType;
            // Kept for download, a file is named as the preview names it. For a destination, or beside another of this
            // document's files with that name, it carries the send's key so no file overwrites another.
            var plain = $"{stem}{r.Extension}";
            delivery.FileName = destination is null && rendered.Count(x => x.Extension == r.Extension) == 1 && !existing.Any(d => d != delivery && d.FileName == plain)
                ? plain
                : $"{stem}-{delivery.DispatchKey.ToString("N")[..8]}{r.Extension}";
            delivery.Attempts = 0;
            delivery.LastError = null;
            delivery.ResponseCode = null;
            delivery.ClaimedAt = null;
            delivery.NextAttemptAt = null;
            delivery.Status = destination is null ? FlowDeliveryStatus.Stored : FlowDeliveryStatus.Queued;
            delivery.DeliveredAt = destination is null ? now : null;
            delivery.UpdatedAt = now;
            await storage.UploadAsync(new MemoryStream(r.Bytes), delivery.ArtifactKey, r.ContentType, ct);
            sent.Add(delivery);
        }

        doc.Status = FlowDeliveryStatus.DocumentStatusFor(existing.Where(d => FlowDeliveryStatus.CountsTowardDocument(d.OutputId, d.Status)).Select(d => d.Status));
        doc.StatusReason = null;
        doc.FlowVersion = version;
        doc.UpdatedAt = now;
        DocumentAudit.Add(db, doc, "document.sent",
            new { mode = mode.ToString().ToLowerInvariant(), outputs = sent.Select(d => d.OutputName).ToList(), version }, userId);
        await db.SaveChangesAsync(ct);

        return (new SendOutcome(null, null, existing), sent.Where(d => d.Status == FlowDeliveryStatus.Queued).ToList());
    }

    private const string KsefConfirm = "ksef_confirm";

    private sealed record KsefIssue(SendOutcome? Refusal, IReadOnlyList<KsefSummary> Summaries, string? Confirm);

    private static readonly System.Text.RegularExpressions.Regex KsefNumber = new(@"^\d{10}-\d{8}-[0-9A-F]{12}-[0-9A-F]{2}$");

    /// <summary>
    /// For outputs routed to a KSeF Production destination: refused when the document already has a production KSeF number, or when
    /// such an output is not the KSeF invoice file; otherwise the preview and the hash a person must confirm (one file's hash, or the
    /// SHA-256 of all of them joined by newlines, in output order). Null when nothing goes to KSeF Production.
    /// </summary>
    private async Task<KsefIssue?> KsefProductionAsync(FlowDocument doc, List<Rendered> rendered, CancellationToken ct)
    {
        var ids = rendered.Select(r => r.Output.DestinationId).OfType<Guid>().Distinct().ToList();
        var production = (await db.Set<FlowDestination>().AsNoTracking()
                .Where(d => d.OrgId == doc.OrgId && ids.Contains(d.Id) && d.Protocol == Core.Constants.DeliveryProtocolConstants.Ksef).ToListAsync(ct))
            .Where(d => FlowDestinationService.KsefEnvironment(d) == "production").Select(d => d.Id).ToHashSet();
        var files = rendered.Where(r => r.Output.DestinationId is { } id && production.Contains(id)).ToList();
        if (files.Count == 0) return null;
        if (files.Count > 1)
            return new(Refuse("output_invalid", new OutputFailure(files[1].Output.Name, "Only one KSeF invoice file can go to KSeF Production from a document.")), [], null);

        var issued = await db.Set<FlowDelivery>().AsNoTracking()
            .Where(d => d.OrgId == doc.OrgId && d.DocumentId == doc.Id && d.RemoteEnvironment == "production" && d.RemoteReference != null)
            .Select(d => d.RemoteReference).FirstOrDefaultAsync(ct);
        // Issued, or possibly issued (a reference KSeF gave before the outcome was known): never sent to Production again.
        if (issued is not null)
            return new(Refuse("ksef_already_issued", new KsefIssuedDetail(KsefNumber.IsMatch(issued) ? issued : null)), [], null);
        if (files.FirstOrDefault(r => r.Output.Standard != "ksef-fa-3") is { } other)
            return new(Refuse("output_invalid", new OutputFailure(other.Output.Name, "A KSeF destination can only send the KSeF invoice file.")), [], null);

        var hashes = files.Select(r => Fa3Summary.ConfirmHash(r.Bytes)).ToList();
        var confirm = hashes.Count == 1 ? hashes[0]
            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', hashes)))).ToLowerInvariant();
        return new(null, [.. files.Select(r => Fa3Summary.Read(r.Bytes))], confirm);
    }

    /// <summary>Postgres stores microseconds; a just-saved tracked entity still holds 100ns ticks. Compare at the stored precision.</summary>
    private static DateTime Micros(DateTime t) => t.AddTicks(-(t.Ticks % 10));

    /// <summary>Moves the document between statuses only if it is still in <paramref name="from"/>.</summary>
    private async Task<bool> ClaimAsync(FlowDocument doc, string from, DateTime? fromUpdatedAt, string to, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        int claimed;
        if (db.Database.IsRelational())
            claimed = await db.FlowDocuments.Where(d => d.Id == doc.Id && d.OrgId == doc.OrgId && d.Status == from
                         && (fromUpdatedAt == null || d.UpdatedAt == fromUpdatedAt))
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, to).SetProperty(d => d.UpdatedAt, now), ct);
        else
        {
            // EF InMemory cannot translate ExecuteUpdateAsync: same transition through the change tracker (single-threaded there).
            var row = await db.FlowDocuments.FirstAsync(d => d.Id == doc.Id && d.OrgId == doc.OrgId, ct);
            claimed = row.Status == from && (fromUpdatedAt == null || Micros(row.UpdatedAt) == fromUpdatedAt) ? 1 : 0;
            if (claimed == 1)
            {
                row.Status = to;
                row.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
            }
        }

        if (claimed == 1) { doc.Status = to; doc.UpdatedAt = now; }
        return claimed == 1;
    }
}
