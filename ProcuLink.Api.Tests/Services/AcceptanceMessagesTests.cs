using ProcuLink.Api.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// The plain-language acceptance messages — the fix for the founder's "errors don't make sense"
/// (was the developer template "unitPrice ('100') failed rule max 50000"). Asserts the human shape:
/// a catalog title lead, what's wrong in words, and a concrete fix.
/// </summary>
public class AcceptanceMessagesTests
{
    [Fact]
    public void Fail_Max_ReadsHuman_ActualVsExpected_AndFix()
    {
        // unitPrice.max is a known catalog rule → "Unit price at most" (carried SEPARATELY as the
        // headline via TitleFor, NOT duplicated into the message — see the title-dup review fix).
        var msg = AcceptanceMessages.ForFail("unitPrice", "max", "100", "50000", lineNumber: 3);

        Assert.StartsWith("Line 3:", msg);
        Assert.Contains("unit price must be at most 50000", msg);
        Assert.Contains("100", msg);                          // the actual value
        Assert.Contains("Lower unit price to 50000 or less", msg); // suggested fix
        Assert.DoesNotContain("failed rule", msg);            // old developer template gone
        // The headline title is NOT repeated in the message (no double-display in the fix card).
        Assert.DoesNotContain("Unit price at most", msg);
        Assert.Equal("Unit price at most", AcceptanceMessages.TitleFor("unitPrice", "max"));
    }

    [Fact]
    public void Fail_UnknownOperator_EmptyExpected_HasNoTrailingSpace()
    {
        var msg = AcceptanceMessages.ForFail("someField", "custom_check", "X", expectedValue: null, lineNumber: null);
        Assert.DoesNotContain(" ).", msg);                    // no "(custom_check )." trailing space
        Assert.Contains("(custom_check)", msg);
    }

    [Fact]
    public void Fail_Required_MissingValue_HasFix()
    {
        var msg = AcceptanceMessages.ForFail("supplierItemCode", "required", actualValue: null, expectedValue: null, lineNumber: 2);

        Assert.StartsWith("Line 2:", msg);
        Assert.Contains("supplier item code is required", msg);
        Assert.Contains("missing", msg);
        Assert.Contains("Enter supplier item code", msg);
    }

    [Fact]
    public void Fail_OrderScoped_HasNoLinePrefix()
    {
        var msg = AcceptanceMessages.ForFail("currency", "in", "USD", "EUR,GBP", lineNumber: null);
        Assert.DoesNotContain("Line ", msg);
        Assert.Contains("currency must be one of: EUR,GBP", msg);
        Assert.Contains("USD", msg);
    }

    [Fact]
    public void Fail_UnknownAdHocRule_HasNoTitleLead_ButStillHuman()
    {
        // A field/operator with no catalog entry → no title, but still a readable sentence.
        var msg = AcceptanceMessages.ForFail("someCustomField", "equals", "X", "Y", lineNumber: null);
        Assert.Contains("some custom field must be Y", msg);
        Assert.Contains("Set some custom field to Y", msg);
        Assert.DoesNotContain("failed rule", msg);
    }

    [Fact]
    public void Fail_LineAmountReconcile_HasPlainExplanation()
    {
        var msg = AcceptanceMessages.ForFail("lineAmount", "line_amount_reconcile", "9.99", null, lineNumber: 5);
        Assert.StartsWith("Line 5:", msg);
        Assert.Contains("doesn't add up", msg);
        Assert.Contains("quantity × unit price", msg);
    }

    [Fact]
    public void Fail_BlankActual_ShowsEmpty_NotQuotedBlank()
    {
        var msg = AcceptanceMessages.ForFail("buyerName", "required", "   ", null, lineNumber: null);
        Assert.Contains("buyer name is required", msg);
    }

    [Theory]
    [InlineData("unitPrice", "unit price")]
    [InlineData("shipToVat", "ship to vat")]
    [InlineData("po.number", "number")]   // dotted → last segment
    [InlineData("", "this field")]
    public void Humanize_SplitsCamelCase_AndDottedPaths(string input, string expected)
    {
        Assert.Equal(expected, AcceptanceMessages.Humanize(input));
    }

    [Fact]
    public void TitleForCode_ReturnsCatalogTitle_OrNullForInvariants()
    {
        Assert.Equal("Supplier item code is required", AcceptanceMessages.TitleForCode("supplierItemCode.required"));
        Assert.Null(AcceptanceMessages.TitleForCode("invariant.po_number_present"));
        Assert.Null(AcceptanceMessages.TitleForCode(null));
    }

    [Fact]
    public void TitleForCode_KnowsOutputCheckCodes()
    {
        Assert.Equal("Buyer item code required for this format", AcceptanceMessages.TitleForCode(AcceptanceMessages.OutputBuyerCodeCode));
        Assert.Equal("Value adjusted for the output", AcceptanceMessages.TitleForCode(AcceptanceMessages.OutputSanitizedCode));
    }

    [Fact]
    public void Pass_IsShortAndPositive()
    {
        var msg = AcceptanceMessages.ForPass("supplierItemCode", "required");
        Assert.Contains("OK", msg);
        Assert.DoesNotContain("required, but", msg);
    }
}
