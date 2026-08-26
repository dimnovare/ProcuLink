using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Middleware;

/// <summary>
/// Answers <see cref="TenantNotResolvedException"/> with 503 + Retry-After.
///
/// <para><b>The defect this closes.</b> Every tenant-scoped endpoint reads
/// <c>ICurrentTenantService.OrganisationId</c>, which throws when the request carries no resolved
/// organisation. Nothing mapped that throw, so <c>UseExceptionHandler()</c> gave it the default
/// answer for an unrecognised exception — HTTP 500. In production it fires on a brand-new
/// organisation's first page load, because Clerk mints the session token before the organisation
/// claim is attached to it. A request that is merely EARLY was being reported as a server fault,
/// four to five times on every scheduled smoke run.</para>
///
/// <para><b>Why 503 + Retry-After, and not 401 or 403.</b> Both were considered and rejected. A 401
/// makes the frontend sign the user out — it would take a customer who is correctly signed in and
/// throw them back to the sign-in screen for a condition that clears by itself in a second. A 403
/// reads as "you may not have this", a settled verdict, when the truth is "not yet — try again in a
/// moment". Both are actively misleading; 503 + Retry-After is the one answer that is true, and it
/// is the answer a client can act on without being told anything false.</para>
///
/// <para><b>Precedent.</b> This is the same answer <see cref="TenantResolutionMiddleware"/> already
/// gives for the sibling condition — tenant resolution could not reach the database — down to
/// sharing its <see cref="TenantResolutionMiddleware.RetryAfterSeconds"/>, so a client cannot see
/// two different retry cadences for what is, from its side, one situation: "no tenant right now".
/// The two differ only in where the request gave up.</para>
///
/// <para><b>Scope.</b> Deliberately narrow — only <see cref="TenantNotResolvedException"/>. Mapping
/// every <see cref="UnauthorizedAccessException"/> here would also swallow
/// <c>ICurrentTenantService.ClerkUserId</c>'s "no sub claim found", which means there is no
/// authenticated user at all: a genuinely different condition that must keep failing the way it
/// does today. Anything this handler does not recognise is returned unhandled, so the default
/// ProblemDetails path still owns it.</para>
/// </summary>
public sealed class TenantNotResolvedExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;
    private readonly ILogger<TenantNotResolvedExceptionHandler> _logger;

    public TenantNotResolvedExceptionHandler(
        IProblemDetailsService problemDetails,
        ILogger<TenantNotResolvedExceptionHandler> logger)
    {
        _problemDetails = problemDetails;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not TenantNotResolvedException) return false;

        _logger.LogWarning(
            "No organisation resolved for {Method} {Path}; answering 503 so the caller can retry.",
            httpContext.Request.Method,
            httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers.RetryAfter = TenantResolutionMiddleware.RetryAfterSeconds;

        // RFC-7807, the same shape every other error on this API returns — and the same fields, so
        // nothing internal is disclosed. Detail says what the caller should do, not what went wrong
        // inside. (In Development, Program.cs's CustomizeProblemDetails additionally attaches the
        // exception message and stack; that is gated on Development and never reaches production.)
        var written = await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc9110#section-15.6.4",
                Title = "Workspace not ready",
                Status = StatusCodes.Status503ServiceUnavailable,
                Detail = "This request is not associated with a workspace yet. "
                       + "That is normally momentary on a newly created workspace — retry shortly.",
            },
        });

        // Handled either way. If no writer took the body we still leave a bodiless 503 +
        // Retry-After, which is exactly what TenantResolutionMiddleware returns for the sibling
        // condition. Returning false here would hand the request back to the default path, which
        // would overwrite the status with 500 — reinstating the defect in the one case where the
        // body could not be written.
        if (!written)
        {
            _logger.LogDebug("No ProblemDetails writer accepted the 503 body; returning it bodiless.");
        }

        return true;
    }
}
