# Whole-document Scriban template — canonical field reference

**Status:** shipped (branch `auto/be-scriban-template`)
**Engine:** `ProcuLink.Transform/Output/ScribanTemplateTransformService.cs` +
`ScribanOrderModel.cs` (model namespace) on Scriban 7.2.4.

This is the **source of truth** for the variables a supplier output **template** can reference.
The mapping-editor "proposed structure" panel should render this list. Keep this doc and
`ScribanOrderModel.Build(...)` in sync — the tests in
`ProcuLink.Transform.Tests/Output/ScribanTemplateTransformServiceTests.cs` assert the contract.

---

## What template mode is

A **whole-document template** maps the canonical order to ANY supplier's required structure by
writing ONE Scriban template that produces the entire output document. It is the most flexible of
the three transform modes:

1. **Template mode** (this doc) — `mappingOverride.outputTemplate` is a non-blank Scriban string →
   renders the whole document. Highest precedence.
2. **Field-by-field override** — `mappingOverride.output` (header/line rules) → CSV/JSON only.
3. **Fixed transformer** — the default when no override is present (byte-for-byte unchanged).

It is **opt-in and safe**: no template → existing behaviour is unchanged. A compile/render error
never crashes the transform — it surfaces as a clear validation failure and the order stays
un-transformed (never delivered from a broken template). The same review guard the fixed transforms
enforce (`NeedsReview` / missing `SupplierItemCode`) still blocks the transform.

### Real example (distributor-style nested JSON)

```scriban
{"customerOrderNumber":"{{OrderNr}}",
 "notes":"Order for {{ShippingAddress.Company}}",
 "shipToInfo":{"contact":"{{ShippingAddress.FirstName}} {{ShippingAddress.LastName}}",
               "city":"{{ShippingAddress.City}}"},
 "lines":[{{ for Line in Lines }}{"customerLineNumber":"{{Line.LineNr}}",
   "distributorPartNumber":"{{Line.DistributorPid}}",
   "quantity":{{Line.Qty}},
   "unitPrice":{{Line.OrderedPrice}}}{{ if !for.last }},{{ end }}{{ end }}]}
```

---

## Top-level globals (header scope — available everywhere)

| Variable | Type | Source | Notes |
|---|---|---|---|
| `OrderNr` | string | `PoNumber` | **Alias** of `PoNumber`. |
| `PoNumber` | string | `PoNumber` | Canonical name; same value as `OrderNr`. |
| `OrderDate` | string | `OrderDate` | ISO `yyyy-MM-dd`. |
| `Currency` | string | `Currency` | e.g. `EUR`. |
| `BuyerName` | string | `BuyerName` (falls back to `canonical_json.buyerName`) | `""` if unknown. |
| `SupplierName` | string | resolved `Supplier.Name`, else `SupplierName` | `""` if unknown. |
| `SubTotal` | number \| "" | enrichment | `""` when absent (never `null`). |
| `TaxTotal` | number \| "" | enrichment | `""` when absent. |
| `GrandTotal` | number \| "" | enrichment | `""` when absent. |
| `PaymentTerms` | string | enrichment | `""` when absent. |
| `ShippingAddress` | object | see below | All sub-keys always present. |
| `CustomFields` | object | header custom fields | Keyed by each header custom-field key. |
| `Lines` | list | order lines | Iterate with `{{ for Line in Lines }}…{{ end }}`. |

Header custom fields are **also** exposed at the top level by their key when they don't shadow a
built-in global (e.g. a custom field `PriceList` → `{{ PriceList }}`).

### `ShippingAddress` object

Every sub-key is always present and defaults to `""` (so a relaxed template never renders `null`).
Populated (later wins) from: a nested `shippingAddress` / `shipTo` / `deliveryAddress` object in
`canonical_json`, then a **header custom field** whose key matches the sub-key (`City`) or
`ShipTo`+sub-key (`ShipToCity`).

```
Company  FirstName  LastName  Address1  Address2  City
ProvinceCode  State  PostalCode  CountryCode  Phone  Email
```

Usage: `{{ ShippingAddress.City }}`, `{{ ShippingAddress.Company }}`, …

---

## `Lines` — each item

| Variable | Type | Source | Notes |
|---|---|---|---|
| `LineNr` | number | `LineNumber` | **Alias** of `LineNumber`. |
| `LineNumber` | number | `LineNumber` | Canonical name. |
| `SupplierItemCode` | string | `SupplierItemCode` | Resolved item code. |
| `DistributorPid` | string | `SupplierItemCode` | **Alias** (distributor-style name). |
| `BuyerItemCode` | string | `BuyerItemCode` | The buyer's own code. |
| `Description` | string | `Description` | `""` if absent. |
| `Qty` | number | `Quantity` | **Alias** of `Quantity`. |
| `Quantity` | number | `Quantity` | Canonical name. |
| `Unit` | string | `Unit` | e.g. `EA`. |
| `OrderedPrice` | number | `UnitPrice` | **Alias** of `UnitPrice`. |
| `UnitPrice` | number | `UnitPrice` | Canonical name. |
| `LineTotal` | number | `Quantity × UnitPrice` | Computed. |
| `LineAmount` | number | printed `LineAmount`, else `LineTotal` | Stated extended total when present. |
| `CustomFields` | object | line custom fields | For this line, keyed by custom-field key. |

Line custom fields are also exposed directly on the line by key when they don't shadow a built-in
member (e.g. `{{ Line.Warehouse }}`).

**Numbers vs strings:** `Qty`/`Quantity`, `UnitPrice`/`OrderedPrice`, `LineTotal`, `LineAmount`, and
the header totals are exposed as **real numbers** so a template can emit them unquoted
(`"quantity":{{ Line.Qty }}`) and do arithmetic (`{{ Line.Qty * Line.UnitPrice }}`).

---

## Loop control (Scriban built-in)

Inside `{{ for Line in Lines }} … {{ end }}`:

| Token | Meaning |
|---|---|
| `for.last` | `true` on the final iteration. Use `{{ if !for.last }},{{ end }}` for JSON-array commas. |
| `for.first` | `true` on the first iteration. |
| `for.index` | 0-based index. |
| `for.rindex` | reverse index. |

`{{ if … }} … {{ else }} … {{ end }}` conditionals are also available.

---

## Safe builtin functions (curated)

A WHOLE-DOCUMENT template may use the PURE value-transform helper families — pipe a value through
them (`{{ value | string.upcase }}`) or call directly (`{{ math.round 1.234 1 }}`):

| Family | Examples |
|---|---|
| `string` | `string.upcase`, `string.downcase`, `string.strip`, `string.replace`, `string.truncate`, `string.pad_left` |
| `array` | `array.size`, `array.first`, `array.last`, `array.join`, `array.map`, `array.sort` |
| `math` | `math.round`, `math.abs`, `math.ceil`, `math.floor`, `math.format` |
| `date` | `date.to_string`, `date.parse`, `date.add_days` |
| `object` | `object.default`, `object.keys`, `object.values` |

**Not available (sandbox):** there is **no** file/network/process surface, **no** `regex`/`html`
helpers, and `{{ include }}` / `{{ import }}` fail closed (no template loader is configured). Unknown
members render as empty rather than throwing (relaxed member access), so a typo degrades gracefully.

---

## Output content type

`mappingOverride.outputTemplateContentType` stamps the artifact's MIME type (default
`application/json`). The file extension follows the content type
(`text/csv` → `.csv`, `application/xml` → `.xml`, `text/plain` → `.txt`, …).
