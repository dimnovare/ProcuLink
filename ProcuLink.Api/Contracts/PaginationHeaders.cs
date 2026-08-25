namespace ProcuLink.Api.Contracts;

/// <summary>
/// Response header names that carry paging metadata alongside a body that is a bare JSON array.
///
/// <para><b>Why headers and not an envelope.</b> Most paged endpoints here wrap their rows in a
/// <see cref="PaginatedResult{T}"/>-shaped body, and new endpoints should keep doing that. These
/// headers exist for the case where an endpoint is paginated AFTER it already has callers reading
/// its body as an array: wrapping the body would be a breaking change delivered as a silent one —
/// the caller would get an object where it expected an array and render nothing rather than fail
/// loudly. Adding headers is additive, so the existing caller is untouched and a future one can
/// build a pager.</para>
///
/// <para><b>These are useless unless CORS exposes them.</b> The browser app is a different origin
/// from this API in every deployed environment, and a cross-origin <c>fetch</c> can read only the
/// CORS-safelisted response headers unless the server names the others in
/// <c>Access-Control-Expose-Headers</c>. All three are listed in the <c>AllowFrontend</c> policy in
/// <c>Program.cs</c> for exactly that reason, and
/// <c>PaginationHeadersAreReadableCrossOriginTests</c> keeps them there.</para>
/// </summary>
public static class PaginationHeaders
{
    /// <summary>Total rows matching the filter, across all pages.</summary>
    public const string TotalCount = "X-Total-Count";

    /// <summary>The 1-based page index actually served (after clamping).</summary>
    public const string Page = "X-Page";

    /// <summary>The page size actually served (after clamping).</summary>
    public const string PageSize = "X-Page-Size";

    /// <summary>All three, for the CORS exposed-header list and the test that guards it.</summary>
    public static readonly string[] All = [TotalCount, Page, PageSize];
}
