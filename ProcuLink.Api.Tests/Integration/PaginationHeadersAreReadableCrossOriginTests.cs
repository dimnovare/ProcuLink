using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcuLink.Api.Contracts;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

// ════════════════════════════════════════════════════════════════════════════
//  GET /api/exceptions is paginated but keeps a bare JSON array as its body, so
//  its existing caller does not break. That leaves the response headers as the
//  ONLY place the row count can ride.
//
//  A header the browser cannot read is a total that does not exist. The app is
//  a different origin from this API in every deployed environment, and a
//  cross-origin fetch can read only the CORS-safelisted response headers unless
//  the server names the rest in Access-Control-Expose-Headers. AllowAnyHeader
//  governs REQUEST headers and says nothing about this — the same trap that
//  once made Retry-After unreadable.
//
//  So the guard is deliberately not "the endpoint sets the header" (which is
//  covered by ExceptionsListPaginationTests and would pass while the number was
//  invisible to every real caller); it is that the policy exposes it.
// ════════════════════════════════════════════════════════════════════════════

[Collection("postgres-container")]
public sealed class PaginationHeadersAreReadableCrossOriginTests : IClassFixture<HardeningTestFactory>
{
    private readonly HardeningTestFactory _factory;

    public PaginationHeadersAreReadableCrossOriginTests(HardeningTestFactory factory) => _factory = factory;

    [Theory]
    [InlineData(PaginationHeaders.TotalCount)]
    [InlineData(PaginationHeaders.Page)]
    [InlineData(PaginationHeaders.PageSize)]
    public void CorsPolicy_ExposesEachPagingHeader(string header)
    {
        var policy = _factory.Services
            .GetRequiredService<IOptions<CorsOptions>>()
            .Value
            .GetPolicy("AllowFrontend");

        Assert.True(policy is not null, "The AllowFrontend CORS policy must exist.");
        Assert.Contains(header, policy!.ExposedHeaders);
    }

    /// <summary>
    /// The list the CORS policy is built from must stay complete: adding a fourth header name to
    /// <see cref="PaginationHeaders"/> and forgetting to route it through <c>All</c> would leave
    /// it unexposed with every test above still green.
    /// </summary>
    [Fact]
    public void PaginationHeaders_AllContainsEveryDeclaredHeaderName()
    {
        var declared = typeof(PaginationHeaders)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(declared);
        Assert.Equal(declared.OrderBy(h => h, StringComparer.Ordinal),
                     PaginationHeaders.All.OrderBy(h => h, StringComparer.Ordinal));
    }
}
