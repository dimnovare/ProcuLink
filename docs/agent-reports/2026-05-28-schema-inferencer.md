# Schema inferencer scaffold — 2026-05-28

Build #1 (one-click setup) scaffold per `docs/format-channel-roadmap.md` §5.
Interface + OpenAI implementation + REST endpoints + tests. Uncommitted.

## Architecture decisions

- **Pluggable layer.** `ISchemaInferencer` lives in `ProcuLink.Core/Services/Ai/`
  next to `IAiMappingService` and `IAiUsageTracker`. Same shape as `IAiMappingService`
  (provider-neutral interface, OpenAI is the default implementation).
- **Method signatures match the spec exactly** — `InferSchemaAsync(stream, fileName,
  contentType, ct)` and `ProposeMappingAsync(schema, ct)`. The org id is resolved
  inside the implementation via `ICurrentTenantService` (request-scoped) so the
  controller does not have to thread it through. Test ctor accepts a `Func<Guid>`
  so unit tests bypass the tenant service entirely.
- **Two-step flow.** `/infer` returns the schema; `/propose-mapping` returns the
  mapping. Separating them lets the frontend re-propose without re-uploading and
  matches the roadmap (§5.2). Neither endpoint persists; user confirmation flows
  through the existing `/api/orders/{id}/resolve` path (unchanged).
- **Local pre-extraction.** Inferencer extracts header + up to 5 sample rows
  deterministically (CSV via hand-rolled splitter, JSON via `System.Text.Json`,
  XML via `System.Xml.Linq`, XLSX via raw `ZipArchive` + `XDocument`). OpenAI is
  then asked only to refine types and drop junk fields — cheaper and safer than
  asking the model to parse bytes. No CsvHelper/ClosedXML referenced (Infrastructure
  csproj unchanged per constraint).
- **Fail safe.** AI provider missing → empty result. Per-org cap reached → empty
  result. Cap check itself fails → empty result (we never bypass the cap silently).
  30 s hard timeout on each OpenAI call via linked CTS.

## OpenAI prompts / structured outputs

- Model: `Ai:OpenAI:InferenceModel` then `Ai:OpenAI:MappingModel`, default `gpt-5-mini`.
- **Inference schema** (`inferred_schema`, strict): `{ fields: [{ name, path,
  inferredType (enum: string|number|integer|date|boolean|unknown), exampleValues[] }] }`.
  System prompt: refine types and drop junk, never invent fields, max 200 fields.
- **Proposal schema** (`proposed_mapping`, strict): `{ mappings: [{ sourceField,
  targetCanonicalField, confidence 0..1, reason }] }`. Canonical targets list
  baked into the user payload: `PoNumber`, `OrderDate`, `Currency`, `BuyerName`,
  `lines[].LineNumber`, `lines[].BuyerItemCode`, `lines[].SupplierItemCode`,
  `lines[].Description`, `lines[].Quantity`, `lines[].Unit`, `lines[].UnitPrice`.
  Confidence ≥0.9 = auto, 0.7–0.89 = review, <0.7 = user decides — same bands
  used by the roadmap UI badges.

## Per-call cost estimate

`gpt-5-mini` at $0.25/1M input + $2/1M output (2026-Q2 list). Per typical 20-column
CSV sample:
- Inference: ~1.5 k input + ~500 output tokens → **≈$0.0014**.
- Proposal:  ~1.0 k input + ~500 output tokens → **≈$0.0013**.
- End-to-end first onboard per supplier: **≈$0.003**. Matches roadmap §5/§7 estimate.

## DI registrations needed (Api + Worker `Program.cs`)

```csharp
using ProcuLink.Core.Services.Ai;
using ProcuLink.Infrastructure.Services.Ai;

builder.Services.AddScoped<ISchemaInferencer, OpenAiSchemaInferencer>();
```

Scoped because it reads `ICurrentTenantService` and `IAiUsageTracker`, both scoped.
Worker can skip registration today — no job invokes the inferencer yet.

Optional config keys (no defaults required):
- `Ai:OpenAI:InferenceModel` — falls back to `Ai:OpenAI:MappingModel`, then `gpt-5-mini`.

## Tests

7 new tests in `ProcuLink.Infrastructure.Tests/Services/Ai/OpenAiSchemaInferencerTests.cs`:

1. `InferSchemaAsync_NoApiKey_ReturnsEmptySchema` — no-op when key missing.
2. `InferSchemaAsync_NonOpenAiProvider_ReturnsEmptySchema` — no-op when provider != openai.
3. `InferSchemaAsync_AtOrOverCap_DoesNotCallOpenAiAndReturnsEmpty` — strict-mock tracker proves no Increment.
4. `InferSchemaAsync_TrackerCapCheckFailure_FailsSafeWithEmptySchema` — cap-check throw → block, never bypass.
5. `ProposeMappingAsync_NoApiKey_ReturnsEmptyMapping`.
6. `ProposeMappingAsync_EmptyFields_ReturnsEmptyMappingWithoutCallingTracker` — strict mock catches accidental tracker calls.
7. `InferSchemaAsync_RealTrackerOverLimit_ReturnsEmpty` — real `AiUsageTracker` on in-memory EF.

`dotnet test ProcuLink.slnx` — 153 passing (90 Transform + 63 Infrastructure). Full build clean.

## Known limitations and next steps

- **No live OpenAI integration test.** All tests run no-op or with strict mocks. A
  recorded-cassette VCR test is the next step but out of scope for the scaffold.
- **XLSX extraction is BCL-only.** ZipArchive + sharedStrings handles 99 % of files
  but misses date-typed cells (style index 14+) and multi-sheet selection. Acceptable
  for first-pass schema sniffing; rev when ClosedXML is added to Infrastructure.csproj.
- **No schema fingerprint yet.** Roadmap §7 (network-effect mapping library) deferred
  until customer count crosses ~20. Today every call goes to OpenAI.
- **Mapping not persisted.** The controller returns the proposal; the frontend writes
  to the (planned) `SupplierPoMapping.sourceMapping` JSONB column. Migration TBD.
- **No EDIFACT/UBL extraction.** `fileType="unknown"` for those formats; the AI
  receives only the filename. Land alongside the EDIFACT parser (roadmap #10).
- **Provider neutrality.** `ISchemaInferencer` deliberately has no OpenAI-specific
  type leaks — an `AnthropicSchemaInferencer` slots in unchanged.
