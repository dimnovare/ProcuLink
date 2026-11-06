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

namespace ProcuLink.Api.Services.Documents;

public enum SendMode { Person, Automatic }

/// <summary>Which output could not be rendered, and why.</summary>
public sealed record OutputFailure(string Output, string? Detail);

public sealed record SendOutcome(string? Error, object? Detail, IReadOnlyList<FlowDelivery> Deliveries)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Renders a read document through every output of its flow, stores the artifacts, and writes one delivery per output:
/// <c>stored</c> for download-only outputs, <c>queued</c> for those with a destination (delivered by <see cref="DeliverDocumentJob"/>).
/// </summary>
public sealed class DocumentSender(ProcuLinkDbContext db, IBillingService billing, IFileStorageService storage, IBackgroundJobClient jobs)
{
    private sealed record Rendered(FlowOutput Output, byte[] Bytes, string ContentType, string Extension);

    private static SendOutcome Refuse(string error, object? detail = null) => new(error, detail, []);

    public async Task<SendOutcome> SendAsync(FlowDocument doc, SendMode mode, string? userId, CancellationToken ct)
    {
        var (observed, observedAt) = (doc.Status, Micros(doc.UpdatedAt));
        if (!DocumentStatus.IsEditable(observed, doc.StatusReason)) return Refuse("not_ready");
        if (!await billing.CanProcessOrdersAsync(doc.OrgId, ct)) return Refuse("processing_paused");

        var outputs = await db.Set<FlowOutput>().Where(o => o.OrgId == doc.OrgId && o.FlowId == doc.FlowId)
            .OrderBy(o => o.CreatedAt).ToListAsync(ct);
        if (outputs.Count == 0) return Refuse("no_outputs");

        // Render everything first: one bad output means nothing is stored or sent.
        var read = DocumentJson.Deserialize<ReadResult>(doc.ReadJson);
        var rendered = new List<Rendered>();
        foreach (var o in outputs)
        {
            var render = DocumentOutputRenderer.Render(o.Format, DocumentJson.Deserialize<OutputNodeTemplate>(o.TemplateJson), read, o.TableKey);
            if (!render.Ok)
            {
                if (mode == SendMode.Automatic)
                {
                    doc.Status = DocumentStatus.NeedsReview;
                    doc.StatusReason = render.Error;
                    doc.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
                return Refuse(render.Error!, new OutputFailure(o.Name, render.Detail));
            }
            using var buffer = new MemoryStream();
            await render.Result!.Content.CopyToAsync(buffer, ct);
            rendered.Add(new(o, buffer.ToArray(), render.Result.ContentType, render.Result.FileExtension));
        }

        // Claim: a double Send, a person racing an automatic send, or an edit since we rendered finds zero rows here.
        if (!await ClaimAsync(doc, observed, observedAt, DocumentStatus.Sending, ct)) return Refuse("not_ready");
        IReadOnlyList<FlowDelivery> queued;
        SendOutcome outcome;
        try
        {
            (outcome, queued) = await StoreAsync(doc, mode, userId, rendered, ct);
        }
        catch
        {
            db.ChangeTracker.Clear();
            await ClaimAsync(doc, DocumentStatus.Sending, null, observed, CancellationToken.None);
            throw;
        }

        // The send is committed. An enqueue failure leaves the rows queued for the sweep; it must not undo the send.
        foreach (var d in queued) DeliverDocumentJob.Enqueue(jobs, d.Id, d.OrgId);
        return outcome;
    }

    private async Task<(SendOutcome, IReadOnlyList<FlowDelivery>)> StoreAsync(FlowDocument doc, SendMode mode, string? userId, List<Rendered> rendered, CancellationToken ct)
    {
        var existing = await db.Set<FlowDelivery>().Where(d => d.OrgId == doc.OrgId && d.DocumentId == doc.Id).ToListAsync(ct);
        var destinationIds = rendered.Select(r => r.Output.DestinationId).OfType<Guid>().Distinct().ToList();
        var destinations = await db.Set<FlowDestination>().Where(d => d.OrgId == doc.OrgId && destinationIds.Contains(d.Id)).ToListAsync(ct);
        var stem = FileNameSanitiser.Sanitise(Path.GetFileNameWithoutExtension(doc.FileName));
        var now = DateTime.UtcNow;
        var sent = new List<FlowDelivery>();

        foreach (var r in rendered)
        {
            var delivery = existing.FirstOrDefault(d => d.OutputId == r.Output.Id);
            if (delivery?.Status is FlowDeliveryStatus.Delivered or FlowDeliveryStatus.Stored or FlowDeliveryStatus.Queued or FlowDeliveryStatus.Delivering)
                continue;
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
            delivery.DispatchKey = Guid.NewGuid();
            delivery.ArtifactKey = $"{doc.OrgId}/documents/{doc.Id}/outputs/{delivery.DispatchKey}{r.Extension}";
            delivery.ContentType = r.ContentType;
            delivery.FileName = $"{stem}-{delivery.DispatchKey.ToString("N")[..8]}{r.Extension}";
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
        doc.UpdatedAt = now;
        DocumentAudit.Add(db, doc, "document.sent",
            new { mode = mode.ToString().ToLowerInvariant(), outputs = sent.Select(d => d.OutputName).ToList() }, userId);
        await db.SaveChangesAsync(ct);

        return (new SendOutcome(null, null, existing), sent.Where(d => d.Status == FlowDeliveryStatus.Queued).ToList());
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
