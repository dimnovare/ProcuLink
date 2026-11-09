using System.Security.Cryptography;
using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Helpers;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Parsing;
using UglyToad.PdfPig.Core;

namespace ProcuLink.Api.Services.Documents;

/// <summary>Thrown by <see cref="DocumentIntakeService.AcceptAsync"/> for a file it refuses: <c>unsupported_format</c>, <c>file_too_large</c> or <c>unreadable_file</c>.</summary>
public sealed class DocumentRejectedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Accepts an uploaded file into a flow: stores it, counts its units, holds an identical file as a duplicate, and queues the read.</summary>
public sealed class DocumentIntakeService(
    ProcuLinkDbContext db, IFileStorageService storage, IBackgroundJobClient jobs,
    DocumentEngineGate gate, IBillingService billing, ILogger<DocumentIntakeService> logger) : IFlowDocumentSubmitter
{
    public IReadOnlyList<string> AcceptedFormats(Guid orgId) => gate.AcceptedFormats(orgId);

    /// <summary>The one way a file enters a flow: engine gate, flow, plan limit, then <see cref="AcceptAsync"/>.</summary>
    public async Task<FlowSubmitResult> SubmitAsync(
        Guid orgId, Guid flowId, Stream content, string fileName, string contentType,
        string source, string? sourceRef, CancellationToken ct)
    {
        if (!gate.IsEnabled(orgId)) return new(null, "document_engine_not_enabled", null);
        if (!await db.Flows.AnyAsync(f => f.OrgId == orgId && f.Id == flowId, ct)) return new(null, "flow_not_found", null);

        var limit = await billing.CheckOrderLimitAsync(orgId, ct);
        if (!limit.Allowed) return new(null, limit.PilotExpired ? "pilot_expired" : "order_limit_reached", limit);

        try
        {
            return new(await AcceptAsync(orgId, flowId, content, fileName, contentType, source, sourceRef, ct), null, null);
        }
        catch (DocumentRejectedException e)
        {
            return new(null, e.Code, null);
        }
    }

    public async Task<FlowDocument> AcceptAsync(
        Guid orgId, Guid flowId, Stream content, string fileName, string contentType,
        string source, string? sourceRef, CancellationToken ct)
    {
        var safeName = FileNameSanitiser.Sanitise(fileName);
        var ext = FileNameSanitiser.GetExtension(fileName).TrimStart('.');
        if (!AcceptedFormats(orgId).Contains(ext)) throw new DocumentRejectedException("unsupported_format");

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        if (buffer.Length > IngressLimits.MaxFileBytes) throw new DocumentRejectedException("file_too_large");
        if (buffer.Length == 0) throw new DocumentRejectedException("unreadable_file");
        var bytes = buffer.ToArray();

        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        int? pages = ext == "pdf" ? CountPages(bytes) : null;
        var units = pages is { } p ? Math.Max(1, (int)Math.Ceiling(p / 10.0)) : 1;

        var id = Guid.NewGuid();
        var key = $"{orgId}/documents/{id}/{safeName}";
        buffer.Position = 0;
        await storage.UploadAsync(buffer, key, contentType, ct);

        var now = DateTime.UtcNow;
        var doc = new FlowDocument
        {
            Id = id, OrgId = orgId, FlowId = flowId, FileName = safeName, ContentType = contentType,
            SourceFileKey = key, Sha256 = sha, PageCount = pages, BillingUnits = units,
            Source = source, SourceRef = sourceRef, Status = DocumentStatus.Received, CreatedAt = now, UpdatedAt = now,
        };

        var duplicate = await db.FlowDocuments.AnyAsync(d => d.OrgId == orgId && d.FlowId == flowId && d.Sha256 == sha, ct);
        if (duplicate)
        {
            doc.Status = DocumentStatus.NeedsReview;
            doc.StatusReason = "duplicate_file";
            doc.ChecksJson = DocumentJson.Serialize(new[]
            {
                new DocumentCheck(DocumentChecks.Duplicate, false, null, "This file was already uploaded to this flow."),
            });
        }

        var audit = new AuditEvent
        {
            Id = Guid.NewGuid(), OrgId = orgId, EntityType = "FlowDocument", EntityId = id,
            Action = "document.received", CreatedAt = now,
            Payload = JsonSerializer.SerializeToDocument(new { fileName = safeName, flowId, source, sourceRef }),
        };
        db.FlowDocuments.Add(doc);
        db.AuditEvents.Add(audit);
        // Never cancelled half-way, and nothing after it throws: a caller that sees a throw knows nothing committed.
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch
        {
            // A caller that saves this context later (a pickup run's status) must not commit them after all.
            db.Entry(doc).State = EntityState.Detached;
            db.Entry(audit).State = EntityState.Detached;
            throw;
        }

        try
        {
            if (!duplicate) ReadDocumentJob.Enqueue(jobs, id, orgId);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not queue the read for document {DocumentId}.", id);
        }

        return doc;
    }

    private static int CountPages(byte[] pdf)
    {
        try { return PdfWordExtractor.Extract(pdf).PageCount; }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new DocumentRejectedException("unreadable_file");
        }
    }
}
