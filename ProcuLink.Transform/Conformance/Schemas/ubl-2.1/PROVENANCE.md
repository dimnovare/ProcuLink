# Vendored: OASIS UBL 2.1 Order schema and its complete import closure

These 14 files are **third-party, verbatim, and must not be edited.** They are the machine-readable
OASIS UBL 2.1 schema, vendored so `UblSchemaValidator` can validate ProcuLink's emitted UBL orders
against the standard itself instead of against ProcuLink's own reading of it.

## Why this exists

Every other conformance checker in `ProcuLink.Transform/Conformance/` validates our output against a
hand-written summary of a standard — presence, structure and cardinality that *we* decided were the
rules. That is circular: a check we wrote cannot be evidence about a specification we did not.

UBL is the one format ProcuLink emits whose normative schema is free, redistributable and
machine-readable, and which .NET can validate in-box. So it is the one place the circle can be
broken without a licence purchase or a new dependency. See `UblSchemaValidator` for the precise,
deliberately narrow claim a schema pass supports.

## Source

| | |
|---|---|
| Package | Universal Business Language Version 2.1 — **OASIS Standard** |
| Release date | 04 November 2013 |
| Retrieved | 2026-08-13 |
| Base URL | `https://docs.oasis-open.org/ubl/os-UBL-2.1/xsd/` |
| Citation | *Universal Business Language Version 2.1.* 04 November 2013. OASIS Standard. `http://docs.oasis-open.org/ubl/os-UBL-2.1/UBL-2.1.html` |

Each file below was fetched individually from `<base URL>/<path>`. The set is not a hand-picked
selection: it is the transitive `xsd:import` / `xsd:include` closure of `maindoc/UBL-Order-2.1.xsd`,
walked to a fixed point. Across the 14 files there are 22 import/include directives resolving to
exactly these 14 locations, every one of them a relative path — there is no `schemaLocation`
pointing off this list, and none pointing at a URL.

A partial set would not fail at validation time. It fails at *schema load* time, and the resulting
error names a missing type, which reads like a defective document rather than a missing file. That
is why the closure is walked rather than curated, and why `UblSchemaClosureTests` re-walks it.

## Files

Sizes and digests are of the bytes as published. `.gitattributes` marks this directory `-text` so no
platform rewrites the line endings — see the note there; under the OASIS licence that is a
compliance requirement, not a preference.

| Path | Bytes | SHA-256 |
|---|---|---|
| `common/CCTS_CCT_SchemaModule-2.1.xsd` | `45268` | `dd546e4809df86b6445589f69f0d6c9df162840ae386574ddfc1da7638103e15` |
| `common/UBL-CommonAggregateComponents-2.1.xsd` | `2420444` | `939172ad8dd057cd403e7f763f6532184dd5ed4b9de24c42ebb35db4792ba613` |
| `common/UBL-CommonBasicComponents-2.1.xsd` | `219895` | `bd4ad043ee1d9da1c7f8018dabf739cfafdfb59143d0d16b9ef769e6b7c408a7` |
| `common/UBL-CommonExtensionComponents-2.1.xsd` | `9491` | `ad7a4e490978adfbcfc5ec0bb20941cf11ac960ccf0c4de8791a7c731a8dbe87` |
| `common/UBL-CommonSignatureComponents-2.1.xsd` | `5548` | `3db472305f029bba5c1ae157bfd0178f715c3f9b94bd8e6c557dbce5e88da874` |
| `common/UBL-ExtensionContentDataType-2.1.xsd` | `4314` | `fcee77a11870208e6377ea6311b9f2a050bca24bdad8606ea02d71e9f9e72f8d` |
| `common/UBL-QualifiedDataTypes-2.1.xsd` | `3590` | `7dcb156e610239c97ae70940cf4653b88e48c3595bf5f56a2204a32e2893e6cf` |
| `common/UBL-SignatureAggregateComponents-2.1.xsd` | `7784` | `17bb6b62d709b4fd81449a37655af36aa6a1276ad4fdb1b2e249a5ed4b7c2172` |
| `common/UBL-SignatureBasicComponents-2.1.xsd` | `4207` | `cef924d7ba3d1d8ade14469325cde1364f8c174e46f0198ec02da8e9e748a489` |
| `common/UBL-UnqualifiedDataTypes-2.1.xsd` | `27300` | `09052d406b4293e2a5f9c2bfee6df10ad4d8d5f0b36e24a6349d7f7936d89eb6` |
| `common/UBL-XAdESv132-2.1.xsd` | `21664` | `a4f726bcf8cc3f7d9ffa4dab99e005535a8e8b60dced1e5d94578d2e05afa96e` |
| `common/UBL-XAdESv141-2.1.xsd` | `1316` | `1fa4625e9cefcb7a9abb5ac1b64315547450031eece8a55bd584e4ba4b79dbc1` |
| `common/UBL-xmldsig-core-schema-2.1.xsd` | `10750` | `101909c9f06456d61ddcc4fb982f1d40dc357b439f393b1a2eb46e42acd60809` |
| `maindoc/UBL-Order-2.1.xsd` | `53715` | `81c56acdcd9cb34411f14d1de50ebc38d87ee2dd11db426c327da6683175cb38` |

`UblSchemaProvenanceTests` re-reads this table and re-hashes the files, so an edit to a vendored
schema — or a re-vendor that silently changed one — fails the build rather than quietly changing
what "schema-valid" means. The table is the registry; the test derives from it and hard-codes
nothing.

## Licence

Redistribution here is permitted, by three separate grants, and all three are conditioned on the
files being **unmodified and carrying their own notices**. Every notice is inside the file it
governs — which is why nothing in this directory may be reformatted, re-indented, or line-ending
normalised.

**OASIS** (the package as a whole, and every `UBL-*` module) — Copyright © OASIS Open 2001–2013.
The OASIS Standard notice permits copying, publication and distribution in whole or in part
"without restriction of any kind", provided the copyright notice and the notice section are kept on
all copies, and states plainly that the document "may not be modified in any way". The permission
is stated to be perpetual and irrevocable. Full text: the *Notices* section of
`http://docs.oasis-open.org/ubl/os-UBL-2.1/UBL-2.1.html`.

**W3C** — `UBL-xmldsig-core-schema-2.1.xsd` is the W3C XML Signature schema, redistributed by OASIS
under the W3C Software Licence, modified by OASIS only to delete the `PUBLIC`/`SYSTEM` identifiers
from its `DOCTYPE`. Copyright 2001 The Internet Society and W3C. Notice retained in the file
header.

**ETSI** — `UBL-XAdESv132-2.1.xsd` and `UBL-XAdESv141-2.1.xsd` are the ETSI XAdES schemas,
redistributed by OASIS, modified by OASIS only to repoint the XML-DSig import at the sibling copy.
Notices retained in the file headers.

**UN/CEFACT** — `CCTS_CCT_SchemaModule-2.1.xsd` is the UN/CEFACT Core Component Type module,
Copyright © UN/CEFACT (2006), carrying the same copy-and-furnish grant. Notice retained in the
file header.

Nothing here is copyleft and nothing obliges ProcuLink to publish source.

## The one DOCTYPE, and why it is safe

`UBL-xmldsig-core-schema-2.1.xsd` is the only file with a `<!DOCTYPE>`. OASIS republished it with
the external `PUBLIC`/`SYSTEM` identifiers already removed, so what remains is an internal subset —
four declarations, resolvable entirely from the file's own bytes, reaching nothing.

`UblSchemaValidator` therefore loads schemas with `DtdProcessing.Parse` (without it the closure
cannot load at all) and `XmlResolver = null` on the same reader, so an external identifier could
never be fetched even if a future re-vendor reintroduced one — the load would throw instead.
`UblSchemaClosureTests` asserts, over the vendored bytes, that no external identifier is present.

## Re-vendoring

1. Re-walk the closure from `maindoc/UBL-Order-2.1.xsd`; do not curate the file list by hand.
2. Copy files in byte-for-byte. Do not reformat, and do not "fix" line endings.
3. Regenerate the table above from the files actually on disk.
4. Run `UblSchemaProvenanceTests` and `UblSchemaClosureTests`.

If OASIS ever publishes a corrected 2.1 errata package, treat it as a new vendor: replace all 14,
regenerate the digests, and re-run the emitted-document validation test — that test is the negative
control proving our own output still passes, and a schema change is exactly when it could stop.
