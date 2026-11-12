using ProcuLink.Infrastructure.Services.Ai;   // OpenAiPdfOrderExtractor (ValidateAndMap, ExtractionDto are internal — see InternalsVisibleTo)
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Ai;

/// <summary>
/// Phase 1 widening: <c>ValidateAndMap</c> now surfaces header-level parties,
/// contact, incoterms/shipping/buyer-order-ref and a raw_fields bag, plus the
/// per-line manufacturer/customer part numbers, discount, UNSPSC, recipient,
/// contract number and net amount. These additive fields ride through as
/// advisory data — they are NOT subject to the anti-hallucination number checks.
/// </summary>
public class ValidateAndMapWideningTests
{
    [Fact]
    public void ValidateAndMap_emits_parties_contact_and_line_mpn_and_raw_fields()
    {
        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.95,
            PoNumber: "4730154181",
            OrderDate: "2026-06-12",
            Currency: "EUR",
            BuyerName: "Exemplar Stahl GmbH",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(
                    LineNumber: 1, BuyerItemCode: "00010", Description: "Lucerne",
                    Quantity: 1, Unit: "ST", UnitPrice: 306.28, LineAmount: 306.28,
                    ManufacturerPartNumber: "PLKMX94EGK", Recipient: "c.testperson@buyer.example.com")
            },
            SupplierName: "Fabrikam GmbH",
            Parties: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionPartyDto(
                    Role: "shipTo", Name: "Exemplar Stahl GmbH", City: "Teststadt", Vat: "ATU99000000")
            },
            // The schema models contact as a nested object (strict-mode), so the DTO
            // carries a ContactDto — ValidateAndMap flattens it to Order.ContactEmail.
            Contact: new OpenAiPdfOrderExtractor.ContactDto(Email: "c.testperson@buyer.example.com"),
            Incoterms: "DDP",
            RawFields: new[] { new OpenAiPdfOrderExtractor.RawFieldDto("EDI id", "999000111") });

        // sourceText contains every emitted number so anti-hallucination passes.
        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, "4730154181 306.28 1 ST EUR 999000111");

        Assert.True(result.Success);
        Assert.Equal("DDP", result.Order!.Incoterms);
        Assert.Equal("c.testperson@buyer.example.com", result.Order.ContactEmail);
        Assert.Equal("ATU99000000", result.Order.Parties!.Single(p => p.Role == "shipTo").Vat);
        Assert.Equal("PLKMX94EGK", result.Order.Lines[0].ManufacturerPartNumber);
        Assert.Contains(result.Order.RawFields!, f => f.Label == "EDI id" && f.Value == "999000111");
    }
}

/// <summary>The PDF extractor maps the invoice-only fields it reads.</summary>
public class PdfInvoiceFieldMappingTests
{
    [Fact]
    public void ValidateAndMap_MapsInvoiceReadFields()
    {
        var lines = new[] { new OpenAiPdfOrderExtractor.ExtractionLineDto(1, "A", "Widget", 1, "PC", 10.0, 10.0) };
        const string source = "1 A Widget 1 PC 10.0 10.0 EUR";

        var filled = OpenAiPdfOrderExtractor.ValidateAndMap(new OpenAiPdfOrderExtractor.ExtractionDto(
            0.95, "INV-1", "2026-11-01", "EUR", "Example Buyer GmbH", lines,
            DueDate: "2026-12-01", SellerTaxId: "DE123456789",
            Iban: "de89 3704 0044 0532 0130 00", PaymentReference: "INV-1001"), source).Order!;
        Assert.Equal(new DateOnly(2026, 12, 1), filled.DueDate);
        Assert.Equal("DE123456789", filled.SellerTaxId);
        Assert.Equal("DE89370400440532013000", filled.Iban);
        Assert.Equal("INV-1001", filled.PaymentReference);

        var empty = OpenAiPdfOrderExtractor.ValidateAndMap(new OpenAiPdfOrderExtractor.ExtractionDto(
            0.95, "INV-1", "2026-11-01", "EUR", "Example Buyer GmbH", lines,
            DueDate: "", SellerTaxId: "", Iban: "", PaymentReference: ""), source).Order!;
        Assert.Null(empty.DueDate);
        Assert.Null(empty.SellerTaxId);
        Assert.Null(empty.Iban);
        Assert.Null(empty.PaymentReference);
    }
}
