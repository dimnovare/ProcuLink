using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Ocr;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Infrastructure.Services.Ai;

/// <summary>
/// Primary PDF parse path: extracts the PDF text layer (PdfPig) and structures it
/// into a canonical <see cref="ExtractedOrder"/> via an OpenAI strict-JSON call.
///
/// Design notes:
///   • Singleton-safe — the organisation id is a method parameter and the scoped
///     <see cref="IAiUsageTracker"/> is resolved per-call from an
///     <see cref="IServiceScopeFactory"/>, so the same instance works in BOTH the
///     API and the background Worker host (mirrors <c>OpenAiMappingService</c>).
///   • No-op (Success=false) when <c>Ai:OpenAI:ApiKey</c> is missing or the
///     provider is not "openai" — the orchestrator then falls back to the
///     deterministic regex <c>PdfOrderParser</c>.
///   • Anti-hallucination validation: every emitted number must appear in the
///     source text and quantity × unit price must reconcile with the stated line
///     amount, otherwise the line is flagged for human review. (This is a
///     defense-in-depth net, not a correctness guarantee — number presence is
///     matched document-wide, so a hallucinated value that coincides with another
///     printed number can pass; the cross-check only runs when a line amount is
///     stated.)
///   • Never throws — all failure paths return Success=false.
/// </summary>
public sealed class OpenAiPdfOrderExtractor : IStructuredOrderExtractor
{
    private const string DefaultModel = "gpt-5-mini";
    internal const double ConfidenceThreshold = 0.6;
    private const int ExtractionMaxTokens = 4000;
    // Cap the text we send so a pathological multi-hundred-page PDF can't blow the
    // per-org monthly token cap in a single call. ~60k chars ≈ ~15k input tokens,
    // comfortably above any real multi-page PO/invoice.
    private const int MaxSourceChars = 60_000;
    // Vision fallback: how many leading pages of a scanned PDF to rasterize + send.
    private const int MaxVisionPages = 3;
    private static readonly TimeSpan OpenAiCallTimeout = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // OpenAI strict mode requires every property listed in "required" AND
    // additionalProperties:false at every object level. "line_amount" is emitted
    // for arithmetic cross-checking only — it is NOT propagated to ExtractedOrder.
    private static readonly BinaryData ExtractionJsonSchema = BinaryData.FromBytes("""
        {
          "type": "object",
          "properties": {
            "confidence":    { "type": "number" },
            "document_type": { "type": "string", "enum": ["purchase_order", "invoice", "other"] },
            "po_number":     { "type": "string" },
            "order_date":    { "type": "string" },
            "currency":      { "type": "string" },
            "buyer_name":    { "type": "string", "description": "The organisation that ISSUED/PLACED the order. On an invoice instead the bill-to customer. Assign from document labels, never from which name is familiar." },
            "supplier_name": { "type": "string", "description": "The party the order is ADDRESSED TO that will fulfil it. On an invoice instead the issuing seller. Must differ from buyer_name." },
            "payment_terms": { "type": "string" },
            "incoterms":     { "type": "string", "description": "Delivery/freight terms, e.g. DDP, EXW, DAP, FCA. Empty if none stated." },
            "shipping_method": { "type": "string" },
            "buyer_order_ref": { "type": "string", "description": "The buyer's own requisition / internal order reference, distinct from po_number. Empty if none." },
            "contact": {
              "type": "object",
              "properties": {
                "name":  { "type": "string" },
                "email": { "type": "string" },
                "phone": { "type": "string" }
              },
              "required": ["name", "email", "phone"],
              "additionalProperties": false
            },
            "parties": {
              "type": "array",
              "description": "Every named party with an address or tax id: ship-to, bill-to, remit-to. Empty array if none. Do NOT duplicate buyer_name/supplier_name unless they carry an address/VAT here.",
              "items": {
                "type": "object",
                "properties": {
                  "role":        { "type": "string", "enum": ["shipTo", "billTo", "remitTo"] },
                  "name":        { "type": "string" },
                  "deliver_to":  { "type": "string", "description": "Per-address attention / care-of line (e.g. a named recipient at this address). Empty if none." },
                  "street":      { "type": "string" },
                  "city":        { "type": "string" },
                  "postal_code": { "type": "string" },
                  "country":     { "type": "string" },
                  "vat":         { "type": "string" },
                  "reference":   { "type": "string" }
                },
                "required": ["role", "name", "deliver_to", "street", "city", "postal_code", "country", "vat", "reference"],
                "additionalProperties": false
              }
            },
            "raw_fields": {
              "type": "array",
              "description": "Any other labelled field on the document NOT captured above (e.g. supplier number, EDI id, contract no, cost centre). Each is a label+value pair exactly as printed. Empty array if none.",
              "items": {
                "type": "object",
                "properties": {
                  "label": { "type": "string" },
                  "value": { "type": "string" }
                },
                "required": ["label", "value"],
                "additionalProperties": false
              }
            },
            "sub_total":     { "type": "number" },
            "tax_total":     { "type": "number" },
            "grand_total":   { "type": "number" },
            "lines": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "line_number":              { "type": "integer" },
                  "buyer_item_code":          { "type": "string" },
                  "manufacturer_part_number": { "type": "string", "description": "The manufacturer/vendor product number (e.g. 'Ihre Materialnr', ManufPN). Empty if none." },
                  "customer_part_number":     { "type": "string" },
                  "description":              { "type": "string" },
                  "quantity":                 { "type": "number" },
                  "unit":                     { "type": "string" },
                  "unit_price":               { "type": "number" },
                  "discount_percent":         { "type": "number" },
                  "line_amount":              { "type": "number" },
                  "net_amount":               { "type": "number" },
                  "tax_rate":                 { "type": "number" },
                  "unspsc":                   { "type": "string" },
                  "recipient":                { "type": "string" },
                  "contract_number":          { "type": "string" },
                  "delivery_date":            { "type": "string" }
                },
                "required": ["line_number", "buyer_item_code", "manufacturer_part_number", "customer_part_number", "description", "quantity", "unit", "unit_price", "discount_percent", "line_amount", "net_amount", "tax_rate", "unspsc", "recipient", "contract_number", "delivery_date"],
                "additionalProperties": false
              }
            }
          },
          "required": ["confidence", "document_type", "po_number", "order_date", "currency", "buyer_name", "supplier_name", "payment_terms", "incoterms", "shipping_method", "buyer_order_ref", "contact", "parties", "raw_fields", "sub_total", "tax_total", "grand_total", "lines"],
          "additionalProperties": false
        }
        """u8.ToArray());

    // Exposed internal so a non-gated regression test can assert the deterministic
    // role language is present (the party-role wording is the fix for buyer/supplier swaps).
    internal const string SystemPrompt =
        "You extract a purchase order (or invoice) from a supplier PDF — its extracted " +
        "text, or a scanned image of it. " +
        "Return ONLY structured data matching the schema. Copy numbers and codes " +
        "EXACTLY as printed — never invent, round, or compute a value that is not " +
        "in the document. Use the document's own line/item codes as buyer_item_code. " +
        "Leave a string field empty and a number 0 when the document does not state it. " +
        "Set line_amount to the printed line total (quantity x unit price) when shown, else 0. " +
        // ── Party-role assignment (deterministic, document-driven) ──
        "Assign the two parties PURELY from the document's own labels and structure. " +
        "For a PURCHASE ORDER: buyer_name = the organisation that ISSUED / PLACED the order " +
        "(the originator — the party on whose letterhead/header the order is raised, labelled " +
        "e.g. 'ordered by', 'bill to', 'buyer', 'from'); supplier_name = the party the order is " +
        "ADDRESSED TO and that will fulfil it (labelled e.g. 'supplier', 'vendor', 'to', " +
        "'deliver from', 'sold by'). " +
        "For an INVOICE the roles INVERT: supplier_name = the issuing seller; " +
        "buyer_name = the bill-to customer. " +
        "Do NOT assume any particular company name is the buyer — a familiar name may be the " +
        "recipient OR the issuer, and a company name merely appearing in the header does not " +
        "make it the buyer. buyer_name and supplier_name MUST be two DIFFERENT parties. " +
        "sub_total/tax_total/grand_total = the document's stated totals when present, else 0. " +
        "delivery_date = the line's requested/printed delivery date as YYYY-MM-DD, else empty. " +
        "Classify document_type: 'invoice' if it is a bill/invoice (e.g. titled Invoice, has an " +
        "invoice number / amount due), 'purchase_order' if it is an order being placed, else 'other'. " +
        // ── Phase 1 lossless capture (parties / contact / line enrichment / raw_fields) ──
        "Capture every named address as a party in 'parties' with its role (shipTo / billTo / " +
        "remitTo), street, city, postal_code, country and VAT/tax id when printed. " +
        // ── Party NAME is mandatory whenever an address is present ──
        "For EACH party (shipTo / billTo / remitTo) you MUST capture its 'name' — the company, " +
        "plant, site, store, warehouse or organisation name printed at or directly above that " +
        "address block (e.g. a delivery site like 'REDACTED-PARTY' or " +
        "'Warehouse 3 — Riga'). NEVER leave a party's 'name' blank when an address is present: if " +
        "only a site, plant, depot or building label is shown, use that label as the 'name'. " +
        "The ship-to name is often a DIFFERENT site/plant than the buyer's head office — extract the " +
        "site name actually printed by the ship-to address; do NOT copy buyer_name into it, and do " +
        "NOT leave it empty just because it differs from the buyer. Keep 'deliver_to' (an " +
        "attention / care-of PERSON at the address) SEPARATE from 'name' (the organisation / site). " +
        "Worked example — buyer 'ACME Foods SA' shipping to a plant: the shipTo party is " +
        "{ name: 'REDACTED-PARTY', street: 'REDACTED-ADDRESS', city: " +
        "'REDACTED-PARTY " +
        "NOT { name: '' }. " +
        "Capture the " +
        "ordering contact (name/email/phone) in 'contact'. Capture incoterms / delivery terms, " +
        "shipping_method and the buyer's own order reference when stated. For each line capture the " +
        "manufacturer/vendor part number, any customer part number, discount %, UNSPSC, per-line " +
        "recipient, contract number and net amount when printed. Put ANY other labelled value you see " +
        "but cannot place into a field into 'raw_fields' as a label+value pair, copied verbatim — " +
        "never invent or omit. Leave a string empty and a number 0 when the document does not state it. " +
        "Set confidence 0.0-1.0 based on how clearly the text is a real purchase order or invoice.";

    private readonly ChatClient? _client;
    private readonly ILogger<OpenAiPdfOrderExtractor> _logger;
    private readonly string _model;
    // The tracker is a scoped EF service; this extractor is a singleton, so we
    // resolve a tracker per-call from the provided scope factory. Tests inject a
    // tracker directly via the internal ctor instead.
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly Func<IAiUsageTracker?>? _trackerFactory;
    // Optional vision fallback for scanned / no-text PDFs. Null → no vision (text-only).
    private readonly IPdfRasterizer? _rasterizer;

    public OpenAiPdfOrderExtractor(
        IConfiguration configuration,
        ILogger<OpenAiPdfOrderExtractor> logger,
        IServiceScopeFactory? scopeFactory = null,
        IPdfRasterizer? rasterizer = null)
    {
        _logger = logger;
        _model = configuration["Ai:OpenAI:ExtractionModel"]
                 ?? configuration["Ai:OpenAI:MappingModel"]
                 ?? DefaultModel;

        var provider = configuration["Ai:Provider"];
        var apiKey = configuration["Ai:OpenAI:ApiKey"];

        if (string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(apiKey))
        {
            _client = new ChatClient(_model, apiKey);
        }

        _scopeFactory = scopeFactory;
        _trackerFactory = null;
        _rasterizer = rasterizer;
    }

    /// <summary>
    /// Test-only ctor: inject a deterministic <see cref="IAiUsageTracker"/> without
    /// an <see cref="IServiceScopeFactory"/>, and optionally force the "client
    /// present" branch via <paramref name="overrideClient"/> without a real key.
    /// </summary>
    internal OpenAiPdfOrderExtractor(
        IConfiguration configuration,
        ILogger<OpenAiPdfOrderExtractor> logger,
        IAiUsageTracker? tracker,
        ChatClient? overrideClient = null,
        IPdfRasterizer? rasterizer = null)
    {
        _logger = logger;
        _model = configuration["Ai:OpenAI:ExtractionModel"]
                 ?? configuration["Ai:OpenAI:MappingModel"]
                 ?? DefaultModel;

        var provider = configuration["Ai:Provider"];
        var apiKey = configuration["Ai:OpenAI:ApiKey"];

        if (overrideClient is not null)
        {
            _client = overrideClient;
        }
        else if (string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(apiKey))
        {
            _client = new ChatClient(_model, apiKey);
        }

        _scopeFactory = null;
        _trackerFactory = () => tracker;
        _rasterizer = rasterizer;
    }

    public bool IsAvailable => _client is not null;

    public async Task<StructuredExtractionResult> ExtractAsync(
        Stream document,
        string contentType,
        Guid organisationId,
        CancellationToken ct)
    {
        if (_client is null)
            return StructuredExtractionResult.Fail("AI provider not configured.");

        // No tenant context → refuse, so the per-org cap can never be bypassed by a
        // caller that forgot to thread the org id (fail closed, not open).
        if (organisationId == Guid.Empty)
            return StructuredExtractionResult.Fail("No organisation context for AI extraction.");

        // Buffer the document so we can extract text (and, in a later phase,
        // rasterise for a vision fallback) over the same bytes.
        byte[] bytes;
        try
        {
            using var ms = new MemoryStream();
            await document.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PDF extraction: could not read document stream (org {OrgId}).", organisationId);
            return StructuredExtractionResult.Fail("Could not read document.");
        }

        // ── Per-org monthly token cap (applies to BOTH the text and vision paths) ──
        await using var trackerScope = _scopeFactory?.CreateAsyncScope();
        var tracker = trackerScope is not null
            ? trackerScope.Value.ServiceProvider.GetService<IAiUsageTracker>()
            : _trackerFactory?.Invoke();

        if (await IsAtOrOverCapAsync(tracker, organisationId, ct))
            return StructuredExtractionResult.Fail(StructuredExtractionResult.UsageCapFailureReason);

        // ── Text layer (primary). PdfPig extracts the digital text; the LLM structures it. ──
        string sourceText;
        try
        {
            // Timeout-bounded so a pathological PDF can't hang the parse pipeline indefinitely.
            sourceText = await PdfTextExtractor.ExtractTextAsync(bytes, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PDF extraction: PdfPig text extraction failed (org {OrgId}).", organisationId);
            return StructuredExtractionResult.Fail("Could not read PDF text layer.");
        }

        // No text layer → scanned / image-only PDF. Use the vision fallback when a
        // rasterizer is wired; otherwise the orchestrator falls back to deterministic parsing.
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            if (_rasterizer is not null)
                return await ExtractViaVisionAsync(bytes, organisationId, tracker, ct);
            return StructuredExtractionResult.Fail("PDF has no extractable text layer.");
        }

        // Bound the input so one oversized document can't overshoot the token cap.
        if (sourceText.Length > MaxSourceChars)
        {
            _logger.LogWarning(
                "PDF extraction: source text {Len} chars exceeds {Max}; truncating before the LLM call (org {OrgId}).",
                sourceText.Length, MaxSourceChars, organisationId);
            sourceText = sourceText[..MaxSourceChars];
        }

        // ── OpenAI strict-JSON structured extraction ─────────────────────────
        try
        {
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(SystemPrompt),
                new UserChatMessage(sourceText),
            };

            var completion = await CompleteWithTimeoutAsync(
                messages,
                new ChatCompletionOptions
                {
                    MaxOutputTokenCount = ExtractionMaxTokens,
                    ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                        jsonSchemaFormatName: "purchase_order_extraction",
                        jsonSchema: ExtractionJsonSchema,
                        jsonSchemaIsStrict: true),
                },
                ct);

            await RecordUsageAsync(tracker, organisationId, completion.Usage?.TotalTokenCount ?? 0, ct);

            var json = completion.Content.FirstOrDefault()?.Text;
            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogWarning("PDF extraction returned empty content (org {OrgId}).", organisationId);
                return StructuredExtractionResult.Fail("AI returned empty response.");
            }

            var dto = JsonSerializer.Deserialize<ExtractionDto>(json, JsonOptions);
            if (dto is null)
            {
                _logger.LogWarning("PDF extraction response could not be deserialised (org {OrgId}).", organisationId);
                return StructuredExtractionResult.Fail("AI response could not be parsed.");
            }

            return ValidateAndMap(dto, sourceText);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("PDF extraction timed out (org {OrgId}).", organisationId);
            return StructuredExtractionResult.Fail("AI request timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PDF extraction failed (org {OrgId}).", organisationId);
            return StructuredExtractionResult.Fail("AI request failed.");
        }
    }

    // ─── Vision fallback (scanned / image-only PDFs) ─────────────────────────

    /// <summary>
    /// Vision path for PDFs with no text layer: rasterize the leading pages and send
    /// them as images to the (vision-capable) model with the same strict schema.
    /// Because there is no text layer to verify numbers against, EVERY extracted line
    /// is flagged for human review — scanned extraction is inherently lower-trust.
    /// Never throws.
    /// </summary>
    private async Task<StructuredExtractionResult> ExtractViaVisionAsync(
        byte[] bytes, Guid organisationId, IAiUsageTracker? tracker, CancellationToken ct)
    {
        IReadOnlyList<byte[]> pages;
        try
        {
            pages = _rasterizer!.RenderPagesPng(bytes, MaxVisionPages);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vision extraction: rasterization failed (org {OrgId}).", organisationId);
            return StructuredExtractionResult.Fail("Could not rasterise the PDF for vision extraction.");
        }

        if (pages.Count == 0)
            return StructuredExtractionResult.Fail("PDF has no extractable text layer and could not be rasterised.");

        try
        {
            var parts = new List<ChatMessageContentPart>
            {
                ChatMessageContentPart.CreateTextPart(
                    "This is a scanned/image PDF with no text layer. Extract the purchase order " +
                    "or invoice from the image(s) per the schema. Copy every code and number exactly."),
            };
            foreach (var png in pages)
                parts.Add(ChatMessageContentPart.CreateImagePart(
                    BinaryData.FromBytes(png), "image/png", ChatImageDetailLevel.High));

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(SystemPrompt),
                new UserChatMessage(parts),
            };

            var completion = await CompleteWithTimeoutAsync(
                messages,
                new ChatCompletionOptions
                {
                    MaxOutputTokenCount = ExtractionMaxTokens,
                    ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                        jsonSchemaFormatName: "purchase_order_extraction",
                        jsonSchema: ExtractionJsonSchema,
                        jsonSchemaIsStrict: true),
                },
                ct);

            await RecordUsageAsync(tracker, organisationId, completion.Usage?.TotalTokenCount ?? 0, ct);

            var json = completion.Content.FirstOrDefault()?.Text;
            if (string.IsNullOrWhiteSpace(json))
                return StructuredExtractionResult.Fail("AI returned empty response.");

            var dto = JsonSerializer.Deserialize<ExtractionDto>(json, JsonOptions);
            if (dto is null)
                return StructuredExtractionResult.Fail("AI response could not be parsed.");

            // No text layer to verify against → map with an empty source, then force
            // EVERY line into review (scanned extraction is inherently lower-trust).
            var result = ValidateAndMap(dto, string.Empty);
            if (result.Success && result.Order is not null)
            {
                var allLines = result.Order.Lines.Select(l => l.LineNumber).ToArray();
                const string scannedReason =
                    "Extracted from a scanned (image-only) PDF — there was no text layer to verify the numbers against.";
                result = result with
                {
                    ReviewLineNumbers = allLines,
                    ReviewReasons     = allLines.ToDictionary(n => n, _ => scannedReason),
                };
                _logger.LogInformation(
                    "Order vision-extracted from {Pages} scanned page(s) (org {OrgId}) — all {Lines} lines flagged for review.",
                    pages.Count, organisationId, allLines.Length);
            }
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Vision extraction timed out (org {OrgId}).", organisationId);
            return StructuredExtractionResult.Fail("AI request timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vision extraction failed (org {OrgId}).", organisationId);
            return StructuredExtractionResult.Fail("AI vision request failed.");
        }
    }

    // ─── Validation + mapping (pure, unit-tested directly) ───────────────────

    /// <summary>
    /// Maps the raw model output onto the canonical <see cref="ExtractedOrder"/> and
    /// applies the anti-hallucination safety net. Returns Success=false when the
    /// order is below the confidence threshold or has no lines (the orchestrator
    /// then falls back to deterministic parsing). When successful, every line whose
    /// emitted numbers do not appear in the source text, or whose
    /// quantity × unit price does not reconcile with the stated line amount, is
    /// reported in <see cref="StructuredExtractionResult.ReviewLineNumbers"/>.
    /// Pure and never throws — malformed numbers (NaN/overflow) flag the line rather
    /// than propagating.
    /// </summary>
    internal static StructuredExtractionResult ValidateAndMap(ExtractionDto dto, string sourceText)
    {
        var confidence = Math.Clamp(dto.Confidence, 0.0, 1.0);
        var rawLines = dto.Lines ?? Array.Empty<ExtractionLineDto>();

        if (confidence < ConfidenceThreshold)
            return StructuredExtractionResult.Fail(
                $"Extraction confidence {confidence:F2} is below the threshold {ConfidenceThreshold:F2}.");

        if (rawLines.Count == 0)
            return StructuredExtractionResult.Fail("No line items were extracted.");

        var sourceNumbers = ExtractSourceNumbers(sourceText);

        var lines = new List<ExtractedOrderLine>(rawLines.Count);
        var reviewLineNumbers = new List<int>();
        // P2 hardening: per-line "why flagged" causes, persisted onto review_reason.
        var reviewReasons = new Dictionary<int, string>();

        for (var idx = 0; idx < rawLines.Count; idx++)
        {
            var l = rawLines[idx];
            // Positional line number — a stable, unique join key for the downstream
            // review overlay and mapping. The model's own line_number is unreliable
            // (it may duplicate or echo a "Pos" column), so we don't trust it here.
            var lineNumber = idx + 1;

            var causes = new List<string>();

            // Convert numbers safely — a NaN/Infinity/out-of-range value from the
            // model flags the line instead of throwing.
            if (!TryToDecimal(l.Quantity, out var quantity) && l.Quantity is not null)
                causes.Add("an extracted numeric value was unreadable");

            decimal? unitPrice = null;
            if (l.UnitPrice is not null)
            {
                if (TryToDecimal(l.UnitPrice, out var up)) unitPrice = up;
                else causes.Add("the extracted unit price was unreadable");
            }

            decimal? lineAmount = null;
            if (l.LineAmount is not null)
            {
                if (TryToDecimal(l.LineAmount, out var la)) lineAmount = la;
                else causes.Add("the extracted line amount was unreadable");
            }

            // Anti-hallucination: every emitted number must appear verbatim in the
            // source text. A zero quantity means "not stated" and is not checked.
            if (quantity != 0m && !NumberAppearsInSource(quantity, sourceNumbers))
                causes.Add("the extracted quantity does not appear in the source document");
            if (unitPrice is { } up2 && up2 != 0m && !NumberAppearsInSource(up2, sourceNumbers))
                causes.Add("the extracted unit price does not appear in the source document");
            if (lineAmount is { } la2 && la2 != 0m && !NumberAppearsInSource(la2, sourceNumbers))
                causes.Add("the extracted line amount does not appear in the source document");

            // Arithmetic: quantity × unit price must reconcile with the stated line amount.
            if (unitPrice is { } u && lineAmount is { } amount && amount != 0m)
            {
                var expected = quantity * u;
                var tolerance = Math.Max(0.02m * Math.Abs(amount), 0.05m);
                if (Math.Abs(expected - amount) > tolerance)
                    causes.Add("quantity × unit price does not match the stated line amount");
            }

            if (causes.Count > 0)
            {
                reviewLineNumbers.Add(lineNumber);
                reviewReasons[lineNumber] = $"AI extraction flagged this line: {string.Join("; ", causes)}.";
            }

            // Phase 4 enrichment (captured as metadata; not gated by anti-hallucination).
            decimal? taxRate = TryToDecimal(l.TaxRate, out var tr) ? tr : null;
            DateOnly? deliveryDate = ParseDateOnly(l.DeliveryDate);

            lines.Add(new ExtractedOrderLine(
                LineNumber: lineNumber,
                BuyerItemCode: l.BuyerItemCode?.Trim() ?? string.Empty,
                Description: string.IsNullOrWhiteSpace(l.Description) ? null : l.Description.Trim(),
                Quantity: quantity,
                Unit: string.IsNullOrWhiteSpace(l.Unit) ? null : l.Unit.Trim(),
                UnitPrice: unitPrice,
                LineAmount: lineAmount,
                TaxRate: taxRate,
                DeliveryDate: deliveryDate,
                // Phase 1 lossless capture (advisory — not gated by anti-hallucination).
                ManufacturerPartNumber: NullIfBlank(l.ManufacturerPartNumber),
                CustomerPartNumber: NullIfBlank(l.CustomerPartNumber),
                DiscountPercent: TryToDecimal(l.DiscountPercent, out var disc) ? disc : null,
                Unspsc: NullIfBlank(l.Unspsc),
                Recipient: NullIfBlank(l.Recipient),
                ContractNumber: NullIfBlank(l.ContractNumber),
                NetAmount: TryToDecimal(l.NetAmount, out var net) ? net : null));
        }

        DateTime? orderDate = null;
        if (!string.IsNullOrWhiteSpace(dto.OrderDate)
            && DateTime.TryParse(dto.OrderDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            orderDate = parsed;
        }

        var order = new ExtractedOrder(
            PoNumber: string.IsNullOrWhiteSpace(dto.PoNumber) ? null : dto.PoNumber.Trim(),
            OrderDate: orderDate,
            BuyerName: string.IsNullOrWhiteSpace(dto.BuyerName) ? null : dto.BuyerName.Trim(),
            Currency: string.IsNullOrWhiteSpace(dto.Currency) ? null : dto.Currency.Trim(),
            Lines: lines,
            SupplierName: string.IsNullOrWhiteSpace(dto.SupplierName) ? null : dto.SupplierName.Trim(),
            SubTotal: TryToDecimal(dto.SubTotal, out var sub) ? sub : null,
            TaxTotal: TryToDecimal(dto.TaxTotal, out var tax) ? tax : null,
            GrandTotal: TryToDecimal(dto.GrandTotal, out var grand) ? grand : null,
            PaymentTerms: string.IsNullOrWhiteSpace(dto.PaymentTerms) ? null : dto.PaymentTerms.Trim(),
            DocumentType: NormalizeDocumentType(dto.DocumentType),
            // Phase 1 lossless capture (advisory — unverifiable; rides through as-is).
            Parties: dto.Parties?.Select(p => new ExtractedParty(
                p.Role, NullIfBlank(p.Name), NullIfBlank(p.Street), NullIfBlank(p.City),
                NullIfBlank(p.PostalCode), NullIfBlank(p.Country), NullIfBlank(p.Vat),
                Reference: NullIfBlank(p.Reference),
                // Per-address attention/care-of line → ExtractedParty.ContactName → cXML <DeliverTo>.
                ContactName: NullIfBlank(p.DeliverTo))).Where(p => HasAnyValue(p)).ToList(),
            ContactName: NullIfBlank(dto.Contact?.Name),
            ContactEmail: NullIfBlank(dto.Contact?.Email),
            ContactPhone: NullIfBlank(dto.Contact?.Phone),
            Incoterms: NullIfBlank(dto.Incoterms),
            ShippingMethod: NullIfBlank(dto.ShippingMethod),
            BuyerOrderRef: NullIfBlank(dto.BuyerOrderRef),
            RawFields: dto.RawFields?.Where(f => !string.IsNullOrWhiteSpace(f.Value))
                .Select(f => new ExtractedRawField(f.Label?.Trim() ?? "", f.Value.Trim())).ToList());

        return new StructuredExtractionResult(
            true, confidence, order, null, reviewLineNumbers,
            reviewReasons.Count > 0 ? reviewReasons : null);
    }

    private static DateOnly? ParseDateOnly(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;

    // Phase 1 lossless capture helpers.
    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // A doc with no addresses yields an empty parties list, not a noise row of all-nulls.
    private static bool HasAnyValue(ExtractedParty p) =>
        p.Name is not null || p.Street is not null || p.City is not null || p.Vat is not null;

    /// <summary>Normalises the model's doc-type to one of "purchase_order" | "invoice" | "other" (null if absent).</summary>
    private static string? NormalizeDocumentType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim().ToLowerInvariant();
        return v switch
        {
            "invoice" => "invoice",
            "purchase_order" or "purchase order" or "po" or "order" => "purchase_order",
            _ => "other",
        };
    }

    // ─── Anti-hallucination number matching ──────────────────────────────────

    // Number-like runs: an optional grouped-thousands form (at least one
    // [.,]ddd group) OR a plain integer/decimal. A regular space is NOT a
    // thousands separator here — PdfPig joins distinct words with single spaces,
    // so "4 500" must tokenise as two numbers (4, 500), never one (4500). The
    // EU/Baltic space-grouped form ("1 250,00") is recovered separately below.
    private static readonly Regex NumberToken = new(
        @"\d{1,3}(?:[.,]\d{3})+(?:[.,]\d+)?|\d+(?:[.,]\d+)?",
        RegexOptions.Compiled);

    // A single-separator token with exactly 3 trailing digits (e.g. "1.234",
    // "1,500") is ambiguous: grouped thousands OR a genuine 3-decimal value.
    private static readonly Regex AmbiguousThreeDecimal = new(
        @"^\d{1,3}[.,]\d{3}$", RegexOptions.Compiled);

    // A single space/NBSP sitting BETWEEN two digits is a thousands separator in
    // EU/Baltic print ("1 250,00"). \x20 = regular space,   = no-break space.
    private static readonly Regex InterDigitSpace = new(
        @"(?<=\d)[\x20 ](?=\d)", RegexOptions.Compiled);

    private static HashSet<decimal> ExtractSourceNumbers(string sourceText)
    {
        var set = new HashSet<decimal>();
        if (string.IsNullOrEmpty(sourceText)) return set;

        foreach (Match m in NumberToken.Matches(sourceText))
            AddNumberCandidates(m.Value, set);

        // Recover space/NBSP-grouped thousands ("1 250,00" -> 1250.00): collapse the
        // inter-digit spaces and tokenise the copy too. The split readings (1, 250.00)
        // are already in the set from the pass above; this only ADDS the merged value.
        // Membership-only — extra candidates make matching more lenient, never cause a
        // false review flag.
        var collapsed = InterDigitSpace.Replace(sourceText, string.Empty);
        if (!string.Equals(collapsed, sourceText, StringComparison.Ordinal))
        {
            foreach (Match m in NumberToken.Matches(collapsed))
                AddNumberCandidates(m.Value, set);
        }

        return set;
    }

    private static void AddNumberCandidates(string token, HashSet<decimal> set)
    {
        if (TryParseLoose(token, out var primary))
            set.Add(Normalize(primary));

        // For the ambiguous 3-trailing-digit case, ALSO add the decimal reading so a
        // correctly-emitted 1.234 still matches its source even though TryParseLoose
        // read the printed "1.234" as grouped thousands (1234). Membership-only.
        if (AmbiguousThreeDecimal.IsMatch(token)
            && decimal.TryParse(token.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var asDecimal))
        {
            set.Add(Normalize(asDecimal));
        }
    }

    private static bool NumberAppearsInSource(decimal value, HashSet<decimal> sourceNumbers) =>
        sourceNumbers.Contains(Normalize(value));

    // Round to 4dp so double→decimal noise and trailing zeros don't cause spurious misses.
    private static decimal Normalize(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Safely converts a model-supplied double to decimal. Returns false (rather
    /// than throwing) for null, NaN, Infinity, or out-of-decimal-range values, so
    /// the pure <see cref="ValidateAndMap"/> honours its never-throw contract.
    /// </summary>
    private static bool TryToDecimal(double? d, out decimal value)
    {
        value = 0m;
        if (d is null) return false;
        var x = d.Value;
        if (double.IsNaN(x) || double.IsInfinity(x)) return false;
        if (x is > 7.9e28 or < -7.9e28) return false; // outside decimal's range
        try { value = (decimal)x; return true; }
        catch (OverflowException) { return false; }
    }

    /// <summary>
    /// Parses a printed number token into a decimal, resolving European vs Anglo
    /// thousands/decimal separators. Lenient by design — its only consumer is the
    /// anti-hallucination "does this number appear in the source" check.
    /// </summary>
    private static bool TryParseLoose(string token, out decimal value)
    {
        value = 0m;
        var t = token.Trim();
        if (t.Length == 0) return false;

        var dots = t.Count(c => c == '.');
        var commas = t.Count(c => c == ',');

        string normalized;
        if (dots > 0 && commas > 0)
        {
            // The right-most of the two is the decimal separator; the other is thousands.
            var decimalSep = t.LastIndexOf('.') > t.LastIndexOf(',') ? '.' : ',';
            var thousandsSep = decimalSep == '.' ? ',' : '.';
            t = t.Replace(thousandsSep.ToString(), string.Empty);
            // A single decimal separator is expected; if more remain, treat them all as thousands.
            normalized = t.Count(c => c == decimalSep) == 1
                ? t.Replace(decimalSep, '.')
                : t.Replace(decimalSep.ToString(), string.Empty);
        }
        else if (commas > 0)
        {
            normalized = NormalizeSingleSeparator(t, ',');
        }
        else if (dots > 0)
        {
            normalized = NormalizeSingleSeparator(t, '.');
        }
        else
        {
            normalized = t;
        }

        return decimal.TryParse(
            normalized, NumberStyles.Number | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out value);
    }

    private static string NormalizeSingleSeparator(string t, char sep)
    {
        var count = t.Count(c => c == sep);
        if (count == 0) return t;

        // Multiple occurrences → grouped thousands (e.g. "1.234.567").
        if (count > 1) return t.Replace(sep.ToString(), string.Empty);

        // Single occurrence: a 3-digit trailing group is ambiguous and read as
        // thousands ("1.234" / "1,500"); any other length is a decimal fraction.
        // (The decimal reading of the 3-digit case is added separately in
        // AddNumberCandidates so a genuine 3dp value still matches.)
        var trailing = t.Length - t.IndexOf(sep) - 1;
        return trailing == 3
            ? t.Replace(sep.ToString(), string.Empty)
            : t.Replace(sep, '.');
    }

    // ─── Plumbing helpers ────────────────────────────────────────────────────

    private async Task<bool> IsAtOrOverCapAsync(IAiUsageTracker? tracker, Guid orgId, CancellationToken ct)
    {
        if (tracker is null || orgId == Guid.Empty) return false;
        try
        {
            if (await tracker.IsAtOrOverLimitAsync(orgId, ct))
            {
                // LogError (→ Sentry): a latched cap silently degrades every PDF upload
                // for this org to the regex fallback, so ops must see it without grepping.
                var snapshot = await tracker.GetCurrentAsync(orgId, ct);
                _logger.LogError(
                    "PDF extraction skipped — org {OrgId} reached monthly token limit {Limit}.",
                    orgId, snapshot.TokensLimit);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            // Cap-check failure must not silently bypass the cap.
            _logger.LogWarning(ex, "PDF extraction cap check failed for org {OrgId}; skipping to be safe.", orgId);
            return true;
        }
    }

    private async Task RecordUsageAsync(IAiUsageTracker? tracker, Guid orgId, int totalTokens, CancellationToken ct)
    {
        if (tracker is null || orgId == Guid.Empty || totalTokens <= 0) return;
        try
        {
            await tracker.IncrementAsync(orgId, totalTokens, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record AI token usage for org {OrgId}.", orgId);
        }
    }

    private async Task<ChatCompletion> CompleteWithTimeoutAsync(
        IList<ChatMessage> messages,
        ChatCompletionOptions options,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(OpenAiCallTimeout);
        return await _client!.CompleteChatAsync(messages, options, cts.Token);
    }

    // ─── DTOs for OpenAI structured outputs ──────────────────────────────────
    // Snake_case [JsonPropertyName] required: the schema uses snake_case keys and
    // JsonSerializerDefaults.Web would otherwise map to camelCase and bind null.

    internal sealed record ExtractionPartyDto(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("name")] string? Name = null,
        [property: JsonPropertyName("deliver_to")] string? DeliverTo = null,
        [property: JsonPropertyName("street")] string? Street = null,
        [property: JsonPropertyName("city")] string? City = null,
        [property: JsonPropertyName("postal_code")] string? PostalCode = null,
        [property: JsonPropertyName("country")] string? Country = null,
        [property: JsonPropertyName("vat")] string? Vat = null,
        [property: JsonPropertyName("reference")] string? Reference = null);

    internal sealed record RawFieldDto(
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("value")] string Value);

    internal sealed record ContactDto(
        [property: JsonPropertyName("name")] string? Name = null,
        [property: JsonPropertyName("email")] string? Email = null,
        [property: JsonPropertyName("phone")] string? Phone = null);

    internal sealed record ExtractionDto(
        [property: JsonPropertyName("confidence")] double Confidence,
        [property: JsonPropertyName("po_number")] string? PoNumber,
        [property: JsonPropertyName("order_date")] string? OrderDate,
        [property: JsonPropertyName("currency")] string? Currency,
        [property: JsonPropertyName("buyer_name")] string? BuyerName,
        [property: JsonPropertyName("lines")] IReadOnlyList<ExtractionLineDto>? Lines,
        [property: JsonPropertyName("document_type")] string? DocumentType = null,
        [property: JsonPropertyName("supplier_name")] string? SupplierName = null,
        [property: JsonPropertyName("payment_terms")] string? PaymentTerms = null,
        [property: JsonPropertyName("sub_total")] double? SubTotal = null,
        [property: JsonPropertyName("tax_total")] double? TaxTotal = null,
        [property: JsonPropertyName("grand_total")] double? GrandTotal = null,
        [property: JsonPropertyName("incoterms")] string? Incoterms = null,
        [property: JsonPropertyName("shipping_method")] string? ShippingMethod = null,
        [property: JsonPropertyName("buyer_order_ref")] string? BuyerOrderRef = null,
        [property: JsonPropertyName("contact")] ContactDto? Contact = null,
        [property: JsonPropertyName("parties")] IReadOnlyList<ExtractionPartyDto>? Parties = null,
        [property: JsonPropertyName("raw_fields")] IReadOnlyList<RawFieldDto>? RawFields = null);

    internal sealed record ExtractionLineDto(
        [property: JsonPropertyName("line_number")] int LineNumber,
        [property: JsonPropertyName("buyer_item_code")] string? BuyerItemCode,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("quantity")] double? Quantity,
        [property: JsonPropertyName("unit")] string? Unit,
        [property: JsonPropertyName("unit_price")] double? UnitPrice,
        [property: JsonPropertyName("line_amount")] double? LineAmount,
        [property: JsonPropertyName("tax_rate")] double? TaxRate = null,
        [property: JsonPropertyName("delivery_date")] string? DeliveryDate = null,
        [property: JsonPropertyName("manufacturer_part_number")] string? ManufacturerPartNumber = null,
        [property: JsonPropertyName("customer_part_number")] string? CustomerPartNumber = null,
        [property: JsonPropertyName("discount_percent")] double? DiscountPercent = null,
        [property: JsonPropertyName("unspsc")] string? Unspsc = null,
        [property: JsonPropertyName("recipient")] string? Recipient = null,
        [property: JsonPropertyName("contract_number")] string? ContractNumber = null,
        [property: JsonPropertyName("net_amount")] double? NetAmount = null);
}
