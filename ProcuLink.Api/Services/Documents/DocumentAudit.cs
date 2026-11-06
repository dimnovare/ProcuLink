using System.Text.Json;
using System.Text.Json.Nodes;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services.Documents;

/// <summary>The one writer of a document's audit rows. Does not save. <paramref name="userId"/> becomes the payload's <c>by</c>.</summary>
public static class DocumentAudit
{
    public static void Add(ProcuLinkDbContext db, FlowDocument d, string action, object payload, string? userId)
    {
        var node = JsonSerializer.SerializeToNode(payload) as JsonObject ?? new JsonObject();
        if (userId is not null) node["by"] = userId;
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(), OrgId = d.OrgId, EntityType = "FlowDocument", EntityId = d.Id, Action = action,
            CreatedAt = DateTime.UtcNow, Payload = JsonSerializer.SerializeToDocument(node),
        });
    }
}
