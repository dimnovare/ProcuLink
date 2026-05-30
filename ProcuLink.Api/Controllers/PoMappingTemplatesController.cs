using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Api.Services.StarterTemplates;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// Serves the static read-only PO mapping starter templates (Erply, Directo, …).
/// No org scoping — templates are global, not per-tenant.
/// </summary>
[Authorize]
[ApiController]
[Route("api/po-mapping-templates")]
public sealed class PoMappingTemplatesController : ControllerBase
{
    private readonly IStarterTemplateService _templates;

    public PoMappingTemplatesController(IStarterTemplateService templates)
    {
        _templates = templates;
    }

    /// <summary>Returns all available PO mapping starter templates.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetAll()
    {
        return Ok(_templates.GetAll());
    }
}
