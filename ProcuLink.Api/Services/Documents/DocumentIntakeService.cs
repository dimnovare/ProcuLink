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

namespace ProcuLink.Api.Services.Documents;

/// <summary>Thrown by <see cref="DocumentIntakeService.AcceptAsync"/> for a file it refuses: <c>unsupported_format</c> or <c>file_too_large</c>.</summary>
public sealed class DocumentRejectedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Accepts an uploaded file into a flow: stores it, counts its units, holds an identical file as a duplicate, and queues the read.</summary>
public sealed class DocumentIntakeService(ProcuLinkDbContext db, IFileStorageService storage, IBackgroundJobClient jobs)
{
    public static readonly string[] AcceptedFormats = ["pdf", "csv", "xlsx"];

    public async Task<FlowDocument> AcceptAsync(
        Guid orgId, Guid flowId, Stream content, string fileName, string contentType, CancellationToken ct)
    {
        var safeName = FileNameSanitiser.Sanitise(fileName);
        var ext = FileNameSanitiser.GetExtension(fileName).TrimStart('.');
        if (!AcceptedFormats.Contains(ext)) throw new DocumentRejectedException("unsupported_format");

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        if (buffer.Length > IngressLimits.MaxFileBytes) throw new DocumentRejectedException("file_too_large");
        var bytes = buffer.ToArray();

        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        int? pages = ext == "pdf" ? PdfWordExtractor.Extract(bytes).PageCount : null;
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
            Status = DocumentStatus.Received, CreatedAt = now, UpdatedAt = now,
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

        db.FlowDocuments.Add(doc);
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(), OrgId = orgId, EntityType = "FlowDocument", EntityId = id,
            Action = "document.received", CreatedAt = now,
            Payload = JsonSerializer.SerializeToDocument(new { fileName = safeName, flowId }),
        });
        await db.SaveChangesAsync(ct);

        if (!duplicate) ReadDocumentJob.Enqueue(jobs, id, orgId);
        return doc;
    }
}
