using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Services;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

/// <summary>Whether a document's buyer can receive Peppol invoices. A read for the document screen only: never a check, never blocks a send.</summary>
[Authorize]
[ApiController]
[Route("api/documents")]
public sealed class PeppolReceiverController(ProcuLinkDbContext db, ICurrentTenantService tenant, DocumentEngineGate gate, PeppolReceiverLookup lookup) : ControllerBase
{
    [HttpGet("{id:guid}/peppol-receiver")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var orgId = tenant.OrganisationId;
        if (!gate.IsEnabled(orgId)) return StatusCode(403, new { error = "document_engine_not_enabled" });
        if (!gate.PeppolOn(orgId)) return StatusCode(403, new { error = "peppol_off" });
        var doc = await db.FlowDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.OrgId == orgId && d.Id == id, ct);
        if (doc is null) return NotFound(new { error = "document_not_found" });
        var flow = await db.Flows.AsNoTracking().FirstAsync(f => f.OrgId == orgId && f.Id == doc.FlowId, ct);
        var fields = (await FlowConfigLoader.CurrentAsync(db, flow, ct)).Fields;
        var read = DocumentJson.Deserialize<ReadResult>(doc.ReadJson);
        string? Text(string meaning) => read.Fields.FirstOrDefault(f => FieldMeanings.Of(f, fields) == meaning && !string.IsNullOrWhiteSpace(f.Value))?.Value;

        if (Text(FieldMeanings.ReceiverEndpointId) is { } endpoint)
            return PeppolParticipant.Parse(endpoint) is { } participant
                ? Ok(await lookup.LookupAsync(participant, derived: false, ct))
                : Ok(new PeppolReceiver(PeppolReceiverStatus.InvalidId, endpoint.Trim()[..Math.Min(60, endpoint.Trim().Length)], false, null, null));
        return PeppolParticipant.FromBelgianVat(Text(FieldMeanings.ReceiverVatId)) is { } fromVat
            ? Ok(await lookup.LookupAsync(fromVat, derived: true, ct))
            : Ok(new PeppolReceiver(PeppolReceiverStatus.NoId, null, false, null, null));
    }
}
