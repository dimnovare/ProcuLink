using System;
using System.Text.Json;
using ProcuLink.Core.Entities;
using ProcuLink.Api.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Phase 2 (D slice) deterministic unit tests for the four new acceptance operators added to the
/// EXISTING pure <see cref="SupplierAcceptanceService.EvaluateProfile"/> executor:
/// <c>date_sanity</c> (printed-date MM/DD vs DD/MM flip risk, read from the lossless
/// <see cref="SourceCapture"/> raw-token bag — no <c>DeliveryDateRaw</c> column exists),
/// <c>not_label</c> (a parser swept a label cell into a data field),
/// <c>line_amount_reconcile</c> (stated line amount vs qty × price, EU-aware), and
/// <c>vat_format</c> (plausible per-country VAT shape — advisory).
/// </summary>
public class SupplierAcceptanceNewOperatorsTests
{
    private static SupplierAcceptanceProfile Profile(params SupplierAcceptanceRule[] rules) =>
        new() { Id = Guid.NewGuid(), Rules = new(rules) };

    private static SupplierAcceptanceRule Rule(string scope, string field, string op, string? expected = null, string severity = "warning") =>
        new() { Id = Guid.NewGuid(), Scope = scope, FieldPath = field, Operator = op, ExpectedValue = expected, Severity = severity };

    private static SourceCapture Capture(string json) => new()
    {
        Id = Guid.NewGuid(), Format = "csv", CapturedAt = DateTime.UtcNow,
        TokensJson = JsonDocument.Parse(json),
    };

    [Fact]
    public void Date_sanity_fails_an_ambiguous_flip_candidate()
    {
        // "06/12/2026" — both day and month ≤ 12 → MM/DD vs DD/MM is ambiguous → fail (flag for review).
        // The ORIGINAL printed string lives in the SourceCapture raw bag (no typed raw-date column).
        var order = new PurchaseOrderEntity
        {
            Currency = "EUR",
            SourceCapture = Capture("""[{"id":"raw:Delivery date","label":"Delivery date","value":"06/12/2026","group":"header"}]"""),
        };
        var results = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(Rule("order", "sourceDate", "date_sanity")), order, DateTime.UtcNow);
        Assert.Contains(results, r => r.Status == "fail");
    }

    [Fact]
    public void Date_sanity_passes_a_disambiguated_date()
    {
        // "25/12/2026" — 25 > 12 disambiguates (must be DD/MM) → pass.
        var order = new PurchaseOrderEntity
        {
            Currency = "EUR",
            SourceCapture = Capture("""[{"id":"raw:Delivery date","label":"Delivery date","value":"25/12/2026","group":"header"}]"""),
        };
        var results = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(Rule("order", "sourceDate", "date_sanity")), order, DateTime.UtcNow);
        Assert.Contains(results, r => r.Status == "pass");
    }

    /// <summary>
    /// This test used to assert <c>pass</c>, and that assertion is why the vacuity survived: the
    /// rule reported a clean check on a document from which no date was ever captured. Absence is
    /// still not a FAILURE (that is 'required''s job) — but it is not a pass either.
    /// </summary>
    [Fact]
    public void Date_sanity_is_not_evaluated_when_there_is_no_date_like_token()
    {
        var order = new PurchaseOrderEntity
        {
            Currency = "EUR",
            SourceCapture = Capture("""[{"id":"raw:Note","label":"Note","value":"handle with care","group":"header"}]"""),
        };
        var results = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(Rule("order", "sourceDate", "date_sanity")), order, DateTime.UtcNow);

        Assert.Contains(results, r => r.Status == OrderValidationResult.StatusNotEvaluated);
        Assert.DoesNotContain(results, r => r.Status == OrderValidationResult.StatusPass);
        Assert.DoesNotContain(results, r => r.Status == OrderValidationResult.StatusFail);
    }

    [Fact]
    public void Not_label_fails_when_city_is_a_label_word()
    {
        var order = new PurchaseOrderEntity { Currency = "EUR", BuyerName = "X" };
        // shipToCity resolves from the first shipTo OrderParty.City — seed it with a swept label cell.
        order.Parties.Add(new OrderParty { Role = "shipTo", City = "UIDNr. ATU" });
        var rule = Rule("order", "shipToCity", "not_label", expected: "UIDNr,City,VAT,UID");
        var results = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(rule), order, DateTime.UtcNow);
        Assert.Contains(results, r => r.Status == "fail");
    }

    [Fact]
    public void Not_label_passes_a_real_city()
    {
        var order = new PurchaseOrderEntity { Currency = "EUR" };
        order.Parties.Add(new OrderParty { Role = "shipTo", City = "Teststadt" });
        var rule = Rule("order", "shipToCity", "not_label", expected: "UIDNr,City,VAT,UID");
        var results = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(rule), order, DateTime.UtcNow);
        Assert.Contains(results, r => r.Status == "pass");
    }

    [Fact]
    public void Not_label_passes_a_value_that_merely_starts_with_a_label_word()
    {
        // "Cityville" starts with the label "City" but continues with a LETTER → it is a real city,
        // not a swept label cell. The tightened boundary check must NOT false-positive it. A bare
        // StartsWith (the old behaviour) would have wrongly failed this.
        var order = new PurchaseOrderEntity { Currency = "EUR" };
        order.Parties.Add(new OrderParty { Role = "shipTo", City = "Cityville" });
        var rule = Rule("order", "shipToCity", "not_label", expected: "City,VAT,UID,UIDNr");
        var results = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(rule), order, DateTime.UtcNow);
        Assert.Contains(results, r => r.Status == "pass");
    }

    [Theory]
    [InlineData("UIDNr.")]        // label + punctuation boundary
    [InlineData("UIDNr ")]        // label + whitespace boundary
    [InlineData("UIDNr 12345")]   // label + space + value
    [InlineData("City:")]         // label + colon
    public void Not_label_fails_a_label_followed_by_a_non_letter_boundary(string swept)
    {
        var order = new PurchaseOrderEntity { Currency = "EUR" };
        order.Parties.Add(new OrderParty { Role = "shipTo", City = swept });
        var rule = Rule("order", "shipToCity", "not_label", expected: "City,VAT,UID,UIDNr");
        var results = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(rule), order, DateTime.UtcNow);
        Assert.Contains(results, r => r.Status == "fail");
    }

    [Fact]
    public void ShipTo_role_match_is_case_insensitive()
    {
        // A future/alternate parser emitting "ShipTo" (PascalCase) must still resolve the ship-to
        // party — otherwise the value reads as null and a not_label / required rule would pass
        // VACUOUSLY (a label cell would slip through). With the case-insensitive role match the swept
        // "UIDNr." label IS seen and the rule correctly fails.
        var order = new PurchaseOrderEntity { Currency = "EUR" };
        order.Parties.Add(new OrderParty { Role = "ShipTo", City = "UIDNr." });
        var rule = Rule("order", "shipToCity", "not_label", expected: "UIDNr,City,VAT,UID");
        var results = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(rule), order, DateTime.UtcNow);
        Assert.Contains(results, r => r.Status == "fail");
    }

    [Fact]
    public void Line_amount_reconcile_fails_when_qty_times_price_diverges()
    {
        var order = new PurchaseOrderEntity
        {
            Currency = "EUR",
            // 2 × 5 = 10, but stated LineAmount = 99 → reconcile fails beyond tolerance.
            Lines = { new PurchaseOrderLineEntity { LineNumber = 1, Quantity = 2m, UnitPrice = 5m, LineAmount = 99m } },
        };
        var results = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(Rule("line", "lineAmount", "line_amount_reconcile", expected: "0.01")), order, DateTime.UtcNow);
        Assert.Contains(results, r => r.Status == "fail");
    }

    [Fact]
    public void Line_amount_reconcile_passes_when_within_tolerance()
    {
        var order = new PurchaseOrderEntity
        {
            Currency = "EUR",
            // 2 × 5 = 10.00, stated 10.005 → within 0.01 tolerance.
            Lines = { new PurchaseOrderLineEntity { LineNumber = 1, Quantity = 2m, UnitPrice = 5m, LineAmount = 10.005m } },
        };
        var results = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(Rule("line", "lineAmount", "line_amount_reconcile", expected: "0.01")), order, DateTime.UtcNow);
        Assert.Contains(results, r => r.Status == "pass");
    }

    /// <summary>
    /// The test that pinned the defect. It asserted <c>pass</c> under the comment "reconcile is
    /// vacuously satisfied (qty × price IS the amount)" — which is precisely the problem: comparing
    /// a value against itself is not a check, and the nine of eleven parsers that never populate
    /// <c>LineAmount</c> made that the NORMAL case, not an edge case.
    /// </summary>
    [Fact]
    public void Line_amount_reconcile_is_not_evaluated_when_no_stated_amount()
    {
        var order = new PurchaseOrderEntity
        {
            Currency = "EUR",
            // No LineAmount — nothing was printed to reconcile against.
            Lines = { new PurchaseOrderLineEntity { LineNumber = 1, Quantity = 3m, UnitPrice = 7m, LineAmount = null } },
        };
        var results = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(Rule("line", "lineAmount", "line_amount_reconcile", expected: "0.01")), order, DateTime.UtcNow);

        Assert.Contains(results, r => r.Status == OrderValidationResult.StatusNotEvaluated);
        Assert.DoesNotContain(results, r => r.Status == OrderValidationResult.StatusPass);
    }

    [Fact]
    public void Vat_format_passes_a_valid_at_vat_and_fails_a_malformed_one()
    {
        var order = new PurchaseOrderEntity { Currency = "EUR" };
        order.Parties.Add(new OrderParty { Role = "shipTo", Country = "AT", Vat = "ATU99000000" });
        var pass = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(Rule("order", "shipToVat", "vat_format")), order, DateTime.UtcNow);
        Assert.Contains(pass, r => r.Status == "pass");

        order.Parties[0] = new OrderParty { Role = "shipTo", Country = "AT", Vat = "NOTAVAT" };
        var fail = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(Rule("order", "shipToVat", "vat_format")), order, DateTime.UtcNow);
        Assert.Contains(fail, r => r.Status == "fail");
    }

    [Theory]
    [InlineData("AT", "ATU12345678")]   // Austria: ATU + 8 digits
    [InlineData("DE", "DE123456789")]   // Germany: DE + 9 digits
    [InlineData("FR", "FRAB123456789")] // France: FR + 2 alnum + 9 digits
    [InlineData("PL", "PL1234567890")]  // Poland: PL + 10 digits
    [InlineData("DK", "DK12345678")]    // Denmark: DK + 8 digits
    [InlineData("FI", "FI12345678")]    // Finland: FI + 8 digits
    [InlineData("NO", "NO123456789")]   // Norway: NO + 9 digits (no MVA suffix here)
    public void Vat_format_accepts_well_formed_country_vats(string country, string vat)
    {
        var order = new PurchaseOrderEntity { Currency = "EUR" };
        order.Parties.Add(new OrderParty { Role = "shipTo", Country = country, Vat = vat });
        var results = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(Rule("order", "shipToVat", "vat_format")), order, DateTime.UtcNow);
        Assert.Contains(results, r => r.Status == "pass");
    }

    [Fact]
    public void Vat_format_country_mismatch_prefix_fails()
    {
        // Country says AT but VAT carries a DE prefix → mismatched country → fail (advisory).
        var order = new PurchaseOrderEntity { Currency = "EUR" };
        order.Parties.Add(new OrderParty { Role = "shipTo", Country = "AT", Vat = "DE123456789" });
        var results = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(Rule("order", "shipToVat", "vat_format")), order, DateTime.UtcNow);
        Assert.Contains(results, r => r.Status == "fail");
    }

    /// <summary>
    /// The generalisation of the two tests above, across all three absence-tolerant operators.
    /// It asserted an "advisory pass" for each; an empty field now reports NOT-EVALUATED.
    /// <para>The behaviour that matters — none of them REFUSES the order on absence, so 'required'
    /// remains the only way to mandate a field — is unchanged and is asserted directly below by
    /// checking that nothing failed.</para>
    /// </summary>
    [Fact]
    public void Empty_value_is_not_evaluated_for_all_new_operators_and_still_never_fails()
    {
        var order = new PurchaseOrderEntity
        {
            Currency = "EUR",
            SourceCapture = Capture("""[]"""),
            Lines = { new PurchaseOrderLineEntity { LineNumber = 1, Quantity = 1m, UnitPrice = 1m } },
        };
        order.Parties.Add(new OrderParty { Role = "shipTo" }); // no city, no vat

        var dateSanity = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(Rule("order", "sourceDate", "date_sanity")), order, DateTime.UtcNow);
        var notLabel = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(Rule("order", "shipToCity", "not_label")), order, DateTime.UtcNow);
        var vatFormat = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(Rule("order", "shipToVat", "vat_format")), order, DateTime.UtcNow);

        foreach (var results in new[] { dateSanity, notLabel, vatFormat })
        {
            Assert.Contains(results, r => r.Status == OrderValidationResult.StatusNotEvaluated);
            Assert.DoesNotContain(results, r => r.Status == OrderValidationResult.StatusPass);
            // Still advisory: absence is 'required''s business, so nothing here refuses the order.
            Assert.DoesNotContain(results, r => r.Status == OrderValidationResult.StatusFail);
        }
    }
}
