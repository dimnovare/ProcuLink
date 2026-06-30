# Force Organization Creation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop personal-workspace tenant fragmentation by forcing every signed-in user into a real Clerk organization before they reach the app, and refusing to silently mint per-user "Personal workspace" tenants on the backend.

**Architecture:** Two enforcement layers shipped in a strict order. (1) **Frontend gate (ship first):** edge middleware redirects any signed-in user with no active org to a new `/onboarding/select-organization` route that makes them create or select a real Clerk org; only then do they reach `(app)`. (2) **Backend hardening (ship second, after the gate is live and verified):** `TenantResolutionMiddleware` only resolves/auto-provisions a tenant when the Clerk tenant key is a real organisation id (`org_…`); a user-only `sub` claim no longer falls back to a personal-workspace tenant — it fails closed. The Clerk-native "session tasks" mechanism (disabling Personal Accounts at the Clerk level) is documented as a **founder action**, because it is **not self-serve** for this pre-2025-08-22 Clerk app.

**Tech Stack:** Next.js 15 App Router + `@clerk/nextjs` ^7.4.1 (frontend, repo `project-proculink`); vitest + Playwright (frontend tests); ASP.NET Core 8 + xUnit + EF Core InMemory (backend, repo `ProcuLink`).

---

## Background — what we are fixing

ProcuLink is multi-tenant: every API request is scoped to one `organisationId`, resolved from the Clerk JWT in
`ProcuLink.Api/Middleware/TenantResolutionMiddleware.cs`:

1. **Prefer** the `org_id` claim — a real Clerk **organisation** (`org_…`).
2. **Fallback** (lines 99–104): no active org → use the user's `sub` claim (`user_…`), label it `"Personal workspace"`, and auto-provision an `Organisation` keyed to that sub.

A brand-new user who signs up but never creates/activates a Clerk **organisation** therefore lands in a tenant keyed to their personal user id. All their work (suppliers, mappings, connections, delivery configs, delivered orders, audit trail) lands in that personal tenant. When they later create a team org to invite colleagues, the JWT carries `org_id=org_…` and the backend auto-provisions a **second, empty** tenant — the day-1 work is stranded in the personal tenant and invisible to the team. Multi-seat is worse: each teammate who logs in before the org is active gets their **own** personal tenant. Prod already contains one such row (`STATUS.md:532`, org `370ca357…` "Personal workspace").

The frontend `AutoActivateOrg` ([project-proculink `src/app/(app)/layout.tsx:23`]) only *activates* an existing first org — it never *creates* one, and the edge middleware (`src/middleware.ts:52`) only checks `userId`, never org membership. Nothing forces org creation.

### Key constraint discovered during investigation (do not skip)

Clerk's modern first-class mechanism for forcing org-only usage is the **`choose-organization` session task**, enabled by turning **Personal Accounts OFF** in the Clerk Dashboard. **This is NOT self-serve for Clerk apps created before 2025-08-22.** ProcuLink's app (`golden-alpaca-43`) predates that, so the toggle is blocked pending a Clerk support migration. **Therefore this plan ships the manual middleware gate (which we fully control today)** and documents the Clerk-native upgrade as a founder action (Phase 4).

---

## Rollout sequencing (CRITICAL — read before executing)

The two repos MUST deploy in this order, with a checkpoint between them:

1. **Phase 1+2 — frontend gate → deploy to Vercel → verify live.** After this, every authenticated user always has an active `org_…` before any tenant-scoped API call. NEW fragmentation stops here.
2. **CHECKPOINT — audit prod for sub-keyed tenants** (Phase 3, the gate in Task 11). Any existing `Organisation` row whose `ClerkOrgId` starts with `user_` (or is a bare UUID, not `org_`) will become **permanently unresolvable** once Phase 3.5 ships. Decide keep-as-dead / migrate / discard for each BEFORE deploying the backend.
3. **Phase 3.5 — backend `org_`-prefix guard → deploy to Railway (API).** Only after the checkpoint passes.

Shipping the backend guard first would fail-close any user currently relying on the personal-workspace fallback. Do not reorder.

---

## File Structure

**Repo `project-proculink` (frontend):**
- Create: `src/components/onboarding/orgGate.ts` — pure decision helper (no React, no Clerk imports). One responsibility: given membership/active-org/bypass state, decide what the gate should do.
- Create: `src/components/onboarding/orgGate.test.ts` — vitest unit tests for the helper (first unit test in `src/`).
- Create: `src/app/onboarding/select-organization/page.tsx` — client component that reads Clerk state, calls `decideOrgGate`, and renders create/select UI or forwards to the app.
- Modify: `src/middleware.ts` — add `redirectToCreateOrg` helper + the `!session.orgId` edge check inside the Clerk branch only.
- Modify: `src/app/sign-up/[[...sign-up]]/page.tsx:279-283` and `src/app/sign-in/[[...sign-in]]/page.tsx:282-286` — point post-auth redirects at the gate.
- Create: `tests/e2e/org-gate-bypass.spec.ts` — Playwright smoke proving the gate is skipped in QA-bypass mode (no regression to mock/QA flows).

**Repo `ProcuLink` (backend):**
- Modify: `ProcuLink.Api/Middleware/TenantResolutionMiddleware.cs:86-177` — remove the `sub` fallback; gate resolution/provisioning on an `org_` prefix.
- Create: `ProcuLink.Api.Tests/Middleware/TenantResolutionMiddlewareSubFallbackTests.cs` — fail-closed tests for sub-only and non-`org_` keys.
- Verify (maybe modify): `ProcuLink.Api.Tests/Middleware/TenantResolutionMiddlewareProvisionThrottleTests.cs` + `…EmitsOrgCreatedTests.cs` — confirm every test `org_id` value starts with `org_`.

---

# Phase 1 — Frontend org gate (repo: project-proculink)

> All Phase 1/2 work is in `C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink`. Run commands with **bun**.

### Task 0: Create the missing vitest setup file (blocker — do first)

`vitest.config.ts:10` declares `setupFiles: ["./src/test/setup.ts"]`, but **that file does not exist** — `bun run test` fails to initialize until it does. There are no unit tests in `src/` yet, so this is the first one. Create the setup file before Task 1.

**Files:**
- Create: `src/test/setup.ts`

- [ ] **Step 1: Create the setup file**

```ts
// src/test/setup.ts
// Vitest global setup. `globals: true` in vitest.config.ts exposes describe/it/
// expect without imports; this file wires Testing Library's jest-dom matchers
// (toBeInTheDocument, etc.) and runs a DOM cleanup after each test.
import "@testing-library/jest-dom/vitest";
import { afterEach } from "vitest";
import { cleanup } from "@testing-library/react";

afterEach(() => {
  cleanup();
});
```

- [ ] **Step 2: Verify the runner initializes**

Run: `bun run test`
Expected: vitest starts and reports "no test files found" (or runs once Task 1 lands) — NOT a setupFiles resolution error.

- [ ] **Step 3: Commit**

```bash
git add src/test/setup.ts
git commit -m "test: add vitest setup file referenced by vitest.config.ts"
```

---

### Task 1: Pure org-gate decision helper (TDD)

**Files:**
- Create: `src/components/onboarding/orgGate.ts`
- Test: `src/components/onboarding/orgGate.test.ts`

- [ ] **Step 1: Write the failing test**

```ts
// src/components/onboarding/orgGate.test.ts
import { describe, it, expect } from "vitest";
import { decideOrgGate } from "./orgGate";

describe("decideOrgGate", () => {
  it("skips the gate entirely in bypass (mock / QA-bypass) mode", () => {
    expect(
      decideOrgGate({ bypass: true, membershipsLoaded: false, membershipOrgIds: [], activeOrgId: null })
    ).toEqual({ kind: "skip" });
  });

  it("is ready when an org is already active", () => {
    expect(
      decideOrgGate({ bypass: false, membershipsLoaded: true, membershipOrgIds: ["org_1"], activeOrgId: "org_1" })
    ).toEqual({ kind: "ready" });
  });

  it("waits while memberships are still loading", () => {
    expect(
      decideOrgGate({ bypass: false, membershipsLoaded: false, membershipOrgIds: [], activeOrgId: null })
    ).toEqual({ kind: "loading" });
  });

  it("prompts creation when the user has zero orgs", () => {
    expect(
      decideOrgGate({ bypass: false, membershipsLoaded: true, membershipOrgIds: [], activeOrgId: null })
    ).toEqual({ kind: "create" });
  });

  it("auto-activates when the user has exactly one org and none active", () => {
    expect(
      decideOrgGate({ bypass: false, membershipsLoaded: true, membershipOrgIds: ["org_solo"], activeOrgId: null })
    ).toEqual({ kind: "activate", orgId: "org_solo" });
  });

  it("prompts selection when the user has multiple orgs and none active", () => {
    expect(
      decideOrgGate({ bypass: false, membershipsLoaded: true, membershipOrgIds: ["org_a", "org_b"], activeOrgId: null })
    ).toEqual({ kind: "select" });
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `bun run test -- orgGate`
Expected: FAIL — `Failed to resolve import "./orgGate"` / `decideOrgGate is not a function`.

- [ ] **Step 3: Write the minimal implementation**

```ts
// src/components/onboarding/orgGate.ts

/**
 * Pure decision model for the post-signup organization gate.
 *
 * The app is multi-tenant: every API call is scoped to the active Clerk
 * organization's id (the org_id JWT claim). A signed-in user with no active org
 * has no org_id, so the backend cannot resolve a tenant. This helper decides
 * what the gate route should do, given the current Clerk + env state. It holds
 * NO React/Clerk imports so it can be unit-tested in isolation (mirrors the
 * pure-derivation pattern in buildChecklistSteps.ts).
 */
export type OrgGateAction =
  | { kind: "skip" } // mock / QA-bypass: no Clerk session exists; render app
  | { kind: "loading" } // membership list not loaded yet
  | { kind: "ready" } // an org is already active; proceed to the app
  | { kind: "activate"; orgId: string } // exactly one org, none active → setActive it
  | { kind: "create" } // zero orgs → show create-organization UI
  | { kind: "select" }; // many orgs, none active → show the org picker

export interface OrgGateInput {
  /** isApiMockMode || isQaBypass — both run without a Clerk session. */
  bypass: boolean;
  /** True once Clerk's userMemberships query has resolved (data !== undefined). */
  membershipsLoaded: boolean;
  /** Org ids of every organization the user is a member of. */
  membershipOrgIds: string[];
  /** The currently active org id, or null when none is active. */
  activeOrgId: string | null;
}

export function decideOrgGate(input: OrgGateInput): OrgGateAction {
  if (input.bypass) return { kind: "skip" };
  if (input.activeOrgId) return { kind: "ready" };
  if (!input.membershipsLoaded) return { kind: "loading" };

  const ids = input.membershipOrgIds;
  if (ids.length === 0) return { kind: "create" };
  if (ids.length === 1) return { kind: "activate", orgId: ids[0] };
  return { kind: "select" };
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `bun run test -- orgGate`
Expected: PASS — 6 passed.

- [ ] **Step 5: Commit**

```bash
git add src/components/onboarding/orgGate.ts src/components/onboarding/orgGate.test.ts
git commit -m "feat(onboarding): pure org-gate decision helper + tests"
```

---

### Task 2: Onboarding route that creates/selects the org

**Files:**
- Create: `src/app/onboarding/select-organization/page.tsx`

This is the route the middleware (Task 3) redirects unrouted users to. It is a Client Component because it reads Clerk hooks and calls `setActive`.

- [ ] **Step 1: Write the page**

```tsx
// src/app/onboarding/select-organization/page.tsx
"use client";

import { Suspense, useEffect, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import {
  useAuth,
  useOrganization,
  useOrganizationList,
  CreateOrganization,
  OrganizationList,
} from "@clerk/nextjs";
import { isApiMockMode, isQaBypass } from "@/lib/api-client";
import { decideOrgGate } from "@/components/onboarding/orgGate";

// Centered shell matching the app background. (Polish: extract the shared
// <AuthShell> used by sign-in/sign-up into src/components/auth/AuthShell.tsx and
// reuse it here for full branding parity.)
function Shell({ children }: { children: React.ReactNode }) {
  return (
    <div
      className="flex min-h-dvh items-center justify-center p-6"
      style={{ background: "#F6F7FA" }}
    >
      {children}
    </div>
  );
}

function GateSpinner() {
  return (
    <div role="status" aria-live="polite" className="text-sm" style={{ color: "var(--ink-muted)" }}>
      Setting up your workspace…
    </div>
  );
}

/**
 * useSearchParams() MUST be inside a Suspense boundary or `next build` fails with
 * "useSearchParams() should be wrapped in a suspense boundary". The default
 * export provides the boundary; all gate logic lives in <SelectOrganizationInner>.
 */
export default function SelectOrganizationPage() {
  return (
    <Suspense fallback={<Shell><GateSpinner /></Shell>}>
      <SelectOrganizationInner />
    </Suspense>
  );
}

/**
 * Post-signup organization gate. The middleware sends any signed-in user with no
 * active org here. We then:
 *   - zero orgs  → render <CreateOrganization> (forces a real org to exist)
 *   - one org    → setActive it, then forward to the app (seamless for returners)
 *   - many orgs  → render <OrganizationList hidePersonal> to pick or create
 *   - active org → forward to the app
 * In mock / QA-bypass mode there is no Clerk session, so we skip straight to the
 * app to avoid starving the dev/e2e flows.
 */
function SelectOrganizationInner() {
  const router = useRouter();
  const params = useSearchParams();
  // Where to go once an org is active. Default to the app dashboard.
  const dest = params.get("redirect_url") || "/bridge";

  const { isLoaded: authLoaded, isSignedIn } = useAuth();
  const { organization } = useOrganization();
  // useOrganizationList returns a DISCRIMINATED UNION: in the not-loaded branch
  // `setActive` is undefined and `userMemberships.data` is undefined. The
  // `!setActive` / `?? []` / `data !== undefined` guards below are LOAD-BEARING —
  // do not "simplify" them away (mirrors the shipped AutoActivateOrg precedent).
  const { userMemberships, setActive } = useOrganizationList({
    userMemberships: { infinite: true },
  });

  // Bounded fallback: a Clerk outage / never-resolving membership list must never
  // strand the user on an infinite spinner (mirrors the app's bounded-fetch
  // pattern). After the deadline we surface a manual retry.
  const [timedOut, setTimedOut] = useState(false);
  useEffect(() => {
    const t = setTimeout(() => setTimedOut(true), 12_000);
    return () => clearTimeout(t);
  }, []);

  const bypass = isApiMockMode || isQaBypass;

  const action = decideOrgGate({
    bypass,
    membershipsLoaded: userMemberships.data !== undefined,
    membershipOrgIds: (userMemberships.data ?? []).map((m) => m.organization.id),
    activeOrgId: organization?.id ?? null,
  });

  useEffect(() => {
    if (bypass) {
      router.replace(dest);
      return;
    }
    // Signed-out visitor hitting this route directly → send to sign-in.
    if (authLoaded && !isSignedIn) {
      router.replace(`/sign-in?redirect_url=${encodeURIComponent(dest)}`);
      return;
    }
    if (action.kind === "ready") {
      router.replace(appendOrgSetFlag(dest));
      return;
    }
    if (action.kind === "activate" && setActive) {
      // Forward with a one-shot org_set flag so the edge middleware does NOT
      // bounce us back here if the org_id cookie hasn't propagated to the edge
      // yet (setActive updates the client session a tick before the __session
      // cookie the middleware reads). AutoActivateOrg in (app) finishes the job.
      void setActive({ organization: action.orgId }).then(() => {
        router.replace(appendOrgSetFlag(dest));
      });
    }
  }, [bypass, authLoaded, isSignedIn, action, dest, router, setActive]);

  // Never spin forever: if memberships never resolved, offer a manual retry.
  if (timedOut && (action.kind === "loading" || action.kind === "activate")) {
    return (
      <Shell>
        <div className="text-center text-sm" style={{ color: "var(--ink-muted)" }}>
          <p>Still setting things up…</p>
          <button type="button" onClick={() => router.refresh()} className="mt-3 underline">
            Retry
          </button>
        </div>
      </Shell>
    );
  }

  if (action.kind === "create") {
    return (
      <Shell>
        <CreateOrganization
          afterCreateOrganizationUrl={appendOrgSetFlag(dest)}
          skipInvitationScreen
        />
      </Shell>
    );
  }
  if (action.kind === "select") {
    return (
      <Shell>
        <OrganizationList
          hidePersonal
          afterSelectOrganizationUrl={appendOrgSetFlag(dest)}
          afterCreateOrganizationUrl={appendOrgSetFlag(dest)}
        />
      </Shell>
    );
  }

  // loading / activate / ready / skip → neutral spinner while the effect resolves.
  return <Shell><GateSpinner /></Shell>;
}

// Appends a one-shot org_set=1 marker the middleware consumes to skip exactly one
// !orgId bounce after the org becomes active, dodging the cookie-propagation race.
function appendOrgSetFlag(dest: string): string {
  const sep = dest.includes("?") ? "&" : "?";
  return `${dest}${sep}org_set=1`;
}
```

- [ ] **Step 2: Type-check / build the route**

Run: `bun run build`
Expected: PASS — the new route compiles. (Pre-existing Sentry/Browserslist warnings are unrelated and acceptable.)

- [ ] **Step 3: Commit**

```bash
git add src/app/onboarding/select-organization/page.tsx
git commit -m "feat(onboarding): /onboarding/select-organization gate route"
```

---

### Task 3: Edge middleware org gate

**Files:**
- Modify: `src/middleware.ts`

The gate lives ONLY in the `clerkMiddleware` branch — never in the QA-bypass or no-Clerk fallback branches. `/onboarding/...` is intentionally NOT in `isProtectedRoute`, so the `if (!isProtectedRoute(req)) return;` early-return means the gate route itself is never org-gated → no redirect loop.

- [ ] **Step 1: Add the `redirectToCreateOrg` helper**

Add immediately after `redirectToLocalSignIn` (currently `src/middleware.ts:38-43`):

```ts
function redirectToCreateOrg(req: NextRequest) {
  const url = new URL("/onboarding/select-organization", req.url);
  url.searchParams.set("redirect_url", req.nextUrl.pathname + req.nextUrl.search);
  return NextResponse.redirect(url);
}
```

- [ ] **Step 2: Add the `!orgId` check inside the Clerk branch**

Replace the current Clerk handler body (currently `src/middleware.ts:48-54`):

```ts
    ? clerkMiddleware(async (auth, req) => {
        if (!isProtectedRoute(req)) return;
        if (isClerkHandshake(req)) return NextResponse.next();

        const session = await auth();
        if (!session.userId) return redirectToLocalSignIn(req);
      })
```

with:

```ts
    ? clerkMiddleware(async (auth, req) => {
        if (!isProtectedRoute(req)) return;
        if (isClerkHandshake(req)) return NextResponse.next();

        const session = await auth();
        if (!session.userId) return redirectToLocalSignIn(req);

        // Signed in but no active Clerk organization → force org creation/selection
        // before any tenant-scoped app route. The backend cannot resolve a tenant
        // without the org_id claim, and a missing org used to fall back to a
        // per-user "Personal workspace" tenant (data fragmentation). The gate route
        // (/onboarding/...) is not in isProtectedRoute, so this never self-loops.
        //
        // Two escape hatches that must NOT be bounced:
        //  - org_set=1: a one-shot flag the gate appends right after setActive, for
        //    the window where the client session has an org but the edge cookie
        //    hasn't caught up. AutoActivateOrg in (app) completes activation.
        //  - /admin: allowlist-gated server-side, not org-scoped, so a platform
        //    admin/ops account may operate without an org.
        const justSetOrg = req.nextUrl.searchParams.has("org_set");
        const isAdmin = req.nextUrl.pathname.startsWith("/admin");
        if (!session.orgId && !justSetOrg && !isAdmin) {
          return redirectToCreateOrg(req);
        }
      })
```

- [ ] **Step 3: Build to verify it compiles**

Run: `bun run build`
Expected: PASS. (`session.orgId` is a typed property on the Clerk auth object.)

- [ ] **Step 4: Commit**

```bash
git add src/middleware.ts
git commit -m "feat(auth): edge gate — signed-in users with no active org are forced to create/select one"
```

---

### Task 4: Point post-auth redirects at the gate

**Files:**
- Modify: `src/app/sign-up/[[...sign-up]]/page.tsx:279-283`
- Modify: `src/app/sign-in/[[...sign-in]]/page.tsx:282-286`

This removes the post-auth → `/bridge` → (redirect) → gate double-hop for new users; the gate forwards returning users to `/bridge` instantly.

- [ ] **Step 1: Update the sign-up redirect**

In `src/app/sign-up/[[...sign-up]]/page.tsx`, change:

```tsx
      <SignUp
        appearance={clerkAppearance}
        fallbackRedirectUrl="/bridge"
        signInFallbackRedirectUrl="/bridge"
      />
```

to:

```tsx
      <SignUp
        appearance={clerkAppearance}
        fallbackRedirectUrl="/onboarding/select-organization"
        signInFallbackRedirectUrl="/onboarding/select-organization"
      />
```

- [ ] **Step 2: Update the sign-in redirect**

In `src/app/sign-in/[[...sign-in]]/page.tsx`, change:

```tsx
      <SignIn
        appearance={clerkAppearance}
        fallbackRedirectUrl="/bridge"
        signUpFallbackRedirectUrl="/bridge"
      />
```

to:

```tsx
      <SignIn
        appearance={clerkAppearance}
        fallbackRedirectUrl="/onboarding/select-organization"
        signUpFallbackRedirectUrl="/onboarding/select-organization"
      />
```

- [ ] **Step 3: Reconcile the three competing post-signup destinations**

There are up to THREE places that decide where a user lands after auth; they must agree or the gate gets bypassed:
1. The Clerk **Dashboard** `afterSignUpUrl` / `afterSignInUrl` settings (CLAUDE.md lists "Clerk post-signup redirect" as a pending founder-config item). **Dashboard settings override the JSX `fallbackRedirectUrl` props.**
2. The `fallbackRedirectUrl` props edited above.
3. The existing marketing route `src/app/(marketing)/welcome/page.tsx` (reads `?upgraded`, greets by first name) — the previously-intended post-signup landing.

- [ ] Audit the Clerk Dashboard (`golden-alpaca-43`) for any set `afterSignUpUrl` / `afterSignInUrl`. If set, either clear them (so the JSX props win) OR set them to `/onboarding/select-organization` too. Record the chosen state.
- [ ] Decide `/welcome`'s fate: either keep it in the chain for brand-new users (gate forwards first-time creators to `/welcome`, which then links to `/bridge`) or consciously retire it. Note the decision; do not silently orphan it.
- [ ] Even if a stale Dashboard setting points at `/bridge`, the edge gate (Task 3) still catches `!orgId` on the first protected route — so the gate cannot be fully bypassed, but the redirect chain should still be made coherent to avoid an extra hop/flash.

- [ ] **Step 4: Build**

Run: `bun run build`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add "src/app/sign-up/[[...sign-up]]/page.tsx" "src/app/sign-in/[[...sign-in]]/page.tsx"
git commit -m "feat(auth): route post-auth landing through the org gate"
```

---

# Phase 2 — Frontend verification (repo: project-proculink)

### Task 5: QA-bypass regression smoke (automated)

The real new-user → create-org flow requires a live Clerk session and is verified manually on preview (Task 6). The automated guard we CAN add is: in QA-bypass mode the gate is skipped and the app still renders — proving mock/QA/e2e flows did not regress.

**Files:**
- Create: `tests/e2e/org-gate-bypass.spec.ts`

- [ ] **Step 1: Write the spec**

```ts
// tests/e2e/org-gate-bypass.spec.ts
import { test, expect } from "@playwright/test";

// Runs against the QA-bypass dev server (PROCULINK_QA_BYPASS_AUTH=true,
// NEXT_PUBLIC_QA_BYPASS_AUTH=true). In that mode there is no Clerk session, so
// the org gate must NOT redirect to /onboarding/select-organization — the app
// shell must render directly. Guards the mock/QA/e2e flows against the new gate.
test("QA-bypass: protected route renders without the org gate", async ({ page }) => {
  await page.goto("/bridge");
  await expect(page).not.toHaveURL(/\/onboarding\/select-organization/);
  await expect(page).toHaveURL(/\/bridge/);
});
```

- [ ] **Step 2: Run it against the QA-bypass dev server**

Per the worktree visual-QA recipe (memory: `project-worktree-visual-qa`), start a dev server with QA-bypass + a placeholder Clerk key, then:

Run: `bun run test:e2e -- tests/e2e/org-gate-bypass.spec.ts`
Expected: PASS — stays on `/bridge`, no redirect to the gate.

- [ ] **Step 3: Run the unit suite + build once more**

Run: `bun run test` then `bun run build`
Expected: unit tests green (incl. the 6 `orgGate` cases); build clean.

- [ ] **Step 4: Commit**

```bash
git add tests/e2e/org-gate-bypass.spec.ts
git commit -m "test(auth): org gate is skipped in QA-bypass mode"
```

### Task 6: Live preview verification (manual, real Clerk)

- [ ] **Step 1:** Deploy the branch to a Vercel preview (or run `bun run dev` against the real Clerk dev instance `golden-alpaca-43`).
- [ ] **Step 2:** Sign up a throwaway user. Expected: after sign-up you are sent to `/onboarding/select-organization` and shown **Create organization** (you cannot reach `/bridge` without creating one). Confirm the browser never reaches `/bridge` until the org is created.
- [ ] **Step 3:** Create an org. Expected: forwarded to `/bridge`; the dashboard loads with no "Organisation not resolved" errors; `GET /api/...` calls carry the org and succeed.
- [ ] **Step 4:** Sign out and back in. Expected: the user transits `/onboarding/select-organization` **briefly** (the edge sees `orgId=null` on the first request until Clerk re-activates the last org), the gate auto-activates the single org, then forwards to `/bridge`. Verify: (a) **no** flash of the "Create organization" UI (a returner with one org must show only the neutral spinner, never `<CreateOrganization>`), and (b) **no redirect loop** — confirm the `org_set=1` one-shot flag lets the forward through even if the org_id cookie lags. This is "near-seamless," not zero-transit; if the flash/loop appears, the `org_set` handshake (Task 2 `appendOrgSetFlag` + Task 3 `justSetOrg`) is miswired.
- [ ] **Step 5:** Record the result (screenshots) in the PR description.

---

# Phase 3 — Pre-backend checkpoint (no code)

### Task 7: Audit prod for sub-keyed tenants BEFORE the backend guard

Once Phase 3.5 ships, any `Organisation` whose `ClerkOrgId` does not start with `org_` becomes permanently unresolvable. Decide what happens to each before deploying.

- [ ] **Step 1:** Query the prod DB (Neon) for sub-keyed orgs:

```sql
SELECT id, clerk_org_id, name, slug, plan, account_status, created_at
FROM organisations
WHERE clerk_org_id NOT LIKE 'org\_%';
```

- [ ] **Step 2:** For each row, RESOLVE it — do **not** "leave as dead". A sub-keyed row left in place becomes resolvable-by-old-code-only and silently strands whatever it holds; the known prod row `370ca357` ("Personal workspace") was auto-provisioned on the **founder's own** first authenticated request and may carry the admin-override/test tenant ("Dim's Organization"). After Phase 3.5, any direct-token / script / browser-file-injection path that doesn't pass the edge gate will fail closed against it. So, per row:
  - (a) **has real/founder data** → migrate into a real `org_…` tenant the owner controls (Phase 5; prefer the single-row re-key fast path for `370ca357` since it has no `org_` twin yet) BEFORE the backend deploy.
  - (b) **truly throwaway / empty** → hard-`DELETE` the row (not "leave dead"), so the count of sub-keyed orgs is provably zero.
- [ ] **Step 3:** Acceptance line — confirm in the PR: *"every sub-keyed `organisations` row is either migrated to an `org_…` tenant or DELETED; zero rows remain resolvable only by the old fallback."* Do NOT proceed to Phase 3.5 until this holds.

---

# Phase 3.5 — Backend `org_`-prefix guard (repo: ProcuLink)

> All Phase 3.5 work is in `C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink`.

> **Blast-radius (verified, no action needed):** removing the `sub` fallback is **Clerk/JWT-only**. The API-key / ingress path does NOT use it — `ApiKeyAuthHandler` (`ProcuLink.Api/Auth/ApiKeyAuthHandler.cs:80`) resolves the tenant independently by looking up the API key's `OrganisationId` in the DB and setting `HttpContext.Items[CurrentTenantService.Items.OrganisationId]` directly; `IngressController` reads that same item, never the claims. So Zapier/Make/custom-webhook/REST-ingress clients are unaffected (and already carry a real org via their key). `sub` itself is still read (`:86`) and used as the analytics `userId` (`:158`) — no unused-variable break after the fallback is removed.

### Task 8: Failing tests — sub-only and non-`org_` keys fail closed (TDD)

**Files:**
- Create: `ProcuLink.Api.Tests/Middleware/TenantResolutionMiddlewareSubFallbackTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Middleware;
using ProcuLink.Api.Services;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Middleware;

/// <summary>
/// The sub-fallback was removed: a request without a real Clerk ORGANISATION id
/// (org_…) must NOT silently provision a per-user "Personal workspace" tenant.
/// It resolves no tenant and fails closed downstream (same shape as the throttle
/// path). The frontend org gate forces org creation before any tenant-scoped call.
/// </summary>
public class TenantResolutionMiddlewareSubFallbackTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task InvokeAsync_SubOnly_NoOrgIdClaim_DoesNotProvision_FailsClosed()
    {
        await using var db = NewDb();
        var analytics = new FakeAnalyticsService();
        var middleware = new TenantResolutionMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<TenantResolutionMiddleware>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", "user_LONELY"),
        }, authenticationType: "test"));

        await middleware.InvokeAsync(ctx, db, analytics);

        Assert.Equal(0, await db.Organisations.CountAsync());
        Assert.Empty(analytics.CapturedEvents);
        Assert.False(ctx.Items.ContainsKey(CurrentTenantService.Items.OrganisationId));
    }

    [Fact]
    public async Task InvokeAsync_NonOrgPrefixedTenantKey_DoesNotProvision()
    {
        await using var db = NewDb();
        var analytics = new FakeAnalyticsService();
        var middleware = new TenantResolutionMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<TenantResolutionMiddleware>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            // Defensive: a non-org_ value in org_id (e.g. a stray uuid) must not provision.
            new Claim("org_id", "1d3c9e7a-0000-4000-8000-000000000000"),
            new Claim("sub", "user_X"),
        }, authenticationType: "test"));

        await middleware.InvokeAsync(ctx, db, analytics);

        Assert.Equal(0, await db.Organisations.CountAsync());
        Assert.False(ctx.Items.ContainsKey(CurrentTenantService.Items.OrganisationId));
    }

    [Fact]
    public async Task InvokeAsync_RealOrgId_StillProvisions()
    {
        await using var db = NewDb();
        var analytics = new FakeAnalyticsService();
        var middleware = new TenantResolutionMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<TenantResolutionMiddleware>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("org_id", "org_REAL_123"),
            new Claim("org_slug", "acme-co"),
            new Claim("sub", "user_abc"),
        }, authenticationType: "test"));

        await middleware.InvokeAsync(ctx, db, analytics);

        Assert.Equal(1, await db.Organisations.CountAsync());
        Assert.True(ctx.Items.ContainsKey(CurrentTenantService.Items.OrganisationId));
    }
}
```

- [ ] **Step 2: Run to verify the two fail-closed tests FAIL (current behaviour still provisions)**

Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --filter "FullyQualifiedName~TenantResolutionMiddlewareSubFallbackTests"`
Expected: `InvokeAsync_SubOnly...` and `InvokeAsync_NonOrgPrefixedTenantKey...` FAIL (an org IS currently created); `InvokeAsync_RealOrgId_StillProvisions` PASSES.

---

### Task 9: Remove the sub-fallback; gate on the `org_` prefix

**Files:**
- Modify: `ProcuLink.Api/Middleware/TenantResolutionMiddleware.cs:95-106` (and the log at `:152-154`)

- [ ] **Step 1: Replace the stale comment + the fallback block together**

Replace (currently `:90-106` — note this also removes the stale comment at `:90-94` that still describes the sub fallback):

```csharp
            // Prefer the Clerk org_id claim. When no Clerk organisation is active in
            // the session (e.g. personal account, or the session hasn't activated the
            // org yet) fall back to the user's sub claim so each user still maps to a
            // tenant. Clerk user IDs start with "user_" and org IDs with "org_", so
            // they share no namespace and won't collide.
            var clerkOrgId = context.User.FindFirst("org_id")?.Value;
            var orgSlug    = context.User.FindFirst("org_slug")?.Value;
            var fellBackToUser = false;

            if (string.IsNullOrEmpty(clerkOrgId) && !string.IsNullOrEmpty(sub))
            {
                clerkOrgId     = sub;
                orgSlug        = "Personal workspace";
                fellBackToUser = true;
            }

            if (!string.IsNullOrEmpty(clerkOrgId))
            {
```

with:

```csharp
            // Only a real Clerk ORGANISATION (org_…) resolves or provisions a tenant.
            // A user with no active org carries no org_id. We deliberately do NOT fall
            // back to the user's sub claim: that silently minted a per-user "Personal
            // workspace" tenant which could never be merged into the team org the user
            // later created (data fragmentation). Such a request continues UNRESOLVED
            // and fails closed downstream (same as the throttle path). The frontend org
            // gate forces org creation before any tenant-scoped call is made.
            // See docs/superpowers/plans/2026-06-30-force-org-creation.md.
            var clerkOrgId = context.User.FindFirst("org_id")?.Value;
            var orgSlug    = context.User.FindFirst("org_slug")?.Value;

            if (!string.IsNullOrEmpty(clerkOrgId)
                && clerkOrgId.StartsWith("org_", StringComparison.Ordinal))
            {
```

- [ ] **Step 2: Fix the log line that referenced `fellBackToUser`**

Replace (currently `:152-154`):

```csharp
                    _logger.LogInformation(
                        "Auto-provisioned organisation '{Name}' (TenantKey={ClerkOrgId}, FellBackToUser={Fallback}).",
                        newOrg.Name, clerkOrgId, fellBackToUser);
```

with:

```csharp
                    _logger.LogInformation(
                        "Auto-provisioned organisation '{Name}' (TenantKey={ClerkOrgId}).",
                        newOrg.Name, clerkOrgId);
```

- [ ] **Step 3: Verify `sub` is still used where it should be**

`sub` is still read at `:86` and passed as `userId` to the `org_created` analytics event at `:158`. Leave both. Only the fallback assignment is removed. (If the compiler warns that `sub` is now unused on some path, it is not — it is still used for the analytics `userId`.)

- [ ] **Step 4: Run the new tests — all three pass**

Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --filter "FullyQualifiedName~TenantResolutionMiddlewareSubFallbackTests"`
Expected: PASS — 3 passed.

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Api/Middleware/TenantResolutionMiddleware.cs ProcuLink.Api.Tests/Middleware/TenantResolutionMiddlewareSubFallbackTests.cs
git commit -m "feat(tenancy): only org_-prefixed Clerk orgs resolve a tenant; drop personal-workspace sub fallback"
```

---

### Task 10: Confirm existing middleware tests still pass

The guard requires every existing test `org_id` value to start with `org_`. The values seen in investigation (`org_TEST_123`, `org_EXISTING`, `org_NEWUSER`) already do, but verify exhaustively.

**Files:**
- Verify: `ProcuLink.Api.Tests/Middleware/TenantResolutionMiddlewareProvisionThrottleTests.cs`
- Verify: `ProcuLink.Api.Tests/Middleware/TenantResolutionMiddlewareEmitsOrgCreatedTests.cs`

- [ ] **Step 1: Grep for any non-`org_` org_id claim in the test files**

Run (from `ProcuLink/`): search both files for `new Claim("org_id",` and confirm every value literal starts with `org_`.
Expected: every `org_id` claim value starts with `org_`. If any does not, change it to an `org_`-prefixed value (these stand in for real Clerk orgs, so the prefix is correct) and note it in the commit.

- [ ] **Step 2: Run the full middleware test set**

Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --filter "FullyQualifiedName~TenantResolutionMiddleware"`
Expected: PASS — all `…EmitsOrgCreatedTests`, `…ProvisionThrottleTests`, and `…SubFallbackTests` green.

- [ ] **Step 3: Run the whole backend suite**

Run: `dotnet test ProcuLink.slnx`
Expected: PASS — full suite green (no regressions in controllers/services that construct `Organisation`).

- [ ] **Step 4: Commit (only if Step 1 changed a test value)**

```bash
git add ProcuLink.Api.Tests/Middleware/
git commit -m "test(tenancy): ensure middleware test org ids are org_-prefixed"
```

---

# Phase 4 — Clerk-native hardening (FOUNDER ACTION, no code)

This is the durable, belt-and-suspenders upgrade. It is **not self-serve** for this app and is therefore tracked as a founder task, not a code task.

- [ ] **Step 1:** In the Clerk Dashboard (`golden-alpaca-43`): **Organizations → Settings → Enable Organizations**, set **Membership required**.
- [ ] **Step 2:** Attempt to turn **Personal Accounts OFF**. For apps created before 2025-08-22 this is blocked self-serve — **open a Clerk support ticket** requesting the disabled-personal-accounts / `choose-organization` session-task migration. Reference: https://clerk.com/docs/guides/configure/session-tasks and the 2025-08-22 changelog.
- [ ] **Step 3 (after Clerk enables it):** Add `taskUrls={{ "choose-organization": "/onboarding/select-organization" }}` to `<ClerkProvider>` so Clerk's native task routes to our existing gate page. The manual middleware gate from Task 3 stays as defense-in-depth (harmless once Clerk also enforces). This is the only follow-up code, gated on Clerk support.
- [ ] **Step 4 (QA prerequisite, independent):** Create two distinct Clerk orgs with sessions and run the cross-tenant isolation test from `docs/qa/2026-06-29-prelaunch-audit-and-test-plan.md`.

---

# Phase 5 — Deferred: merge an already-fragmented personal tenant (build on demand)

Only needed if Task 7 found a sub-keyed org with real customer data. Documented here so it is not re-discovered later.

**Fast path (preferred when there is no twin yet — covers the known prod row `370ca357`):** if the personal org has NOT yet had a colliding `org_…` twin auto-provisioned, a **single-row re-key** is far simpler and safer than a multi-table merge:

```sql
-- Run in the window AFTER the team org is created in Clerk but BEFORE the owner's
-- first API call carrying that org_id (which would auto-provision the twin). Single
-- row, all child FKs reference organisations.id (unchanged) so they follow for free.
UPDATE organisations
SET clerk_org_id = :newClerkOrgId   -- the org_… id of the team org from Clerk
WHERE id = :personalOrganisationId; -- e.g. 370ca357…
-- Optionally regenerate slug if you want it to read like the team name.
```

The ordering rule is the catch: the re-key must land between "team org created in Clerk" and "first API call with that `org_id`", or the twin already exists and you fall back to the merge below. For the single known prod row this is trivially serialisable — do it as a **manual one-off**, not an endpoint.

**Merge path (when the twin already exists):** once the user has already hit the API with the team org, the backend has auto-provisioned a **second** `Organisation` row (`org_…`). `ClerkOrgId` has a UNIQUE index, so you cannot re-key the personal row to the new `org_…` (collision). The clean operation is then **re-point child rows from the source (personal) org to the target (team) org inside one transaction, then delete the empty source org.**

**Recommended design:** an admin endpoint `POST /api/admin/organisations/merge { sourceOrganisationId, targetOrganisationId }` modeled on the existing admin bulk-erase controller (memory: `project-bulk-erase-endpoint`), org-scoped, refuses if either id is missing or equal. It re-points every tenant-scoped table from source → target, then deletes the source row.

**Tables to re-point** (derive the authoritative list at build time by grepping `OrganisationId` across `ProcuLink.Core/Entities/` — the `Organisation` navigation set names them: `Suppliers`, `PurchaseOrders`, `ItemMappings`, `OutboundArtifacts`, `DeliveryAttempts`, `AuditEvents`, `ApiKeys`, `IntegrationSubscriptions`, plus connection/revision, delivery-config, rules, templates, invoice/line, and ASN tables). Wrap the whole re-point + delete in ONE `BeginTransactionAsync`/`Commit`, and remember `ExecuteUpdate`/`ExecuteDelete` auto-commit immediately (memory: `project-executeupdate-autocommit-window`) — so enlist them all on the same transaction. Add an `AuditEvent` recording the merge. Write an ingest→merge→reload round-trip test on **real Postgres** (EF InMemory does not enforce FKs — memory: `project-inmemory-masks-postgres-fk`).

This phase is intentionally NOT implemented now — open it only when a real fragmented customer appears.

---

# Phase 6 — Docs, analytics & cleanup (after the gate is live)

Small but real follow-ons surfaced in review. Do after Phase 1+2 are deployed and verified.

### Task 11: Gate funnel analytics

Forced org creation is a new drop-off point; instrument it so the conversion hit is measurable.

- [ ] **Step 1:** Emit analytics on the gate route (frontend `analytics`/PostHog, reusing the existing client). Events: `org_gate_shown` (Inner mounts with `action.kind === "create"`), `org_gate_org_created` (after `<CreateOrganization>` completes / forward fires), `org_gate_abandoned` (route unmount before an org is active). Keep it provider-neutral, same pattern as existing FE events.
- [ ] **Step 2:** Confirm the backend `org_created` event still fires exactly once per real org (it now only fires on `org_`-prefixed provisioning — verified). No backend analytics change needed.

### Task 12: Reconcile AutoActivateOrg

- [ ] **Step 1:** Add a comment at `src/app/(app)/layout.tsx:23` marking `AutoActivateOrg` as a **defense-in-depth fallback** now that the edge gate guarantees an active org before `(app)` mounts (and the `org_set=1` hand-off relies on it to finish activation). It exits early when an org is active, so it cannot double-fire `setActive` with the gate — verify this in the live test (a user landing directly on an `(app)` URL while signed-in-without-org is bounced to the gate by the edge *before* `(app)` mounts, so the two never run concurrently).

### Task 13: Update the docs

- [ ] **Step 1:** `CLAUDE.md` — replace the "personal-workspace fallback" description and resolve the pending "Clerk post-signup redirect" config item (now wired to the gate / Dashboard per Task 4 Step 3).
- [ ] **Step 2:** `STATUS.md:532` — update the `370ca357` "Personal workspace" note to record its resolution (deleted or merged, per Task 7).
- [ ] **Step 3:** `docs/qa/2026-06-29-prelaunch-audit-and-test-plan.md` — mark the "personal-workspace `sub` fallback can fragment a B2B team" prerequisite (line ~47) as addressed; keep the two-org cross-tenant isolation test (Phase 4 Step 4) as the remaining live check.

---

## Self-Review

**1. Spec coverage** (against the 3-step fix agreed with the founder + adversarial-review hardening):
- Step 1 "force org creation/selection post-signup" → Phase 1 (Tasks 0–4) + Phase 4 (Clerk-native). ✔
- Step 2 "tighten the backend fallback" → Phase 3.5 (Tasks 8–10). ✔
- Step 3 "migration for already-fragmented data" → Phase 5 (deferred; single-row re-key fast path + merge path). ✔
- Sequencing requirement (FE before BE) → Rollout section + Phase 3 checkpoint (Task 7, now delete-or-merge, never leave-dead). ✔
- QA/mock safety → Task 1 `skip` branch, Task 3 (gate only in Clerk branch), Task 5 (automated proof). ✔
- Returning-user cookie-lag loop → `org_set=1` one-shot hand-off (Task 2 `appendOrgSetFlag` + Task 3 `justSetOrg`); infinite-spinner guard (Task 2 bounded timeout). ✔
- Next 15 build rule → `useSearchParams` under `<Suspense>` (Task 2). ✔
- vitest bootstrap → `src/test/setup.ts` (Task 0). ✔
- Redirect-chain coherence (Clerk Dashboard `afterSignUpUrl` overrides JSX props; `/welcome`) → Task 4 Step 3. ✔
- Org-less platform admin → `/admin` exempted from the `!orgId` bounce (Task 3). ✔
- API-key/ingress blast-radius → verified safe (Phase 3.5 note). ✔
- Funnel visibility + docs drift → Phase 6 (Tasks 11–13). ✔

**2. Placeholder scan:** No "TBD"/"add error handling"/"similar to". Every code step shows full code. Phase 5 lists a grep-derived table set because it is explicitly deferred (not executed now). ✔

**3. Type consistency:** `decideOrgGate` / `OrgGateAction` / `OrgGateInput` names match across Task 1 (def) and Task 2 (use). The `activate` action carries `orgId` in both. Middleware uses `session.orgId` / `session.userId` consistently. Backend guard `clerkOrgId.StartsWith("org_", StringComparison.Ordinal)` matches the test claim values (`org_REAL_123`, `org_TEST_123`, `org_EXISTING`, `org_NEWUSER`). ✔

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-06-30-force-org-creation.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

**Which approach?**
