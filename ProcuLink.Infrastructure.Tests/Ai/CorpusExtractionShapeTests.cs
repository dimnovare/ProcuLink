using ProcuLink.Infrastructure.Services.Ai;   // OpenAiPdfOrderExtractor (ValidateAndMap, ExtractionDto are internal — see InternalsVisibleTo)
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Ai;

/// <summary>
/// Deterministic, CI-safe regression fixtures for the 12-PO corpus (Task 9).
///
/// These tests pin the MAPPING CONTRACT, not the LLM. For each real vendor shape
/// we hand-build the <see cref="OpenAiPdfOrderExtractor.ExtractionDto"/> the model
/// SHOULD return (parties / VAT / EDI ids / manufacturer P/N / incoterms /
/// per-line recipient / raw_fields populated from the DocParser-confirmed corpus
/// values), then call <see cref="OpenAiPdfOrderExtractor.ValidateAndMap"/> with a
/// <c>sourceText</c> that contains every emitted number (so the anti-hallucination
/// net passes) and assert the widened fields survive onto <c>result.Order</c>.
///
/// NO OpenAI key is required and there is NO network call — the actual live
/// extraction proof (which IS non-deterministic and needs a real key) is the
/// operator checklist in
/// <c>docs/superpowers/plans/2026-06-13-phase1-corpus-live-check.md</c>.
///
/// Confidence is 0.95, comfortably above
/// <see cref="OpenAiPdfOrderExtractor.ConfidenceThreshold"/> (0.6).
/// </summary>
public class CorpusExtractionShapeTests
{
    private const double HighConfidence = 0.95; // > ConfidenceThreshold (0.6)

    /// <summary>
    /// AT / EUR, German-labelled PDF ("Bestellung"). Line manufacturer P/N
    /// <c>SCPMX94EGK</c>, ship-to party VAT <c>ATU99000000</c>, ordering contact
    /// email <c>c.testperson@buyer.example.com</c>, incoterms <c>DDP</c>. All four must
    /// survive onto the canonical order.
    /// </summary>
    [Fact]
    public void PdfAt_Eur_surfaces_mpn_shipto_vat_contact_and_incoterms()
    {
        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: HighConfidence,
            PoNumber: "4730154181",
            OrderDate: "2026-06-12",
            Currency: "EUR",
            BuyerName: "Exemplar Stahl GmbH",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(
                    LineNumber: 1, BuyerItemCode: "00010", Description: "Panasonic switch",
                    Quantity: 1, Unit: "ST", UnitPrice: 306.28, LineAmount: 306.28,
                    ManufacturerPartNumber: "SCPMX94EGK"),
            },
            SupplierName: "REDACTED-PARTY",
            Incoterms: "DDP",
            Contact: new OpenAiPdfOrderExtractor.ContactDto(Email: "c.testperson@buyer.example.com"),
            Parties: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionPartyDto(
                    Role: "shipTo", Name: "Exemplar Stahl GmbH", City: "Teststadt", Vat: "ATU99000000"),
            },
            RawFields: new[] { new OpenAiPdfOrderExtractor.RawFieldDto("EDI id", "999000111") });

        // Every emitted number present → anti-hallucination passes, zero review flags.
        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, "4730154181 306.28 1 ST EUR 999000111");

        Assert.True(result.Success);
        Assert.Empty(result.ReviewLineNumbers);
        Assert.Equal("DDP", result.Order!.Incoterms);
        Assert.Equal("c.testperson@buyer.example.com", result.Order.ContactEmail);
        Assert.Equal("ATU99000000", result.Order.Parties!.Single(p => p.Role == "shipTo").Vat);
        Assert.Equal("SCPMX94EGK", result.Order.Lines[0].ManufacturerPartNumber);
    }

    /// <summary>
    /// NO / NOK, single-line PDF. A per-line <c>Recipient</c>
    /// (<c>robin.testperson@buyer.example.com</c>) must survive onto the line. This is the
    /// classic field the old fixed-canonical header dropped.
    /// </summary>
    [Fact]
    public void PdfNo_Nok_surfaces_per_line_recipient()
    {
        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: HighConfidence,
            PoNumber: "10123140",
            OrderDate: "2026-06-12",
            Currency: "NOK",
            BuyerName: "Exemplar Seafood ASA",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(
                    LineNumber: 1, BuyerItemCode: "FEED-001", Description: "Salmon feed",
                    Quantity: 10, Unit: "PC", UnitPrice: 25.0, LineAmount: 250.0,
                    Recipient: "robin.testperson@buyer.example.com"),
            },
            SupplierName: "REDACTED-PARTY");

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, "10123140 10 25.0 250.0 PC NOK");

        Assert.True(result.Success);
        Assert.Empty(result.ReviewLineNumbers);
        Assert.Equal("robin.testperson@buyer.example.com", result.Order!.Lines[0].Recipient);
    }

    /// <summary>
    /// FI / EUR PDF. An EDI id <c>003700000004</c> arrives in
    /// <c>raw_fields</c> (it has no canonical slot) and must appear verbatim in
    /// <c>result.Order.RawFields</c>.
    /// </summary>
    [Fact]
    public void PdfFi_Eur_surfaces_edi_id_in_raw_fields()
    {
        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: HighConfidence,
            PoNumber: "2680200079",
            OrderDate: "2026-06-12",
            Currency: "EUR",
            BuyerName: "Exemplar Vakuutus",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(
                    LineNumber: 1, BuyerItemCode: "ITM-9", Description: "Service",
                    Quantity: 1, Unit: "PC", UnitPrice: 100.0, LineAmount: 100.0),
            },
            SupplierName: "REDACTED-PARTY",
            RawFields: new[] { new OpenAiPdfOrderExtractor.RawFieldDto("EDI id", "003700000004") });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, "2680200079 1 100.0 PC EUR 003700000004");

        Assert.True(result.Success);
        Assert.Empty(result.ReviewLineNumbers);
        Assert.Contains(result.Order!.RawFields!, f => f.Label == "EDI id" && f.Value == "003700000004");
    }

    /// <summary>
    /// FR / EUR PDF, alphanumeric PO number. Header <c>Incoterms</c> <c>FCA</c> must surface,
    /// and a line manufacturer P/N <c>X2791HS-B1</c> must ride through.
    /// </summary>
    [Fact]
    public void PdfFr_Eur_surfaces_header_incoterms_and_line_mpn()
    {
        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: HighConfidence,
            PoNumber: "E032180",
            OrderDate: "2026-06-12",
            Currency: "EUR",
            BuyerName: "Manufacture Exemple de Composants",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(
                    LineNumber: 1, BuyerItemCode: "00100", Description: "Tyre component",
                    Quantity: 4, Unit: "EA", UnitPrice: 50.0, LineAmount: 200.0,
                    ManufacturerPartNumber: "X2791HS-B1"),
            },
            SupplierName: "REDACTED-PARTY",
            Incoterms: "FCA");

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, "E032180 4 50.0 200.0 EA EUR");

        Assert.True(result.Success);
        Assert.Empty(result.ReviewLineNumbers);
        Assert.Equal("FCA", result.Order!.Incoterms);
        Assert.Equal("X2791HS-B1", result.Order.Lines[0].ManufacturerPartNumber);
    }

    /// <summary>
    /// NO / USD services PDF. A per-line <c>Recipient</c> AND a <c>raw_fields</c>
    /// entry must both survive (two different lossless channels on one order).
    /// </summary>
    [Fact]
    public void PdfNo_Usd_surfaces_per_line_recipient_and_raw_field()
    {
        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: HighConfidence,
            PoNumber: "226131000790",
            OrderDate: "2026-06-12",
            Currency: "USD",
            BuyerName: "Exemplar Marine AS",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(
                    LineNumber: 1, BuyerItemCode: "SRV-1", Description: "Survey service",
                    Quantity: 2, Unit: "PC", UnitPrice: 500.0, LineAmount: 1000.0,
                    Recipient: "requisitioner@buyer.example.com"),
            },
            SupplierName: "REDACTED-PARTY",
            RawFields: new[] { new OpenAiPdfOrderExtractor.RawFieldDto("Cost centre", "CC-4471") });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, "226131000790 2 500.0 1000.0 PC USD CC-4471");

        Assert.True(result.Success);
        Assert.Empty(result.ReviewLineNumbers);
        Assert.Equal("requisitioner@buyer.example.com", result.Order!.Lines[0].Recipient);
        Assert.Contains(result.Order.RawFields!, f => f.Label == "Cost centre" && f.Value == "CC-4471");
    }

    /// <summary>
    /// DK / DKK PDF. A bill-to party carrying VAT
    /// <c>DK99000000</c> must survive as a <c>billTo</c> party with its VAT.
    /// </summary>
    [Fact]
    public void PdfDk_Dkk_surfaces_billto_party_and_vat()
    {
        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: HighConfidence,
            PoNumber: "4509404105",
            OrderDate: "2026-06-12",
            Currency: "DKK",
            BuyerName: "Exemplar Valves A/S",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(
                    LineNumber: 1, BuyerItemCode: "PART-7", Description: "Valve",
                    Quantity: 5, Unit: "PC", UnitPrice: 40.0, LineAmount: 200.0),
            },
            SupplierName: "REDACTED-PARTY",
            Parties: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionPartyDto(
                    Role: "billTo", Name: "Exemplar Valves A/S", City: "Teststad",
                    Country: "DK", Vat: "DK99000000"),
            });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, "4509404105 5 40.0 200.0 PC DKK");

        Assert.True(result.Success);
        Assert.Empty(result.ReviewLineNumbers);
        var billTo = result.Order!.Parties!.Single(p => p.Role == "billTo");
        Assert.Equal("Exemplar Valves A/S", billTo.Name);
        Assert.Equal("DK99000000", billTo.Vat);
    }
}
