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
            BuyerName: "REDACTED-PARTY",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(
                    LineNumber: 1, BuyerItemCode: "00010", Description: "Panasonic",
                    Quantity: 1, Unit: "ST", UnitPrice: 306.28, LineAmount: 306.28,
                    ManufacturerPartNumber: "SCPMX94EGK", Recipient: "redacted@example.invalid")
            },
            SupplierName: "REDACTED-PARTY",
            Parties: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionPartyDto(
                    Role: "shipTo", Name: "REDACTED-PARTY", City: "Linz", Vat: "REDACTED-TAXID")
            },
            // The schema models contact as a nested object (strict-mode), so the DTO
            // carries a ContactDto — ValidateAndMap flattens it to Order.ContactEmail.
            Contact: new OpenAiPdfOrderExtractor.ContactDto(Email: "redacted@example.invalid"),
            Incoterms: "DDP",
            RawFields: new[] { new OpenAiPdfOrderExtractor.RawFieldDto("EDI id", "REDACTED-TAXID") });

        // sourceText contains every emitted number so anti-hallucination passes.
        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, "REDACTED-DOCNO");

        Assert.True(result.Success);
        Assert.Equal("DDP", result.Order!.Incoterms);
        Assert.Equal("redacted@example.invalid", result.Order.ContactEmail);
        Assert.Equal("REDACTED-TAXID", result.Order.Parties!.Single(p => p.Role == "shipTo").Vat);
        Assert.Equal("SCPMX94EGK", result.Order.Lines[0].ManufacturerPartNumber);
        Assert.Contains(result.Order.RawFields!, f => f.Label == "EDI id" && f.Value == "REDACTED-TAXID");
    }
}

/// <summary>
/// Phase 1 parity: the email-body extractor's <c>MapToOrder</c> now carries the
/// same lossless additions as the PDF path — header-level parties (address/VAT)
/// and a raw_fields bag — so an inbound PO arriving as an email body is no longer
/// stripped of every field the fixed canonical header had no slot for. Email
/// stays a subset (no contact object / incoterms) — parties + raw_fields are the
/// high-value additions.
/// </summary>
public class EmailExtractorWideningTests
{
    [Fact]
    public void Email_MapToOrder_carries_parties_and_raw_fields()
    {
        var dto = new OpenAiEmailBodyOrderExtractor.ExtractionDto(
            0.9, "PO-9", "2026-06-12", "EUR", "Acme",
            new[] { new OpenAiEmailBodyOrderExtractor.ExtractionLineDto(1, "B1", "Item", 1, "PC", 9.0) },
            Parties: new[] { new OpenAiEmailBodyOrderExtractor.ExtractionPartyDto("shipTo", Name: "DC", Vat: "ATU2") },
            RawFields: new[] { new OpenAiEmailBodyOrderExtractor.RawFieldDto("PR", "PR-1") });

        var order = OpenAiEmailBodyOrderExtractor.MapToOrderForTest(dto);

        Assert.Equal("ATU2", order.Parties!.Single().Vat);
        Assert.Contains(order.RawFields!, f => f.Label == "PR");
    }
}
