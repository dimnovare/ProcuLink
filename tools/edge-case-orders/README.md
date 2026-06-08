# edge-case-orders

Synthetic adversarial-order generator + live bulk-upload harness for the ProcuLink
ingest pipeline. Zero dependencies, Node ≥ 18.

- `generate.mjs` — emit adversarial CSV / cXML / UBL files → `out/` (+ `out/manifest.json`)
- `run-live.mjs` — upload a folder to a running API, poll + score outcomes → `live-results.json`
- `run-live.md` — the proven live-upload recipe (Clerk token, endpoint, caveats) + real-PO inventory

`out/`, `results/`, and `live-results.json` are gitignored — they hold generated and/or real order data.

Quick start:

```bash
node generate.mjs                                   # build the corpus
node run-live.mjs --base http://localhost:5223 \    # run it live (token from run-live.md)
  --supplier <guid> [--token "<jwt>"] --dir ./out
```

This is a standalone tooling folder — it does **not** add a .NET project and is not part of
`ProcuLink.slnx`.
