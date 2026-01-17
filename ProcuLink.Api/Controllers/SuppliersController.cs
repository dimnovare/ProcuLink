using Microsoft.AspNetCore.Mvc;

namespace ProcuLink.Api.Controllers;

[ApiController]
[Route("api/suppliers")]
public class SuppliersController : ControllerBase
{
    /// <summary>
    /// Get list of available suppliers (hardcoded for MVP)
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(string[]), StatusCodes.Status200OK)]
    public IActionResult GetSuppliers()
    {
        var suppliers = new[]
        {
            "Acme Industrial Supplies",
            "Global Parts Co.",
            "TechComponents Ltd.",
            "FastTrack Distribution",
            "Precision Manufacturing Inc."
        };

        return Ok(suppliers);
    }
}
