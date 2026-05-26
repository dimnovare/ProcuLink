# ProcuLink Production Hardening Roadmap

> **For agentic workers:** This is the next grouped roadmap after Phase 4 Groups C-H. Use `/superpowers:brainstorm` and `/superpowers:write-plan` before implementing any group. Do not skip straight into broad engine work while visible UI/UX issues remain.

**Goal:** Move ProcuLink from a feature-complete commercial build into a trustworthy working product: polished UI, verified live flows, standards-ready engines, and commercial readiness.

**Product posture:** This is not a throwaway MVP. Treat ProcuLink as a real B2B SaaS for buyer/procurement teams sending purchase orders to suppliers/services in the correct format and delivery channel.

---

## Phase 5 — Production Hardening And Standards

### Group I — UI/UX Production Polish And Responsive QA

**Status:** Next.

**Purpose:** Make the product feel reliable before layering broader engines on top. Visible UI defects weaken trust, especially in the Bridge Layer metaphor.

Tasks:

- [ ] Audit all current Bridge Layer routes on desktop, tablet, and mobile:
  - `/`
  - `/bridge`
  - `/inbox`
  - `/inbox/[orderId]`
  - `/upload`
  - `/library/suppliers`
  - `/library/suppliers/[id]`
  - `/library/mappings`
  - `/library/rules`
  - `/library/templates`
  - `/operations/log`
  - `/operations/connectors`
  - `/operations/webhooks`
  - `/settings`
- [ ] Fix the known Wire Topology issue: traveller/pulse dots must never appear detached from a visible wire path.
- [ ] Verify motion respects `prefers-reduced-motion` and communicates state rather than decoration.
- [ ] Polish app shell, sidebar, topbar, route naming, active states, and mobile navigation.
- [ ] Polish core flows:
  - sign-in/sign-up return path;
  - first upload;
  - inbox queue;
  - canonical review;
  - mapping resolution;
  - supplier delivery config;
  - settings/billing/email;
  - empty, loading, error, and read-only states.
- [ ] Ensure text never overflows controls/cards on common mobile widths.
- [ ] Add or improve keyboard/focus states for operational screens.
- [ ] Run `bun run build`.
- [ ] Use browser QA screenshots for representative desktop and mobile viewports.

Acceptance:

- No detached topology pulse/dot.
- No obvious overlapping text or broken layout on mobile.
- Primary flows have clear empty/error/loading states.
- Bridge Layer remains the locked visual direction; no new generic SaaS theme.

---

### Group J — Live End-To-End QA And Deployment Hardening

**Status:** After Group I.

**Purpose:** Confirm that the deployed system works with real service configuration instead of only local mocks/builds.

Tasks:

- [ ] Audit Vercel frontend env vars:
  - `NEXT_PUBLIC_API_BASE_URL`
  - `NEXT_PUBLIC_USE_MOCK`
  - `NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY`
  - `NEXT_PUBLIC_SENTRY_DSN`
- [ ] Audit Railway backend/worker env vars:
  - database connection;
  - Clerk authority;
  - Stripe keys and price ids;
  - delivery encryption key;
  - OpenAI provider config;
  - R2/local storage;
  - Sentry;
  - frontend URL/CORS.
- [ ] Verify Railway migration path and database schema are current.
- [ ] Verify Clerk login, protected routes, organisation resolution, and sign-out.
- [ ] Verify Stripe Checkout, Portal, and webhooks with Stripe test events.
- [ ] Verify upload -> parse -> normalize -> validate -> mapping resolution -> transform -> artifact download.
- [ ] Verify HTTP delivery config test-fire against a controlled endpoint.
- [ ] Verify Erply and Directo test-fire against sandbox endpoints or documented stubs.
- [ ] Verify IMAP polling against a controlled mailbox/app password.
- [ ] Verify Sentry/logging captures frontend and backend failures without leaking secrets.

Acceptance:

- Deployed Vercel/Railway flow works without local-only assumptions.
- Known live-service gaps are documented in `STATUS.md`.
- Any failed live QA item becomes a specific follow-up issue/group, not vague "needs testing".

---

### Group K — Standards And Engine Hardening

**Status:** After Group I/J scoping. Do not start broad implementation without a standards matrix.

**Purpose:** Build the real integration engine deliberately so ProcuLink can receive common order formats and emit common supplier/service outputs with confidence.

Tasks:

- [ ] Create a standards matrix with support levels:
  - input format;
  - output format;
  - parser/transform owner;
  - validation depth;
  - sample fixtures;
  - plan gate;
  - confidence/status.
- [ ] Version the canonical purchase-order model and document required/optional fields.
- [ ] Add schema/business validation architecture for supplier-specific requirements.
- [ ] Harden cXML order input/output.
- [ ] Add UBL/Peppol BIS Order support plan before implementation.
- [ ] Add supplier CSV/XLSX output template strategy.
- [ ] Add JSON/API payload template strategy.
- [ ] Assess EDI order support scope before implementation:
  - X12 850;
  - EDIFACT ORDERS;
  - parser/library choice;
  - conformance fixtures.
- [ ] Plan OCR/scanned PDF support separately from text-based PDF parsing.
- [ ] Build conformance fixtures and regression tests for every supported standard.

Acceptance:

- Standards work is backed by fixtures and explicit conformance expectations.
- Agents do not claim "any format" unless support level is defined.
- New engines use the existing `Parse -> Normalize -> Validate -> Review -> Transform -> Deliver` loop.

---

### Group L — Trust, Onboarding, And Commercial Readiness

**Status:** After Group I starts; can run in parallel with live QA where files do not overlap.

**Purpose:** Make ProcuLink credible to real B2B buyers and help new users reach first value quickly.

Tasks:

- [ ] Clarify landing-page copy around concrete ROI:
  - fewer manual order edits;
  - fewer supplier rejections;
  - faster order turnaround.
- [ ] Add onboarding path/checklist for first supplier flow:
  - upload sample order;
  - map fields/items;
  - validate;
  - generate output;
  - configure delivery.
- [ ] Add demo/sample data mode that looks realistic but does not imply fake production customers.
- [ ] Add trust/security surfaces:
  - privacy;
  - terms;
  - security/compliance overview;
  - support/contact path.
- [ ] Add product analytics event plan:
  - signup;
  - first upload;
  - first successful transform;
  - first delivery;
  - billing upgrade click;
  - mapping accepted/rejected.
- [ ] Prepare sales/demo assets after the UI polish pass.

Acceptance:

- A new buyer/procurement user understands what to do next after sign-up.
- Marketing language is concrete, not abstract integration jargon.
- Trust pages and support routes exist before broader public launch.

---

## Rules For Future Agents

- `STATUS.md` remains the source of truth.
- Group I is next unless the user explicitly reprioritizes.
- Use the local design system for all UI work.
- No Lovable, no Vite, no React Router, no old Starter pricing.
- Do not start Group K broad standards implementation until the standards matrix is written and approved.
