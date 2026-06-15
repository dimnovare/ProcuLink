using System.Globalization;
using ProcuLink.Core.Entities;

namespace ProcuLink.Api.Services;

/// <summary>
/// Mandatory, supplier-INDEPENDENT order safety checks. These ALWAYS run for every order
/// regardless of whether a supplier acceptance profile exists, so a genuinely invalid order
/// (negative quantity, missing price, no currency, no identifier) can never surface as a clean
/// "Passed" — closing the trust hole where an order with no configured rules, or an order with a
/// nonsense value, was reported green ("meets all acceptance rules").
///
/// Invariant rows are tagged by a <c>invariant.*</c> <see cref="OrderValidationResult.Code"/> and
/// carry NO <see cref="OrderValidationResult.ProfileId"/> / <see cref="OrderValidationResult.RuleId"/>
/// (they belong to no supplier profile). The frontend uses the code prefix to separate "safety
/// checks" from "supplier acceptance rules" and to decide when "no rules" must read as
/// "Not checked", never "Passed".
/// </summary>
public static class InvariantValidator
{
    public const string CodePrefix = "invariant.";

    /// <summary>True when <paramref name="code"/> is an invariant safety-check code (not a supplier rule).</summary>
    public static bool IsInvariantCode(string? code) =>
        code is not null && code.StartsWith(CodePrefix, StringComparison.Ordinal);

    /// <summary>
    /// Evaluate the mandatory invariants against a loaded order. Pure — touches no database; the
    /// returned rows are detached (the caller persists them alongside the supplier-rule rows).
    /// </summary>
    public static IReadOnlyList<OrderValidationResult> Evaluate(
        Guid orgId, Guid orderId, PurchaseOrderEntity order, DateTime now)
    {
        var results = new List<OrderValidationResult>();

        // ── Order-level invariants ────────────────────────────────────────────────
        results.Add(Row(orgId, orderId, null,
            code: CodePrefix + "po_number_present",
            pass: !string.IsNullOrWhiteSpace(order.PoNumber),
            severity: "error",
            passMsg: "PO number is present.",
            failMsg: "PO number is missing — every order must carry an identifier.",
            now: now));

        results.Add(Row(orgId, orderId, null,
            code: CodePrefix + "currency_present",
            pass: !string.IsNullOrWhiteSpace(order.Currency),
            severity: "error",
            passMsg: $"Currency is set ({order.Currency}).",
            failMsg: "Currency is missing — every order must state a currency.",
            now: now));

        // ── Line-level invariants ─────────────────────────────────────────────────
        foreach (var line in order.Lines)
        {
            // Quantity must be strictly positive. A zero or negative quantity (e.g. a parser that
            // swept "-3" through with no sign check) is unambiguously invalid → error.
            results.Add(Row(orgId, orderId, line.LineNumber,
                code: CodePrefix + "quantity_positive",
                pass: line.Quantity > 0m,
                severity: "error",
                passMsg: $"Line {line.LineNumber}: quantity {Fmt(line.Quantity)} is valid.",
                failMsg: $"Line {line.LineNumber}: quantity is {Fmt(line.Quantity)} — must be greater than zero.",
                now: now));

            // Unit price: negative is invalid (error); zero is suspicious but legitimate for a free
            // line (warning) — flag, never silently accept. A non-numeric source price parses to 0
            // and is also caught here.
            var priceOk = line.UnitPrice > 0m;
            var priceSeverity = line.UnitPrice < 0m ? "error" : "warning";
            results.Add(Row(orgId, orderId, line.LineNumber,
                code: CodePrefix + "unit_price_valid",
                pass: priceOk,
                severity: priceSeverity,
                passMsg: $"Line {line.LineNumber}: unit price {Fmt(line.UnitPrice)} is valid.",
                failMsg: line.UnitPrice < 0m
                    ? $"Line {line.LineNumber}: unit price is {Fmt(line.UnitPrice)} — cannot be negative."
                    : $"Line {line.LineNumber}: unit price is zero — confirm this is intentional.",
                now: now));
        }

        return results;
    }

    private static OrderValidationResult Row(
        Guid orgId, Guid orderId, int? lineNumber,
        string code, bool pass, string severity, string passMsg, string failMsg, DateTime now) => new()
    {
        Id         = Guid.NewGuid(),
        OrgId      = orgId,
        OrderId    = orderId,
        ProfileId  = null,   // invariants belong to no supplier profile
        RuleId     = null,
        LineNumber = lineNumber,
        Severity   = severity,
        Status     = pass ? "pass" : "fail",
        Code       = code,
        Message    = pass ? passMsg : failMsg,
        DetectedAt = now,
    };

    private static string Fmt(decimal d) => d.ToString("0.####", CultureInfo.InvariantCulture);
}
