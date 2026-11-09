using Microsoft.AspNetCore.Mvc;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;

namespace ProcuLink.Api.Controllers;

/// <summary>Maps a <see cref="FlowSubmitResult"/> to the HTTP answer every intake route gives.</summary>
public static class IntakeResults
{
    public static IActionResult From(ControllerBase c, FlowSubmitResult r, string flowName, IReadOnlyList<string> accepted)
    {
        if (r.Document is { } d)
            return c.Created($"/api/documents/{d.Id}", DocumentSummaryDto.From(d, flowName));
        return r.Error switch
        {
            "document_engine_not_enabled" => c.StatusCode(403, new { error = r.Error }),
            "flow_not_found" => c.NotFound(new { error = r.Error }),
            "pilot_expired" or "order_limit_reached" => c.StatusCode(429, new
            {
                error = r.Error, plan = r.Limit?.Plan, limit = r.Limit?.Limit, upgradeUrl = "/settings",
            }),
            "unsupported_format" => c.StatusCode(415, new { error = r.Error, accepted }),
            "file_too_large" => c.StatusCode(413, new { error = r.Error }),
            "unreadable_file" => c.StatusCode(422, new { error = r.Error }),
            _ => throw new InvalidOperationException($"Unmapped submit error '{r.Error}'."),
        };
    }
}
