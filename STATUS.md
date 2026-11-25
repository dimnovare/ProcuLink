# ProcuLink — Current Status

_Short and factual. Update it when something ships or is removed. History lives in `git log`._

> **Rewritten 2026-11-25.** The file had grown to ~1,400 lines of dated snapshots, almost all about
> the purchase-order pipeline that plan 13b (#308, 2026-11-13) removed. They are gone from here;
> `git log -- STATUS.md` has them. `CLAUDE.md` holds the durable rules and the capability list.

> **This repository is PUBLIC.** No real names, emails, account / tenant / client ids, live
> identifiers or company legal names here, in code, fixtures or commit messages. Describe them by
> role.

## What it is now

A document tool: **any document in → read → checked → transformed → sent**. Relaunched as the
document tool 2026-11-14 (production: `proculink.eu` + `api.proculink.eu`; Railway API + Worker,
and the Worker is mandatory).

- **One way in.** `POST /api/documents` takes any file with no flow chosen; it is read in the
  workspace inbox flow and sorted into the flow it resembles (#318). A first-of-its-kind document
  asks one question.
- **Flows** hold the repeat setup: intakes, fields, outputs, destinations, lookups, versions
  (publish / restore, #289). A flow never put in use can be deleted (#325).
- **The old order pipeline is a read-only archive** (`OrdersController`: list, detail, artifact
  download). Its tables and migrations stay; there is no drop migration.

## Ways in (`IntakeKind`)

- Browser upload, the file API (`/api/v1/flows/{flowId}/documents`, `plk_` keys), inbound email
  (Postmark webhook), and pickups run by `FlowPickupJob`: IMAP, SFTP, FTP, FTPS, S3, and an
  **Outlook / Microsoft 365 mailbox** read through Microsoft Graph with a connected account (#347,
  2026-11-24). The Outlook list call asks for no headers; headers are fetched only for mail that
  matches the intake's sender / subject filter.
- One file of several documents becomes one document each (#324). Long files are read ten pages
  at a time, up to 100 pages (`ReadLimits`, #316).

## Reading and checks

- PDF, CSV, XLSX always; Word, text, XML, JSON and EDI behind the formats switch (#284); scanned
  PDFs and photos behind the scan switch (#281).
- **Checks never block a send.** On an automatic flow a failed `duplicate` or new-sender check holds
  the document for a person (`SendDocumentJob.ShouldSend`); a sample and an unconfirmed sort are
  never sent by themselves.
- **Sender check** (#341): an emailed document whose From address the receiving side did not
  confirm (`SenderAuthentication`: Postmark's `X-Spam-Tests` DKIM result, or a mailbox provider's
  topmost `Authentication-Results` DMARC pass) is marked unconfirmed and counts as a new sender.
- **Customer-written checks on a value** (plan 31, #346): `FieldCheckOps` one of / between / in a
  lookup / not after today. A failing one marks the value; it never refuses the document.
- AI suggestions are never auto-applied; the AI allowance follows documents read (#310).

## Outputs and destinations

- Formats (`FlowOutputFormats`): CSV, JSON, XML, XLSX, PDF, text, fixed-width. One way to write
  dates and amounts in a flow's files (#339); unique column names in CSV and JSON (#336, #337);
  valid XML tag names from labels (#333), repeated names numbered in the XML files we build (#345);
  the XML table element takes the flow's name for the table, not its
  internal key, so every file of a flow has one shape.
- Standards (`StandardOutputs`): cXML 1.2, UBL 2.1 Order, X12 850, EDIFACT ORDERS (a fresh
  interchange number per sent file, #330); behind the Peppol switch, Peppol BIS Billing 3, EN 16931
  CII and XRechnung, with on-demand rule checks that never stop a send.
- Destinations (`DeliveryProtocolConstants`): email (Postmark), SFTP, FTP, FTPS, S3, web address
  (+ OAuth2), Google Drive, Google Sheets (CSV only). Delivery sweep, ops alert, dead-letter log and
  bounce handling (#307).

## Account, billing, alerts

- Billing: the document ladder `PlanConstants.DocumentLadder` (Starter / Pro / Business / Scale)
  with a 30-day trial (`PilotDuration`); the legacy order ladder is kept for existing payers only.
  The frontend's `src/lib/plans.ts` mirrors it.
- Deletion on request: documents, an ended paid workspace, and its sign-in accounts (#320).
- Phone alerts over Web Push, with a daily summary from the Worker (#327, #328).

## Switches (`DocumentEngineGate`, API and Worker)

`EnabledForAll` / `EnabledOrgIds` turn the engine on; inside it, scans, formats, lookup AI
suggestions, outputs designed from documentation, Peppol outputs, and native connectors (Google
Drive / Sheets). Each is a `…ForAll` flag or a list of org ids.

## Open

- XML: numbering repeated element names in templates inferred from a sample needs a design call
  (an existing test relies on a repeated element).
- Plans and specs are written in the frontend repo's `docs/superpowers/` and are not committed.
