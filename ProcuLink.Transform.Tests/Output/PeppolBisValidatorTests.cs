using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// Peppol wedge — Track A. Proves the lightweight BIS Billing 3.0 mandatory-field
/// validator passes a complete document and flags missing mandatory fields.
/// </summary>
public class PeppolBisValidatorTests
{
    private static InvoiceEntity MakeInvoice() => new()
    {
        Id            = Guid.NewGuid(),
        InvoiceNumber = "INV-2026-001",
        IssueDate     = new DateOnly(2026, 5, 28),
        DueDate       = new DateOnly(2026, 6, 27),
        Currency      = "EUR",
        BuyerRef      = "PO-9001",
        SubTotal      = 100m,
        TaxTotal      = 20m,
        GrandTotal    = 120m,
        Status        = "approved",
    };

    private static InvoiceLineEntity MakeLine() => new()
    {
        Id          = Guid.NewGuid(),
        LineNumber  = 1,
        Description = "Steel bracket",
        Quantity    = 10m,
        UnitCode    = "EA",
        UnitPrice   = 10m,
        TaxRate     = 0.20m,
        LineTotal   = 100m,
    };

    private static PeppolPartyOptions FullParty() => new()
    {
        SellerName           = "Northwind Trading OÜ",
        SellerEndpointId     = "0192:998765432",
        SellerEndpointScheme = "0192",
        SellerVatId          = "EE100123456",
        BuyerName            = "Fabrikam AS",
        BuyerEndpointId      = "0088:1234567890123",
        BuyerEndpointScheme  = "0088",
        BuyerVatId           = "EE100987654",
    };

    private static string GenerateXml(PeppolPartyOptions opts)
    {
        var svc = new PeppolBisInvoiceTransformService(opts);
        return svc.BuildDocument(MakeInvoice(), new[] { MakeLine() }).ToString();
    }

    [Fact]
    public void Validate_CompleteDocument_IsValidWithNoErrors()
    {
        var xml    = GenerateXml(FullParty());
        var result = new PeppolBisValidator().Validate(xml);

        result.IsValid.Should().BeTrue(
            "a fully-populated BIS document should clear the mandatory checks. Issues: {0}",
            string.Join(" | ", result.Issues.Select(i => $"{i.Severity}:{i.RuleId}:{i.Message}")));
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_MissingSellerEndpoint_FlagsMandatoryError()
    {
        // Drop the seller endpoint ID (BT-34) — a mandatory field for Peppol routing.
        var opts = new PeppolPartyOptions
        {
            SellerName       = "Northwind Trading OÜ",
            SellerEndpointId = null,           // ← missing mandatory field
            SellerVatId      = "EE100123456",
            BuyerName        = "Fabrikam AS",
            BuyerEndpointId  = "0088:1234567890123",
            BuyerVatId       = "EE100987654",
        };
        var xml    = GenerateXml(opts);
        var result = new PeppolBisValidator().Validate(xml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(i =>
            i.BusinessTerm == "BT-34" && i.Severity == "error");
    }

    [Fact]
    public void Validate_MissingBuyerName_FlagsMandatoryError()
    {
        var opts = new PeppolPartyOptions
        {
            SellerName       = "Northwind Trading OÜ",
            SellerEndpointId = "0192:998765432",
            BuyerName        = null,           // ← missing mandatory buyer name (BT-44)
            BuyerEndpointId  = "0088:1234567890123",
        };
        var xml    = GenerateXml(opts);
        var result = new PeppolBisValidator().Validate(xml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(i => i.BusinessTerm == "BT-44");
    }

    [Fact]
    public void Validate_WrongCustomizationId_FlagsError()
    {
        var xml = GenerateXml(FullParty())
            .Replace(PeppolBisInvoiceTransformService.CustomizationId, "urn:not-peppol");
        var result = new PeppolBisValidator().Validate(xml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(i => i.RuleId == "PLK-BIS-CustomizationID");
    }

    [Fact]
    public void Validate_BrokenTaxCalc_FlagsCalcError()
    {
        // Corrupt the (uniquely-tagged) TaxInclusiveAmount so the
        // TaxExclusive + TaxAmount == TaxInclusive reconciliation fails.
        var xml = GenerateXml(FullParty())
            .Replace(
                "<cbc:TaxInclusiveAmount currencyID=\"EUR\">120.00</cbc:TaxInclusiveAmount>",
                "<cbc:TaxInclusiveAmount currencyID=\"EUR\">999.00</cbc:TaxInclusiveAmount>");
        var result = new PeppolBisValidator().Validate(xml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(i => i.RuleId == "PLK-BIS-TaxCalc");
    }

    [Fact]
    public void Validate_MissingVatId_IsWarningNotError()
    {
        // No VAT ids → recommended-but-not-fatal in our lightweight checker.
        var opts = new PeppolPartyOptions
        {
            SellerName       = "Northwind Trading OÜ",
            SellerEndpointId = "0192:998765432",
            SellerEndpointScheme = "0192",
            BuyerName        = "Fabrikam AS",
            BuyerEndpointId  = "0088:1234567890123",
            BuyerEndpointScheme = "0088",
            // no SellerVatId / BuyerVatId
        };
        var result = new PeppolBisValidator().Validate(GenerateXml(opts));

        result.IsValid.Should().BeTrue();
        result.Warnings.Should().Contain(i => i.BusinessTerm == "BT-31");
        result.Warnings.Should().Contain(i => i.BusinessTerm == "BT-48");
    }

    [Fact]
    public void Validate_NotXml_FlagsWellFormednessError()
    {
        var result = new PeppolBisValidator().Validate("this is not xml <<<");
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(i => i.RuleId == "PLK-BIS-WellFormed");
    }

    [Fact]
    public void Validate_WrongRoot_FlagsRootError()
    {
        var result = new PeppolBisValidator().Validate("<Order/>");
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(i => i.RuleId == "PLK-BIS-Root");
    }
}
