using System.Reflection;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// WP-39 §4.1 — the passport reported PASSING checks as "validation issues".
///
/// On a clean, successfully delivered production order the audit trail rendered a green
/// "✓ Validated" node labelled "3 validation issues". <c>GET /api/orders/{id}/passport</c>
/// returned, verbatim:
///
/// <code>
/// {"code":"invariant.quantity_positive","lineNumber":1,"message":"Line 1: quantity 2 is valid.","severity":"error"}
/// {"code":"invariant.unit_price_valid","lineNumber":1,"message":"Line 1: unit price 376.2 is valid.","severity":"warning"}
/// {"code":"invariant.po_number_present","lineNumber":null,"message":"PO number is present.","severity":"error"}
/// {"code":"invariant.currency_present","lineNumber":null,"message":"Currency is set (EUR).","severity":"error"}
/// </code>
///
/// Every message says the check PASSED; three carry severity "error". Severity says how loud a
/// rule is when it fails, not whether it failed — <see cref="InvariantValidator"/> deliberately
/// emits a row per check performed so a rule-less order cannot show a vacuous green "Passed", and
/// it records the outcome in <see cref="OrderValidationResult.Status"/> ("pass" | "fail").
///
/// The passport projection dropped exactly that field, leaving severity as the only signal a
/// consumer could reach for. The fix is additive: carry <c>Status</c>. Suppressing passing rows
/// instead would resurrect the trust hole the invariants were built to close.
/// </summary>
public class PassportValidationOutcomeTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static readonly DateTime T0 = new(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);

    private static async Task<(Guid orgId, Guid orderId)> SeedOrderAsync(ProcuLinkDbContext db)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Northwind Trading OÜ", CreatedAt = T0 });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = "WP39-QA-001",
            OrderDate  = DateOnly.FromDateTime(T0),
            Currency   = "EUR",
            Status     = OrderStatusConstants.Delivered,
            CreatedAt  = T0,
            UpdatedAt  = T0.AddMinutes(5),
        });
        db.PurchaseOrderLines.Add(new PurchaseOrderLineEntity
        {
            Id               = Guid.NewGuid(),
            OrderId          = orderId,
            LineNumber       = 1,
            BuyerItemCode    = "00010",
            SupplierItemCode = "110C0Y3NL0",
            Description      = "Widget",
            Quantity         = 2m,
            Unit             = "EA",
            UnitPrice        = 376.20m,
            Confidence       = 1.0f,
            NeedsReview      = false,
        });

        await db.SaveChangesAsync();
        return (orgId, orderId);
    }

    private static OrderValidationResult Row(
        Guid orgId, Guid orderId, int? lineNumber, string code, string status, string severity, string message) => new()
    {
        Id         = Guid.NewGuid(),
        OrgId      = orgId,
        OrderId    = orderId,
        LineNumber = lineNumber,
        Severity   = severity,
        Status     = status,
        Code       = code,
        Message    = message,
        DetectedAt = T0,
    };

    /// <summary>
    /// The production payload, verbatim. Four checks, all of them PASSES, three of them
    /// carrying severity "error". Nothing here is a validation issue.
    /// </summary>
    [Fact]
    public async Task PassingChecks_WithErrorSeverity_AreReportedAsPasses()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db);

        db.OrderValidationResults.AddRange(
            Row(orgId, orderId, 1,    "invariant.quantity_positive",  "pass", "error",   "Line 1: quantity 2 is valid."),
            Row(orgId, orderId, 1,    "invariant.unit_price_valid",   "pass", "warning", "Line 1: unit price 376.2 is valid."),
            Row(orgId, orderId, null, "invariant.po_number_present",  "pass", "error",   "PO number is present."),
            Row(orgId, orderId, null, "invariant.currency_present",   "pass", "error",   "Currency is set (EUR)."));
        await db.SaveChangesAsync();

        var result = await new PassportService(db).GetAsync(orgId, orderId, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var rows = result.Value!.ValidationResults;
        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.Equal("pass", r.Status));
        Assert.Empty(rows.Where(r => r.Status == "fail"));
    }

    /// <summary>
    /// The other direction, and the reason severity cannot stand in for outcome: a row that
    /// really FAILED at severity "warning" must still read as a failure. A consumer filtering
    /// on severity would silently drop it.
    /// </summary>
    [Fact]
    public async Task FailingCheck_WithWarningSeverity_IsReportedAsFail()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db);

        db.OrderValidationResults.Add(
            Row(orgId, orderId, 1, "rule.description_length", "fail", "warning", "Line 1: description is longer than 40 characters."));
        await db.SaveChangesAsync();

        var result = await new PassportService(db).GetAsync(orgId, orderId, CancellationToken.None);

        var row = Assert.Single(result.Value!.ValidationResults);
        Assert.Equal("fail", row.Status);
        Assert.Equal("warning", row.Severity);
    }

    /// <summary>
    /// The structural guard, bound to the sibling projection rather than to a copied field list.
    ///
    /// <see cref="OrderValidationResultDto"/> and <see cref="PassportValidationResult"/> are two
    /// projections of the same entity, shipped to the same frontend. Whatever the sibling
    /// considers necessary to describe a row, the passport needs too — this test fails if a
    /// field is added to one and forgotten on the other, which is exactly how <c>Status</c> went
    /// missing. Computed members of the sibling (e.g. <c>Title</c>) are excluded by construction:
    /// the walk only considers sibling properties that exist on the entity.
    /// </summary>
    [Fact]
    public void PassportValidationDto_CarriesEveryEntityFieldTheSiblingProjectionCarries()
    {
        static HashSet<string> Names(Type t) =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToHashSet();

        var entity   = Names(typeof(OrderValidationResult));
        var sibling  = Names(typeof(OrderValidationResultDto));
        var passport = Names(typeof(PassportValidationResult));

        var shared = sibling.Intersect(entity).ToHashSet();

        // Anti-vacuity floor: if reflection ever returns an empty or shrunken set the assertion
        // below passes for the wrong reason. These five are what both projections describe today.
        Assert.Equal(
            new[] { "Code", "LineNumber", "Message", "Severity", "Status" },
            shared.OrderBy(n => n, StringComparer.Ordinal).ToArray());

        var missing = shared.Except(passport).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.True(
            missing.Length == 0,
            $"PassportValidationResult drops {string.Join(", ", missing)} — field(s) the sibling "
          + $"projection over the same entity carries. A consumer cannot recover them.");
    }
}
