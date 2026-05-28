# Email Body NLP Order Extractor — Agent Report

**Date:** 2026-05-28  
**Agent:** Claude Sonnet 4.6  
**Build:** ✅ 0 errors, 0 warnings (`dotnet build ProcuLink.slnx --no-restore`)  
**Tests:** 5 new tests in `OpenAiEmailBodyOrderExtractorTests` — all guard-path tests, no live API needed

---

## Files Created

| File | Purpose |
|---|---|
| `ProcuLink.Core/Services/Ai/IEmailBodyOrderExtractor.cs` | Interface + Core-native result records |
| `ProcuLink.Infrastructure/Services/Ai/OpenAiEmailBodyOrderExtractor.cs` | OpenAI-backed implementation |
| `ProcuLink.Infrastructure.Tests/Services/Ai/OpenAiEmailBodyOrderExtractorTests.cs` | 5 guard-path unit tests |

---

## Design Decisions

### Why `ExtractedOrder` instead of `ParsedOrder`

The spec asked for `ParsedOrder?` in the `EmailBodyExtractionResult` record placed in `ProcuLink.Core`. This is architecturally impossible: `ProcuLink.Transform` (where `ParsedOrder` lives) already has a project reference to `ProcuLink.Core`. Adding the reverse reference would create a circular dependency.

The solution: define `ExtractedOrder` and `ExtractedOrderLine` as Core-native records in the same interface file. Their shapes are identical to `ParsedOrder` / `ParsedOrderLine`. When `InboundEmailRouter` wires this service, it maps `ExtractedOrder` to a `ParsedOrder` for handoff to the existing parse pipeline — a trivial field-by-field copy since the types are structurally identical.

### Mirror of `OpenAiSchemaInferencer`

`OpenAiEmailBodyOrderExtractor` mirrors `OpenAiSchemaInferencer` exactly in:
- Constructor signature and field layout
- `internal` test-ctor with `Func<Guid> orgIdProvider` and `ChatClient? overrideClient`
- `ResolveOrgIdOrEmpty()` pattern
- `IsAtOrOverCapAsync()` fail-safe (exception → treat as blocked, never bypass)
- `RecordUsageAsync()` swallowed-exception pattern
- `CompleteWithTimeoutAsync()` with 30-second linked `CancellationTokenSource`

---

## DI Registration Needed in Program.cs

```csharp
// ProcuLink.Api/Program.cs (and ProcuLink.Worker/Program.cs if used there)
builder.Services.AddScoped<IEmailBodyOrderExtractor, OpenAiEmailBodyOrderExtractor>();
```

Register as **Scoped** (same as `ISchemaInferencer` / `OpenAiSchemaInferencer`), because:
- `ICurrentTenantService` is scoped per-request
- The per-org cap check reads tenant state per request

Wiring is a follow-up — this commit is strictly the service layer.

---

## Config Key

| Key | Default | Notes |
|---|---|---|
| `Ai:OpenAI:ExtractionModel` | falls back to `Ai:OpenAI:MappingModel` then `gpt-5-mini` | Set to `gpt-5-mini` for cost efficiency; `gpt-5-nano` acceptable for cheapest testing |

No new required config keys. The service is a no-op when `Ai:OpenAI:ApiKey` is absent or `Ai:Provider` ≠ `"openai"`.

---

## How `InboundEmailRouter` Should Call This (Follow-up Wiring)

The router currently processes only attachments. When no supported attachment is present but the email body is non-empty, the router should:

```csharp
// In InboundEmailRouter.RouteAsync — after the attachment loop, if no orders were created:
if (createdOrderIds.Count == 0 && !string.IsNullOrWhiteSpace(payload.Body))
{
    var extraction = await _extractor.ExtractAsync(payload.Body, ct);
    if (extraction.Success && extraction.Order is not null)
    {
        // Map ExtractedOrder → ParsedOrder and inject into the existing
        // CreateStubAsync + ParseOrderJob pipeline.
        // The stub should be tagged with source = "email_body_nlp"
        // so the review UI can show appropriate provenance.
        var parsedOrder = MapExtractedToParsed(extraction.Order);
        var stubResult  = await CreateStubFromParsedOrder(orgId, parsedOrder, ct);
        if (stubResult.IsSuccess)
        {
            await _parseJobEnqueuer.EnqueueAsync(stubResult.Value!.Id, orgId, ct);
            createdOrderIds.Add(stubResult.Value!.Id);
        }
    }
}
```

The body field must be added to `InboundEmailPayload` first (currently it only has `Attachments`).

---

## Confidence Threshold Rationale (0.6)

0.6 is the minimum threshold below which the model is essentially guessing. Above 0.6 the model has identified likely PO structure (line items, quantities, a PO number or buyer name). The procurement user still reviews the extracted lines before delivery — the threshold is not about safety, it is about not flooding the review queue with obvious non-PO emails (autoreply, newsletters, conversation threads).

At 0.7+ the result is reliable enough to auto-label as "ready for review". Between 0.6 and 0.7, the UI should show a lower-confidence badge. Below 0.6, the body is treated as unstructured prose and no order is created — the email is logged and the user is notified.

Adjust via config or a future `Ai:EmailExtraction:ConfidenceThreshold` key when real production data is available.

---

## Per-Call Cost Estimate

Using `gpt-5-mini` (default):

| Item | Tokens | Cost (est.) |
|---|---|---|
| System prompt | ~50 tokens | |
| Email body (median PO email) | ~300–800 tokens | |
| Structured output response | ~200–400 tokens | |
| **Total per extraction** | **~600–1,300 tokens** | **$0.0002–0.0004** |

At 500 emails/month (Operations tier): ~$0.10–0.20/month in AI costs.  
The existing per-org monthly token cap (`Ai:OpenAI:MonthlyTokenLimitPerOrg`, default 100,000 tokens) already covers this — each extraction consumes < 1% of the default cap.

---

## Known Limitations

1. **Body field not yet in `InboundEmailPayload`** — the router record currently has only `Attachments`. A follow-up commit must add `Body: string?` before the router can call this service.

2. **No deduplication** — the same email body could be extracted twice if the same message is polled again before the IMAP `\Seen` flag propagates. The existing message-id deduplication in `EmailPollingJob` protects against this for IMAP polling, but a Postmark Inbound webhook has no such guard yet.

3. **Multi-order emails not handled** — the extractor assumes one purchase order per email body. Emails with multiple orders (e.g., "PO-1 for supplier A, PO-2 for supplier B") will produce a single merged extraction. This is a known limitation of the single-call schema.

4. **No attachment fallback** — when an email has both attachments and a body, the attachment takes precedence (that is the intended design). The body extractor is only invoked when no supported attachment is present.

5. **Language** — the system prompt is English-only. International buyer emails in other languages may produce lower confidence scores. A future iteration can add language detection and a localized prompt.

6. **OCR not included** — this service handles plain-text bodies only. HTML bodies should be stripped to plain text by the caller (e.g., stripping tags). Embedded images in HTML bodies are out of scope.
