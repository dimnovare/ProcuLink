using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenAI.Chat;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Ocr;
using ProcuLink.Infrastructure.Services.Ai;

namespace ProcuLink.Infrastructure.Tests.Services.Ai;

/// <summary>
/// Tests for the LLM-backed PDF structured extractor.
///
/// The OpenAI call itself is never exercised live — the validation/mapping logic
/// (<c>ValidateAndMap</c>) is a pure function tested directly, and the plumbing
/// paths (no key, over cap) are tested via the no-op / cap short-circuits.
/// </summary>
public class OpenAiPdfOrderExtractorTests
{
    // ── ValidateAndMap: happy path ───────────────────────────────────────────

    [Fact]
    public void ValidateAndMap_HappyPath_MapsCanonicalFields_AndFlagsNothing()
    {
        const string source =
            "PO Number: PO-2026-008412\n" +
            "Buyer: Heinrich Industries\n" +
            "Currency: EUR\n" +
            "1 HEI-PLT-09 Mounting plate 4 PCS 12.50 50.00\n" +
            "2 HEI-BRK-40 Steel bracket 8 PCS 7.25 58.00";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.95,
            PoNumber: "PO-2026-008412",
            OrderDate: "2026-05-20",
            Currency: "EUR",
            BuyerName: "Heinrich Industries",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(1, "HEI-PLT-09", "Mounting plate", 4, "PCS", 12.50, 50.00),
                new OpenAiPdfOrderExtractor.ExtractionLineDto(2, "HEI-BRK-40", "Steel bracket", 8, "PCS", 7.25, 58.00),
            });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Success.Should().BeTrue();
        result.FailureReason.Should().BeNull();
        result.Confidence.Should().BeApproximately(0.95, 0.0001);
        result.ReviewLineNumbers.Should().BeEmpty("every number reconciles and appears in the source");

        result.Order.Should().NotBeNull();
        result.Order!.PoNumber.Should().Be("PO-2026-008412");
        result.Order.OrderDate.Should().Be(new DateTime(2026, 5, 20));
        result.Order.BuyerName.Should().Be("Heinrich Industries");
        result.Order.Currency.Should().Be("EUR");

        result.Order.Lines.Should().HaveCount(2);
        result.Order.Lines[0].LineNumber.Should().Be(1);
        result.Order.Lines[0].BuyerItemCode.Should().Be("HEI-PLT-09");
        result.Order.Lines[0].Description.Should().Be("Mounting plate");
        result.Order.Lines[0].Quantity.Should().Be(4m);
        result.Order.Lines[0].Unit.Should().Be("PCS");
        result.Order.Lines[0].UnitPrice.Should().Be(12.50m);
        result.Order.Lines[1].BuyerItemCode.Should().Be("HEI-BRK-40");
    }

    // ── SystemPrompt: deterministic party-role wording (regression for buyer/supplier swap) ──

    [Fact]
    public void SystemPrompt_ContainsDeterministicPartyRoleLanguage()
    {
        // Regression guard for the founder-found bug where the extractor swapped the
        // buyer and supplier names (it picked the system-customer name as the buyer).
        // The fix is the deterministic, label-driven role wording in the system prompt;
        // assert the load-bearing phrases stay present so a future edit can't silently
        // regress to the old ambiguous "issuing vendor/seller" definition.
        var prompt = OpenAiPdfOrderExtractor.SystemPrompt;

        // For a purchase order the buyer is the ISSUER/originator, not "the seller".
        prompt.Should().Contain("ISSUED");
        prompt.Should().Contain("PLACED");
        // Roles invert for an invoice.
        prompt.Should().Contain("INVOICE the roles INVERT");
        // Do not assume a familiar name is the buyer — assign purely from labels.
        prompt.Should().Contain("Do NOT assume");
        prompt.Should().Contain("PURELY from the document");
        // The two parties must be distinct.
        prompt.Should().Contain("MUST be two DIFFERENT parties");
        // The old ambiguous definition must be gone.
        prompt.Should().NotContain("supplier_name = the issuing vendor/seller");
    }

    // ── F-14: positional line index must not be emitted as the item code ─────

    [Fact]
    public void ValidateAndMap_PositionalIndexCode_WithRealMpn_PromotesTheRealCode()
    {
        // The multi-vendor "Pos." shape: the model put the positional "Pos." index ("0001")
        // into buyer_item_code while the genuine part number sits in manufacturer_part_number.
        // The real code must win — a positional counter is not an item code.
        const string source = "0001 Humongous Nova A57 1 PC 1469,00 1469,00 HG-A576BZABEEE";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "PO-1", OrderDate: "", Currency: "PLN", BuyerName: "Exemplar Elektro",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(
                    1, "0001", "Humongous Nova A57", 1, "PC", 1469.00, 1469.00,
                    ManufacturerPartNumber: "HG-A576BZABEEE"),
            });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Success.Should().BeTrue();
        result.Order!.Lines[0].BuyerItemCode.Should().Be("HG-A576BZABEEE",
            "the positional index 0001 must be replaced by the genuine part number");
        result.Order.Lines[0].ManufacturerPartNumber.Should().Be("HG-A576BZABEEE");
    }

    [Fact]
    public void ValidateAndMap_PositionalIndexCode_WithCustomerPartNumber_PromotesIt()
    {
        // EXEMPLAR SEAFOOD shape: "Pos. Part No." — model echoes the position ("1"); the real "Part No."
        // (43469659) landed in customer_part_number. Promote the real code.
        const string source = "1 43469659 VanArsdel WorkBook L14 1 Piece 12157,84 12157,84";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "PO-1", OrderDate: "", Currency: "NOK", BuyerName: "EXEMPLAR SEAFOOD",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(
                    1, "1", "VanArsdel WorkBook L14", 1, "Piece", 12157.84, 12157.84,
                    CustomerPartNumber: "43469659"),
            });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Order!.Lines[0].BuyerItemCode.Should().Be("43469659");
    }

    [Fact]
    public void ValidateAndMap_PositionalIndexCode_NoRealCode_FlagsLineForReview()
    {
        // Only a positional counter and no genuine part number anywhere → do NOT silently
        // deliver "0010" as if it were a code; leave it but flag the line for review.
        const string source = "0010 Some service 2 ST 376,20 752,40";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "PO-1", OrderDate: "", Currency: "EUR", BuyerName: "Exemplar Verkehr",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(1, "0010", "Some service", 2, "ST", 376.20, 752.40),
            });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Success.Should().BeTrue();
        result.ReviewLineNumbers.Should().Contain(1,
            "a positional index with no real part number must surface for a human, not ship as a code");
    }

    [Fact]
    public void ValidateAndMap_GenuineNumericPartNumber_IsNotTreatedAsPositional()
    {
        // A real 8-digit part number that is not the line position must be kept as-is —
        // the positional guard must be narrow enough not to clobber legitimate codes.
        const string source = "1 43469659 VanArsdel WorkBook 1 Piece 12157,84 12157,84";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "PO-1", OrderDate: "", Currency: "NOK", BuyerName: "EXEMPLAR SEAFOOD",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(1, "43469659", "VanArsdel WorkBook", 1, "Piece", 12157.84, 12157.84),
            });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Order!.Lines[0].BuyerItemCode.Should().Be("43469659");
        result.ReviewLineNumbers.Should().NotContain(1);
    }

    // ── F-21: locale-aware order-date interpretation (verbatim raw date) ──────

    [Fact]
    public void ValidateAndMap_EuropeanRawOrderDate_IsReadDayFirst_NotMonthFirst()
    {
        // The day-first EU date bug: printed "12.06.2026" (12 June) but the model
        // returned an inverted ISO order_date of 2026-12-06 (6 December). When the verbatim
        // printed date is supplied it must be re-interpreted day-first for the EU document.
        const string source = "Bestelldatum 12.06.2026 Currency EUR";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "PO-1",
            OrderDate: "2026-12-06",          // the model's WRONG month-first reading
            Currency: "EUR", BuyerName: "Exemplar Verkehr",
            Lines: new[] { new OpenAiPdfOrderExtractor.ExtractionLineDto(1, "X", "Y", 1, "PCS", 0, 0) },
            OrderDateRaw: "12.06.2026");      // the date exactly as printed

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Order!.OrderDate.Should().Be(new DateTime(2026, 6, 12),
            "an EU document's 12.06.2026 is 12 June, re-derived from the verbatim printed date");
    }

    [Fact]
    public void ValidateAndMap_UsdRawOrderDate_IsReadMonthFirst()
    {
        // A clearly-US document (USD) keeps month-first for the same ambiguous shape.
        const string source = "Order date 12/06/2026 Currency USD";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "PO-1", OrderDate: "",
            Currency: "USD", BuyerName: "US Buyer Inc",
            Lines: new[] { new OpenAiPdfOrderExtractor.ExtractionLineDto(1, "X", "Y", 1, "PCS", 0, 0) },
            OrderDateRaw: "12/06/2026");

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Order!.OrderDate.Should().Be(new DateTime(2026, 12, 6));
    }

    [Fact]
    public void ValidateAndMap_NoRawDate_FallsBackToModelInterpretedIso()
    {
        // Back-compat: with no verbatim raw date the model's own ISO order_date is used.
        const string source = "1 X Y 1 PCS";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "PO-1", OrderDate: "2026-05-20", Currency: "EUR", BuyerName: "Acme",
            Lines: new[] { new OpenAiPdfOrderExtractor.ExtractionLineDto(1, "X", "Y", 1, "PCS", 0, 0) });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Order!.OrderDate.Should().Be(new DateTime(2026, 5, 20));
    }

    // ── F-13: buyer/supplier collapse guard ──────────────────────────────────

    [Fact]
    public void ValidateAndMap_BuyerEqualsSupplier_DropsBuyerRatherThanShowingBothAsSame()
    {
        // The two roles MUST be distinct. If the model collapses them onto the same party
        // we cannot trust the buyer attribution, so drop it (→ human review) rather than
        // present the recipient as the buyer.
        const string source = "1 X Y 1 PCS";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "PO-1", OrderDate: "", Currency: "EUR",
            BuyerName: "FABRIKAM B2B APS",
            Lines: new[] { new OpenAiPdfOrderExtractor.ExtractionLineDto(1, "X", "Y", 1, "PCS", 0, 0) },
            SupplierName: "Fabrikam B2B ApS");

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Order!.BuyerName.Should().BeNull(
            "buyer and supplier are the same party → the buyer cannot be trusted, leave it for review");
        result.Order.SupplierName.Should().Be("Fabrikam B2B ApS");
    }

    [Fact]
    public void SystemPrompt_TellsModel_SupplierNumberIdentifiesTheRecipient()
    {
        // F-13 hardening: the recipient (supplier) in these POs is the party the document
        // labels with a supplier/vendor number ("Vendor No.", "Lieferant", "Numer dostawcy",
        // "Supplier:"). Assert the load-bearing guidance stays present.
        var prompt = OpenAiPdfOrderExtractor.SystemPrompt;
        prompt.Should().Contain("SUPPLIER NUMBER");
        prompt.Should().Contain("is the SUPPLIER");
    }

    [Fact]
    public void SystemPrompt_TellsModel_PositionalIndexIsNotAnItemCode()
    {
        // F-14 hardening: buyer_item_code must be a real product/part number, never the
        // positional "Pos."/"Position" line counter.
        var prompt = OpenAiPdfOrderExtractor.SystemPrompt;
        prompt.Should().Contain("positional");
    }

    // ── ValidateAndMap: anti-hallucination (number not in source) ────────────

    [Fact]
    public void ValidateAndMap_LineWithNumberNotInSource_FlagsLineForReview()
    {
        // unit_price 13.50 never appears in the source text; quantity (4) and
        // line_amount (54.00) do, and 4 × 13.50 = 54.00 reconciles — so the ONLY
        // trigger is the hallucinated unit price.
        const string source = "1 ABC Widget 4 PCS 54.00";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "PO-1", OrderDate: "", Currency: "EUR", BuyerName: "Acme",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(1, "ABC", "Widget", 4, "PCS", 13.50, 54.00),
            });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Success.Should().BeTrue("a partially-suspect order is still returned, but flagged");
        result.ReviewLineNumbers.Should().Contain(1);
    }

    // ── ValidateAndMap: arithmetic mismatch (all numbers present) ────────────

    [Fact]
    public void ValidateAndMap_QuantityTimesPriceDoesNotMatchAmount_FlagsLineForReview()
    {
        // 4, 12.50 and 99.99 all appear in the source (no hallucination), but
        // 4 × 12.50 = 50.00 ≠ 99.99 → the line must be flagged.
        const string source = "1 ABC Widget 4 PCS 12.50 99.99";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "PO-1", OrderDate: "", Currency: "EUR", BuyerName: "Acme",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(1, "ABC", "Widget", 4, "PCS", 12.50, 99.99),
            });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Success.Should().BeTrue();
        result.ReviewLineNumbers.Should().Contain(1);
    }

    [Fact]
    public void ValidateAndMap_BelowConfidenceThreshold_ReturnsFailureSoCallerFallsBack()
    {
        const string source = "1 ABC Widget 4 PCS 12.50 50.00";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.3, PoNumber: "PO-1", OrderDate: "", Currency: "EUR", BuyerName: "Acme",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(1, "ABC", "Widget", 4, "PCS", 12.50, 50.00),
            });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Success.Should().BeFalse();
        result.Order.Should().BeNull();
        result.FailureReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ValidateAndMap_NoLines_ReturnsFailure()
    {
        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.95, PoNumber: "PO-1", OrderDate: "", Currency: "EUR", BuyerName: "Acme",
            Lines: Array.Empty<OpenAiPdfOrderExtractor.ExtractionLineDto>());

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, "PO-1 nothing here");

        result.Success.Should().BeFalse();
        result.Order.Should().BeNull();
    }

    // ── ValidateAndMap: review-flag regression fixes ────────────────────────

    [Fact]
    public void ValidateAndMap_SpaceSeparatedNumbers_AreNotMergedAsThousands()
    {
        // Regression: "125 500" (price 125, amount 500) must tokenise as two numbers,
        // not one grouped "125 500" = 125500 — otherwise both trip the source check.
        const string source = "1 WIDGET-X 4 PCS 125 500";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "PO-1", OrderDate: "", Currency: "EUR", BuyerName: "Acme",
            Lines: new[]
            {
                // 4 x 125 = 500 — internally consistent, every number in the source.
                new OpenAiPdfOrderExtractor.ExtractionLineDto(1, "WIDGET-X", "Widget", 4, "PCS", 125, 500),
            });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Success.Should().BeTrue();
        result.ReviewLineNumbers.Should().BeEmpty("125 and 500 both appear verbatim in the source");
    }

    [Fact]
    public void ValidateAndMap_EuSpaceGroupedThousands_AreNotFalseFlagged()
    {
        // EU/Baltic convention: a space groups thousands ("1 250,00" = 1250.00).
        // PdfPig emits these space-joined; the merged reading must still match so a
        // correctly-extracted line for the Baltic ICP isn't sent to review.
        const string source = "Quantity 3 Unit price 1 250,00 Amount 3 750,00";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "PO-1", OrderDate: "", Currency: "EUR", BuyerName: "Baltic OU",
            Lines: new[]
            {
                // 3 x 1250.00 = 3750.00 — internally consistent.
                new OpenAiPdfOrderExtractor.ExtractionLineDto(1, "WIDGET", "Widget", 3, "PCS", 1250.00, 3750.00),
            });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Success.Should().BeTrue();
        result.ReviewLineNumbers.Should().BeEmpty("space-grouped 1 250,00 / 3 750,00 must match the emitted values");
    }

    [Fact]
    public void ValidateAndMap_GenuineThreeDecimalValue_IsNotFlagged()
    {
        // "1.234" printed as a 3-decimal unit price must still match even though the
        // loose parser also reads it as grouped thousands (1234).
        const string source = "1 WIDGET 2 KG 1.234";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "PO-1", OrderDate: "", Currency: "EUR", BuyerName: "Acme",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(1, "WIDGET", "Widget", 2, "KG", 1.234, 0),
            });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Success.Should().BeTrue();
        result.ReviewLineNumbers.Should().BeEmpty("the 3-decimal unit price appears in the source");
    }

    [Fact]
    public void ValidateAndMap_DuplicateModelLineNumbers_AreRenumberedPositionally()
    {
        // The model echoes the same line_number for both lines; only the second is
        // suspect. Positional numbering must target exactly the suspect line.
        const string source = "1 AA 3 PCS 10.00 30.00 2 BB 5 PCS 20.00 100.00";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "PO-1", OrderDate: "", Currency: "EUR", BuyerName: "Acme",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(7, "AA", "First", 3, "PCS", 10.00, 30.00),
                // unit_price 777 never appears in source → suspect.
                new OpenAiPdfOrderExtractor.ExtractionLineDto(7, "BB", "Second", 5, "PCS", 777.00, 100.00),
            });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Success.Should().BeTrue();
        result.Order!.Lines.Select(l => l.LineNumber).Should().Equal(1, 2);
        result.ReviewLineNumbers.Should().Equal(new[] { 2 });
    }

    [Fact]
    public void ValidateAndMap_OverflowingNumber_FlagsLineWithoutThrowing()
    {
        const string source = "1 WIDGET 1 PCS";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "PO-1", OrderDate: "", Currency: "EUR", BuyerName: "Acme",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(1, "WIDGET", "Widget", 1e40, "PCS", 0, 0),
            });

        var act = () => OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        var result = act.Should().NotThrow().Subject;
        result.Success.Should().BeTrue();
        result.ReviewLineNumbers.Should().Contain(1, "an out-of-range quantity must be flagged, not thrown");
        result.Order!.Lines[0].Quantity.Should().Be(0m);
    }

    [Fact]
    public async Task ExtractAsync_EmptyOrganisationId_FailsClosed()
    {
        var extractor = CreateExtractor(
            new Dictionary<string, string?>
            {
                ["Ai:Provider"] = "openai",
                ["Ai:OpenAI:ApiKey"] = "sk-test-key",
            },
            tracker: null);

        await using var pdf = new MemoryStream(CreatePdf("PO Number: PO-1", "1 ABC Widget 4 PCS 12.50"));
        var result = await extractor.ExtractAsync(pdf, "application/pdf", Guid.Empty, CancellationToken.None);

        result.Success.Should().BeFalse("a missing tenant must never reach the uncapped OpenAI call");
    }

    // ── Snake_case JSON binding (proves the [JsonPropertyName] attributes work) ─

    [Fact]
    public void ExtractionDto_BindsSnakeCaseJson_UnderWebDefaults()
    {
        const string json = """
            {
              "confidence": 0.9,
              "po_number": "PO-1",
              "order_date": "2026-05-20",
              "currency": "EUR",
              "buyer_name": "Acme",
              "lines": [
                { "line_number": 2, "buyer_item_code": "ABC", "description": "Widget",
                  "quantity": 4, "unit": "PCS", "unit_price": 12.5, "line_amount": 50 }
              ]
            }
            """;

        var dto = JsonSerializer.Deserialize<OpenAiPdfOrderExtractor.ExtractionDto>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        dto.Should().NotBeNull();
        dto!.PoNumber.Should().Be("PO-1");
        dto.BuyerName.Should().Be("Acme");
        dto.Currency.Should().Be("EUR");
        dto.Lines.Should().ContainSingle();
        dto.Lines![0].BuyerItemCode.Should().Be("ABC");
        dto.Lines[0].UnitPrice.Should().Be(12.5);
        dto.Lines[0].LineAmount.Should().Be(50);
    }

    // ── Phase 4: enrichment + doc-type classification ───────────────────────

    [Fact]
    public void ValidateAndMap_CapturesEnrichmentAndDocumentType()
    {
        const string source = "1 ABC Widget 4 PCS 12.50 50.00";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "INV-1", OrderDate: "2026-05-20", Currency: "EUR", BuyerName: "Acme",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(
                    1, "ABC", "Widget", 4, "PCS", 12.50, 50.00,
                    TaxRate: 0.20, DeliveryDate: "2026-06-30"),
            },
            DocumentType: "invoice",
            SupplierName: "Supplier Co",
            PaymentTerms: "Net 30",
            SubTotal: 50.00,
            TaxTotal: 10.00,
            GrandTotal: 60.00);

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Success.Should().BeTrue();
        result.Order!.DocumentType.Should().Be("invoice");
        result.Order.SupplierName.Should().Be("Supplier Co");
        result.Order.PaymentTerms.Should().Be("Net 30");
        result.Order.SubTotal.Should().Be(50.00m);
        result.Order.TaxTotal.Should().Be(10.00m);
        result.Order.GrandTotal.Should().Be(60.00m);

        var line = result.Order.Lines[0];
        line.LineAmount.Should().Be(50.00m);
        line.TaxRate.Should().Be(0.20m);
        line.DeliveryDate.Should().Be(new DateOnly(2026, 6, 30));
    }

    [Theory]
    [InlineData("invoice", "invoice")]
    [InlineData("INVOICE", "invoice")]
    [InlineData("purchase_order", "purchase_order")]
    [InlineData("purchase order", "purchase_order")]
    [InlineData("something", "other")]
    public void ValidateAndMap_NormalizesDocumentType(string raw, string expected)
    {
        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "P", OrderDate: "", Currency: "EUR", BuyerName: "A",
            Lines: new[] { new OpenAiPdfOrderExtractor.ExtractionLineDto(1, "X", "Y", 1, "PCS", 0, 0) },
            DocumentType: raw);

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, "1 X Y 1 PCS");

        result.Order!.DocumentType.Should().Be(expected);
    }

    // ── Plumbing: no-op when no key ──────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_NoApiKey_ReturnsFailure_AndIsNotAvailable()
    {
        var extractor = CreateExtractor(
            new Dictionary<string, string?> { ["Ai:Provider"] = "openai" }, // no ApiKey
            tracker: null);

        extractor.IsAvailable.Should().BeFalse();

        await using var pdf = new MemoryStream(CreatePdf("PO Number: PO-1", "1 ABC Widget 4 PCS 12.50"));
        var result = await extractor.ExtractAsync(pdf, "application/pdf", Guid.NewGuid(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Order.Should().BeNull();
        result.FailureReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void IsAvailable_WithApiKeyAndOpenAiProvider_IsTrue()
    {
        var extractor = CreateExtractor(
            new Dictionary<string, string?>
            {
                ["Ai:Provider"] = "openai",
                ["Ai:OpenAI:ApiKey"] = "sk-test-key",
            },
            tracker: null);

        extractor.IsAvailable.Should().BeTrue();
    }

    // ── Plumbing: per-org token cap short-circuits before any OpenAI call ─────

    [Fact]
    public async Task ExtractAsync_AtOrOverCap_DoesNotCallOpenAi_AndReturnsFailure()
    {
        var orgId = Guid.NewGuid();
        var tracker = new Mock<IAiUsageTracker>(MockBehavior.Strict);
        tracker.Setup(t => t.IsAtOrOverLimitAsync(orgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);
        // The blocked-path log line resolves the org's limit via the snapshot.
        tracker.Setup(t => t.GetCurrentAsync(orgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new AiUsageSnapshot(orgId, 2026, 6, 1000, 1000));

        var extractor = CreateExtractor(
            new Dictionary<string, string?>
            {
                ["Ai:Provider"] = "openai",
                ["Ai:OpenAI:ApiKey"] = "sk-test-key",
            },
            tracker: tracker.Object);

        await using var pdf = new MemoryStream(CreatePdf("PO Number: PO-1", "1 ABC Widget 4 PCS 12.50"));
        var result = await extractor.ExtractAsync(pdf, "application/pdf", orgId, CancellationToken.None);

        result.Success.Should().BeFalse("the per-org cap blocks the extraction call");
        tracker.Verify(t => t.IsAtOrOverLimitAsync(orgId, It.IsAny<CancellationToken>()), Times.Once);
        tracker.Verify(
            t => t.IncrementAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Phase 2: vision fallback routing (no text layer) ─────────────────────

    [Fact]
    public async Task ExtractAsync_NoTextLayer_WithRasterizer_RoutesToVision()
    {
        // A no-text PDF + a wired rasterizer → the extractor takes the vision path.
        // The rasterizer returns no pages, so it short-circuits to a failure BEFORE
        // any OpenAI call (deterministic, no network).
        var rasterizer = new Mock<IPdfRasterizer>();
        rasterizer.Setup(r => r.RenderPagesPng(It.IsAny<byte[]>(), It.IsAny<int>()))
                  .Returns(Array.Empty<byte[]>());

        var extractor = CreateExtractor(
            new Dictionary<string, string?> { ["Ai:Provider"] = "openai", ["Ai:OpenAI:ApiKey"] = "sk-test-key" },
            tracker: null,
            overrideClient: new ChatClient("gpt-4o-mini", "sk-test-key"),
            rasterizer: rasterizer.Object);

        await using var pdf = new MemoryStream(CreateNoTextPdf());
        var result = await extractor.ExtractAsync(pdf, "application/pdf", Guid.NewGuid(), CancellationToken.None);

        result.Success.Should().BeFalse();
        rasterizer.Verify(r => r.RenderPagesPng(It.IsAny<byte[]>(), It.IsAny<int>()), Times.Once,
            "no text layer must route to the vision rasterizer");
    }

    [Fact]
    public async Task ExtractAsync_NoTextLayer_NoRasterizer_FailsForDeterministicFallback()
    {
        var extractor = CreateExtractor(
            new Dictionary<string, string?> { ["Ai:Provider"] = "openai", ["Ai:OpenAI:ApiKey"] = "sk-test-key" },
            tracker: null,
            overrideClient: new ChatClient("gpt-4o-mini", "sk-test-key"),
            rasterizer: null); // no vision

        await using var pdf = new MemoryStream(CreateNoTextPdf());
        var result = await extractor.ExtractAsync(pdf, "application/pdf", Guid.NewGuid(), CancellationToken.None);

        result.Success.Should().BeFalse("no rasterizer → caller falls back to the deterministic parser");
        result.FailureReason.Should().Contain("text layer");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static OpenAiPdfOrderExtractor CreateExtractor(
        Dictionary<string, string?> config,
        IAiUsageTracker? tracker,
        ChatClient? overrideClient = null,
        IPdfRasterizer? rasterizer = null)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        return new OpenAiPdfOrderExtractor(
            cfg, NullLogger<OpenAiPdfOrderExtractor>.Instance, tracker, overrideClient, rasterizer);
    }

    // A 1-page PDF with no /Contents → no text layer (PdfPig extracts nothing),
    // which drives the vision fallback branch.
    private static byte[] CreateNoTextPdf()
    {
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n",
        };
        var pdf = new StringBuilder();
        pdf.AppendLine("%PDF-1.4");
        var offsets = new List<int> { 0 };
        foreach (var obj in objects)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(obj);
        }
        var xrefOffset = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.AppendLine("xref");
        pdf.AppendLine("0 4");
        pdf.AppendLine("0000000000 65535 f ");
        for (var i = 1; i <= 3; i++)
            pdf.AppendLine(offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n ");
        pdf.AppendLine("trailer");
        pdf.AppendLine("<< /Size 4 /Root 1 0 R >>");
        pdf.AppendLine("startxref");
        pdf.AppendLine(xrefOffset.ToString(CultureInfo.InvariantCulture));
        pdf.AppendLine("%%EOF");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    // Minimal valid text PDF (mirrors ProcuLink.Transform.Tests.PdfOrderParserTests).
    private static byte[] CreatePdf(params string[] lines)
    {
        var content = new StringBuilder();
        content.AppendLine("BT");
        content.AppendLine("/F1 12 Tf");
        content.AppendLine("72 720 Td");
        foreach (var line in lines)
        {
            content.Append('(').Append(EscapePdfText(line)).AppendLine(") Tj");
            content.AppendLine("0 -18 Td");
        }
        content.AppendLine("ET");
        var contentText = content.ToString();

        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>\nendobj\n",
            "4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
            string.Create(CultureInfo.InvariantCulture, $"5 0 obj\n<< /Length {Encoding.ASCII.GetByteCount(contentText)} >>\nstream\n{contentText}endstream\nendobj\n"),
        };

        var pdf = new StringBuilder();
        pdf.AppendLine("%PDF-1.4");
        var offsets = new List<int> { 0 };
        foreach (var obj in objects)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(obj);
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.AppendLine("xref");
        pdf.AppendLine("0 6");
        pdf.AppendLine("0000000000 65535 f ");
        for (var i = 1; i <= 5; i++)
            pdf.AppendLine(offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n ");
        pdf.AppendLine("trailer");
        pdf.AppendLine("<< /Size 6 /Root 1 0 R >>");
        pdf.AppendLine("startxref");
        pdf.AppendLine(xrefOffset.ToString(CultureInfo.InvariantCulture));
        pdf.AppendLine("%%EOF");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static string EscapePdfText(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("(", "\\(", StringComparison.Ordinal)
             .Replace(")", "\\)", StringComparison.Ordinal);
}
