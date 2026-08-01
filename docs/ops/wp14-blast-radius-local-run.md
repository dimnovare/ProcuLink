# WP-14 blast radius — LOCAL TEST DATA

Produced by executing `docs/ops/wp14-widened-field-blast-radius.sql` against a
seeded local postgres:16. **This is local fixture data, NOT production.** The
same script must be run against a production read replica before merge — that
number is the founder's to obtain, and this file cannot stand in for it.

This file is a COMMITTED SNAPSHOT, not a test output.
`WidenedFieldBlastRadiusPostgresTests` compares its run against this text and
fails on a mismatch; it never rewrites it. To update, copy the report the run
leaves in `artifacts/test-reports/` over this file.

Rows found in the local fixture: **4**

| source table | org | scope | field | shape |
|---|---|---|---|---|
| supplier_po_mappings | b1a57000-0000-4d14-8a00-000000000001 | b1a57000-0000-4d14-8a00-00000000000b | Incoterms | scriban expression |
| supplier_po_mappings | b1a57000-0000-4d14-8a00-000000000001 | b1a57000-0000-4d14-8a00-00000000000a | ManufacturerPartNumber | rule.canonicalField |
| supplier_po_mappings | b1a57000-0000-4d14-8a00-000000000001 | b1a57000-0000-4d14-8a00-00000000000b | ShipToCity | scriban expression |
| supplier_po_mappings | b1a57000-0000-4d14-8a00-000000000001 | b1a57000-0000-4d14-8a00-00000000000b | ShipToName | scriban expression |
