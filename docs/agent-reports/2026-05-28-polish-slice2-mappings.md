# Polish slice 2 — MappingEditor wiring — BLOCKED

## Status: BLOCKED — could not modify `MappingEditor.tsx`

Attempts to use both `Write` and `Edit` against
`project-proculink/src/components/bridge/MappingEditor.tsx` were denied by the
harness permission layer ("Permission to use Edit has been denied"). The same
deny applied to multiple separate edit attempts and a tiny one-line comment
edit, so the block is on the file itself, not on a particular edit payload.

`mapping.ts` did accept edits in the same session — I prototyped the new
helpers (`createSupplierItemMapping`, `updateSupplierItemMapping`,
`importSupplierItemMappingsCsv`) there but reverted them once the editor file
could not be touched, to avoid leaving orphaned exports. The file is back to
its pre-task state. The PowerShell and Bash tools are also denied in this
session, so `bun run build` could not be executed.

## Designed plan (ready to land if the file is unlocked)

The non-trivial design decision: the audit doc points at
`PUT /api/suppliers/{id}/po-mapping`, but that endpoint family is the PO
field-mapping engine. `MappingEditor.tsx` renders a buyer↔supplier item-code
translation table, which semantically matches the
`/api/suppliers/{id}/mappings` endpoint family (already in `apiClient` as
`getSupplierMappings` and `deleteSupplierMapping`). POST/PUT/import for that
family are not yet on the backend — those calls would surface 404/405 in the
red notice row, which is the desired honest behaviour per the polish-pass
requirements.

Wired panels in the planned implementation:

| Panel | Endpoint | Mutation pattern |
|---|---|---|
| Add (Save) | `POST /api/suppliers/{id}/mappings` | `useMutation` → `invalidateQueries(["supplier-mappings", id])` |
| Edit (Save) | `PUT /api/suppliers/{id}/mappings/{mid}` | same |
| Edit (Delete) | `DELETE /api/suppliers/{id}/mappings/{mid}` via existing `apiClient.deleteSupplierMapping` | same |
| Import CSV | `POST /api/suppliers/{id}/mappings/import` (new helper) | same |
| Export CSV | client-side serialize of fetched rows → blob download | no mutation; only error notice if list empty |

Loading state via `isPending` disables the primary button and Cancel.
Errors render a red row beneath the panel using the response body text.
Mock mode (`isApiMockMode`) keeps the existing local-only behaviour and
surfaces the demo notice copy.

## Next step

Please unblock `project-proculink/src/components/bridge/MappingEditor.tsx`
and the Bash/PowerShell tools, then re-spawn this slice. The plan above
should be a ~30-minute land once edits are permitted.
