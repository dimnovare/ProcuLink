# T7 — Configurable cXML DOCTYPE/DTD (flexible, per-supplier)

> Status: **DESIGN.** Founder: "we need flexibility with DTD, I don't know which we'll need" → the cXML
> `<!DOCTYPE>` must be **configurable per supplier**, not hardcoded. Default unset = NO DOCTYPE =
> byte-identical to today (existing cXML deliveries unchanged). Grounded on `main` 2026-06-21.

## Where cXML identity lives today (the seam)
`CxmlTransformService.TransformAsync(order, format, ct, CxmlCredentialConfig?)` builds the cXML
`XDocument` with `XDeclaration` only — **no DOCTYPE** (`CxmlTransformService.cs:115`). Two identity
sources compose into that `CxmlCredentialConfig` (`OrderTransformService.MergeCxmlIdentity` `:568`):
1. **Always-present** delivery-config credentials — `_cxmlResolver.ResolveAsync` reads the supplier's
   `SupplierDeliveryConfig.CxmlConfigJson` → `CxmlCredentialConfig` (From/To/Sender + SenderSharedSecret).
   This applies to EVERY cXML supplier (`OrderTransformService.cs:140-141`).
2. **Pinned-revision** envelope — `outputTree?.Envelope.Cxml` (`CxmlEnvelope`), so a published revision
   reproduces under the SAME identity (`:227-229`); merged over (1) in `MergeCxmlIdentity`.

Per-credential null → legacy default (byte-identical) — the existing fallback contract
(`CxmlCredentialConfig.cs:12-16`). The DTD rides this same contract.

## The change
Add an OPTIONAL DTD to BOTH identity records (init-only props — back-compat, positional ctors unchanged):
- `CxmlCredentialConfig` (Core): `string? DtdSystemId`, `string? DtdPublicId`.
- `CxmlEnvelope` (OutputNode.cs): `string? DtdSystemId`, `string? DtdPublicId`.
- `MergeCxmlIdentity` — compose the DTD with the SAME precedence it uses for From/To/Sender (envelope
  value wins when set, else the live delivery-config value).
- `CxmlTransformService` — when the effective `DtdSystemId` is non-blank, add
  `new XDocumentType("cXML", DtdPublicId /*null→SYSTEM form*/, DtdSystemId, null)` to the `XDocument`
  BEFORE the root element. Unset → no DocumentType → **byte-identical**.
- `CxmlConfigJson` (the delivery-config JSON the resolver reads/writes) gains `dtdSystemId`/`dtdPublicId`;
  `CxmlCredentialResolver` populates them; `DeliveryConfigService` round-trips them.

Emitted form:
- SYSTEM only (DtdPublicId null): `<!DOCTYPE cXML SYSTEM "{DtdSystemId}">`
- PUBLIC (both set): `<!DOCTYPE cXML PUBLIC "{DtdPublicId}" "{DtdSystemId}">`

## Byte-safety (the gate)
- DtdSystemId null/blank → **NO** DocumentType node → cXML bytes byte-identical to today. PIN with a
  characterization test (existing cXML byte-parity style). This is the non-negotiable gate.
- **Verify empirically FIRST** (per masterplan guidance): confirm the transform's serialization path
  (`XDocument` → the writer/`ToString` it uses) actually emits the `<!DOCTYPE>` line and where (before
  root, after XDeclaration). Write the characterization test before wiring.
- No migration — `CxmlConfigJson` is existing jsonb; `CxmlEnvelope` rides the override JSON (additive).

## Tests
- Byte-parity: unset DTD → identical cXML to the pre-feature transform.
- DtdSystemId set → `<!DOCTYPE cXML SYSTEM "…">` present, correct, before `<cXML>`; root + Header unchanged.
- Both set → PUBLIC form.
- Merge: envelope DTD overrides the live delivery-config DTD (mirrors From/To/Sender precedence).
- Round-trip: a DTD set in the delivery config reaches the delivered cXML; survives revision pinning.

## FE (T8-scoped to the DTD)
Add to the cXML section of the supplier delivery-config editor (`DeliveryConfigEditor`, where From/To/Sender
cXML credentials are set): a **"cXML DTD (SYSTEM id / URI)"** free-text input + an optional **"Public id"**
input. Free-text so ANY DTD works (the flexibility ask); offer a `<datalist>` of common cXML DTD URIs
(e.g. `http://xml.cxml.org/schemas/cXML/1.2.024/cXML.dtd`, `…/1.2.014/…`, `…/1.2.040/…`) as suggestions,
NOT a closed dropdown. Writes `dtdSystemId`/`dtdPublicId` into the cXML config the editor already saves.
Helper copy: "Leave blank for no DOCTYPE. Set the exact DTD URI your supplier's cXML requires."

## Acceptance
- A supplier with a configured cXML DTD delivers cXML whose `<!DOCTYPE>` matches the configured URI;
  preview == delivery.
- A supplier with no DTD configured delivers byte-identical cXML to today.
- The DTD is free-text (any version), set per supplier in the UI.
