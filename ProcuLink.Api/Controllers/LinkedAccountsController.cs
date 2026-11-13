using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ProcuLink.Api.Auth;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Api.Services.Google;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Google;
using ProcuLink.Infrastructure.Services.Google;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// An org's connected Google accounts (plan 9b). Connecting and the Picker token need the native-connector
/// flag and Google config (R2); listing and disconnecting do not, so an account can always be removed.
/// </summary>
[Authorize]
[ApiController]
[Route("api/linked-accounts")]
public sealed class LinkedAccountsController(
    ICurrentTenantService tenant, DocumentEngineGate gate, IOptions<GoogleOptions> google,
    GoogleConnectService connect, ILinkedAccountTokens tokens, ILogger<LinkedAccountsController> logger) : ControllerBase
{
    private IActionResult? Refuse() =>
        gate.IsEnabled(tenant.OrganisationId) ? null : StatusCode(403, new { error = "document_engine_not_enabled" });

    private bool Available => gate.NativeConnectorsOn(tenant.OrganisationId) && google.Value.IsConfigured;

    private IActionResult? RefuseConnect() =>
        Refuse() ?? (Available ? null : StatusCode(403, new { error = "native_connectors_off" }));

    private static IActionResult Unavailable() => new ObjectResult(new { error = GoogleAuthException.Unavailable }) { StatusCode = 502 };

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        return Ok(new { available = Available, accounts = await connect.ListAsync(tenant.OrganisationId, ct) });
    }

    [HttpPost("google/start")]
    [RequireOrgAdmin]
    public IActionResult Start([FromBody] GoogleStartRequest request)
    {
        if (RefuseConnect() is { } refused) return refused;
        try
        {
            return Ok(new { authorizeUrl = connect.Start(tenant.OrganisationId, tenant.ClerkUserId, request.Origin, request.ReturnTo) });
        }
        catch (GoogleConnectException ex)
        {
            return StatusCode(ex.Status, new { error = ex.Code });
        }
    }

    [HttpPost("google/complete")]
    [RequireOrgAdmin]
    public async Task<IActionResult> Complete([FromBody] GoogleCompleteRequest request, CancellationToken ct)
    {
        if (RefuseConnect() is { } refused) return refused;
        try
        {
            var (account, returnTo) = await connect.CompleteAsync(tenant.OrganisationId, tenant.ClerkUserId, request.Code, request.State, ct);
            return Ok(new { account, returnTo });
        }
        catch (GoogleConnectException ex)
        {
            return StatusCode(ex.Status, new { error = ex.Code });
        }
        catch (GoogleAuthException ex)
        {
            logger.LogWarning("Google account connect failed: {GoogleError}.", ex.Code); // Google's error value, never a token
            return Unavailable();
        }
    }

    /// <summary>A short-lived access token for the browser's Google Picker: the one place a token is returned.</summary>
    [HttpPost("{id:guid}/picker")]
    [RequireOrgAdmin]
    public async Task<IActionResult> Picker(Guid id, CancellationToken ct)
    {
        if (RefuseConnect() is { } refused) return refused;
        var result = await tokens.GetGoogleAccessTokenAsync(tenant.OrganisationId, id, ct);
        if (result.Token is { } token)
            return Ok(new { accessToken = token, apiKey = google.Value.PickerApiKey, appId = google.Value.ProjectNumber });
        return result.Failure?.Outcome == DeliveryOutcome.NotRetryable ? Conflict(new { error = "account_reconnect" }) : Unavailable();
    }

    [HttpDelete("{id:guid}")]
    [RequireOrgAdmin]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        return await connect.DeleteAsync(tenant.OrganisationId, id, ct) switch
        {
            null => NotFound(new { error = "account_not_found" }),
            false => Conflict(new { error = "account_in_use" }),
            true => NoContent(),
        };
    }
}

public sealed record GoogleStartRequest(string Origin, string? ReturnTo);

public sealed record GoogleCompleteRequest(string Code, string State);
