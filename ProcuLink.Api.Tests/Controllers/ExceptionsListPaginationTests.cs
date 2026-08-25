using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

// ════════════════════════════════════════════════════════════════════════════
//  GET /api/exceptions returned the organisation's ENTIRE all-time exception
//  history in one response.
//
//  Nothing deletes an exception row: resolving or ignoring one flips its State
//  and leaves it in place, so the table grows monotonically from an account's
//  first order onward and the response size is a function of account age.
//
//  The fix carries a compatibility constraint as important as the bound itself,
//  and these tests pin BOTH halves:
//
//   (a) the body stays a bare JSON array. The browser app
//       (project-proculink/src/lib/api/operations.ts, realGetExceptions) does
//       `res.json() as Promise<OrderException[]>` and then spreads it. Handing
//       that caller an envelope object would not surface as a useful error — it
//       would render an empty work list — and the frontend is NOT being changed
//       in this wave. So the total rides on response headers, which is additive.
//
//   (b) an existing caller supplies no paging parameters at all, so the DEFAULT
//       window has to be one that does not visibly truncate a real workspace.
//       It is the clamp ceiling, 200.
// ════════════════════════════════════════════════════════════════════════════

public class ExceptionsListPaginationTests
{
    private static (ExceptionsController Ctrl, Mock<IOrderExceptionService> Svc, Guid OrgId) Build()
    {
        var orgId  = Guid.NewGuid();
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var svc  = new Mock<IOrderExceptionService>();
        var ctrl = new ExceptionsController(svc.Object, tenant.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return (ctrl, svc, orgId);
    }

    private static OrderException Row(Guid orgId) => new()
    {
        Id = Guid.NewGuid(), OrgId = orgId, OrderId = Guid.NewGuid(),
        Stage = "Map", Code = "unresolved_mapping", Severity = "warning",
        State = "open", Message = "needs a supplier item code", CreatedAt = DateTime.UtcNow,
    };

    private static void SetupPage(
        Mock<IOrderExceptionService> svc, Guid orgId, IReadOnlyList<OrderException> rows,
        int total, int page, int pageSize) =>
        svc.Setup(s => s.ListAsync(orgId, It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(),
                                   It.IsAny<CancellationToken>()))
           .ReturnsAsync(new OrderExceptionPage(rows, total, page, pageSize));

    // ── (a) backward compatibility ───────────────────────────────────────────

    /// <summary>
    /// The one assertion the untouched frontend depends on. If this fails, the browser app's
    /// exceptions page silently shows nothing.
    /// </summary>
    [Fact]
    public async Task List_BodyIsStillABareArrayOfDtos_NotAnEnvelope()
    {
        var (ctrl, svc, orgId) = Build();
        SetupPage(svc, orgId, new[] { Row(orgId), Row(orgId) }, total: 2, page: 1, pageSize: 200);

        var ok = (await ctrl.List(null, ct: CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject;

        ok.Value.Should().BeAssignableTo<IEnumerable<OrderExceptionDto>>(
                "the browser app reads this body as OrderException[] and is not being changed")
            .Which.Should().HaveCount(2);
    }

    /// <summary>
    /// A caller that passes nothing must get the clamp ceiling, not a tidy-looking 25 or 50.
    /// The endpoint had no window at all until now, so anything smaller silently truncates a
    /// live operator's work list.
    /// </summary>
    [Fact]
    public async Task List_WithNoQueryParameters_AsksForPageOneAtTheCeiling()
    {
        var (ctrl, svc, orgId) = Build();
        SetupPage(svc, orgId, Array.Empty<OrderException>(), total: 0, page: 1, pageSize: 200);

        await ctrl.List(null, ct: CancellationToken.None);

        svc.Verify(s => s.ListAsync(orgId, null, 1, OrderExceptionPaging.DefaultPageSize,
                                    It.IsAny<CancellationToken>()), Times.Once);
        OrderExceptionPaging.DefaultPageSize.Should().Be(OrderExceptionPaging.MaxPageSize,
            "the default is the ceiling precisely because the endpoint used to be unbounded");
    }

    // ── (b) the window is honoured, and reported ─────────────────────────────

    [Fact]
    public async Task List_PassesPageAndPageSizeAndStateThrough()
    {
        var (ctrl, svc, orgId) = Build();
        SetupPage(svc, orgId, Array.Empty<OrderException>(), total: 0, page: 3, pageSize: 25);

        await ctrl.List("open", page: 3, pageSize: 25, ct: CancellationToken.None);

        svc.Verify(s => s.ListAsync(orgId, "open", 3, 25, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task List_ReportsTotalAndAppliedWindowOnHeaders()
    {
        var (ctrl, svc, orgId) = Build();
        SetupPage(svc, orgId, new[] { Row(orgId) }, total: 4_812, page: 2, pageSize: 200);

        await ctrl.List(null, page: 2, ct: CancellationToken.None);

        var headers = ctrl.Response.Headers;
        headers[PaginationHeaders.TotalCount].ToString().Should().Be("4812",
            "a pager cannot be rendered from a page of rows alone");
        headers[PaginationHeaders.Page].ToString().Should().Be("2");
        headers[PaginationHeaders.PageSize].ToString().Should().Be("200");
    }

    /// <summary>
    /// The headers report what was SERVED, not what was ASKED FOR. A caller that requests
    /// pageSize=5000 receives 200 rows; telling it "pageSize: 5000" would make it conclude it
    /// had reached the end of a 200-row history and stop paging.
    /// </summary>
    [Fact]
    public async Task List_HeadersReportTheClampedWindow_NotTheRequestedOne()
    {
        var (ctrl, svc, orgId) = Build();
        // What the service does with an over-large ask: clamps, and says so on the result.
        SetupPage(svc, orgId, Array.Empty<OrderException>(), total: 900, page: 1,
                  pageSize: OrderExceptionPaging.MaxPageSize);

        await ctrl.List(null, page: 1, pageSize: 5000, ct: CancellationToken.None);

        ctrl.Response.Headers[PaginationHeaders.PageSize].ToString()
            .Should().Be(OrderExceptionPaging.MaxPageSize.ToString(),
                "the caller got the ceiling, so it must be told the ceiling");
    }
}
