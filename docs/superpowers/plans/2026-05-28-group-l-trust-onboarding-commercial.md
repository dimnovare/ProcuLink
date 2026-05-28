# Group L — Trust, Onboarding + Commercial Readiness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Do not execute this plan in the same session as it was written — the founder reviews phased plans before execution.**

**Goal:** Land Group L of Phase 5 — make ProcuLink credible to a real B2B procurement buyer by (1) correcting the legal entity, (2) adding DPA + subprocessors + AUP + cookies + status link, (3) instrumenting a real funnel via PostHog, (4) finishing the 4-step onboarding wizard + sample-order path + welcome pages, (5) adding `/help` MDX docs + in-app Help + contact form, and (6) shipping placeholder sales/demo assets (`/customers`, `/one-pager`, `/watch`, "Book a demo" CTA).

**Architecture:** Single phased plan, executed sequentially. Trust/legal lands first (Phase 1-3) so every other page is correct. Analytics lands next (Phase 4) so subsequent onboarding/welcome work is measurable. Onboarding + demo (Phase 5-7) builds on the existing `BridgeOnboardingWizard` + `OnboardingChecklist`. Support (Phase 8-9) and sales assets (Phase 10) close out, followed by dead-code cleanup and full verification. No new engines, no new input/output formats — that belongs to Group K.

**Tech Stack:**
- Frontend: Next.js 15 App Router, TypeScript, Tailwind (where existing), `@clerk/nextjs`, TanStack Query v5, `posthog-js`, `@next/mdx` for help docs, `fuse.js` for client-side help search.
- Backend: ASP.NET Core 8, EF Core 8 + Npgsql, `PostHog` (official .NET SDK), `MailKit` (already present for IMAP — reused for support form).
- Tests: xUnit (existing `ProcuLink.Infrastructure.Tests` + `ProcuLink.Transform.Tests`), Playwright (already installed for `/qa-screenshots`).
- bun only — never npm.

**Constraints (re-stated):**
- All EF queries scoped by `organisationId`.
- `@clerk/nextjs` (not `@clerk/clerk-react`).
- Honest copy only — do not reintroduce the removed 84% / 1m 42s / €4.20 / 99.7% stats.
- Direction 4 — The Bridge Layer is the locked visual direction. Reuse existing design tokens (`#0B1A2F` navy, `#1E66C9` blue, `Bricolage Grotesque` headers, `Inter` body).
- Legal entity is **ProcuLink OÜ**, registration **17477775**, Katusepapi 6, Tallinn, Estonia.

---

## Phase 1 — Legal entity rename (ProcuLink OÜ)

The four marketing legal pages plus the root + marketing footers currently reference "ESTORIA CAPITAL GROUP OÜ". Founder confirmed: rename to **ProcuLink OÜ**, keep registration number 17477775 and Katusepapi 6 Tallinn address. `docs/trust/gdpr.md` already uses "ProcuLink OÜ" but says `(registered in Estonia, TBC)` / `(registration number TBC)` — those placeholders are now resolvable.

### Task 1.1 — Rename legal entity across all surfaces

**Files:**
- Modify: `project-proculink/src/app/(marketing)/privacy/page.tsx` (lines containing entity name)
- Modify: `project-proculink/src/app/(marketing)/terms/page.tsx` (lines containing entity name)
- Modify: `project-proculink/src/app/(marketing)/layout.tsx:61` (footer copyright)
- Modify: `project-proculink/src/app/page.tsx:563` (root footer copyright)
- Modify: `ProcuLink/docs/trust/gdpr.md` (lines 8 and 97)

- [ ] **Step 1: Read each file to confirm current entity strings**

```bash
grep -n "Estoria\|ESTORIA\|registration TBC\|registration number TBC" \
  project-proculink/src/app/page.tsx \
  project-proculink/src/app/(marketing)/layout.tsx \
  project-proculink/src/app/(marketing)/privacy/page.tsx \
  project-proculink/src/app/(marketing)/terms/page.tsx \
  ProcuLink/docs/trust/gdpr.md
```

Expected: 4 Estoria hits in frontend, 2 TBC hits in `gdpr.md`.

- [ ] **Step 2: Edit `(marketing)/layout.tsx` footer**

Replace `© 2026 Estoria Capital Group OÜ` with `© 2026 ProcuLink OÜ`.

- [ ] **Step 3: Edit `src/app/page.tsx` footer**

Same replacement: `© 2026 Estoria Capital Group OÜ` → `© 2026 ProcuLink OÜ`.

- [ ] **Step 4: Edit `privacy/page.tsx`**

Replace these two strings:

`old_string`: `ESTORIA CAPITAL GROUP OÜ (&quot;ProcuLink&quot;, &quot;we&quot;, &quot;us&quot;, &quot;our&quot;) operates the ProcuLink procurement
        automation platform at proculink.com. Company registration: 17477775. Registered
        address: Katusepapi 6, Tallinn, Estonia.`
`new_string`: `ProcuLink OÜ (&quot;ProcuLink&quot;, &quot;we&quot;, &quot;us&quot;, &quot;our&quot;) operates the ProcuLink procurement
        automation platform at proculink.com. Company registration: 17477775. Registered
        address: Katusepapi 6, Tallinn, Estonia.`

`old_string`: `Registered address: ESTORIA CAPITAL GROUP OÜ, Katusepapi 6, Tallinn, Estonia`
`new_string`: `Registered address: ProcuLink OÜ, Katusepapi 6, Tallinn, Estonia`

- [ ] **Step 5: Edit `terms/page.tsx`**

Replace these two strings:

`old_string`: `&quot;ProcuLink&quot; is operated by ESTORIA CAPITAL GROUP OÜ (registration 17477775,
        Katusepapi 6, Tallinn, Estonia).`
`new_string`: `&quot;ProcuLink&quot; is operated by ProcuLink OÜ (registration 17477775,
        Katusepapi 6, Tallinn, Estonia).`

`old_string`: `The ProcuLink software, design, documentation, and brand are owned by ESTORIA
        CAPITAL GROUP OÜ.`
`new_string`: `The ProcuLink software, design, documentation, and brand are owned by ProcuLink OÜ.`

- [ ] **Step 6: Edit `docs/trust/gdpr.md`**

`old_string`: `- **ProcuLink OÜ** (registered in Estonia, TBC) is the **Data Processor**.`
`new_string`: `- **ProcuLink OÜ** (registration 17477775, Tallinn, Estonia) is the **Data Processor**.`

`old_string`: `ProcuLink OÜ (registration number TBC) is registered in Tallinn, Estonia, and reports to the Estonian Data Protection Inspectorate (AKI) as required.`
`new_string`: `ProcuLink OÜ (registration number 17477775) is registered in Tallinn, Estonia, and reports to the Estonian Data Protection Inspectorate (AKI) as required.`

- [ ] **Step 7: Verify no Estoria mentions remain**

```bash
grep -rn "Estoria\|ESTORIA" project-proculink/src ProcuLink/docs
```

Expected: zero matches.

- [ ] **Step 8: Frontend build**

```bash
cd project-proculink && bun run build
```

Expected: success, no new warnings.

- [ ] **Step 9: Commit**

```bash
cd project-proculink
git add src/app/page.tsx "src/app/(marketing)/layout.tsx" "src/app/(marketing)/privacy/page.tsx" "src/app/(marketing)/terms/page.tsx"
git commit -m "fix(legal): rename ESTORIA CAPITAL GROUP OÜ to ProcuLink OÜ across legal pages and footers"

cd ../ProcuLink
git add docs/trust/gdpr.md
git commit -m "docs(trust): replace registration TBC placeholders with ProcuLink OÜ details"
```

---

## Phase 2 — Add `/dpa`, `/subprocessors`, `/aup`, and `/status` footer link

These four surfaces are pre-requisites for any enterprise buyer + EU launch. Each is a full Server Component reusing the existing legal-page style (the `S = { page, h1, h2, p, li, ... }` style object). Status link points to an env var so the founder can wire it to Instatus/BetterStack later.

### Task 2.1 — `/dpa` Data Processing Addendum

**Files:**
- Create: `project-proculink/src/app/(marketing)/dpa/page.tsx`

- [ ] **Step 1: Create `dpa/page.tsx`**

Full file content:

```tsx
import type { Metadata } from "next";
import Link from "next/link";

export const metadata: Metadata = {
  title: "Data Processing Addendum — ProcuLink",
  description: "GDPR Article 28 Data Processing Addendum for ProcuLink customers.",
};

const S = {
  page:    { maxWidth: 760, margin: "0 auto", padding: "56px 32px 80px" },
  h1:      { fontFamily: "'Bricolage Grotesque', Inter, sans-serif", fontSize: "clamp(28px, 4vw, 40px)", fontWeight: 700, letterSpacing: "-0.025em", color: "#0B1A2F", marginBottom: 8 },
  updated: { fontSize: 13, color: "#8A93A5", marginBottom: 40 },
  intro:   { fontSize: 15.5, lineHeight: 1.7, color: "#56627A", marginBottom: 40 },
  h2:      { fontFamily: "'Bricolage Grotesque', Inter, sans-serif", fontSize: 20, fontWeight: 600, color: "#0B1A2F", margin: "40px 0 12px", letterSpacing: "-0.015em" },
  h3:      { fontFamily: "'Bricolage Grotesque', Inter, sans-serif", fontSize: 16, fontWeight: 600, color: "#0B1A2F", margin: "24px 0 8px" },
  p:       { fontSize: 14.5, lineHeight: 1.75, color: "#3D4A5C", marginBottom: 14 },
  li:      { fontSize: 14.5, lineHeight: 1.75, color: "#3D4A5C", marginBottom: 6 },
  callout: { background: "#F6F7FA", border: "1px solid #E2E6EE", borderLeft: "3px solid #1E66C9", borderRadius: 8, padding: "16px 18px", margin: "16px 0 24px", fontSize: 13.5, lineHeight: 1.6, color: "#3D4A5C" },
};

export default function DpaPage() {
  return (
    <div style={S.page}>
      <h1 style={S.h1}>Data Processing Addendum</h1>
      <p style={S.updated}>Effective: May 2026 · Version 1.0</p>

      <p style={S.intro}>
        This Data Processing Addendum (&quot;DPA&quot;) forms part of the agreement between
        ProcuLink OÜ (the &quot;Processor&quot;) and the customer organisation (the &quot;Controller&quot;)
        for the processing of personal data under the EU General Data Protection
        Regulation 2016/679 (&quot;GDPR&quot;).
      </p>

      <div style={S.callout}>
        <strong>For customers who need a counter-signed DPA:</strong> Email{" "}
        <a href="mailto:legal@proculink.com" style={{ color: "#1E66C9" }}>legal@proculink.com</a>{" "}
        and include your organisation legal name and contact for signature. We will return
        a counter-signed PDF within 5 business days.
      </div>

      <h2 style={S.h2}>1. Definitions</h2>
      <p style={S.p}>
        Capitalised terms used but not defined here have the meaning given in the GDPR.
        &quot;Service&quot; means the ProcuLink procurement automation platform as described in
        the <Link href="/terms" style={{ color: "#1E66C9" }}>Terms of Service</Link>.
      </p>

      <h2 style={S.h2}>2. Roles and scope</h2>
      <p style={S.p}>
        The Controller determines the purposes and means of processing personal data
        submitted to the Service. ProcuLink processes personal data on the Controller&apos;s
        documented instructions as set out in this DPA and the Terms of Service.
      </p>

      <h2 style={S.h2}>3. Processor obligations (GDPR Art. 28)</h2>
      <ul style={{ paddingLeft: 20, marginBottom: 14 }}>
        <li style={S.li}>Process personal data only on documented instructions from the Controller.</li>
        <li style={S.li}>Ensure persons authorised to process personal data are under a duty of confidentiality.</li>
        <li style={S.li}>Implement the technical and organisational measures described in <strong>Annex II</strong>.</li>
        <li style={S.li}>Use sub-processors only as listed in <strong>Annex III</strong> and provide 30 days&apos; prior written notice of additions or replacements.</li>
        <li style={S.li}>Assist the Controller in responding to data-subject rights requests under GDPR Chapter III.</li>
        <li style={S.li}>Notify the Controller without undue delay (within 72 hours of awareness) of any personal data breach affecting the Controller&apos;s data.</li>
        <li style={S.li}>On termination, delete or return all Controller personal data within the retention windows in the <Link href="/privacy" style={{ color: "#1E66C9" }}>Privacy Policy</Link>.</li>
        <li style={S.li}>Make available the information necessary to demonstrate compliance with GDPR Art. 28(3).</li>
      </ul>

      <h2 style={S.h2}>4. International transfers</h2>
      <p style={S.p}>
        All Controller personal data is processed in EU-region or EU-compliant infrastructure
        as described in the <Link href="/subprocessors" style={{ color: "#1E66C9" }}>Subprocessors</Link>{" "}
        page. Where any sub-processor processes data outside the EEA, the relevant Standard
        Contractual Clauses (Commission Implementing Decision 2021/914) apply.
      </p>

      <h2 style={S.h2}>5. Audits</h2>
      <p style={S.p}>
        On reasonable written request and no more than once per calendar year, ProcuLink will
        provide the Controller with a summary of its security and compliance controls. Onsite
        audits are not provided as standard; mutual non-disclosure terms apply to any audit
        information shared.
      </p>

      <h2 style={S.h2}>Annex I — Parties and processing details</h2>
      <h3 style={S.h3}>Controller</h3>
      <p style={S.p}>The customer organisation that accepts the Terms of Service.</p>

      <h3 style={S.h3}>Processor</h3>
      <p style={S.p}>
        ProcuLink OÜ · Registration 17477775 · Katusepapi 6, Tallinn, Estonia · Contact:{" "}
        <a href="mailto:legal@proculink.com" style={{ color: "#1E66C9" }}>legal@proculink.com</a>
      </p>

      <h3 style={S.h3}>Categories of data subjects</h3>
      <p style={S.p}>Employees and authorised users of the Controller; suppliers identified in purchase orders submitted by the Controller.</p>

      <h3 style={S.h3}>Categories of personal data</h3>
      <p style={S.p}>
        Account data (name, work email, organisation), purchase-order content (which may include
        contact names and emails for the Controller&apos;s suppliers), authentication tokens,
        and usage data.
      </p>

      <h3 style={S.h3}>Purpose and duration</h3>
      <p style={S.p}>
        Processing is for the provision of the Service and runs for the term of the agreement
        plus the retention windows described in the <Link href="/privacy" style={{ color: "#1E66C9" }}>Privacy Policy</Link>.
      </p>

      <h2 style={S.h2}>Annex II — Technical and organisational measures</h2>
      <ul style={{ paddingLeft: 20, marginBottom: 14 }}>
        <li style={S.li}><strong>Encryption in transit</strong>: TLS 1.2+ for all client and inter-service traffic.</li>
        <li style={S.li}><strong>Encryption at rest</strong>: AES-256-GCM authenticated encryption for delivery credentials and IMAP passwords. Cloudflare R2 server-side encryption for stored order files.</li>
        <li style={S.li}><strong>Access control</strong>: Clerk-issued JWT authentication, organisation-scoped session isolation, every database query bound to the authenticated organisation id.</li>
        <li style={S.li}><strong>Logging and monitoring</strong>: Sentry error monitoring (EU region) without PII leakage; structured backend logging; audit trail for status transitions, delivery attempts, and mapping changes.</li>
        <li style={S.li}><strong>Backups</strong>: Daily automated PostgreSQL backups with point-in-time recovery.</li>
        <li style={S.li}><strong>Personnel</strong>: All personnel with access to production data are under written confidentiality obligations.</li>
        <li style={S.li}><strong>Incident response</strong>: Documented breach-notification process; target 72-hour Controller notification on confirmed personal-data breach.</li>
        <li style={S.li}><strong>Sub-processor management</strong>: 30 days&apos; prior written notice for additions or replacements (see Annex III).</li>
      </ul>

      <h2 style={S.h2}>Annex III — Authorised sub-processors</h2>
      <p style={S.p}>
        The current list of authorised sub-processors is maintained at{" "}
        <Link href="/subprocessors" style={{ color: "#1E66C9" }}>/subprocessors</Link>. The
        Controller may subscribe to change notifications by emailing{" "}
        <a href="mailto:privacy@proculink.com" style={{ color: "#1E66C9" }}>privacy@proculink.com</a>{" "}
        with the subject line &quot;Subprocessor notifications&quot;.
      </p>

      <p style={{ ...S.p, marginTop: 40, paddingTop: 24, borderTop: "1px solid #E2E6EE" }}>
        <Link href="/privacy" style={{ color: "#1E66C9", marginRight: 16 }}>Privacy Policy</Link>
        <Link href="/terms" style={{ color: "#1E66C9", marginRight: 16 }}>Terms of Service</Link>
        <Link href="/subprocessors" style={{ color: "#1E66C9", marginRight: 16 }}>Subprocessors</Link>
        <Link href="/security" style={{ color: "#1E66C9" }}>Security</Link>
      </p>
    </div>
  );
}
```

- [ ] **Step 2: Verify build**

```bash
cd project-proculink && bun run build
```

Expected: success. New route `/dpa` listed in build output.

- [ ] **Step 3: Commit**

```bash
git add "src/app/(marketing)/dpa/page.tsx"
git commit -m "feat(legal): add GDPR Article 28 Data Processing Addendum at /dpa"
```

### Task 2.2 — `/subprocessors` standalone page

**Files:**
- Create: `project-proculink/src/app/(marketing)/subprocessors/page.tsx`

- [ ] **Step 1: Create `subprocessors/page.tsx`**

Full file content:

```tsx
import type { Metadata } from "next";
import Link from "next/link";

export const metadata: Metadata = {
  title: "Subprocessors — ProcuLink",
  description: "Current list of ProcuLink subprocessors and how to subscribe to change notifications.",
};

const S = {
  page:    { maxWidth: 760, margin: "0 auto", padding: "56px 32px 80px" },
  h1:      { fontFamily: "'Bricolage Grotesque', Inter, sans-serif", fontSize: "clamp(28px, 4vw, 40px)", fontWeight: 700, letterSpacing: "-0.025em", color: "#0B1A2F", marginBottom: 8 },
  updated: { fontSize: 13, color: "#8A93A5", marginBottom: 40 },
  intro:   { fontSize: 15.5, lineHeight: 1.7, color: "#56627A", marginBottom: 40 },
  h2:      { fontFamily: "'Bricolage Grotesque', Inter, sans-serif", fontSize: 20, fontWeight: 600, color: "#0B1A2F", margin: "40px 0 12px", letterSpacing: "-0.015em" },
  p:       { fontSize: 14.5, lineHeight: 1.75, color: "#3D4A5C", marginBottom: 14 },
  table:   { width: "100%", borderCollapse: "collapse" as const, fontSize: 13.5, marginBottom: 20 },
  th:      { textAlign: "left" as const, padding: "10px 12px", background: "#F6F7FA", borderBottom: "1px solid #E2E6EE", color: "#0B1A2F", fontWeight: 600 },
  td:      { padding: "10px 12px", borderBottom: "1px solid #F1F3F7", color: "#3D4A5C", verticalAlign: "top" as const },
  callout: { background: "#F6F7FA", border: "1px solid #E2E6EE", borderLeft: "3px solid #2E8E3A", borderRadius: 8, padding: "16px 18px", margin: "24px 0", fontSize: 13.5, lineHeight: 1.6, color: "#3D4A5C" },
};

const SUBPROCESSORS = [
  { name: "Clerk",         purpose: "Authentication and session management", location: "US, EU data residency available", contract: "Clerk DPA" },
  { name: "Stripe",        purpose: "Payment processing and subscription management", location: "US, EU establishment", contract: "Stripe DPA + SCCs" },
  { name: "Railway",       purpose: "API and database hosting", location: "EU (Frankfurt region)", contract: "Railway DPA" },
  { name: "Cloudflare R2", purpose: "Purchase-order file and artifact storage", location: "EU region bucket", contract: "Cloudflare DPA" },
  { name: "Vercel",        purpose: "Frontend hosting and CDN", location: "Global CDN, source data EU", contract: "Vercel DPA + SCCs" },
  { name: "Sentry",        purpose: "Error monitoring and diagnostics", location: "EU region", contract: "Sentry DPA" },
  { name: "OpenAI",        purpose: "AI mapping suggestions (line-level item code suggestions)", location: "US", contract: "OpenAI Enterprise DPA + SCCs" },
  { name: "PostHog",       purpose: "Product analytics (anonymised usage data)", location: "EU (eu.posthog.com)", contract: "PostHog DPA" },
];

export default function SubprocessorsPage() {
  return (
    <div style={S.page}>
      <h1 style={S.h1}>Subprocessors</h1>
      <p style={S.updated}>Effective: May 2026 · Version 1.0</p>

      <p style={S.intro}>
        ProcuLink uses the following subprocessors to deliver the Service. Each subprocessor
        is bound by a written data-processing agreement aligned with the requirements of
        GDPR Article 28.
      </p>

      <h2 style={S.h2}>Current subprocessors</h2>
      <table style={S.table}>
        <thead>
          <tr>
            <th style={S.th}>Subprocessor</th>
            <th style={S.th}>Purpose</th>
            <th style={S.th}>Location</th>
            <th style={S.th}>Contract</th>
          </tr>
        </thead>
        <tbody>
          {SUBPROCESSORS.map((s) => (
            <tr key={s.name}>
              <td style={{ ...S.td, fontWeight: 600, color: "#0B1A2F" }}>{s.name}</td>
              <td style={S.td}>{s.purpose}</td>
              <td style={S.td}>{s.location}</td>
              <td style={S.td}>{s.contract}</td>
            </tr>
          ))}
        </tbody>
      </table>

      <div style={S.callout}>
        <strong>30-day change notification.</strong> Before adding or replacing a subprocessor,
        we will give existing customers at least 30 days&apos; prior written notice. To
        subscribe to subprocessor change notifications, email{" "}
        <a href="mailto:privacy@proculink.com" style={{ color: "#1E66C9" }}>privacy@proculink.com</a>{" "}
        with the subject line &quot;Subprocessor notifications&quot;. We track the subscriber list
        manually and will email all subscribers when this page changes.
      </div>

      <h2 style={S.h2}>How to object</h2>
      <p style={S.p}>
        Customers who object to a new subprocessor have 14 days from the notice to raise the
        objection in writing. Where the objection cannot be resolved, the customer may
        terminate the subscription without further fees for the unused remainder of the term.
      </p>

      <p style={{ ...S.p, marginTop: 40, paddingTop: 24, borderTop: "1px solid #E2E6EE" }}>
        <Link href="/dpa" style={{ color: "#1E66C9", marginRight: 16 }}>Data Processing Addendum</Link>
        <Link href="/privacy" style={{ color: "#1E66C9", marginRight: 16 }}>Privacy Policy</Link>
        <Link href="/security" style={{ color: "#1E66C9" }}>Security</Link>
      </p>
    </div>
  );
}
```

- [ ] **Step 2: Update `/privacy` to link to standalone `/subprocessors`**

In `src/app/(marketing)/privacy/page.tsx`, find the `<h2 style={S.h2}>Subprocessors</h2>` section. Above the existing table, add a sentence:

`old_string`: `      <h2 style={S.h2}>Subprocessors</h2>
      <table style={S.table}>`
`new_string`: `      <h2 style={S.h2}>Subprocessors</h2>
      <p style={S.p}>
        The authoritative list of subprocessors is maintained at{" "}
        <Link href="/subprocessors" style={{ color: "#1E66C9" }}>/subprocessors</Link>{" "}
        with a 30-day change-notification commitment. The current snapshot:
      </p>
      <table style={S.table}>`

- [ ] **Step 3: Verify build**

```bash
cd project-proculink && bun run build
```

Expected: success.

- [ ] **Step 4: Commit**

```bash
git add "src/app/(marketing)/subprocessors/page.tsx" "src/app/(marketing)/privacy/page.tsx"
git commit -m "feat(legal): add standalone /subprocessors page with 30-day change-notification commitment"
```

### Task 2.3 — `/aup` Acceptable Use Policy

**Files:**
- Create: `project-proculink/src/app/(marketing)/aup/page.tsx`
- Modify: `project-proculink/src/app/(marketing)/terms/page.tsx` (link section 3 to /aup)

- [ ] **Step 1: Create `aup/page.tsx`**

Full file content:

```tsx
import type { Metadata } from "next";
import Link from "next/link";

export const metadata: Metadata = {
  title: "Acceptable Use Policy — ProcuLink",
  description: "How ProcuLink may and may not be used.",
};

const S = {
  page:    { maxWidth: 720, margin: "0 auto", padding: "56px 32px 80px" },
  h1:      { fontFamily: "'Bricolage Grotesque', Inter, sans-serif", fontSize: "clamp(28px, 4vw, 40px)", fontWeight: 700, letterSpacing: "-0.025em", color: "#0B1A2F", marginBottom: 8 },
  updated: { fontSize: 13, color: "#8A93A5", marginBottom: 40 },
  intro:   { fontSize: 15.5, lineHeight: 1.7, color: "#56627A", marginBottom: 40 },
  h2:      { fontFamily: "'Bricolage Grotesque', Inter, sans-serif", fontSize: 20, fontWeight: 600, color: "#0B1A2F", margin: "40px 0 12px", letterSpacing: "-0.015em" },
  p:       { fontSize: 14.5, lineHeight: 1.75, color: "#3D4A5C", marginBottom: 14 },
  li:      { fontSize: 14.5, lineHeight: 1.75, color: "#3D4A5C", marginBottom: 6 },
};

export default function AupPage() {
  return (
    <div style={S.page}>
      <h1 style={S.h1}>Acceptable Use Policy</h1>
      <p style={S.updated}>Effective: May 2026 · Version 1.0</p>

      <p style={S.intro}>
        This Acceptable Use Policy supplements the{" "}
        <Link href="/terms" style={{ color: "#1E66C9" }}>Terms of Service</Link> and applies to
        all use of the ProcuLink platform.
      </p>

      <h2 style={S.h2}>Permitted use</h2>
      <p style={S.p}>
        ProcuLink may be used by businesses and organisations for the purpose of automating
        legitimate procurement workflows: receiving, parsing, mapping, validating,
        transforming, and delivering purchase orders to suppliers and service providers.
      </p>

      <h2 style={S.h2}>Prohibited use</h2>
      <p style={S.p}>You must not use ProcuLink to:</p>
      <ul style={{ paddingLeft: 20, marginBottom: 14 }}>
        <li style={S.li}>Process orders for goods or services that are illegal in the supplier&apos;s or buyer&apos;s jurisdiction.</li>
        <li style={S.li}>Upload malware, exploits, or files crafted to attack ProcuLink, suppliers, or other tenants.</li>
        <li style={S.li}>Attempt to bypass authentication, organisation isolation, or rate limits.</li>
        <li style={S.li}>Reverse engineer, decompile, or extract proprietary algorithms or models.</li>
        <li style={S.li}>Send unsolicited bulk messages or use delivery destinations to spam recipients.</li>
        <li style={S.li}>Process the personal data of natural persons unrelated to procurement (for example, marketing email lists).</li>
        <li style={S.li}>Share account credentials, sublicense access, or resell the Service without a written agreement.</li>
        <li style={S.li}>Operate workloads that materially degrade Service availability for other customers.</li>
      </ul>

      <h2 style={S.h2}>Reporting abuse</h2>
      <p style={S.p}>
        To report abuse of the ProcuLink platform, email{" "}
        <a href="mailto:abuse@proculink.com" style={{ color: "#1E66C9" }}>abuse@proculink.com</a>.
        Include the affected organisation, supplier, or delivery destination, and a description
        of the issue. We will respond within 2 business days.
      </p>

      <h2 style={S.h2}>Enforcement</h2>
      <p style={S.p}>
        We may suspend or terminate access for violations of this policy, with or without
        notice depending on severity. Where suspension is preventive (for example, an active
        attack), we will document and notify the responsible account contact promptly.
      </p>

      <p style={{ ...S.p, marginTop: 40, paddingTop: 24, borderTop: "1px solid #E2E6EE" }}>
        <Link href="/terms" style={{ color: "#1E66C9", marginRight: 16 }}>Terms of Service</Link>
        <Link href="/privacy" style={{ color: "#1E66C9", marginRight: 16 }}>Privacy Policy</Link>
        <Link href="/security" style={{ color: "#1E66C9" }}>Security</Link>
      </p>
    </div>
  );
}
```

- [ ] **Step 2: Link from Terms §3 to /aup**

In `terms/page.tsx`, replace section 3 with a short pointer:

`old_string`: `      <h2 style={S.h2}>3. Acceptable use</h2>
      <p style={S.p}>You may use ProcuLink only for lawful business procurement purposes. You must not:</p>
      <ul style={{ paddingLeft: 20, marginBottom: 14 }}>
        <li style={S.li}>Upload malicious files or attempt to compromise the security or integrity of the service</li>
        <li style={S.li}>Use the service to process orders for prohibited or illegal goods or services</li>
        <li style={S.li}>Share account credentials with unauthorised REDACTED-ORDER-DATA</li>
        <li style={S.li}>Attempt to reverse-engineer, decompile, or extract proprietary algorithms or code</li>
        <li style={S.li}>Use the service in a way that could harm other users or impair service availability</li>
      </ul>`
`new_string`: `      <h2 style={S.h2}>3. Acceptable use</h2>
      <p style={S.p}>
        Your use of ProcuLink is subject to the{" "}
        <Link href="/aup" style={{ color: "#1E66C9" }}>Acceptable Use Policy</Link>, which is
        incorporated into these Terms by reference. The Acceptable Use Policy describes the
        permitted and prohibited categories of use, including the prohibition of malicious
        uploads, illegal goods, credential sharing, reverse engineering, and abuse of Service
        availability.
      </p>`

- [ ] **Step 3: Verify build**

```bash
cd project-proculink && bun run build
```

Expected: success.

- [ ] **Step 4: Commit**

```bash
git add "src/app/(marketing)/aup/page.tsx" "src/app/(marketing)/terms/page.tsx"
git commit -m "feat(legal): extract Acceptable Use Policy to /aup and link from Terms §3"
```

### Task 2.4 — `/status` external link in marketing footer

Status pages (Instatus, BetterStack, etc.) are hosted externally. We just need a footer link that reads the URL from an env var. When the env var is empty, hide the link.

**Files:**
- Modify: `project-proculink/src/app/(marketing)/layout.tsx` (footer)
- Modify: `project-proculink/.env.example`
- Modify: `project-proculink/.env` (committed; URL placeholder)

- [ ] **Step 1: Add `NEXT_PUBLIC_STATUS_URL` to `.env.example`**

Append to `project-proculink/.env.example`:

```
# Optional external status page URL (e.g. https://status.proculink.com). When empty, the footer link is hidden.
NEXT_PUBLIC_STATUS_URL=
```

- [ ] **Step 2: Add the same line (empty) to committed `.env`**

Append to `project-proculink/.env`:

```
NEXT_PUBLIC_STATUS_URL=
```

- [ ] **Step 3: Add the link to marketing footer**

In `(marketing)/layout.tsx`, find the link group and add the Status link + DPA + Subprocessors + AUP. Replace the existing link group:

`old_string`: `          <a href="/pricing" style={{ color: "inherit" }}>Pricing</a>
          <a href="/how-it-works" style={{ color: "inherit" }}>How it works</a>
          <a href="/sign-in" style={{ color: "inherit" }}>Sign in</a>
          <span style={{ color: "#D0D5DE" }}>·</span>
          <a href="/privacy" style={{ color: "inherit" }}>Privacy</a>
          <a href="/terms" style={{ color: "inherit" }}>Terms</a>
          <a href="/security" style={{ color: "inherit" }}>Security</a>
          <a href="/support" style={{ color: "inherit" }}>Support</a>`
`new_string`: `          <a href="/pricing" style={{ color: "inherit" }}>Pricing</a>
          <a href="/how-it-works" style={{ color: "inherit" }}>How it works</a>
          <a href="/sign-in" style={{ color: "inherit" }}>Sign in</a>
          <span style={{ color: "#D0D5DE" }}>·</span>
          <a href="/privacy" style={{ color: "inherit" }}>Privacy</a>
          <a href="/terms" style={{ color: "inherit" }}>Terms</a>
          <a href="/aup" style={{ color: "inherit" }}>AUP</a>
          <a href="/dpa" style={{ color: "inherit" }}>DPA</a>
          <a href="/subprocessors" style={{ color: "inherit" }}>Subprocessors</a>
          <a href="/security" style={{ color: "inherit" }}>Security</a>
          <a href="/support" style={{ color: "inherit" }}>Support</a>
          {process.env.NEXT_PUBLIC_STATUS_URL ? (
            <a href={process.env.NEXT_PUBLIC_STATUS_URL} target="_blank" rel="noopener noreferrer" style={{ color: "inherit" }}>Status</a>
          ) : null}`

- [ ] **Step 4: Update root `/page.tsx` footer the same way**

Open `src/app/page.tsx` and locate the marketing-style footer near line 563. Apply the same link additions (Privacy/Terms/AUP/DPA/Subprocessors/Security/Support/Status).

(If the root page footer uses a different markup, mirror the Phase 1 entity-rename pattern and add the same anchor tags — keep the rest of the markup intact.)

- [ ] **Step 5: Verify build**

```bash
cd project-proculink && bun run build
```

Expected: success.

- [ ] **Step 6: Commit**

```bash
git add "src/app/(marketing)/layout.tsx" src/app/page.tsx .env .env.example
git commit -m "feat(legal): add /aup /dpa /subprocessors /status links to marketing footer"
```

---

## Phase 3 — Cookie consent banner

Required so we can legally fire PostHog from EU visitors. Two states: `functional-only` (default, no PostHog) and `analytics-allowed` (PostHog active). State persisted in `localStorage` under a single key. Banner shows on first marketing visit; `useCookieConsent()` hook exposes state to any component.

### Task 3.1 — `useCookieConsent` hook

**Files:**
- Create: `project-proculink/src/lib/cookie-consent.ts`

- [x] **Step 1: Create `cookie-consent.ts`**

Full file content:

```ts
"use client";

import { useEffect, useState } from "react";

const STORAGE_KEY = "proculink_cookie_consent_v1";

export type CookieConsent = "unknown" | "functional-only" | "analytics-allowed";

function readConsent(): CookieConsent {
  if (typeof window === "undefined") return "unknown";
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (raw === "functional-only" || raw === "analytics-allowed") return raw;
    return "unknown";
  } catch {
    return "unknown";
  }
}

export function getCookieConsentSnapshot(): CookieConsent {
  return readConsent();
}

export function setCookieConsent(value: Exclude<CookieConsent, "unknown">) {
  if (typeof window === "undefined") return;
  try {
    window.localStorage.setItem(STORAGE_KEY, value);
    window.dispatchEvent(new CustomEvent("proculink:cookie-consent", { detail: value }));
  } catch {
    // localStorage may be unavailable in private modes — fail silently
  }
}

export function useCookieConsent(): [CookieConsent, (v: Exclude<CookieConsent, "unknown">) => void] {
  const [consent, setConsent] = useState<CookieConsent>("unknown");

  useEffect(() => {
    setConsent(readConsent());
    const onChange = (e: Event) => {
      const detail = (e as CustomEvent<CookieConsent>).detail;
      if (detail === "functional-only" || detail === "analytics-allowed") {
        setConsent(detail);
      }
    };
    window.addEventListener("proculink:cookie-consent", onChange);
    return () => window.removeEventListener("proculink:cookie-consent", onChange);
  }, []);

  return [consent, setCookieConsent];
}
```

- [x] **Step 2: Commit**

```bash
git add src/lib/cookie-consent.ts
git commit -m "feat(privacy): add useCookieConsent hook with localStorage persistence"
```

### Task 3.2 — `CookieConsentBanner` component + mount in root layout

**Files:**
- Create: `project-proculink/src/components/marketing/CookieConsentBanner.tsx`
- Modify: `project-proculink/src/app/layout.tsx` (mount the banner)

- [x] **Step 1: Create `CookieConsentBanner.tsx`**

Full file content:

```tsx
"use client";

import Link from "next/link";
import { useCookieConsent } from "@/lib/cookie-consent";

export function CookieConsentBanner() {
  const [consent, setConsent] = useCookieConsent();

  if (consent !== "unknown") return null;

  return (
    <div
      role="dialog"
      aria-label="Cookie consent"
      style={{
        position: "fixed",
        bottom: 16,
        left: 16,
        right: 16,
        zIndex: 60,
        maxWidth: 720,
        margin: "0 auto",
        background: "#FFFFFF",
        border: "1px solid #E2E6EE",
        borderRadius: 12,
        boxShadow: "0 10px 30px rgba(11,26,47,0.12)",
        padding: "18px 20px",
        display: "flex",
        flexWrap: "wrap",
        gap: 16,
        alignItems: "center",
        justifyContent: "space-between",
      }}
    >
      <p
        style={{
          margin: 0,
          fontSize: 13.5,
          lineHeight: 1.6,
          color: "#3D4A5C",
          flex: "1 1 320px",
        }}
      >
        ProcuLink uses functional cookies to keep you signed in, and optional analytics
        cookies to improve the product. We don&apos;t use advertising or cross-site tracking.{" "}
        <Link href="/privacy" style={{ color: "#1E66C9" }}>See our Privacy Policy</Link>.
      </p>
      <div style={{ display: "flex", gap: 8 }}>
        <button
          type="button"
          onClick={() => setConsent("functional-only")}
          style={{
            background: "#FFFFFF",
            color: "#0B1A2F",
            border: "1px solid #C6CDDA",
            borderRadius: 6,
            padding: "8px 14px",
            fontSize: 13,
            fontWeight: 500,
            cursor: "pointer",
          }}
        >
          Reject
        </button>
        <button
          type="button"
          onClick={() => setConsent("analytics-allowed")}
          style={{
            background: "#0B1A2F",
            color: "#FFFFFF",
            border: "none",
            borderRadius: 6,
            padding: "8px 14px",
            fontSize: 13,
            fontWeight: 600,
            cursor: "pointer",
          }}
        >
          Accept analytics
        </button>
      </div>
    </div>
  );
}
```

- [x] **Step 2: Mount in root layout**

Read `src/app/layout.tsx` to confirm structure. Add an import for `CookieConsentBanner` and render it inside the `<body>` (after `{children}` but before any analytics scripts).

```tsx
// Add to imports at top:
import { CookieConsentBanner } from "@/components/marketing/CookieConsentBanner";

// Inside <body>, after the rendered children:
<CookieConsentBanner />
```

- [x] **Step 3: Verify build and visual**

```bash
cd project-proculink && bun run build
```

Expected: success. Manually load `http://localhost:3000` in an incognito window — banner appears on first load, vanishes after click, stays gone on reload.

- [x] **Step 4: Commit**

```bash
git add src/components/marketing/CookieConsentBanner.tsx src/app/layout.tsx
git commit -m "feat(privacy): add cookie consent banner with functional-only / analytics-allowed states"
```

---

## Phase 4 — PostHog analytics + event taxonomy

PostHog Cloud EU. Frontend SDK respects `useCookieConsent()` (no events fire on `unknown` or `functional-only`). Backend SDK fires unconditionally for transactional/server events that don't depend on a browser session (e.g. `org_created`, `first_delivery_succeeded` from Hangfire). Both SDKs no-op when API keys are missing so dev/test/CI stay clean.

### Task 4.1 — Event taxonomy reference doc

**Files:**
- Create: `ProcuLink/docs/analytics-event-taxonomy.md`

- [ ] **Step 1: Create the doc**

Full file content:

```markdown
# ProcuLink Analytics Event Taxonomy

> Version 1.0 — 2026-05-28. Sent to PostHog Cloud EU (`https://eu.posthog.com`). Frontend events respect the cookie consent banner; backend events fire unconditionally because they are transactional.

## Identifiers

- `distinct_id`: Clerk `user.id`. Captured at sign-in via PostHog `identify()`. Anonymous marketing visitors use a PostHog-generated UUID.
- `$groups.organisation`: ProcuLink `Organisation.Id`. Captured on every authenticated event.
- `$set` on identify: `clerk_email`, `clerk_full_name`, `signup_at`.
- `$set` on org link: `org_name`, `plan`, `status`.

## Sources

- **Frontend** (`posthog-js` v1.x) — user-facing interactions in `(app)` and `(marketing)` routes.
- **Backend** (`PostHog` .NET SDK) — Hangfire jobs, Stripe webhooks, OnboardingService side effects.

## Properties common to every event

| Property         | Source    | Notes |
|------------------|-----------|-------|
| `app_version`    | both      | `process.env.NEXT_PUBLIC_BUILD_VERSION` (frontend) / assembly version (backend) |
| `environment`    | both      | `development` / `production` |
| `plan`           | both      | When known: `pilot` / `growth` / `operations` / `integration` / `enterprise` |
| `organisation_id`| both      | When authenticated |

## Events

### Identity + lifecycle (backend)

| Event                | When                                                                 | Properties                                  |
|----------------------|----------------------------------------------------------------------|---------------------------------------------|
| `signup`             | First Clerk user webhook (`user.created`) for the org                | `via=clerk`, `email_domain`                 |
| `org_created`        | `OrganisationService.CreateAsync` succeeds                           | `plan=pilot`, `created_via=signup_flow`     |
| `billing_upgraded`   | Stripe `checkout.session.completed` webhook for an active org        | `from_plan`, `to_plan`, `stripe_session_id` |
| `billing_downgraded` | Stripe subscription change to a lower plan                           | `from_plan`, `to_plan`                      |
| `billing_cancelled`  | Stripe `customer.subscription.deleted`                               | `previous_plan`, `had_orders_this_month`    |

### Onboarding milestones (mixed)

| Event                       | Source    | When                                                       | Properties                            |
|-----------------------------|-----------|------------------------------------------------------------|---------------------------------------|
| `wizard_opened`             | frontend  | `BridgeOnboardingWizard` mounted                           | `step=1`                              |
| `wizard_step_completed`     | frontend  | Step 1/2/3/4 success handler runs                          | `step`, `step_name`                   |
| `wizard_dismissed`          | frontend  | User clicks "Skip for now"                                 | `at_step`                             |
| `first_supplier_added`      | backend   | First `Supplier` row for org                               | `supplier_id`                         |
| `first_upload_started`      | frontend  | First `POST /api/orders/upload` for org                    | `file_kind=csv\|xlsx\|pdf`            |
| `first_upload_parsed`       | backend   | `ParseOrderJob` success for first org order                | `order_id`, `parser=csv\|xlsx\|pdf`   |
| `first_mapping_resolved`    | backend   | First `PurchaseOrderLine.SupplierItemCode` set manually    | `order_id`, `via=manual\|ai_suggestion`|
| `first_transform_succeeded` | backend   | First `TransformOrderJob` success                          | `order_id`, `output_format`           |
| `first_delivery_succeeded`  | backend   | First `delivered` order status set                         | `order_id`, `protocol`                |

### Sample order (Phase 6)

| Event                    | Source    | When                                                | Properties                  |
|--------------------------|-----------|-----------------------------------------------------|-----------------------------|
| `sample_order_started`   | frontend  | "Try with sample order" clicked                     | `from_route=/upload`        |
| `sample_order_completed` | backend   | Sample run finishes (parse + transform succeed)     | `order_id`, `duration_ms`   |
| `sample_order_failed`    | backend   | Sample run errors                                   | `order_id`, `reason`        |

### Support + help (Phase 8-9)

| Event                   | Source   | When                                            | Properties                       |
|-------------------------|----------|-------------------------------------------------|----------------------------------|
| `help_article_opened`   | frontend | `/help/<slug>` rendered                         | `slug`                           |
| `help_search_performed` | frontend | `/help` search input has non-empty query        | `query_length`, `result_count`   |
| `support_form_submitted`| backend  | `POST /api/support/contact` succeeds            | `category`, `org_plan`           |

### Sales (Phase 10)

| Event                   | Source   | When                                                  | Properties                  |
|-------------------------|----------|-------------------------------------------------------|-----------------------------|
| `book_demo_clicked`     | frontend | "Book a 15-min demo" CTA clicked                      | `from_route`, `plan=pilot`  |
| `watch_demo_started`    | frontend | `/watch` mounted with a Loom URL configured           | `loom_url_hash`             |

## Anti-events (deliberately not tracked)

- PO line content, supplier names, buyer names, or any extracted file content.
- IMAP credentials, Stripe card details, Clerk tokens.
- Page views with query strings that may contain order IDs (PostHog `mask` config).

## SDK init checklist

- Frontend: `posthog.init(key, { api_host: "https://eu.posthog.com", capture_pageview: false, mask_personal_data_properties: true, persistence: "memory" })` until consent is `analytics-allowed`, then upgrade to `localStorage+cookie`.
- Backend: `PostHogClient(apiKey, host: "https://eu.posthog.com")` — singleton in DI. Flush every 30 s + on shutdown.

## When to bump this doc

- Adding a new event or property → bump minor version, append row.
- Removing or renaming an event → bump minor version, mark old row deprecated with date.
- Changing identifier semantics → bump major version and notify analytics-allowed consumers.
```

- [ ] **Step 2: Commit**

```bash
cd ProcuLink
git add docs/analytics-event-taxonomy.md
git commit -m "docs(analytics): add PostHog event taxonomy v1.0"
```

### Task 4.2 — Backend PostHog client wrapper (TDD)

**Files:**
- Modify: `ProcuLink/ProcuLink.Infrastructure/ProcuLink.Infrastructure.csproj` (add PostHog package)
- Create: `ProcuLink/ProcuLink.Core/Services/IAnalyticsService.cs`
- Create: `ProcuLink/ProcuLink.Infrastructure/Services/PostHogAnalyticsService.cs`
- Create: `ProcuLink/ProcuLink.Infrastructure.Tests/Services/PostHogAnalyticsServiceTests.cs`
- Modify: `ProcuLink/ProcuLink.Api/Program.cs` (register `IAnalyticsService`)
- Modify: `ProcuLink/ProcuLink.Api/appsettings.Development.json` (Analytics:PostHog config)
- Modify: `ProcuLink/ProcuLink.Api/appsettings.Production.json` (empty placeholder)

- [ ] **Step 1: Add the PostHog NuGet package**

```bash
cd ProcuLink/ProcuLink.Infrastructure
dotnet add package PostHog --version 5.4.0
```

(If the official `PostHog` package version differs at execution time, pin the latest stable from nuget.org. Confirm it targets net8.0.)

- [ ] **Step 2: Create `IAnalyticsService.cs`**

```csharp
namespace ProcuLink.Core.Services;

public interface IAnalyticsService
{
    /// <summary>
    /// Captures a server-side event. No-op when no PostHog key is configured.
    /// </summary>
    Task CaptureAsync(
        Guid organisationId,
        string? userId,
        string eventName,
        IReadOnlyDictionary<string, object?>? properties = null,
        CancellationToken ct = default);

    /// <summary>
    /// Sets person-level properties on the given distinct id.
    /// </summary>
    Task SetPersonPropertiesAsync(
        string distinctId,
        IReadOnlyDictionary<string, object?> properties,
        CancellationToken ct = default);

    /// <summary>
    /// Flushes the in-memory queue. Called on shutdown.
    /// </summary>
    Task FlushAsync(CancellationToken ct = default);
}
```

- [ ] **Step 3: Write failing test**

`ProcuLink.Infrastructure.Tests/Services/PostHogAnalyticsServiceTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services;

public class PostHogAnalyticsServiceTests
{
    [Fact]
    public async Task CaptureAsync_NoOps_WhenApiKeyMissing()
    {
        var opts = Options.Create(new PostHogOptions { ApiKey = null, Host = "https://eu.posthog.com" });
        var svc  = new PostHogAnalyticsService(opts, NullLogger<PostHogAnalyticsService>.Instance);

        // Should not throw, should not attempt network calls.
        await svc.CaptureAsync(
            organisationId: Guid.NewGuid(),
            userId: "user_123",
            eventName: "test_event",
            properties: new Dictionary<string, object?> { ["foo"] = "bar" });
    }

    [Fact]
    public async Task CaptureAsync_AlwaysIncludesOrganisationGroup_WhenKeyConfigured()
    {
        // Integration-shaped contract test: ensures we tag $groups.organisation so PostHog
        // cohort/funnel filtering works. Verified via the service's in-memory test sink.
        var opts = Options.Create(new PostHogOptions { ApiKey = "phc_test", Host = "https://eu.posthog.com" });
        var svc  = new PostHogAnalyticsService(opts, NullLogger<PostHogAnalyticsService>.Instance);

        var orgId = Guid.NewGuid();
        await svc.CaptureAsync(orgId, "user_abc", "first_supplier_added");

        var queued = svc.PeekTestQueue();
        Assert.Single(queued);
        Assert.Equal("first_supplier_added", queued[0].EventName);
        Assert.Equal("user_abc", queued[0].DistinctId);
        Assert.True(queued[0].Groups.ContainsKey("organisation"));
        Assert.Equal(orgId.ToString(), queued[0].Groups["organisation"]);
    }
}
```

- [ ] **Step 4: Run the test — expect failure**

```bash
cd ProcuLink
dotnet test ProcuLink.Infrastructure.Tests --no-restore --filter PostHogAnalyticsServiceTests
```

Expected: compile errors — `PostHogOptions`, `PostHogAnalyticsService`, `PeekTestQueue` not defined.

- [ ] **Step 5: Implement `PostHogAnalyticsService.cs`**

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PostHog;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

public sealed class PostHogOptions
{
    public string? ApiKey { get; set; }
    public string  Host   { get; set; } = "https://eu.posthog.com";
}

public sealed class PostHogAnalyticsService : IAnalyticsService, IAsyncDisposable
{
    private readonly PostHogOptions                _opts;
    private readonly ILogger<PostHogAnalyticsService> _log;
    private readonly PostHogClient?                _client;
    private readonly ConcurrentQueue<TestEnvelope> _testQueue = new();

    public PostHogAnalyticsService(IOptions<PostHogOptions> opts, ILogger<PostHogAnalyticsService> log)
    {
        _opts = opts.Value;
        _log  = log;

        if (!string.IsNullOrWhiteSpace(_opts.ApiKey))
        {
            _client = new PostHogClient(new PostHogOptions
            {
                ApiKey = _opts.ApiKey!,
                Host   = _opts.Host,
            });
        }
    }

    public Task CaptureAsync(
        Guid organisationId,
        string? userId,
        string eventName,
        IReadOnlyDictionary<string, object?>? properties = null,
        CancellationToken ct = default)
    {
        var distinctId = userId ?? $"org_{organisationId}";
        var groups = new Dictionary<string, string> { ["organisation"] = organisationId.ToString() };
        var props  = properties is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(properties);

        // Always present.
        props.TryAdd("environment", Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "development");

        _testQueue.Enqueue(new TestEnvelope(eventName, distinctId, groups, props));

        if (_client is null) return Task.CompletedTask;

        try
        {
            _client.Capture(new CaptureProperties
            {
                DistinctId = distinctId,
                Event      = eventName,
                Properties = props,
                Groups     = groups,
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PostHog capture failed for event {Event}", eventName);
        }
        return Task.CompletedTask;
    }

    public Task SetPersonPropertiesAsync(string distinctId, IReadOnlyDictionary<string, object?> properties, CancellationToken ct = default)
    {
        if (_client is null) return Task.CompletedTask;
        try
        {
            _client.Identify(new IdentifyProperties
            {
                DistinctId = distinctId,
                PersonProperties = properties,
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PostHog identify failed for {DistinctId}", distinctId);
        }
        return Task.CompletedTask;
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_client is null) return;
        try { await _client.FlushAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "PostHog flush failed"); }
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAsync(default);
        _client?.Dispose();
    }

    public IReadOnlyList<TestEnvelope> PeekTestQueue() => _testQueue.ToArray();

    public sealed record TestEnvelope(
        string EventName,
        string DistinctId,
        IReadOnlyDictionary<string, string> Groups,
        IReadOnlyDictionary<string, object?> Properties);
}
```

Note: the PostHog .NET SDK API differs slightly between minor versions — if `PostHogClient` / `Capture` / `Identify` signatures don't match, use the SDK's documented current API for v5.x and keep the wrapper surface identical to `IAnalyticsService`.

- [ ] **Step 6: Run the tests — expect pass**

```bash
dotnet test ProcuLink.Infrastructure.Tests --no-restore --filter PostHogAnalyticsServiceTests
```

Expected: both tests pass.

- [ ] **Step 7: Register in DI**

In `ProcuLink.Api/Program.cs`, add near other service registrations:

```csharp
builder.Services.Configure<PostHogOptions>(builder.Configuration.GetSection("Analytics:PostHog"));
builder.Services.AddSingleton<IAnalyticsService, PostHogAnalyticsService>();
```

And register a graceful flush on shutdown:

```csharp
var analyticsFlushHost = app.Services.GetRequiredService<IHostApplicationLifetime>();
analyticsFlushHost.ApplicationStopping.Register(() =>
{
    var svc = app.Services.GetRequiredService<IAnalyticsService>();
    try { svc.FlushAsync(default).GetAwaiter().GetResult(); } catch { /* swallow */ }
});
```

- [ ] **Step 8: Add config to `appsettings.Development.json`**

In `ProcuLink.Api/appsettings.Development.json`:

```json
"Analytics": {
  "PostHog": {
    "ApiKey": "",
    "Host": "https://eu.posthog.com"
  }
}
```

And mirror an empty version into `appsettings.Production.json`.

- [ ] **Step 9: Backend verification**

```bash
cd ProcuLink
dotnet build ProcuLink.slnx --no-restore
dotnet test ProcuLink.slnx --no-restore
```

Expected: build success; all existing tests + new tests pass.

- [ ] **Step 10: Commit**

```bash
git add ProcuLink.Core/Services/IAnalyticsService.cs \
        ProcuLink.Infrastructure/Services/PostHogAnalyticsService.cs \
        ProcuLink.Infrastructure/ProcuLink.Infrastructure.csproj \
        ProcuLink.Infrastructure.Tests/Services/PostHogAnalyticsServiceTests.cs \
        ProcuLink.Api/Program.cs \
        ProcuLink.Api/appsettings.Development.json \
        ProcuLink.Api/appsettings.Production.json
git commit -m "feat(analytics): add PostHog backend client wrapper with no-op when key missing"
```

### Task 4.3 — Wire backend events into existing services

**Files:**
- Modify: `ProcuLink/ProcuLink.Infrastructure/Services/OrganisationService.cs` (or wherever org create lives) — emit `org_created`
- Modify: `ProcuLink/ProcuLink.Infrastructure/Services/SupplierService.cs` (first supplier) — emit `first_supplier_added`
- Modify: `ProcuLink/ProcuLink.Worker/Jobs/ParseOrderJob.cs` — emit `first_upload_parsed`
- Modify: `ProcuLink/ProcuLink.Worker/Jobs/TransformOrderJob.cs` — emit `first_transform_succeeded`
- Modify: `ProcuLink/ProcuLink.Infrastructure/Services/DeliveryService.cs` — emit `first_delivery_succeeded`
- Modify: `ProcuLink/ProcuLink.Infrastructure/Services/StripeBillingService.cs` (webhook handlers) — emit `billing_upgraded`, `billing_downgraded`, `billing_cancelled`

- [ ] **Step 1: Locate the actual service files**

```bash
cd ProcuLink
grep -rln "class OrganisationService\|class SupplierService\|class DeliveryService\|class StripeBillingService" ProcuLink.Infrastructure/Services
ls ProcuLink.Worker/Jobs
```

Record the actual class file paths discovered. Substitute them below if they differ from the assumed paths.

- [ ] **Step 2: Inject `IAnalyticsService` into each touched service**

For each of the six services/jobs, add `IAnalyticsService` to the constructor and store as `_analytics`.

- [ ] **Step 3: Emit `org_created` after org persisted**

In the org-create method, after `SaveChangesAsync`:

```csharp
await _analytics.CaptureAsync(
    organisationId: org.Id,
    userId: createdByUserId,
    eventName: "org_created",
    properties: new Dictionary<string, object?>
    {
        ["plan"]        = "pilot",
        ["created_via"] = "signup_flow",
    },
    ct: ct);
```

- [ ] **Step 4: Emit `first_supplier_added` only when count was previously zero**

In `SupplierService.CreateAsync`, before the create:

```csharp
var hadSuppliers = await _db.Suppliers.AnyAsync(s => s.OrgId == organisationId && s.DeletedAt == null, ct);
```

After save, if `!hadSuppliers`:

```csharp
if (!hadSuppliers)
{
    await _analytics.CaptureAsync(
        organisationId: organisationId,
        userId: currentUserId,
        eventName: "first_supplier_added",
        properties: new Dictionary<string, object?> { ["supplier_id"] = supplier.Id });
}
```

- [ ] **Step 5: Emit `first_upload_parsed` in `ParseOrderJob`**

After the parse-success branch, check whether this is the org's first parsed order (`AnyAsync(o => o.OrgId == orgId && o.Status != "parsing" && o.Id != currentOrderId)` is false) and emit accordingly with `order_id` + `parser`.

- [ ] **Step 6: Emit `first_transform_succeeded` in `TransformOrderJob`**

Same pattern: if no prior org order has reached `ready_to_deliver` or `delivered`, emit with `order_id` + `output_format`.

- [ ] **Step 7: Emit `first_delivery_succeeded` in `DeliveryService`**

In the dispatcher-success branch where status flips to `delivered`, check if any prior org order is `delivered`; if not, emit with `order_id` + `protocol`.

- [ ] **Step 8: Emit `billing_upgraded` / `billing_downgraded` / `billing_cancelled` in Stripe webhooks**

In each handler, compute `from_plan` and `to_plan` and emit the matching event.

- [ ] **Step 9: Add unit test for "first" logic**

Add one xUnit test per emitter in `ProcuLink.Infrastructure.Tests` using a fake `IAnalyticsService` recording events to a list (`FakeAnalyticsService` under `ProcuLink.Infrastructure.Tests/TestDoubles/FakeAnalyticsService.cs`).

Example for `first_supplier_added`:

```csharp
[Fact]
public async Task SupplierService_EmitsFirstSupplierAdded_OnlyOnce()
{
    var analytics = new FakeAnalyticsService();
    var svc = new SupplierService(/* deps */, analytics);
    var orgId = Guid.NewGuid();

    await svc.CreateAsync(orgId, new CreateSupplierPayload { Name = "Acme A" }, default);
    await svc.CreateAsync(orgId, new CreateSupplierPayload { Name = "Acme B" }, default);

    Assert.Single(analytics.CapturedEvents, e => e.EventName == "first_supplier_added");
}
```

- [ ] **Step 10: Backend verification**

```bash
dotnet build ProcuLink.slnx --no-restore
dotnet test ProcuLink.slnx --no-restore
```

Expected: all green.

- [ ] **Step 11: Commit**

```bash
git add ProcuLink.Infrastructure/Services/ \
        ProcuLink.Worker/Jobs/ \
        ProcuLink.Infrastructure.Tests/
git commit -m "feat(analytics): emit org_created + first_supplier/upload/transform/delivery + billing events"
```

### Task 4.4 — Frontend PostHog SDK + identify + consent gate

**Files:**
- Modify: `project-proculink/package.json` (add `posthog-js`)
- Create: `project-proculink/src/lib/analytics.ts`
- Modify: `project-proculink/src/app/layout.tsx` (mount `<AnalyticsBoot />`)
- Create: `project-proculink/src/components/analytics/AnalyticsBoot.tsx`
- Modify: `project-proculink/.env.example` + `project-proculink/.env`

- [x] **Step 1: Add `posthog-js`**

```bash
cd project-proculink
bun add posthog-js
```

- [x] **Step 2: Add env vars**

Append to `.env.example` and `.env`:

```
NEXT_PUBLIC_POSTHOG_KEY=
NEXT_PUBLIC_POSTHOG_HOST=https://eu.posthog.com
```

- [x] **Step 3: Create `src/lib/analytics.ts`**

```ts
"use client";

import posthog from "posthog-js";
import { getCookieConsentSnapshot } from "@/lib/cookie-consent";

let initialised = false;

function maybeInit() {
  if (initialised) return;
  const key  = process.env.NEXT_PUBLIC_POSTHOG_KEY;
  const host = process.env.NEXT_PUBLIC_POSTHOG_HOST ?? "https://eu.posthog.com";
  if (!key) return;

  const consent = getCookieConsentSnapshot();
  posthog.init(key, {
    api_host:          host,
    capture_pageview:  false,
    persistence:       consent === "analytics-allowed" ? "localStorage+cookie" : "memory",
    autocapture:       false,
    disable_session_recording: true,
    mask_personal_data_properties: true,
  });

  // If consent isn't given yet, opt out of capturing until it is.
  if (consent !== "analytics-allowed") {
    posthog.opt_out_capturing();
  }
  initialised = true;
}

export function capture(event: string, properties: Record<string, unknown> = {}) {
  maybeInit();
  if (!process.env.NEXT_PUBLIC_POSTHOG_KEY) return;
  posthog.capture(event, {
    environment: process.env.NODE_ENV,
    ...properties,
  });
}

export function identifyUser(userId: string, traits: Record<string, unknown> = {}) {
  maybeInit();
  if (!process.env.NEXT_PUBLIC_POSTHOG_KEY) return;
  posthog.identify(userId, traits);
}

export function setGroup(orgId: string, traits: Record<string, unknown> = {}) {
  maybeInit();
  if (!process.env.NEXT_PUBLIC_POSTHOG_KEY) return;
  posthog.group("organisation", orgId, traits);
}

export function onConsentChanged(value: "functional-only" | "analytics-allowed") {
  if (!process.env.NEXT_PUBLIC_POSTHOG_KEY) return;
  if (!initialised) maybeInit();
  if (value === "analytics-allowed") {
    posthog.set_config({ persistence: "localStorage+cookie" });
    posthog.opt_in_capturing();
  } else {
    posthog.opt_out_capturing();
  }
}
```

- [x] **Step 4: Create `AnalyticsBoot.tsx`**

```tsx
"use client";

import { useEffect } from "react";
import { useUser } from "@clerk/nextjs";
import { useCookieConsent } from "@/lib/cookie-consent";
import { capture, identifyUser, onConsentChanged, setGroup } from "@/lib/analytics";

export function AnalyticsBoot() {
  const { user, isLoaded } = useUser();
  const [consent] = useCookieConsent();

  // React to consent changes.
  useEffect(() => {
    if (consent === "analytics-allowed" || consent === "functional-only") {
      onConsentChanged(consent);
    }
  }, [consent]);

  // Identify + group on sign-in.
  useEffect(() => {
    if (!isLoaded || !user) return;
    identifyUser(user.id, {
      email_domain: (user.primaryEmailAddress?.emailAddress ?? "").split("@")[1] ?? "",
    });
    const orgId = user.publicMetadata?.organisationId as string | undefined;
    if (orgId) setGroup(orgId, {});
  }, [isLoaded, user]);

  // Manual pageview capture so we don't leak query strings.
  useEffect(() => {
    capture("$pageview", { path: typeof window !== "undefined" ? window.location.pathname : "" });
  }, []);

  return null;
}
```

- [x] **Step 5: Mount `AnalyticsBoot` in root layout**

Add to `src/app/layout.tsx` inside `<body>` before `<CookieConsentBanner />`:

```tsx
import { AnalyticsBoot } from "@/components/analytics/AnalyticsBoot";

// inside body:
<AnalyticsBoot />
```

- [x] **Step 6: Smoke check in dev**

```bash
bun run build
```

Expected: success. Run dev (`bun run dev`), open `http://localhost:3000`, accept cookies, verify PostHog network requests appear in DevTools when an API key is set. When key is empty (default), no requests fire.

- [x] **Step 7: Commit**

```bash
git add package.json bun.lockb src/lib/analytics.ts src/components/analytics/AnalyticsBoot.tsx src/app/layout.tsx .env .env.example
git commit -m "feat(analytics): add posthog-js consent-aware boot and identify"
```

### Task 4.5 — Instrument frontend wizard + first-upload events

**Files:**
- Modify: `project-proculink/src/components/bridge/OnboardingWizard.tsx` — emit `wizard_opened`, `wizard_step_completed`, `wizard_dismissed`
- Modify: `project-proculink/src/components/bridge/UploadWorkbench.tsx` (or wherever upload submit lives) — emit `first_upload_started` on first org upload

- [ ] **Step 1: Wire `wizard_opened` and per-step events**

In `BridgeOnboardingWizard`, on first mount call `capture("wizard_opened", { step: 1 })`. On each step success handler, call `capture("wizard_step_completed", { step, step_name })`. On dismiss, `capture("wizard_dismissed", { at_step: currentStep })`.

- [ ] **Step 2: Wire `first_upload_started`**

In `UploadWorkbench` upload submit handler (the one that currently calls `apiClient.uploadOrder`), check `recentOrders.length === 0` (or query `/api/onboarding/status`) and only emit if this is the first upload. Pass `file_kind` (`csv` / `xlsx` / `pdf`).

- [ ] **Step 3: Commit**

```bash
git add src/components/bridge/OnboardingWizard.tsx src/components/bridge/UploadWorkbench.tsx
git commit -m "feat(analytics): emit wizard + first_upload_started frontend events"
```

---

## Phase 5 — Onboarding wizard: 4 full steps

Existing `BridgeOnboardingWizard` covers step 1 (add supplier). Extend with steps 2 (upload first PO), 3 (resolve mappings), 4 (configure delivery). Each step must use real APIs (no mock-only). The wizard reads `useQuery` against `/api/onboarding/status` to determine the entry step.

### Task 5.1 — Extend `OnboardingStatus` contract

**Files:**
- Modify: `ProcuLink/ProcuLink.Api/Controllers/OnboardingController.cs`
- Modify: `project-proculink/src/types/procurement.ts`

- [ ] **Step 1: Add `hasResolvedMapping` to backend response**

In `OnboardingController.GetStatus`, add a fourth flag:

```csharp
var hasResolvedMapping = await _db.PurchaseOrderLines
    .AnyAsync(l => l.PurchaseOrder.OrgId == orgId && l.SupplierItemCode != null, ct);

return Ok(new
{
    hasSupplier,
    hasUpload,
    hasResolvedMapping,
    hasDelivery,
});
```

(Confirm the navigation property name `PurchaseOrder` on `PurchaseOrderLine`; if it differs, use the actual one.)

- [ ] **Step 2: Mirror in TypeScript**

In `src/types/procurement.ts`, locate `OnboardingStatus` and add `hasResolvedMapping: boolean;` (place it between `hasUpload` and `hasDelivery`).

- [ ] **Step 3: Backend tests + build**

```bash
cd ProcuLink
dotnet build ProcuLink.slnx --no-restore
dotnet test ProcuLink.slnx --no-restore
```

Expected: green.

- [ ] **Step 4: Commit**

```bash
git add ProcuLink.Api/Controllers/OnboardingController.cs
git commit -m "feat(onboarding): add hasResolvedMapping flag to /api/onboarding/status"

cd ../project-proculink
git add src/types/procurement.ts
git commit -m "feat(onboarding): mirror hasResolvedMapping in TypeScript types"
```

### Task 5.2 — Extend `BridgeOnboardingWizard` to 4 steps

**Files:**
- Modify: `project-proculink/src/components/bridge/OnboardingWizard.tsx`

The existing file has `Step1AddSupplier`. Add `Step2UploadOrder`, `Step3ResolveMapping`, `Step4ConfigureDelivery` following the same component structure (props with `onSuccess`, design tokens `T`, navy/blue/border palette, similar input style + submit button).

- [ ] **Step 1: Read the existing wizard file end-to-end**

```bash
wc -l "src/components/bridge/OnboardingWizard.tsx"
```

Use the existing `Step1AddSupplier` shape as the template.

- [ ] **Step 2: Add `Step2UploadOrder`**

Above the wizard's main `OnboardingWizard` function, add:

```tsx
interface Step2Props {
  defaultSupplier: Supplier;
  onSuccess: (orderId: string) => void;
}

function Step2UploadOrder({ defaultSupplier, onSuccess }: Step2Props) {
  const [file, setFile] = useState<File | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!file) return;
    setLoading(true);
    setError(null);
    try {
      const order = await apiClient.uploadOrder({
        file,
        defaultSupplierId: defaultSupplier.id,
      });
      capture("wizard_step_completed", { step: 2, step_name: "upload_order" });
      onSuccess(order.id);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Upload failed. Please try again.");
    } finally {
      setLoading(false);
    }
  }

  return (
    <form onSubmit={handleSubmit} style={{ display: "flex", flexDirection: "column", gap: 16 }}>
      <div>
        <h2 style={{ fontSize: 18, fontWeight: 700, color: T.text, margin: "0 0 6px", letterSpacing: "-0.02em", fontFamily: "'Bricolage Grotesque', Inter, sans-serif" }}>
          Upload your first purchase order
        </h2>
        <p style={{ fontSize: 13, color: T.muted, margin: 0, lineHeight: 1.55 }}>
          Upload a CSV, XLSX, or PDF purchase order for <strong>{defaultSupplier.name}</strong>. ProcuLink will parse the lines.
        </p>
      </div>

      <input
        ref={inputRef}
        type="file"
        accept=".csv,.xlsx,application/pdf"
        onChange={(e) => setFile(e.target.files?.[0] ?? null)}
        disabled={loading}
        style={{ fontSize: 13 }}
      />

      {error && <p style={{ fontSize: 12, color: T.red, margin: 0 }}>{error}</p>}

      <button
        type="submit"
        disabled={loading || !file}
        style={{
          height: 40,
          background: loading || !file ? "#C6CDDA" : T.navy,
          color: "#fff",
          border: "none",
          borderRadius: 6,
          fontSize: 13.5,
          fontWeight: 600,
          cursor: loading || !file ? "not-allowed" : "pointer",
        }}
      >
        {loading ? "Uploading…" : "Upload and parse"}
      </button>
    </form>
  );
}
```

Import `capture` from `@/lib/analytics`.

- [ ] **Step 3: Add `Step3ResolveMapping`**

```tsx
interface Step3Props {
  orderId: string;
  onSuccess: () => void;
}

function Step3ResolveMapping({ orderId, onSuccess }: Step3Props) {
  const router = useRouter();

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
      <div>
        <h2 style={{ fontSize: 18, fontWeight: 700, color: T.text, margin: "0 0 6px", letterSpacing: "-0.02em", fontFamily: "'Bricolage Grotesque', Inter, sans-serif" }}>
          Review and resolve
        </h2>
        <p style={{ fontSize: 13, color: T.muted, margin: 0, lineHeight: 1.55 }}>
          We&apos;ll take you to the order review screen. Confirm field mappings and any line items that need supplier codes, then click &quot;Resolve all&quot;.
        </p>
      </div>

      <button
        type="button"
        onClick={() => {
          capture("wizard_step_completed", { step: 3, step_name: "resolve_mapping_started" });
          onSuccess();
          router.push(`/inbox/${orderId}`);
        }}
        style={{
          height: 40,
          background: T.navy,
          color: "#fff",
          border: "none",
          borderRadius: 6,
          fontSize: 13.5,
          fontWeight: 600,
          cursor: "pointer",
        }}
      >
        Open order review
      </button>
    </div>
  );
}
```

- [ ] **Step 4: Add `Step4ConfigureDelivery`**

```tsx
interface Step4Props {
  supplier: Supplier;
  onSuccess: () => void;
}

function Step4ConfigureDelivery({ supplier, onSuccess }: Step4Props) {
  const router = useRouter();

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
      <div>
        <h2 style={{ fontSize: 18, fontWeight: 700, color: T.text, margin: "0 0 6px", letterSpacing: "-0.02em", fontFamily: "'Bricolage Grotesque', Inter, sans-serif" }}>
          Configure delivery
        </h2>
        <p style={{ fontSize: 13, color: T.muted, margin: 0, lineHeight: 1.55 }}>
          Tell ProcuLink how to deliver finished orders to <strong>{supplier.name}</strong>. HTTP webhook is the simplest option for first delivery.
        </p>
      </div>

      <button
        type="button"
        onClick={() => {
          capture("wizard_step_completed", { step: 4, step_name: "delivery_config_opened" });
          onSuccess();
          router.push(`/library/suppliers/${supplier.id}?tab=delivery`);
        }}
        style={{
          height: 40,
          background: T.navy,
          color: "#fff",
          border: "none",
          borderRadius: 6,
          fontSize: 13.5,
          fontWeight: 600,
          cursor: "pointer",
        }}
      >
        Open delivery config
      </button>
    </div>
  );
}
```

- [ ] **Step 5: Wire the four steps in the main `OnboardingWizard` component**

In the main `OnboardingWizard` function, replace the existing single-step body with a state machine driven by `hasSupplier`, `hasUpload`, `hasResolvedMapping`, `hasDelivery` from `useQuery("/api/onboarding/status")`. Use `StepIndicator current={N} total={4}` from the existing component. Track local `firstSupplier` + `firstOrderId` state captured from successful step results.

- [ ] **Step 6: Frontend build**

```bash
bun run build
```

Expected: success.

- [ ] **Step 7: Commit**

```bash
git add src/components/bridge/OnboardingWizard.tsx
git commit -m "feat(onboarding): extend wizard to 4 steps (supplier/upload/resolve/delivery) with analytics"
```

---

## Phase 6 — Sample order: backend endpoint + frontend "Try with sample order" button

Goal: a new user can click one button on `/upload` and see end-to-end parsing + transform happen against a checked-in sample CSV without using their own data. The created order is flagged `is_sample` and excluded from `orders_this_month` quota.

### Task 6.1 — Sample CSV fixture + DB column + EF migration

**Files:**
- Create: `ProcuLink/ProcuLink.Api/Fixtures/sample-order.csv`
- Modify: `ProcuLink/ProcuLink.Infrastructure/Entities/PurchaseOrder.cs` — add `bool IsSample` column
- Modify: `ProcuLink/ProcuLink.Infrastructure/Entities/Supplier.cs` — add `bool IsSample` column
- Create: `ProcuLink/ProcuLink.Infrastructure/Migrations/YYYYMMDDHHmmss_AddIsSampleFlags.cs` (via `dotnet ef migrations add`)
- Modify: `ProcuLink/ProcuLink.Infrastructure/Services/BillingService.cs` (or wherever `orders_this_month` is incremented) — skip increment when `IsSample`

- [ ] **Step 1: Create the sample CSV**

`ProcuLink.Api/Fixtures/sample-order.csv`:

```
po_number,buyer_name,line_no,item_code,description,quantity,unit_price,currency
DEMO-2026-001,Northwind Trading OÜ,1,ACME-WIDGET-A,Widget A 10mm,12,4.50,EUR
DEMO-2026-001,Northwind Trading OÜ,2,ACME-WIDGET-B,Widget B 20mm,6,8.25,EUR
DEMO-2026-001,Northwind Trading OÜ,3,ACME-BRACKET-S,Bracket short,24,1.95,EUR
```

Mark it as embedded content under the `ProcuLink.Api.csproj` so it ships in the published output:

```xml
<ItemGroup>
  <EmbeddedResource Include="Fixtures\sample-order.csv" />
</ItemGroup>
```

- [ ] **Step 2: Add `IsSample` to entities**

In `PurchaseOrder.cs`:

```csharp
public bool IsSample { get; set; }
```

In `Supplier.cs`:

```csharp
public bool IsSample { get; set; }
```

With `HasDefaultValue(false)` for each in `OnModelCreating` for both entities.

- [ ] **Step 3: Create migration**

```bash
cd ProcuLink
dotnet ef migrations add AddIsSampleFlags --project ProcuLink.Infrastructure --startup-project ProcuLink.Api
```

- [ ] **Step 4: Skip quota for sample orders**

In `BillingService` (or `OrdersController` where `orders_this_month` increments), guard:

```csharp
if (!order.IsSample) org.OrdersThisMonth += 1;
```

Audit existing tests that assert quota increments to ensure they don't break.

- [ ] **Step 5: Verify build + tests**

```bash
dotnet build ProcuLink.slnx --no-restore
dotnet test ProcuLink.slnx --no-restore
```

Expected: green.

- [ ] **Step 6: Commit**

```bash
git add ProcuLink.Api/Fixtures/sample-order.csv \
        ProcuLink.Api/ProcuLink.Api.csproj \
        ProcuLink.Infrastructure/Entities/PurchaseOrder.cs \
        ProcuLink.Infrastructure/Entities/Supplier.cs \
        ProcuLink.Infrastructure/ProcuLinkDbContext.cs \
        ProcuLink.Infrastructure/Migrations/ \
        ProcuLink.Infrastructure/Services/BillingService.cs
git commit -m "feat(sample): add sample-order.csv fixture + IsSample flags + quota skip"
```

### Task 6.2 — Sample-order endpoint (TDD)

**Files:**
- Create: `ProcuLink/ProcuLink.Core/Services/ISampleOrderService.cs`
- Create: `ProcuLink/ProcuLink.Infrastructure/Services/SampleOrderService.cs`
- Create: `ProcuLink/ProcuLink.Infrastructure.Tests/Services/SampleOrderServiceTests.cs`
- Create: `ProcuLink/ProcuLink.Api/Controllers/SampleOrderController.cs`

- [ ] **Step 1: Define the service interface**

```csharp
namespace ProcuLink.Core.Services;

public interface ISampleOrderService
{
    Task<Guid> CreateAndEnqueueAsync(Guid organisationId, string? createdByUserId, CancellationToken ct);
}
```

- [ ] **Step 2: Write failing tests**

`SampleOrderServiceTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services;

public class SampleOrderServiceTests
{
    [Fact]
    public async Task CreateAndEnqueueAsync_CreatesSampleSupplier_IfMissing()
    {
        await using var db = TestDb.Create();
        var orgId = Guid.NewGuid();
        var svc   = TestSampleOrderService.Create(db);

        await svc.CreateAndEnqueueAsync(orgId, "user_abc", default);

        var samples = await db.Suppliers.Where(s => s.OrgId == orgId && s.IsSample).ToListAsync();
        Assert.Single(samples);
        Assert.Equal("__sample__", samples[0].Code);
    }

    [Fact]
    public async Task CreateAndEnqueueAsync_ReusesExistingSampleSupplier()
    {
        await using var db = TestDb.Create();
        var orgId = Guid.NewGuid();
        var svc   = TestSampleOrderService.Create(db);

        await svc.CreateAndEnqueueAsync(orgId, "user_abc", default);
        await svc.CreateAndEnqueueAsync(orgId, "user_abc", default);

        Assert.Single(await db.Suppliers.Where(s => s.OrgId == orgId && s.IsSample).ToListAsync());
        Assert.Equal(2, await db.PurchaseOrders.CountAsync(o => o.OrgId == orgId && o.IsSample));
    }

    [Fact]
    public async Task CreateAndEnqueueAsync_DoesNotIncrementOrdersThisMonth()
    {
        await using var db = TestDb.Create();
        var orgId = Guid.NewGuid();
        var org   = new Organisation { Id = orgId, Plan = "growth", OrdersThisMonth = 5 };
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var svc = TestSampleOrderService.Create(db);
        await svc.CreateAndEnqueueAsync(orgId, "user_abc", default);

        var reloaded = await db.Organisations.FirstAsync(o => o.Id == orgId);
        Assert.Equal(5, reloaded.OrdersThisMonth);
    }
}
```

`TestDb` and `TestSampleOrderService` are small helpers — wire them in `ProcuLink.Infrastructure.Tests/Support/` following the existing in-memory DbContext pattern used by other tests in that project.

- [ ] **Step 3: Implement `SampleOrderService.cs`**

```csharp
using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure.Entities;
using ProcuLink.Worker.Jobs;

namespace ProcuLink.Infrastructure.Services;

public sealed class SampleOrderService : ISampleOrderService
{
    private const string SampleCode = "__sample__";
    private readonly ProcuLinkDbContext     _db;
    private readonly IBackgroundJobClient   _jobs;
    private readonly IFileStorageService    _files;
    private readonly IAnalyticsService      _analytics;

    public SampleOrderService(ProcuLinkDbContext db, IBackgroundJobClient jobs, IFileStorageService files, IAnalyticsService analytics)
    {
        _db        = db;
        _jobs      = jobs;
        _files     = files;
        _analytics = analytics;
    }

    public async Task<Guid> CreateAndEnqueueAsync(Guid organisationId, string? createdByUserId, CancellationToken ct)
    {
        // 1. Ensure a `__sample__` supplier exists.
        var supplier = await _db.Suppliers
            .FirstOrDefaultAsync(s => s.OrgId == organisationId && s.Code == SampleCode, ct);
        if (supplier is null)
        {
            supplier = new Supplier
            {
                Id = Guid.NewGuid(),
                OrgId = organisationId,
                Name = "ProcuLink Sample Supplier",
                Code = SampleCode,
                IsSample = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            _db.Suppliers.Add(supplier);
        }

        // 2. Load sample CSV from embedded resource.
        await using var stream = typeof(SampleOrderService).Assembly
            .GetManifestResourceStream("ProcuLink.Api.Fixtures.sample-order.csv")
            ?? throw new InvalidOperationException("Sample fixture not found.");
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        ms.Position = 0;

        // 3. Upload to file storage so the existing ParseOrderJob can consume it.
        var storageKey = $"sample/{organisationId}/{Guid.NewGuid()}.csv";
        await _files.UploadAsync(storageKey, ms, "text/csv", ct);

        // 4. Create stub PurchaseOrder, IsSample=true.
        var order = new PurchaseOrder
        {
            Id = Guid.NewGuid(),
            OrgId = organisationId,
            SupplierId = supplier.Id,
            FileName = "sample-order.csv",
            StorageKey = storageKey,
            Status = "parsing",
            IsSample = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.PurchaseOrders.Add(order);

        await _db.SaveChangesAsync(ct);

        // 5. Enqueue parse (which already enqueues transform on success).
        _jobs.Enqueue<ParseOrderJob>(j => j.RunAsync(order.Id, default));

        // 6. Capture analytics.
        await _analytics.CaptureAsync(
            organisationId: organisationId,
            userId: createdByUserId,
            eventName: "sample_order_started",
            properties: new Dictionary<string, object?> { ["order_id"] = order.Id },
            ct: ct);

        return order.Id;
    }
}
```

Adjust the actual property/method names (`Supplier.Code`, `IFileStorageService.UploadAsync`) to match the codebase. If `Supplier` does not yet have an `IsSample` column, add it in this task's first step and roll it into the Phase 6.1 migration.

- [ ] **Step 4: Run tests — expect pass**

```bash
dotnet test ProcuLink.Infrastructure.Tests --no-restore --filter SampleOrderServiceTests
```

Expected: 3 tests pass.

- [ ] **Step 5: Add controller endpoint**

`ProcuLink.Api/Controllers/SampleOrderController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/onboarding/sample-order")]
public class SampleOrderController : ControllerBase
{
    private readonly ISampleOrderService    _samples;
    private readonly ICurrentTenantService  _tenant;
    private readonly ICurrentUserService    _user;

    public SampleOrderController(ISampleOrderService samples, ICurrentTenantService tenant, ICurrentUserService user)
    {
        _samples = samples;
        _tenant  = tenant;
        _user    = user;
    }

    [HttpPost]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        var orderId = await _samples.CreateAndEnqueueAsync(_tenant.OrganisationId, _user.UserId, ct);
        return Ok(new { orderId, isSample = true });
    }
}
```

Register `ISampleOrderService` → `SampleOrderService` in `Program.cs`.

- [ ] **Step 6: Backend verification**

```bash
dotnet build ProcuLink.slnx --no-restore
dotnet test ProcuLink.slnx --no-restore
```

Expected: green.

- [ ] **Step 7: Commit**

```bash
git add ProcuLink.Core/Services/ISampleOrderService.cs \
        ProcuLink.Infrastructure/Services/SampleOrderService.cs \
        ProcuLink.Infrastructure.Tests/Services/SampleOrderServiceTests.cs \
        ProcuLink.Infrastructure.Tests/Support/ \
        ProcuLink.Api/Controllers/SampleOrderController.cs \
        ProcuLink.Api/Program.cs
git commit -m "feat(sample): POST /api/onboarding/sample-order — embed CSV, idempotent sample supplier, quota-skipped"
```

### Task 6.3 — Frontend "Try with sample order" button on `/upload`

**Files:**
- Modify: `project-proculink/src/lib/api-client.ts` (add `runSampleOrder()`)
- Modify: `project-proculink/src/components/bridge/UploadWorkbench.tsx` (or wherever the upload page is rendered)

- [ ] **Step 1: Extend `api-client.ts`**

Add:

```ts
async runSampleOrder(): Promise<{ orderId: string; isSample: true }> {
  const res = await this._request("POST", "/api/onboarding/sample-order");
  return res as { orderId: string; isSample: true };
}
```

- [ ] **Step 2: Add the button + banner to UploadWorkbench**

Add a "Try with sample order" button rendered near the file-drop area. Use existing button + card styles (no new visual direction). On click:

```tsx
async function handleSample() {
  capture("sample_order_started", { from_route: "/upload" });
  setLoading(true);
  try {
    const { orderId } = await apiClient.runSampleOrder();
    router.push(`/inbox/${orderId}?sample=1`);
  } catch (err) {
    setError(err instanceof Error ? err.message : "Failed to start sample run.");
  } finally {
    setLoading(false);
  }
}
```

In the order review screen (`SpineReview` or its host page), when `searchParams.get("sample") === "1"` or `order.isSample` is true, render an inline banner:

```tsx
<div style={{ background: "#FFF8E1", border: "1px solid #F6D88E", color: "#7A5A0A", padding: "10px 14px", borderRadius: 8, fontSize: 13, marginBottom: 16 }}>
  This is a sample order. It uses an example CSV and doesn&apos;t count toward your monthly quota.
</div>
```

- [ ] **Step 3: Frontend verification**

```bash
bun run build
```

Run dev, click "Try with sample order" with the backend running. Confirm navigation to a parsed sample order and banner visible.

- [ ] **Step 4: Commit**

```bash
git add src/lib/api-client.ts src/components/bridge/UploadWorkbench.tsx src/components/bridge/SpineReview.tsx
git commit -m "feat(sample): Try with sample order button on /upload with non-quota banner on review"
```

---

## Phase 7 — `/welcome` post-signup and post-checkout pages

Two narrow `/welcome` flows. Both are Client Components (need Clerk session + analytics + query params). Both render inside the marketing layout so they feel consistent with the rest of the unauth surfaces.

### Task 7.1 — `/welcome` post-signup landing

**Files:**
- Create: `project-proculink/src/app/(marketing)/welcome/page.tsx`
- Modify: `project-proculink/src/middleware.ts` (allow `/welcome` as a marketing-authenticated bridge — already in `(marketing)`, just confirm)
- Modify: Clerk sign-up redirect target — set to `/welcome` in Clerk dashboard (founder action; document in this task)

- [ ] **Step 1: Create the page**

```tsx
"use client";

import { useEffect } from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useUser } from "@clerk/nextjs";
import { capture } from "@/lib/analytics";

const S = {
  page:   { maxWidth: 680, margin: "0 auto", padding: "72px 32px 80px", textAlign: "center" as const },
  h1:     { fontFamily: "'Bricolage Grotesque', Inter, sans-serif", fontSize: "clamp(30px, 4vw, 44px)", fontWeight: 700, letterSpacing: "-0.025em", color: "#0B1A2F", margin: "0 0 12px" },
  sub:    { fontSize: 16, color: "#56627A", lineHeight: 1.6, margin: "0 0 36px" },
  card:   { background: "#FFFFFF", border: "1px solid #E2E6EE", borderRadius: 12, padding: 28, textAlign: "left" as const, boxShadow: "0 4px 14px rgba(11,26,47,0.05)", marginBottom: 16 },
  step:   { display: "flex", gap: 14, padding: "12px 0", borderBottom: "1px solid #F1F3F7", alignItems: "flex-start" },
  stepNum:{ width: 28, height: 28, borderRadius: "50%", background: "#0B1A2F", color: "#fff", display: "flex", alignItems: "center", justifyContent: "center", fontWeight: 700, fontSize: 13, flexShrink: 0 },
  stepBody: { flex: 1 },
  stepTitle: { fontSize: 14.5, fontWeight: 600, color: "#0B1A2F", margin: 0 },
  stepDesc:  { fontSize: 13, color: "#56627A", margin: "4px 0 0", lineHeight: 1.55 },
  cta:    { display: "inline-block", background: "#0B1A2F", color: "#fff", textDecoration: "none", padding: "12px 22px", borderRadius: 8, fontWeight: 600, fontSize: 14, marginTop: 8 },
  skip:   { display: "block", marginTop: 16, color: "#8A93A5", fontSize: 13 },
};

export default function WelcomePage() {
  const { user, isLoaded } = useUser();
  const searchParams = useSearchParams();
  const upgraded = searchParams.get("upgraded");

  useEffect(() => {
    if (!isLoaded) return;
    capture("welcome_viewed", { upgraded: upgraded ?? "" });
  }, [isLoaded, upgraded]);

  return (
    <div style={S.page}>
      <h1 style={S.h1}>Welcome to ProcuLink{user?.firstName ? `, ${user.firstName}` : ""}.</h1>
      <p style={S.sub}>
        ProcuLink turns the purchase orders you send out into the exact format each supplier needs, and delivers them automatically. Here&apos;s how to get to your first delivered order.
      </p>

      {upgraded && (
        <div style={{ ...S.card, borderLeft: "3px solid #2E8E3A", marginBottom: 16 }}>
          <h2 style={{ fontFamily: "'Bricolage Grotesque', Inter, sans-serif", fontSize: 18, fontWeight: 600, color: "#0B1A2F", margin: "0 0 6px", textAlign: "left" }}>
            You&apos;re on {upgraded.charAt(0).toUpperCase() + upgraded.slice(1)}.
          </h2>
          <p style={{ fontSize: 13.5, color: "#56627A", lineHeight: 1.55, margin: 0, textAlign: "left" }}>
            Your subscription is active. Your billing portal is in <Link href="/settings" style={{ color: "#1E66C9" }}>Settings → Billing</Link>. Receipt was emailed to {user?.primaryEmailAddress?.emailAddress ?? "your inbox"}.
          </p>
        </div>
      )}

      <div style={S.card}>
        {[
          { n: 1, t: "Add your first supplier", d: "Tell us the name of one supplier you currently send orders to." },
          { n: 2, t: "Upload a purchase order", d: "CSV, XLSX, or PDF. We parse the lines for you." },
          { n: 3, t: "Confirm field and item mapping", d: "Resolve anything we couldn't match automatically." },
          { n: 4, t: "Send to your supplier", d: "Configure HTTP webhook delivery, or download the formatted output." },
        ].map((s) => (
          <div key={s.n} style={S.step}>
            <div style={S.stepNum}>{s.n}</div>
            <div style={S.stepBody}>
              <p style={S.stepTitle}>{s.t}</p>
              <p style={S.stepDesc}>{s.d}</p>
            </div>
          </div>
        ))}
      </div>

      <Link href="/bridge" style={S.cta}>Open the dashboard</Link>
      <Link href="/bridge?onboard=skip" style={S.skip}>Skip the wizard for now</Link>
    </div>
  );
}
```

- [ ] **Step 2: Configure Clerk to redirect to `/welcome` after sign-up**

This is a Clerk dashboard action. Document in the plan:

> Founder action — in the Clerk dashboard for the `golden-alpaca-43` instance, set the post-sign-up redirect URL to `/welcome`. Repeat for any production Clerk instance once configured.

If you also want a code-level fallback, add `afterSignUpUrl="/welcome"` to `<SignUp />` in the existing sign-up page.

- [ ] **Step 3: Verify build**

```bash
bun run build
```

Expected: success. Manually verify by opening `http://localhost:3000/welcome` while signed in.

- [ ] **Step 4: Commit**

```bash
git add "src/app/(marketing)/welcome/page.tsx" src/app/sign-up/
git commit -m "feat(onboarding): /welcome post-signup landing with optional upgraded-plan callout"
```

### Task 7.2 — Stripe `success_url` routes to `/welcome?upgraded={plan}`

**Files:**
- Modify: `ProcuLink/ProcuLink.Api/Controllers/BillingController.cs` (Stripe Checkout `success_url`)

- [ ] **Step 1: Update Stripe Checkout `success_url`**

In `BillingController` Checkout creation, set `success_url` to `{frontendUrl}/welcome?upgraded={planKey}&session_id={CHECKOUT_SESSION_ID}` and `cancel_url` to `{frontendUrl}/settings`.

- [ ] **Step 2: Backend verification**

```bash
dotnet build ProcuLink.slnx --no-restore
dotnet test ProcuLink.slnx --no-restore
```

Expected: green.

- [ ] **Step 3: Commit**

```bash
git add ProcuLink.Api/Controllers/BillingController.cs
git commit -m "feat(billing): Stripe Checkout success_url routes to /welcome?upgraded={plan}"
```

---

## Phase 8 — `/help` MDX docs landing

Seven articles under `(marketing)/help/<slug>/page.mdx`. Index page at `/help` lists all articles + provides Fuse.js search. MDX is rendered natively by Next 15 — no extra MDX server.

### Task 8.1 — Set up `@next/mdx` rendering

**Files:**
- Modify: `project-proculink/next.config.ts` (or `.js`/`.mjs`) — enable MDX pages
- Modify: `project-proculink/package.json` — add `@next/mdx`, `@mdx-js/loader`, `@mdx-js/react`

- [ ] **Step 1: Install MDX deps**

```bash
cd project-proculink
bun add @next/mdx @mdx-js/loader @mdx-js/react
```

- [ ] **Step 2: Edit `next.config.ts`**

Open the existing `next.config.ts` (or `.mjs`) and wrap the config:

```ts
import type { NextConfig } from "next";
import createMDX from "@next/mdx";

const withMDX = createMDX({ extension: /\.mdx?$/ });

const nextConfig: NextConfig = {
  pageExtensions: ["ts", "tsx", "mdx"],
  // … existing options
};

export default withMDX(nextConfig);
```

Preserve any existing Sentry wrapper around `nextConfig`.

- [ ] **Step 3: Commit**

```bash
git add next.config.ts package.json bun.lockb
git commit -m "build(help): enable .mdx page extension via @next/mdx"
```

### Task 8.2 — Help index and 7 articles

**Files:**
- Create: `project-proculink/src/app/(marketing)/help/page.tsx`
- Create: `project-proculink/src/app/(marketing)/help/first-upload/page.mdx`
- Create: `project-proculink/src/app/(marketing)/help/mapping-basics/page.mdx`
- Create: `project-proculink/src/app/(marketing)/help/delivery-config/page.mdx`
- Create: `project-proculink/src/app/(marketing)/help/ai-suggestions/page.mdx`
- Create: `project-proculink/src/app/(marketing)/help/billing-faq/page.mdx`
- Create: `project-proculink/src/app/(marketing)/help/email-polling/page.mdx`
- Create: `project-proculink/src/app/(marketing)/help/troubleshooting/page.mdx`
- Create: `project-proculink/src/lib/help-articles.ts` (metadata index)

- [ ] **Step 1: Create `src/lib/help-articles.ts`**

```ts
export interface HelpArticle {
  slug: string;
  title: string;
  blurb: string;
  category: "Getting started" | "Mapping" | "Delivery" | "AI" | "Billing" | "Email" | "Troubleshooting";
}

export const HELP_ARTICLES: HelpArticle[] = [
  { slug: "first-upload",     title: "Your first purchase order upload",   blurb: "Walk through uploading a CSV, XLSX, or PDF and getting it parsed.",   category: "Getting started" },
  { slug: "mapping-basics",   title: "PO field mapping basics",            blurb: "Map your CSV columns to the canonical purchase-order fields ProcuLink expects.", category: "Mapping" },
  { slug: "delivery-config",  title: "Configuring supplier delivery",      blurb: "Set up HTTP webhook delivery with credentials and test-fire.",         category: "Delivery" },
  { slug: "ai-suggestions",   title: "How AI mapping suggestions work",    blurb: "When OpenAI runs, what confidence means, and how to confirm or clear suggestions.", category: "AI" },
  { slug: "billing-faq",      title: "Billing and plans FAQ",              blurb: "Pilot, Growth, Operations, Integration, Enterprise — what's included and what happens at quota.", category: "Billing" },
  { slug: "email-polling",    title: "Email polling (IMAP) setup",         blurb: "Receive POs as email attachments — only on Integration and above.",   category: "Email" },
  { slug: "troubleshooting",  title: "Troubleshooting common parse errors",blurb: "Date format mismatches, missing columns, encoding issues — what to fix.", category: "Troubleshooting" },
];
```

- [ ] **Step 2: Create `/help/page.tsx` index with Fuse.js search**

```bash
bun add fuse.js
```

```tsx
"use client";

import { useMemo, useState } from "react";
import Link from "next/link";
import Fuse from "fuse.js";
import { HELP_ARTICLES, type HelpArticle } from "@/lib/help-articles";
import { capture } from "@/lib/analytics";

const S = {
  page:    { maxWidth: 880, margin: "0 auto", padding: "56px 32px 80px" },
  h1:      { fontFamily: "'Bricolage Grotesque', Inter, sans-serif", fontSize: "clamp(28px, 4vw, 40px)", fontWeight: 700, letterSpacing: "-0.025em", color: "#0B1A2F", marginBottom: 8 },
  sub:     { fontSize: 15.5, color: "#56627A", lineHeight: 1.6, marginBottom: 32 },
  search:  { width: "100%", height: 44, padding: "0 14px", border: "1px solid #E2E6EE", borderRadius: 8, fontSize: 14, marginBottom: 28, background: "#FFFFFF", color: "#0B1A2F" },
  group:   { marginBottom: 28 },
  groupTitle: { fontFamily: "'Bricolage Grotesque', Inter, sans-serif", fontSize: 13, fontWeight: 600, color: "#8A93A5", letterSpacing: "0.04em", textTransform: "uppercase" as const, marginBottom: 10 },
  card:    { display: "block", padding: "14px 16px", background: "#FFFFFF", border: "1px solid #E2E6EE", borderRadius: 8, marginBottom: 8, textDecoration: "none", color: "inherit" },
  cardTitle: { fontSize: 14.5, fontWeight: 600, color: "#0B1A2F", margin: 0 },
  cardBlurb: { fontSize: 13, color: "#56627A", margin: "4px 0 0", lineHeight: 1.5 },
  empty:   { fontSize: 13.5, color: "#8A93A5", padding: 16, textAlign: "center" as const },
};

export default function HelpIndex() {
  const [q, setQ] = useState("");

  const fuse = useMemo(() => new Fuse(HELP_ARTICLES, {
    keys: ["title", "blurb", "category"],
    threshold: 0.4,
  }), []);

  const grouped = useMemo(() => {
    const list: HelpArticle[] = q.trim() ? fuse.search(q).map(r => r.item) : HELP_ARTICLES;
    return list.reduce<Record<string, HelpArticle[]>>((acc, a) => {
      (acc[a.category] ??= []).push(a);
      return acc;
    }, {});
  }, [q, fuse]);

  return (
    <div style={S.page}>
      <h1 style={S.h1}>Help</h1>
      <p style={S.sub}>Short, focused articles for the most common ProcuLink tasks.</p>

      <input
        type="search"
        value={q}
        onChange={(e) => {
          setQ(e.target.value);
          if (e.target.value.length >= 2) {
            capture("help_search_performed", { query_length: e.target.value.length, result_count: fuse.search(e.target.value).length });
          }
        }}
        placeholder="Search articles"
        style={S.search}
      />

      {Object.keys(grouped).length === 0 && <p style={S.empty}>No articles match &ldquo;{q}&rdquo;.</p>}

      {Object.entries(grouped).map(([cat, arts]) => (
        <div key={cat} style={S.group}>
          <h2 style={S.groupTitle}>{cat}</h2>
          {arts.map((a) => (
            <Link key={a.slug} href={`/help/${a.slug}`} style={S.card} onClick={() => capture("help_article_opened", { slug: a.slug })}>
              <p style={S.cardTitle}>{a.title}</p>
              <p style={S.cardBlurb}>{a.blurb}</p>
            </Link>
          ))}
        </div>
      ))}
    </div>
  );
}
```

- [ ] **Step 3: Create the 7 MDX articles**

For each article, the file is `src/app/(marketing)/help/<slug>/page.mdx` with this shape (substitute the body for each topic):

```mdx
export const metadata = {
  title: "Your first purchase order upload — ProcuLink Help",
  description: "Walk through uploading a CSV, XLSX, or PDF and getting it parsed.",
};

# Your first purchase order upload

ProcuLink accepts CSV, XLSX, and text-based PDF purchase orders. Image-only scanned PDFs are not yet supported.

## Step 1 — Open the upload screen

Sign in and go to **Upload** in the left sidebar.

## Step 2 — Choose a file

Drag a file into the drop zone, or click **Browse**. Supported types: `.csv`, `.xlsx`, `.pdf`.

## Step 3 — Pick the supplier

Select the supplier this order is for. If you haven&apos;t added the supplier yet, finish the onboarding wizard first.

## Step 4 — Watch the parse

ProcuLink parses the file in the background and routes you to the **Review** screen when it&apos;s ready. Parse times are usually under 10 seconds for CSV/XLSX and under 30 seconds for PDF.

## Step 5 — Resolve and send

In Review, fix anything we couldn&apos;t match automatically (item codes, dates) and click **Send to supplier**.

---

Need help? Email [support@proculink.com](mailto:support@proculink.com) or see [Troubleshooting common parse errors](/help/troubleshooting).
```

Write the remaining six articles in the same style. Keep each under 300 words. Article bodies:

- **`mapping-basics`** — what canonical fields are required (`po_number`, `line_no`, `item_code`, `quantity`, `unit_price`), how to use Replace/Trim/DateFormat/Concat manipulators, how to test against a real file.
- **`delivery-config`** — picking HTTP vs Erply vs Directo, putting webhook URL + auth, running test-fire, what statuses mean (`ready_to_deliver` → `delivering` → `delivered` / `delivery_failed`).
- **`ai-suggestions`** — when OpenAI runs (only after deterministic mapping leaves a line unresolved), what confidence + reason + provenance mean, how to confirm or clear suggestions, plan gating (none — runs for all paying plans when org `Ai:OpenAI:ApiKey` is configured by ProcuLink).
- **`billing-faq`** — Pilot 14 days / 20 orders / 1 supplier; Growth €149 / 150 orders / 5 suppliers; Operations €399 / 500 orders / 10 suppliers; Integration €999 / 1,000 orders / 20 suppliers; what happens at quota (429 banner + upgrade CTA); read-only after Pilot expiry; how to cancel via Stripe Portal.
- **`email-polling`** — Integration+ only, host/port/SSL/folder/username/password, polling cadence (5 min), CSV/XLSX/PDF attachments only, marked as seen, body-only parsing deferred.
- **`troubleshooting`** — common parse errors: missing required column, ambiguous date format, encoding (UTF-8 BOM), PDF text vs scan, supplier code mismatch.

- [ ] **Step 4: Frontend verification**

```bash
bun run build
```

Expected: success. Manually open `/help`, search "delivery" → see `delivery-config` card. Click → MDX article renders.

- [ ] **Step 5: Commit**

```bash
git add "src/app/(marketing)/help/" src/lib/help-articles.ts package.json bun.lockb
git commit -m "feat(help): /help landing + 7 MDX articles + Fuse.js search"
```

---

## Phase 9 — In-app Help button + contact form

### Task 9.1 — In-app Help slide-over

**Files:**
- Modify: `project-proculink/src/components/bridge/BridgeTopbar.tsx`
- Create: `project-proculink/src/components/bridge/HelpSlideover.tsx`

- [ ] **Step 1: Create `HelpSlideover.tsx`**

```tsx
"use client";

import { useEffect } from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { capture } from "@/lib/analytics";

interface Props {
  open:   boolean;
  onClose: () => void;
}

const CONTEXTUAL_LINKS: Record<string, { href: string; title: string }> = {
  "/upload":           { href: "/help/first-upload",  title: "Your first purchase order upload" },
  "/library/mappings": { href: "/help/mapping-basics", title: "PO field mapping basics" },
  "/library/suppliers": { href: "/help/delivery-config", title: "Configuring supplier delivery" },
  "/settings":         { href: "/help/billing-faq", title: "Billing and plans FAQ" },
};

export function HelpSlideover({ open, onClose }: Props) {
  const pathname = usePathname();
  const contextual = Object.entries(CONTEXTUAL_LINKS).find(([prefix]) => pathname?.startsWith(prefix))?.[1];

  useEffect(() => { if (open) capture("help_slideover_opened", { route: pathname }); }, [open, pathname]);

  if (!open) return null;

  return (
    <div
      role="dialog"
      aria-label="Help"
      style={{ position: "fixed", inset: 0, zIndex: 70, background: "rgba(11,26,47,0.32)", display: "flex", justifyContent: "flex-end" }}
      onClick={onClose}
    >
      <aside
        onClick={(e) => e.stopPropagation()}
        style={{
          width: "min(380px, 100%)",
          background: "#FFFFFF",
          height: "100%",
          padding: "24px 22px",
          boxShadow: "-12px 0 30px rgba(11,26,47,0.12)",
          overflowY: "auto",
        }}
      >
        <header style={{ display: "flex", alignItems: "center", justifyContent: "space-between", marginBottom: 18 }}>
          <h2 style={{ fontFamily: "'Bricolage Grotesque', Inter, sans-serif", fontSize: 18, fontWeight: 600, color: "#0B1A2F", margin: 0 }}>Help</h2>
          <button type="button" onClick={onClose} aria-label="Close" style={{ background: "transparent", border: "none", cursor: "pointer", fontSize: 18, color: "#8A93A5" }}>×</button>
        </header>

        {contextual && (
          <section style={{ background: "#F6F7FA", border: "1px solid #E2E6EE", borderLeft: "3px solid #1E66C9", borderRadius: 8, padding: "12px 14px", marginBottom: 18 }}>
            <p style={{ margin: 0, fontSize: 12, fontWeight: 600, color: "#8A93A5", textTransform: "uppercase", letterSpacing: "0.05em" }}>For this page</p>
            <Link href={contextual.href} onClick={onClose} style={{ display: "block", marginTop: 6, fontSize: 14, fontWeight: 600, color: "#0B1A2F" }}>{contextual.title}</Link>
          </section>
        )}

        <nav style={{ display: "flex", flexDirection: "column", gap: 8 }}>
          <Link href="/help" onClick={onClose} style={{ padding: "10px 12px", border: "1px solid #E2E6EE", borderRadius: 6, fontSize: 14, color: "#0B1A2F", textDecoration: "none" }}>Open help docs →</Link>
          <Link href="/support" onClick={onClose} style={{ padding: "10px 12px", border: "1px solid #E2E6EE", borderRadius: 6, fontSize: 14, color: "#0B1A2F", textDecoration: "none" }}>Contact support</Link>
          <Link href="/support#report-a-bug" onClick={onClose} style={{ padding: "10px 12px", border: "1px solid #E2E6EE", borderRadius: 6, fontSize: 14, color: "#0B1A2F", textDecoration: "none" }}>Report a bug</Link>
        </nav>
      </aside>
    </div>
  );
}
```

- [ ] **Step 2: Wire the Help button into `BridgeTopbar`**

Read `BridgeTopbar.tsx`. Locate the right-hand control cluster (where breadcrumb/user menu sit). Add a Help button:

```tsx
const [helpOpen, setHelpOpen] = useState(false);

// In the button row, before the user menu:
<button
  type="button"
  aria-label="Help"
  onClick={() => setHelpOpen(true)}
  style={{ /* match existing topbar icon-button styles */ }}
>
  ?
</button>

// At the end of the topbar JSX:
<HelpSlideover open={helpOpen} onClose={() => setHelpOpen(false)} />
```

- [ ] **Step 3: Verify build**

```bash
bun run build
```

Expected: success.

- [ ] **Step 4: Commit**

```bash
git add src/components/bridge/HelpSlideover.tsx src/components/bridge/BridgeTopbar.tsx
git commit -m "feat(help): in-app Help button + slide-over with route-aware contextual link"
```

### Task 9.2 — Contact form (`POST /api/support/contact`)

**Files:**
- Create: `ProcuLink/ProcuLink.Core/Services/ISupportContactService.cs`
- Create: `ProcuLink/ProcuLink.Infrastructure/Services/SupportContactService.cs`
- Create: `ProcuLink/ProcuLink.Infrastructure.Tests/Services/SupportContactServiceTests.cs`
- Create: `ProcuLink/ProcuLink.Api/Controllers/SupportController.cs`
- Modify: `project-proculink/src/app/(marketing)/support/page.tsx` — add contact form
- Modify: `project-proculink/src/lib/api-client.ts` — `submitSupportRequest`

- [ ] **Step 1: Define contract**

```csharp
namespace ProcuLink.Core.Services;

public sealed record SupportContactRequest(
    string Category,
    string Subject,
    string Message,
    string? UserEmail,
    string? UserAgent,
    string? Route);

public interface ISupportContactService
{
    Task SubmitAsync(Guid? organisationId, string? userId, SupportContactRequest req, CancellationToken ct);
}
```

- [ ] **Step 2: Failing test**

```csharp
public class SupportContactServiceTests
{
    [Fact]
    public async Task SubmitAsync_SendsEmail_WithExpectedSubjectAndCategory()
    {
        var fakeMail = new FakeEmailSender();
        var fakeAnalytics = new FakeAnalyticsService();
        var svc = new SupportContactService(fakeMail, fakeAnalytics, NullLogger<SupportContactService>.Instance);

        var orgId = Guid.NewGuid();
        await svc.SubmitAsync(orgId, "user_abc",
            new SupportContactRequest("bug", "Cannot upload PDF", "Stack trace …", "u@example.com", "Mozilla/5", "/upload"),
            default);

        Assert.Single(fakeMail.Sent);
        var sent = fakeMail.Sent[0];
        Assert.Contains("[support][bug]", sent.Subject);
        Assert.Equal("support@proculink.com", sent.To);
        Assert.Contains("u@example.com", sent.Body);
        Assert.Contains("/upload", sent.Body);

        Assert.Single(fakeAnalytics.CapturedEvents, e => e.EventName == "support_form_submitted");
    }
}
```

- [ ] **Step 3: Implement**

```csharp
public sealed class SupportContactService : ISupportContactService
{
    private const string SupportInbox = "support@proculink.com";
    private readonly IEmailSender _mail;
    private readonly IAnalyticsService _analytics;
    private readonly ILogger<SupportContactService> _log;

    public SupportContactService(IEmailSender mail, IAnalyticsService analytics, ILogger<SupportContactService> log)
    {
        _mail = mail; _analytics = analytics; _log = log;
    }

    public async Task SubmitAsync(Guid? organisationId, string? userId, SupportContactRequest req, CancellationToken ct)
    {
        var subject = $"[support][{req.Category}] {req.Subject}".Trim();
        var body = $"""
            Org:     {organisationId?.ToString() ?? "(unauthenticated)"}
            User:    {userId ?? "(none)"} {req.UserEmail ?? ""}
            Route:   {req.Route ?? "(none)"}
            Agent:   {req.UserAgent ?? "(unknown)"}

            ---

            {req.Message}
            """;

        await _mail.SendAsync(SupportInbox, subject, body, ct);

        if (organisationId.HasValue)
        {
            await _analytics.CaptureAsync(
                organisationId: organisationId.Value,
                userId: userId,
                eventName: "support_form_submitted",
                properties: new Dictionary<string, object?>
                {
                    ["category"] = req.Category,
                    ["route"]    = req.Route ?? "(none)",
                },
                ct: ct);
        }
    }
}
```

Use whichever existing `IEmailSender` abstraction already wraps SMTP for ProcuLink (the IMAP / Hangfire work introduced MailKit). If no abstraction exists yet, add a minimal one (`SendAsync(string to, string subject, string body, CancellationToken)`) in `ProcuLink.Core/Services` with an SMTP-backed implementation.

- [ ] **Step 4: Add controller**

```csharp
[ApiController]
[Route("api/support")]
public class SupportController : ControllerBase
{
    private readonly ISupportContactService _support;
    private readonly ICurrentTenantService  _tenant;
    private readonly ICurrentUserService    _user;

    public SupportController(ISupportContactService support, ICurrentTenantService tenant, ICurrentUserService user)
    {
        _support = support; _tenant = tenant; _user = user;
    }

    [AllowAnonymous]
    [HttpPost("contact")]
    public async Task<IActionResult> Contact([FromBody] SupportContactRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Message) || string.IsNullOrWhiteSpace(req.Category))
            return BadRequest(new { error = "category and message are required" });

        Guid? orgId   = _tenant.IsAuthenticated ? _tenant.OrganisationId : null;
        string? userId = _user.IsAuthenticated ? _user.UserId : null;
        await _support.SubmitAsync(orgId, userId, req, ct);
        return Ok(new { ok = true });
    }
}
```

`AllowAnonymous` because unauthenticated marketing visitors should also be able to file support requests.

- [ ] **Step 5: Tests + build**

```bash
dotnet build ProcuLink.slnx --no-restore
dotnet test ProcuLink.slnx --no-restore
```

Expected: green.

- [ ] **Step 6: Add a contact form to `/support`**

In `support/page.tsx`, replace the mailto-only flow with a form (use a Client Component wrapper for the form). Form fields: Category select (`general | bug | billing | security`), Subject, Message, optional Email (for unauth users). Submit calls `apiClient.submitSupportRequest`.

- [ ] **Step 7: Frontend verification**

```bash
bun run build
```

Expected: success.

- [ ] **Step 8: Commit**

```bash
git add ProcuLink.Core/Services/ISupportContactService.cs \
        ProcuLink.Infrastructure/Services/SupportContactService.cs \
        ProcuLink.Infrastructure.Tests/Services/SupportContactServiceTests.cs \
        ProcuLink.Api/Controllers/SupportController.cs

git commit -m "feat(support): POST /api/support/contact emails support@proculink.com + analytics"

cd ../project-proculink
git add "src/app/(marketing)/support/page.tsx" src/lib/api-client.ts
git commit -m "feat(support): contact form on /support submits via API"
```

---

## Phase 10 — Sales/demo assets + dead-code cleanup + final verification

### Task 10.1 — `/customers` placeholder page

**Files:**
- Create: `project-proculink/src/app/(marketing)/customers/page.tsx`

- [ ] **Step 1: Create the page**

```tsx
import type { Metadata } from "next";
import Link from "next/link";

export const metadata: Metadata = {
  title: "Customers — ProcuLink",
  description: "Procurement teams using ProcuLink to deliver purchase orders to their suppliers.",
};

const S = {
  page:   { maxWidth: 880, margin: "0 auto", padding: "72px 32px 80px" },
  h1:     { fontFamily: "'Bricolage Grotesque', Inter, sans-serif", fontSize: "clamp(30px, 4vw, 46px)", fontWeight: 700, letterSpacing: "-0.025em", color: "#0B1A2F", marginBottom: 12 },
  sub:    { fontSize: 16, color: "#56627A", lineHeight: 1.6, marginBottom: 48, maxWidth: 600 },
  grid:   { display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(280px, 1fr))", gap: 18 },
  card:   { background: "#FFFFFF", border: "1px solid #E2E6EE", borderRadius: 12, padding: 22, boxShadow: "0 4px 14px rgba(11,26,47,0.04)" },
  badge:  { display: "inline-block", padding: "3px 9px", background: "#F6F7FA", border: "1px solid #E2E6EE", borderRadius: 999, fontSize: 11, fontWeight: 600, color: "#56627A", letterSpacing: "0.04em", textTransform: "uppercase" as const, marginBottom: 14 },
  cardTitle: { fontFamily: "'Bricolage Grotesque', Inter, sans-serif", fontSize: 17, fontWeight: 600, color: "#0B1A2F", margin: "0 0 10px" },
  cardBlurb: { fontSize: 13.5, color: "#56627A", margin: 0, lineHeight: 1.6 },
  cta:    { display: "inline-block", marginTop: 56, background: "#0B1A2F", color: "#fff", textDecoration: "none", padding: "12px 22px", borderRadius: 8, fontWeight: 600, fontSize: 14 },
};

export default function CustomersPage() {
  return (
    <div style={S.page}>
      <h1 style={S.h1}>Procurement teams using ProcuLink.</h1>
      <p style={S.sub}>
        We&apos;re in early pilots with B2B procurement teams across Estonia and the EU. Public case studies will appear here as pilots conclude.
      </p>

      <div style={S.grid}>
        <article style={S.card}>
          <span style={S.badge}>Coming soon — anonymised pilot</span>
          <h2 style={S.cardTitle}>Mid-market wholesaler · ~120 POs/month</h2>
          <p style={S.cardBlurb}>
            Replaces manual reformatting of CSV purchase orders for five rotating suppliers. Pilot scoped to a single buyer team.
          </p>
        </article>

        <article style={S.card}>
          <span style={S.badge}>Coming soon — anonymised pilot</span>
          <h2 style={S.cardTitle}>Industrial distributor · ~500 POs/month</h2>
          <p style={S.cardBlurb}>
            HTTP webhook delivery into a partner ERP, with PO field mapping handled per-supplier and IMAP ingestion as a fallback.
          </p>
        </article>
      </div>

      <Link href="/pricing" style={S.cta}>See pricing →</Link>
    </div>
  );
}
```

- [ ] **Step 2: Add link to marketing nav (if MarketingNav has a primary link row)**

Open `src/components/marketing/MarketingNav.tsx`. Add a `Customers` link between `How it works` and `Pricing` (or wherever the link order makes sense).

- [ ] **Step 3: Verify build + commit**

```bash
bun run build
git add "src/app/(marketing)/customers/page.tsx" src/components/marketing/MarketingNav.tsx
git commit -m "feat(sales): add /customers page with anonymised pilot placeholders"
```

### Task 10.2 — `/one-pager` printable A4

The print stylesheet lives in a CSS module so we don't need `dangerouslySetInnerHTML`. CSS modules are first-class in Next 15.

**Files:**
- Create: `project-proculink/src/app/(marketing)/one-pager/page.tsx`
- Create: `project-proculink/src/app/(marketing)/one-pager/print.module.css`

- [ ] **Step 1: Create the CSS module**

`project-proculink/src/app/(marketing)/one-pager/print.module.css`:

```css
.root {
  max-width: 820px;
  margin: 0 auto;
  padding: 32px 36px;
  color: #0B1A2F;
  font-family: Inter, sans-serif;
}

@media print {
  @page {
    size: A4;
    margin: 14mm;
  }
  .root {
    padding: 0;
  }
  /* Hide marketing chrome when printing — nav and footer sit in (marketing)/layout.tsx */
  :global(header),
  :global(footer),
  :global(nav) {
    display: none !important;
  }
}
```

The `:global(…)` selectors target the marketing layout chrome without leaking outside this CSS module.

- [ ] **Step 2: Create the page**

`project-proculink/src/app/(marketing)/one-pager/page.tsx`:

```tsx
import type { Metadata } from "next";
import styles from "./print.module.css";

export const metadata: Metadata = {
  title: "ProcuLink — one-pager",
  description: "Print-friendly one-page overview of ProcuLink for procurement teams.",
};

const S = {
  brand:   { fontFamily: "'Bricolage Grotesque', Inter, sans-serif", fontSize: 22, fontWeight: 700, color: "#0B1A2F", marginBottom: 24, letterSpacing: "-0.02em" },
  h1:      { fontFamily: "'Bricolage Grotesque', Inter, sans-serif", fontSize: 30, fontWeight: 700, color: "#0B1A2F", margin: "0 0 10px", letterSpacing: "-0.02em" },
  lead:    { fontSize: 15, color: "#3D4A5C", lineHeight: 1.5, margin: "0 0 28px", maxWidth: 640 },
  h2:      { fontFamily: "'Bricolage Grotesque', Inter, sans-serif", fontSize: 14, fontWeight: 700, color: "#0B1A2F", textTransform: "uppercase" as const, letterSpacing: "0.05em", margin: "0 0 8px" },
  p:       { fontSize: 13.5, color: "#3D4A5C", lineHeight: 1.55, margin: "0 0 10px" },
  threeCol:{ display: "grid", gridTemplateColumns: "repeat(3, 1fr)", gap: 22, margin: "16px 0 28px" },
  step:    { padding: 12, background: "#F6F7FA", border: "1px solid #E2E6EE", borderRadius: 8 },
  stepN:   { fontFamily: "'Bricolage Grotesque', Inter, sans-serif", fontSize: 18, fontWeight: 700, color: "#1E66C9" },
  stepT:   { fontSize: 13, fontWeight: 600, color: "#0B1A2F", margin: "4px 0 4px" },
  stepD:   { fontSize: 12, color: "#56627A", lineHeight: 1.5, margin: 0 },
  table:   { width: "100%", borderCollapse: "collapse" as const, fontSize: 12.5, marginBottom: 24 },
  th:      { textAlign: "left" as const, padding: "8px 10px", background: "#F6F7FA", borderBottom: "1px solid #E2E6EE", color: "#0B1A2F", fontWeight: 600 },
  td:      { padding: "8px 10px", borderBottom: "1px solid #F1F3F7", color: "#3D4A5C" },
  contact: { fontSize: 12, color: "#56627A", marginTop: 12, borderTop: "1px solid #E2E6EE", paddingTop: 12 },
};

export default function OnePagerPage() {
  return (
    <div className={styles.root}>
      <p style={S.brand}>ProcuLink</p>

      <h1 style={S.h1}>Stop reformatting purchase orders. Start delivering them.</h1>
      <p style={S.lead}>
        ProcuLink is a B2B outbound procurement bridge for buyer teams. We import the
        POs you send, validate them, map fields and item codes per supplier, transform
        to the format each supplier requires, and deliver them automatically over HTTP,
        ERP, or email.
      </p>

      <h2 style={S.h2}>How it works</h2>
      <div style={S.threeCol}>
        {[
          { n: "1", t: "Import", d: "Upload CSV / XLSX / PDF, or let ProcuLink poll an IMAP mailbox." },
          { n: "2", t: "Map + transform", d: "Per-supplier field + item-code mapping with AI suggestions. Output to CSV, XML, cXML, JSON." },
          { n: "3", t: "Deliver", d: "HTTP webhook, Erply, Directo, or download. Full audit trail and delivery status." },
        ].map((s) => (
          <div key={s.n} style={S.step}>
            <div style={S.stepN}>0{s.n}</div>
            <p style={S.stepT}>{s.t}</p>
            <p style={S.stepD}>{s.d}</p>
          </div>
        ))}
      </div>

      <h2 style={S.h2}>Pricing</h2>
      <table style={S.table}>
        <thead><tr><th style={S.th}>Plan</th><th style={S.th}>Price</th><th style={S.th}>Orders / month</th><th style={S.th}>Suppliers</th></tr></thead>
        <tbody>
          <tr><td style={S.td}>Pilot</td><td style={S.td}>Free, 14 days</td><td style={S.td}>20 total</td><td style={S.td}>1</td></tr>
          <tr><td style={S.td}>Growth</td><td style={S.td}>€149/mo</td><td style={S.td}>150</td><td style={S.td}>5</td></tr>
          <tr><td style={S.td}>Operations</td><td style={S.td}>€399/mo</td><td style={S.td}>500</td><td style={S.td}>10</td></tr>
          <tr><td style={S.td}>Integration</td><td style={S.td}>€999/mo</td><td style={S.td}>1,000</td><td style={S.td}>20</td></tr>
          <tr><td style={S.td}>Enterprise</td><td style={S.td}>From €2,500/mo</td><td style={S.td}>Custom</td><td style={S.td}>Custom</td></tr>
        </tbody>
      </table>

      <h2 style={S.h2}>Trust + security</h2>
      <p style={S.p}>
        EU-region infrastructure. AES-256-GCM for delivery credentials and IMAP passwords. Org-scoped query isolation.
        GDPR-aligned DPA available at <strong>proculink.com/dpa</strong>. Subprocessors at <strong>proculink.com/subprocessors</strong>.
      </p>

      <div style={S.contact}>
        ProcuLink OÜ · Registration 17477775 · Katusepapi 6, Tallinn, Estonia<br />
        hello@proculink.com · support@proculink.com · proculink.com
      </div>
    </div>
  );
}
```

- [ ] **Step 3: Verify print rendering**

Run dev, open `/one-pager`, press Ctrl/Cmd-P. Confirm one-page A4 layout. Marketing nav and footer should be hidden during print.

- [ ] **Step 4: Commit**

```bash
git add "src/app/(marketing)/one-pager/page.tsx" "src/app/(marketing)/one-pager/print.module.css"
git commit -m "feat(sales): add /one-pager printable A4 overview"
```

### Task 10.3 — `/watch` Loom slot + "Book a demo" CTA

**Files:**
- Create: `project-proculink/src/app/(marketing)/watch/page.tsx`
- Modify: `project-proculink/src/components/bridge/UploadWorkbench.tsx` (Book-a-demo CTA for Pilot)
- Modify: `project-proculink/src/components/bridge/settings/BillingSection.tsx` (Book-a-demo CTA for Pilot)
- Modify: `project-proculink/.env` + `.env.example` (`NEXT_PUBLIC_WALKTHROUGH_LOOM_URL`, `NEXT_PUBLIC_BOOK_DEMO_URL`)

- [ ] **Step 1: Add env vars (empty defaults)**

Append to `.env` and `.env.example`:

```
NEXT_PUBLIC_WALKTHROUGH_LOOM_URL=
NEXT_PUBLIC_BOOK_DEMO_URL=
```

- [ ] **Step 2: Create `/watch`**

```tsx
"use client";

import { useEffect } from "react";
import Link from "next/link";
import { capture } from "@/lib/analytics";

export default function WatchPage() {
  const loomUrl = process.env.NEXT_PUBLIC_WALKTHROUGH_LOOM_URL ?? "";

  useEffect(() => {
    if (loomUrl) capture("watch_demo_started", { loom_url_hash: hashUrl(loomUrl) });
  }, [loomUrl]);

  return (
    <div style={{ maxWidth: 880, margin: "0 auto", padding: "72px 32px 80px" }}>
      <h1 style={{ fontFamily: "'Bricolage Grotesque', Inter, sans-serif", fontSize: "clamp(30px, 4vw, 44px)", fontWeight: 700, color: "#0B1A2F", marginBottom: 12, letterSpacing: "-0.025em" }}>
        Watch a 90-second walkthrough
      </h1>
      <p style={{ fontSize: 16, color: "#56627A", lineHeight: 1.6, marginBottom: 32, maxWidth: 600 }}>
        See how a single CSV upload becomes a delivered supplier order.
      </p>

      {loomUrl ? (
        <div style={{ position: "relative", paddingBottom: "56.25%", height: 0, borderRadius: 12, overflow: "hidden", boxShadow: "0 6px 22px rgba(11,26,47,0.1)" }}>
          <iframe
            src={loomUrl}
            title="ProcuLink walkthrough"
            allow="autoplay; fullscreen"
            style={{ position: "absolute", inset: 0, width: "100%", height: "100%", border: "0" }}
          />
        </div>
      ) : (
        <div style={{ background: "#F6F7FA", border: "1px dashed #C6CDDA", borderRadius: 12, padding: 48, textAlign: "center", color: "#8A93A5", fontSize: 14 }}>
          The walkthrough video is being recorded. Email <a href="mailto:hello@proculink.com" style={{ color: "#1E66C9" }}>hello@proculink.com</a> if you&apos;d like an early link.
        </div>
      )}

      <p style={{ marginTop: 36, fontSize: 14, color: "#56627A" }}>
        Prefer a live walkthrough? <Link href="/pricing" style={{ color: "#1E66C9" }}>See pricing</Link> or book a 15-minute demo from inside the product.
      </p>
    </div>
  );
}

function hashUrl(url: string): string {
  let h = 0;
  for (let i = 0; i < url.length; i++) h = ((h << 5) - h + url.charCodeAt(i)) | 0;
  return Math.abs(h).toString(16);
}
```

- [ ] **Step 3: Book-a-demo CTA on `/upload` and Billing tab for Pilot accounts**

In `UploadWorkbench`, after determining `billing.plan === "pilot"`, render a small card above the file-drop:

```tsx
{billing?.plan === "pilot" && process.env.NEXT_PUBLIC_BOOK_DEMO_URL && (
  <div style={{ background: "#F6F7FA", border: "1px solid #E2E6EE", borderLeft: "3px solid #1E66C9", borderRadius: 8, padding: "12px 16px", marginBottom: 16, display: "flex", alignItems: "center", justifyContent: "space-between", gap: 12, flexWrap: "wrap" }}>
    <p style={{ margin: 0, fontSize: 13.5, color: "#3D4A5C" }}>On Pilot? Get a guided 15-minute walkthrough with the team.</p>
    <a
      href={process.env.NEXT_PUBLIC_BOOK_DEMO_URL}
      target="_blank"
      rel="noopener noreferrer"
      onClick={() => capture("book_demo_clicked", { from_route: "/upload", plan: "pilot" })}
      style={{ background: "#0B1A2F", color: "#fff", padding: "8px 14px", borderRadius: 6, fontSize: 13, fontWeight: 600, textDecoration: "none" }}
    >
      Book a 15-min demo →
    </a>
  </div>
)}
```

Mirror the same block in `BillingSection.tsx` (settings → billing tab) inside the Pilot-state branch.

- [ ] **Step 4: Verify build**

```bash
bun run build
```

Expected: success.

- [ ] **Step 5: Commit**

```bash
git add "src/app/(marketing)/watch/page.tsx" \
        src/components/bridge/UploadWorkbench.tsx \
        src/components/bridge/settings/BillingSection.tsx \
        .env .env.example
git commit -m "feat(sales): /watch Loom slot + Pilot Book-a-demo CTAs on /upload and billing settings"
```

### Task 10.4 — Dead-code cleanup + STATUS.md update + final verification

**Files:**
- Delete: `project-proculink/src/components/onboarding/OnboardingWizard.tsx`
- Delete: `project-proculink/src/views/Dashboard.tsx` (if confirmed unused)
- Modify: `ProcuLink/STATUS.md` (Group L summary)

- [ ] **Step 1: Confirm both files are unused**

```bash
cd project-proculink
grep -rn "components/onboarding/OnboardingWizard\|views/Dashboard" src
```

Expected: zero matches (or matches only inside the candidate-for-deletion files themselves).

- [ ] **Step 2: Delete the files**

```bash
rm src/components/onboarding/OnboardingWizard.tsx
rm src/views/Dashboard.tsx
```

If the directories are now empty, delete them too.

- [ ] **Step 3: Update STATUS.md**

In `STATUS.md`, replace the Group L row in the Phase 5 roadmap table and add a "Group L summary" section near the existing Group I/J/K sections. Use the same factual tone as existing entries. Suggested content:

```markdown
### Group L — trust, onboarding + commercial readiness (in progress)

- **Legal entity corrected**: `ESTORIA CAPITAL GROUP OÜ` → `ProcuLink OÜ` across all marketing legal pages and docs/trust/gdpr.md.
- **New legal/trust pages**: `/dpa` (GDPR Art. 28 DPA + annexes), `/subprocessors` (with 30-day change-notification commitment), `/aup` (Acceptable Use Policy), `/status` env-driven footer link.
- **Cookie consent banner** with `useCookieConsent()` (functional-only / analytics-allowed states).
- **PostHog Cloud EU analytics**: backend `IAnalyticsService` + frontend `posthog-js` SDK; both no-op without keys. Event taxonomy at `docs/analytics-event-taxonomy.md`. Backend events: `org_created`, `first_supplier/upload/transform/delivery_*`, `billing_upgraded/downgraded/cancelled`, `sample_order_*`, `support_form_submitted`. Frontend events: wizard, sample-order, help, book-demo.
- **Onboarding wizard extended to 4 steps**: supplier → upload → resolve mapping → configure delivery. New `hasResolvedMapping` flag on `/api/onboarding/status`.
- **Sample order path**: `POST /api/onboarding/sample-order` creates a hidden `__sample__` supplier (idempotent), parses an embedded CSV fixture, marks the order `IsSample=true` so it does not increment quota. "Try with sample order" button on `/upload`, banner on the review page.
- **Welcome pages**: `/welcome` post-signup landing + `/welcome?upgraded={plan}` post-Stripe-Checkout state. Clerk redirect target documented.
- **/help MDX docs**: 7 articles (first-upload, mapping-basics, delivery-config, ai-suggestions, billing-faq, email-polling, troubleshooting). Fuse.js client search.
- **In-app Help slide-over** in `BridgeTopbar` with route-aware contextual link.
- **`/support` contact form** wired to `POST /api/support/contact`; emails support@proculink.com + emits `support_form_submitted`.
- **Sales/demo assets**: `/customers` placeholder, `/one-pager` printable A4, `/watch` env-driven Loom slot, in-app Pilot Book-a-demo CTAs on `/upload` and billing settings.
- **Dead-code cleanup**: removed unused `src/components/onboarding/OnboardingWizard.tsx` and `src/views/Dashboard.tsx`.

Manual/live QA still recommended before public launch:
- DPA counter-signature flow exercised with a real prospect.
- PostHog Cloud EU project created, API keys set in Vercel/Railway, funnel from `signup` → `first_delivery_succeeded` visible.
- Status page URL (Instatus/BetterStack) configured in `NEXT_PUBLIC_STATUS_URL`.
- Loom walkthrough recorded and pasted into `NEXT_PUBLIC_WALKTHROUGH_LOOM_URL`.
- Cal.com/Calendly slot configured and pasted into `NEXT_PUBLIC_BOOK_DEMO_URL`.
- Stripe `success_url` updated to point at the production Vercel `/welcome` route.
```

- [ ] **Step 4: Final full-stack verification**

```bash
cd project-proculink
bun run build

cd ../ProcuLink
dotnet build ProcuLink.slnx --no-restore
dotnet test ProcuLink.slnx --no-restore
```

Expected: both builds succeed, all tests pass.

- [ ] **Step 5: Commit cleanup + STATUS update**

```bash
cd project-proculink
git add -A
git commit -m "chore(cleanup): remove unused OnboardingWizard.tsx and views/Dashboard.tsx"

cd ../ProcuLink
git add STATUS.md
git commit -m "docs(status): record Group L (trust, onboarding, commercial readiness) completion"
```

- [ ] **Step 6: Push both branches**

```bash
cd project-proculink && git push origin main
cd ../ProcuLink && git push origin main
```

---

## Spec coverage summary (self-review)

| Group L scope item                                | Phase(s)            |
|---------------------------------------------------|---------------------|
| Concrete ROI copy on landing                      | (already shipped — ROICalculator.tsx) |
| Onboarding path/checklist for first supplier flow | Phase 5 (wizard) + Phase 7 (welcome) |
| Sample data mode that's realistic but not fake    | Phase 6 (sample-order endpoint + button) |
| Privacy page                                      | (already shipped, entity-renamed in Phase 1) |
| Terms page                                        | (already shipped, entity-renamed in Phase 1) |
| Security / compliance overview                    | (already shipped, plus DPA in Phase 2) |
| Support/contact path                              | Phase 9 (contact form) + Phase 8 (/help) + in-app Help (Phase 9) |
| Analytics event plan (signup → first delivery)    | Phase 4 (taxonomy + backend + frontend SDK + emitters) |
| Billing upgrade click event                       | Phase 4.5 + Phase 10.3 (Book-a-demo also captured) |
| Mapping accepted/rejected                         | Phase 4.3 (`first_mapping_resolved`, via=manual\|ai_suggestion) |
| Sales/demo assets after UI polish                 | Phase 10 (/customers, /one-pager, /watch, book-a-demo) |
| New B2B buyer understands what to do next         | Phase 5 wizard + Phase 7 /welcome + Phase 8 /help |
| Concrete marketing language                       | Phase 1 (entity correctness), copy reuses existing landing |
| Trust pages and support routes before launch      | Phase 2 (/dpa, /subprocessors, /aup, /status), Phase 9 (contact) |

## What this plan deliberately does not do

- New input or output formats — that belongs to Group K hardening (cXML already merged on a feature branch).
- Live deployment QA against real Stripe/Clerk/IMAP — that belongs to Group J.
- Cookie banner geolocation (showing only to EU visitors) — global banner is simpler and conservative.
- Help docs CMS — MDX files in the repo are enough until volume justifies a CMS.
- Crisp/Intercom live chat — adds a subprocessor + cookie row; deferred.
- Public read-only `demo@proculink.com` org — founder declined; on-demand sample-order is the alternative.
- Auto-seed sample data on org creation — same reason; on-demand only.
- Status page hosting — link only; founder hosts the actual board on Instatus/BetterStack.

## Open verification items for the founder before/during execution

1. Confirm registration **17477775** + Katusepapi 6 Tallinn address apply to ProcuLink OÜ (assumed from Phase 1 question).
2. Create PostHog Cloud EU project; set `NEXT_PUBLIC_POSTHOG_KEY` (Vercel) and `Analytics:PostHog:ApiKey` (Railway API + Worker).
3. Configure Clerk post-sign-up redirect to `/welcome` in the Clerk dashboard.
4. Update Stripe Checkout `success_url` to the production Vercel `/welcome?upgraded={plan}` once Vercel domain is finalised.
5. Optional now, required for launch: set `NEXT_PUBLIC_STATUS_URL`, `NEXT_PUBLIC_WALKTHROUGH_LOOM_URL`, `NEXT_PUBLIC_BOOK_DEMO_URL`.
