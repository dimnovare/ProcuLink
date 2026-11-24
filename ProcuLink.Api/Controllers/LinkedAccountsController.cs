using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ProcuLink.Api.Auth;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Api.Services.Google;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Google;
using ProcuLink.Infrastructure.Services.Google;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// An org's connected accounts (plan 9b: Google; plan 30: Microsoft). Connecting and the Picker token need the native-connector
/// flag and the provider's config; listing and disconnecting do not, so an account can always be removed.
/// </summary>
[Authorize]
[ApiController]
[Route("api/linked-accounts")]
public sealed class LinkedAccountsController(
    ICurrentTenantService tenant, DocumentEngineGate gate, IOptions<GoogleOptions> google, LinkedAccountAvailability availability,
    GoogleConnectService connect, ILinkedAccountTokens tokens, ILogger<LinkedAccountsController> logger) : ControllerBase
{
    private IActionResult? Refuse() =>
        gate.IsEnabled(tenant.OrganisationId) ? null : StatusCode(403, new { error = "document_engine_not_enabled" });

    private IActionResult? RefuseConnect(string provider) =>
        Refuse() ?? (availability.On(tenant.OrganisationId, provider) ? null : StatusCode(403, new { error = "native_connectors_off" }));

    private static IActionResult Unavailable(string provider) => new ObjectResult(new { error = $"{provider}_{OAuthException.Unavailable}" }) { StatusCode = 502 };

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        return Ok(new { available = availability.On(tenant.OrganisationId, LinkedAccountProviders.Google), accounts = await connect.ListAsync(tenant.OrganisationId, ct) });
    }

    [HttpPost("{provider}/start")]
    [RequireOrgAdmin]
    public IActionResult Start(string provider, [FromBody] GoogleStartRequest request)
    {
        if (RefuseConnect(provider) is { } refused) return refused;
        try
        {
            return Ok(new { authorizeUrl = connect.Start(tenant.OrganisationId, tenant.ClerkUserId, provider, request.Origin, request.ReturnTo) });
        }
        catch (GoogleConnectException ex)
        {
            return StatusCode(ex.Status, new { error = ex.Code });
        }
    }

    [HttpPost("{provider}/complete")]
    [RequireOrgAdmin]
    public async Task<IActionResult> Complete(string provider, [FromBody] GoogleCompleteRequest request, CancellationToken ct)
    {
        if (RefuseConnect(provider) is { } refused) return refused;
        try
        {
            var (account, returnTo) = await connect.CompleteAsync(tenant.OrganisationId, tenant.ClerkUserId, provider, request.Code, request.State, ct);
            return Ok(new { account, returnTo });
        }
        catch (GoogleConnectException ex)
        {
            return StatusCode(ex.Status, new { error = ex.Code });
        }
        catch (OAuthException ex) when (ex.Code == "invalid_grant")
        {
            return BadRequest(new { error = "invalid_state" }); // used or expired code, or PKCE mismatch: only a fresh start works
        }
        catch (OAuthException ex)
        {
            logger.LogWarning("{Provider} account connect failed: {ProviderError}.", provider, ex.Code); // the provider's error value, never a token
            return Unavailable(provider);
        }
    }

    /// <summary>A short-lived access token for the browser's Google Picker: the one place a token is returned.</summary>
    [HttpPost("{id:guid}/picker")]
    [RequireOrgAdmin]
    public async Task<IActionResult> Picker(Guid id, CancellationToken ct)
    {
        if (RefuseConnect(LinkedAccountProviders.Google) is { } refused) return refused;
        var result = await tokens.GetAccessTokenAsync(tenant.OrganisationId, id, LinkedAccountProviders.Google, ct);
        if (result.Token is { } token)
            return Ok(new { accessToken = token, apiKey = google.Value.PickerApiKey, appId = google.Value.ProjectNumber });
        return result.Failure?.Outcome == DeliveryOutcome.NotRetryable ? Conflict(new { error = "account_reconnect" }) : Unavailable(LinkedAccountProviders.Google);
    }

    [HttpDelete("{id:guid}")]
    [RequireOrgAdmin]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        return await connect.DeleteAsync(tenant.OrganisationId, tenant.ClerkUserId, id, ct) switch
        {
            null => NotFound(new { error = "account_not_found" }),
            false => Conflict(new { error = "account_in_use" }),
            true => NoContent(),
        };
    }
}

public sealed record GoogleStartRequest(string Origin, string? ReturnTo);

public sealed record GoogleCompleteRequest(string Code, string State);
