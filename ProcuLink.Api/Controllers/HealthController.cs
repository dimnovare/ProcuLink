using Microsoft.AspNetCore.Mvc;

namespace ProcuLink.Api.Controllers;

[ApiController]
public class HealthController : ControllerBase
{
    /// <summary>
    /// Health check endpoint
    /// </summary>
    [HttpGet("/health")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    public IActionResult Health()
    {
        return Ok("OK");
    }
}
