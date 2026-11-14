using System.Text.Json;
using System.Text.Json.Nodes;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services.Documents;

/// <summary>Writes a document's audit rows (the read job writes its own) and reads who they name. Does not save. <paramref name="userId"/> becomes the payload's <c>by</c>.</summary>
public static class DocumentAudit
{
    public static AuditEvent Add(ProcuLinkDbContext db, FlowDocument d, string action, object payload, string? userId)
    {
        var node = JsonSerializer.SerializeToNode(payload) as JsonObject ?? new JsonObject();
        if (userId is not null) node["by"] = userId;
        return db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(), OrgId = d.OrgId, EntityType = "FlowDocument", EntityId = d.Id, Action = action,
            CreatedAt = DateTime.UtcNow, Payload = JsonSerializer.SerializeToDocument(node),
        }).Entity;
    }

    /// <summary>The person a document event names (its payload's <c>by</c>), or null.</summary>
    public static string? By(JsonDocument? payload) =>
        payload?.RootElement is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("by", out var b) && b.ValueKind == JsonValueKind.String
            ? b.GetString() : null;
}
