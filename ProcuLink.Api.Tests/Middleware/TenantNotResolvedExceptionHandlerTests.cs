using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Middleware;
using ProcuLink.Api.Services;
using ProcuLink.Core.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Middleware;

/// <summary>
/// The mapping, examined one layer below HTTP: which exceptions
/// <see cref="TenantNotResolvedExceptionHandler"/> claims, which it refuses, and what it leaves on
/// the response either way.
///
/// <para><see cref="Integration.UnresolvedTenantAnswers503Tests"/> proves the same thing over the
/// real pipeline and is the primary evidence. These pin the parts that are hard to observe from
/// outside: that an unrecognised exception is returned UNHANDLED rather than quietly given a 503,
/// that the 503 survives a body writer refusing the write, and that the two throw sites in
/// <see cref="CurrentTenantService"/> really do carry different types — which is the entire reason
/// the mapping can be precise at all.</para>
/// </summary>
public class TenantNotResolvedExceptionHandlerTests
{
    // ── Test doubles ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Captures the ProblemDetails the handler asks for, and can refuse the write so the handler's
    /// behaviour on that branch is observable.
    /// </summary>
    private sealed class CapturingProblemDetailsService : IProblemDetailsService
    {
        public ProblemDetailsContext? Captured { get; private set; }
        public bool WriteSucceeds { get; init; } = true;

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            Captured = context;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            Captured = context;
            return ValueTask.FromResult(WriteSucceeds);
        }
    }

    private static TenantNotResolvedExceptionHandler NewHandler(CapturingProblemDetailsService problems) =>
        new(problems, NullLogger<TenantNotResolvedExceptionHandler>.Instance);

    // ── 1. The exception it exists for ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TenantNotResolved_Is503_WithRetryAfter_AndAnRfc7807Body()
    {
        var problems = new CapturingProblemDetailsService();
        var ctx = new DefaultHttpContext();

        var handled = await NewHandler(problems)
            .TryHandleAsync(ctx, new TenantNotResolvedException(), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ctx.Response.StatusCode);
        Assert.Equal("2", ctx.Response.Headers.RetryAfter.ToString());

        var problem = Assert.IsType<ProblemDetailsContext>(problems.Captured).ProblemDetails;
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.Status);
        Assert.False(string.IsNullOrWhiteSpace(problem.Title));
        Assert.False(string.IsNullOrWhiteSpace(problem.Detail));
    }

    /// <summary>
    /// Returning false when the body could not be written would hand the request back to the
    /// default path, which overwrites the status with 500 — reinstating the defect in exactly the
    /// case where the least is known. A bodiless 503 + Retry-After is the right answer there, and
    /// it is what <see cref="TenantResolutionMiddleware"/> already returns for its own 503.
    /// </summary>
    [Fact]
    public async Task TenantNotResolved_StaysA503_EvenWhenNoWriterTakesTheBody()
    {
        var problems = new CapturingProblemDetailsService { WriteSucceeds = false };
        var ctx = new DefaultHttpContext();

        var handled = await NewHandler(problems)
            .TryHandleAsync(ctx, new TenantNotResolvedException(), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ctx.Response.StatusCode);
        Assert.Equal("2", ctx.Response.Headers.RetryAfter.ToString());
    }

    // ── 2. Everything else is left alone ────────────────────────────────────────────────────────

    /// <summary>
    /// The sibling. <c>ICurrentTenantService.ClerkUserId</c> throws this for "no sub claim — there
    /// is no authenticated user at all", which is not retryable and must keep its existing answer.
    /// A handler written against <see cref="UnauthorizedAccessException"/> — the type BOTH throw
    /// sites used to share — would swallow it here.
    /// </summary>
    [Theory]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(Exception))]
    public async Task AnythingElse_IsReturnedUnhandled_WithTheResponseUntouched(Type exceptionType)
    {
        var problems = new CapturingProblemDetailsService();
        var ctx = new DefaultHttpContext();
        var exception = (Exception)Activator.CreateInstance(exceptionType, "not a tenant problem")!;

        var handled = await NewHandler(problems).TryHandleAsync(ctx, exception, CancellationToken.None);

        Assert.False(handled);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode); // untouched — the default path still owns it
        Assert.True(string.IsNullOrEmpty(ctx.Response.Headers.RetryAfter.ToString()));
        Assert.Null(problems.Captured);
    }

    // ── 3. The two throw sites are genuinely different types ────────────────────────────────────

    /// <summary>
    /// The precondition the whole mapping rests on. If these two ever share a type again, the
    /// handler above either misses the case it exists for or swallows the one it must not — and
    /// nothing else in the suite would notice, because both throws come from the same two lines of
    /// the same property pair.
    /// </summary>
    [Fact]
    public void CurrentTenantService_ThrowsADifferentTypeForEachMissingThing()
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var tenant = new CurrentTenantService(accessor);

        // No organisation stamped in Items → retryable, mapped to 503.
        var unresolved = Assert.Throws<TenantNotResolvedException>(() => tenant.OrganisationId);
        Assert.Equal(TenantNotResolvedException.DefaultMessage, unresolved.Message);

        // No sub claim → not retryable, deliberately NOT the type above.
        var unauthenticated = Assert.Throws<UnauthorizedAccessException>(() => tenant.ClerkUserId);
        Assert.IsNotType<TenantNotResolvedException>(unauthenticated);
        Assert.Contains("No sub claim found", unauthenticated.Message);
    }

    /// <summary>The resolved case, so the two throws above are not the only outcomes proved.</summary>
    [Fact]
    public void CurrentTenantService_ServesBothWhenTheRequestCarriesThem()
    {
        var orgId = Guid.NewGuid();
        var http = new DefaultHttpContext();
        http.Items[CurrentTenantService.Items.OrganisationId] = orgId;
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("sub", "user_RESOLVED") }, authenticationType: "test"));

        var tenant = new CurrentTenantService(new HttpContextAccessor { HttpContext = http });

        Assert.Equal(orgId, tenant.OrganisationId);
        Assert.Equal("user_RESOLVED", tenant.ClerkUserId);
    }
}
