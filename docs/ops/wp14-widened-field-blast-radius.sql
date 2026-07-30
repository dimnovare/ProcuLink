-- ============================================================================
-- WP-14 BLAST RADIUS — READ-ONLY
--
-- WHAT THIS ANSWERS
--   WP-14 widened the output row bag from 21 keys to 53. For a supplier whose
--   stored output configuration already NAMES one of the 32 newly-exposed
--   fields, the delivered bytes change on deploy:
--     * a rule with CanonicalField = "ManufacturerPartNumber" emitted an EMPTY
--       cell before and emits the part number after;
--     * a Fallback["ShipToName","BuyerName"] manipulator returned "Acme Buyer AS"
--       before (first arg unresolvable) and returns "Acme Warehouse" after — a
--       DIFFERENT value, not a newly-filled blank;
--     * a Scriban expression {{order.ShipToCity}} rendered "" and now renders the
--       city.
--   Pinned revisions do NOT protect: the row bag is code, not part of the
--   revision snapshot, and ReplayService re-renders through the same path.
--
--   The trigger is machine-generated, not only hand-authored:
--   OutputNodeTemplateInferrer auto-binds CanonicalField="ManufacturerPartNumber"
--   for any sample column whose name contains "manufacturerpart" or "mfgpart".
--
-- HOW TO USE IT
--   Run it against a PRODUCTION READ REPLICA (or with a read-only role) BEFORE
--   merging PR #78. Zero rows = the blast radius is empty and the merge is a
--   no-op for delivered bytes. Any rows = those suppliers' documents change, and
--   each one wants a look before deploy.
--
-- SAFETY
--   Every statement is a SELECT. There is no INSERT/UPDATE/DELETE/DDL here and
--   none should ever be added — this file is referenced by
--   WidenedFieldBlastRadiusPostgresTests, which asserts it contains no mutating
--   keyword. If you need to CHANGE something you found, do it deliberately
--   somewhere else.
--
-- WHAT IT DOES NOT COVER (stated so the answer is not mistaken for more than it is)
--   * A supplier's output authored entirely in whole-document Scriban TEMPLATE
--     mode is covered (template_content is scanned) but a template that builds a
--     field name dynamically (string concatenation) cannot be found by a text
--     scan.
--   * `custom_fields` whose KEY collides with a new built-in name are reported
--     separately: those do NOT change, because SetIfNotUserAuthored yields to a
--     user-authored key — they are listed only so the reader can see the guard
--     applied.
-- ============================================================================

-- ── The 32 names WP-14 newly exposed (21 -> 53). ────────────────────────────
-- Keep in sync with ProcuLink.Transform/Output/CanonicalRowFields.cs. The test
-- that executes this file derives the same list by reflection and fails if this
-- literal drifts from it.
WITH new_names(name) AS (
    VALUES
        -- header: buyer identity + references
        ('BuyerOrderRef'), ('BuyerTaxId'),
        -- header: ordering contact
        ('ContactName'), ('ContactEmail'), ('ContactPhone'),
        -- header: shipping terms
        ('Incoterms'), ('ShippingMethod'),
        -- header: ship-to block
        ('ShipToName'), ('ShipToDeliverTo'), ('ShipToStreet'), ('ShipToCity'),
        ('ShipToPostalCode'), ('ShipToCountry'), ('ShipToEmail'), ('ShipToPhone'),
        -- header: bill-to block
        ('BillToName'), ('BillToDeliverTo'), ('BillToStreet'), ('BillToCity'),
        ('BillToPostalCode'), ('BillToCountry'), ('BillToEmail'), ('BillToPhone'),
        -- line: money
        ('TaxAmount'), ('DiscountPercent'), ('NetAmount'),
        -- line: product identity
        ('ManufacturerPartNumber'), ('ManufacturerName'), ('CustomerPartNumber'), ('Unspsc'),
        -- line: routing / contract references
        ('Recipient'), ('ContractNumber')
),

-- ── 1. Live supplier output configs (supplier_po_mappings.config_json) ──────
-- Whole-document text match first (cheap, catches rules, manipulator params,
-- Scriban expressions and OutputNode trees alike), then the name is reported so
-- a human can look at the exact config.
po_mapping_hits AS (
    SELECT
        'supplier_po_mappings'          AS source_table,
        m.id                            AS row_id,
        m.org_id,
        m.supplier_id::text             AS scope_id,
        n.name                          AS referenced_field,
        -- Which SHAPE the name appears in, so the reader can tell a binding from
        -- a coincidental column name.
        CASE
            WHEN m.config_json::text ILIKE '%"canonicalField":"' || n.name || '"%'
              OR m.config_json::text ILIKE '%"canonicalField": "' || n.name || '"%' THEN 'rule.canonicalField'
            WHEN m.config_json::text ILIKE '%{{%' || n.name || '%}}%'               THEN 'scriban expression'
            WHEN m.config_json::text ILIKE '%"params"%' || n.name || '%'            THEN 'manipulator param'
            ELSE 'other (inspect)'
        END                             AS shape
    FROM supplier_po_mappings m
    CROSS JOIN new_names n
    WHERE m.config_json IS NOT NULL
      AND m.config_json::text LIKE '%' || n.name || '%'
),

-- ── 2. Per-ORDER mapping overrides stored on the order (canonical_json) ─────
-- An override lives under the order's canonical json; a per-order rule naming a
-- new field changes THAT order's re-render (preview, re-transform, replay).
order_override_hits AS (
    SELECT
        'purchase_orders'               AS source_table,
        o.id                            AS row_id,
        o.org_id,
        o.po_number                     AS scope_id,
        n.name                          AS referenced_field,
        CASE
            WHEN o.canonical_json::text ILIKE '%"canonicalField":"' || n.name || '"%'
              OR o.canonical_json::text ILIKE '%"canonicalField": "' || n.name || '"%' THEN 'rule.canonicalField'
            WHEN o.canonical_json::text ILIKE '%{{%' || n.name || '%}}%'               THEN 'scriban expression'
            ELSE 'other (inspect)'
        END                             AS shape
    FROM purchase_orders o
    CROSS JOIN new_names n
    WHERE o.canonical_json IS NOT NULL
      AND o.canonical_json::text LIKE '%' || n.name || '%'
),

-- ── 3. PUBLISHED revision snapshots ────────────────────────────────────────
-- A pinned revision does not protect the row bag, so a published revision whose
-- output config names a new field changes too. Same scan, different table.
revision_hits AS (
    SELECT
        'supplier_connection_revisions' AS source_table,
        r.id                            AS row_id,
        r.org_id,
        r.supplier_id::text             AS scope_id,
        n.name                          AS referenced_field,
        'revision output snapshot'      AS shape
    FROM supplier_connection_revisions r
    CROSS JOIN new_names n
    WHERE r.output_mapping_json IS NOT NULL
      AND r.output_mapping_json::text LIKE '%' || n.name || '%'
)

SELECT * FROM po_mapping_hits
UNION ALL
SELECT * FROM order_override_hits
UNION ALL
SELECT * FROM revision_hits
ORDER BY source_table, org_id, referenced_field;
