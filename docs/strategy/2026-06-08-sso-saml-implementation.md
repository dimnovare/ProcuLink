# SSO / SAML — implementation reference (2026-06-08)

Status of the capability: **plan-gating is real; the Clerk SAML connection is NOT yet
configured.** Per the offer⇔works rule, SSO must be presented as "available on
Enterprise / contact us", never as "SSO works", until a Clerk Enterprise Connection
is configured for the customer org AND the prod Clerk instance cutover has happened.

Source of the plan: `docs/strategy/2026-06-08-program-design.md` → section `sso-saml`.

---

## 1. Architecture summary — why the backend barely changes

Auth is Clerk (`@clerk/nextjs`). Enterprise SSO (SAML/OIDC) is a **Clerk-native**
capability: connections are configured **per Clerk Organization** in the Clerk
Dashboard and enforced per email domain. JIT provisioning auto-adds the SSO user
to that Organization with the Organization's default role on first sign-in.

The decisive fact: **a SAML login produces the same session JWT shape as a normal
login.** The frontend sends the default Clerk session token (no custom jwt-template
— `window.Clerk.session.getToken()` with no argument) as a Bearer token. The
backend:

- validates that JWT via `AddJwtBearer` (Authority = `Clerk:Authority`,
  `ValidateAudience=false`, `MapInboundClaims=false`, `NameClaimType="sub"`) in
  `ProcuLink.Api/Program.cs`, with token-binding enforced by checking `azp` against
  the configured frontend origins in `OnTokenValidated`
  (`ProcuLink.Api/Auth/ClerkTokenValidation.IsAuthorizedParty`);
- resolves the tenant from the `org_id` claim (falling back to `sub` for personal
  workspaces) and reads `org_slug` for the name, auto-provisioning an unseen org as
  a 14-day Pilot, in `ProcuLink.Api/Middleware/TenantResolutionMiddleware.cs`.

A SAML-authenticated user inside a Clerk Organization therefore maps to the right
tenant with **zero** auth/token code change. **Do NOT touch the JWT validation or
tenant-resolution code for SSO.** The only backend work is presentation/policy:
plan-gate SSO to Enterprise.

---

## 2. What is CODE vs CONFIG

| Concern | Code or Config | Where |
|---|---|---|
| Validate the SAML-login JWT | already CODE (unchanged) | `Program.cs` `AddJwtBearer` + `ClerkTokenValidation` |
| Map SAML user → ProcuLink tenant | already CODE (unchanged) | `TenantResolutionMiddleware` |
| Plan-gate SSO to Enterprise | CODE (this change) | `BillingFeature.Sso` + `PlanConstants.MinimumPlan` |
| Surface SSO availability to the UI | CODE (this change) | `BillingStatus.SsoAvailable` (computed) |
| Enable a SAML connection for a customer | **CONFIG** (Clerk Dashboard) | per Clerk Organization — see §4 |
| Set the customer org to Enterprise plan | CONFIG/ops | admin limit override or Stripe/manual plan set |
| Prod Clerk instance cutover | CONFIG/ops (prerequisite) | Clerk Dashboard (currently on the dev instance) |

There is **no migration** in this change. The gate's join key is the existing
`Organisation.Plan` column. We deliberately did NOT add `Organisation.SsoEnforced`
— it is optional/later (internal audit only) and not needed for the capability.

---

## 3. The backend change that shipped (this branch)

Pure plan-gating metadata. No auth, tenancy, delivery, transform, or parse changes.
No new EF query, no raw SQL, no migration.

1. **`ProcuLink.Core/Constants/BillingFeature.cs`** — added enum member `Sso`.
2. **`ProcuLink.Core/Constants/PlanConstants.cs`** — mapped
   `MinimumPlan[BillingFeature.Sso] = Enterprise`. This reuses the existing ordinal
   plan-rank comparison in `PlanHasFeature(plan, feature)`, so SSO behaves exactly
   like the other Enterprise-only gates (`ErpConnectors`, `CustomSupplierRules`,
   `SlaOnboarding`). `HasFeatureAsync(orgId, BillingFeature.Sso, ct)` works for free
   via the central map — a future SSO controller can call it with no extra wiring.
3. **`ProcuLink.Core/Services/BillingStatus.cs`** — added `bool SsoAvailable = false`
   as the last positional field (default keeps existing construction sites correct).
4. **`ProcuLink.Api/Services/StripeBillingService.GetStatusAsync`** — computes
   `SsoAvailable = PlanConstants.PlanHasFeature(plan, BillingFeature.Sso)` and passes
   it into the DTO. This is option (a) from the program design: no Clerk Backend API
   dependency. `GET /api/billing/status` returns the `BillingStatus` record directly
   (`BillingController.GetStatus`), so the field is surfaced to the frontend with no
   controller change.

Important semantics: **`SsoAvailable == true` means the org's PLAN includes SSO. It
does NOT assert a SAML connection is configured.** The actual connection is
provisioned per-org in the Clerk Dashboard (§4). The frontend uses `SsoAvailable`
only to choose between the gated upsell state (false) and the
available/contact-us/configured state (true).

Tests (in `ProcuLink.Api.Tests`):
- `Constants/PlanFeatureGateTests.cs` — `Sso_IsAvailableOnEnterprise`,
  `Sso_IsRestrictedBelowEnterprise` (Theory over Pilot/Growth/Operations/
  Integration/**Distributor**), `Sso_IsIncludedInEnterpriseFeatureSet_AndAbsentFromDistributor`.
  The Distributor case is the important guard: Distributor outranks Integration but is
  still not Enterprise, so SSO must stay gated.
- `Services/StripeBillingServicePricingTests.cs` —
  `GetStatus_Enterprise_ReportsSsoAvailableTrue`,
  `GetStatus_BelowEnterprise_ReportsSsoAvailableFalse` (Theory over the five non-Enterprise plans).

---

## 4. Clerk Dashboard steps to enable a SAML connection for a customer org

These are CONFIG, done once per Enterprise customer (white-glove). Do them on the
**production** Clerk instance (the dev `golden-alpaca-43` instance is not the place
to configure a real customer's SSO — the prod cutover is a prerequisite).

1. **Confirm the customer is on Enterprise** in ProcuLink first. A freshly
   SSO-provisioned Clerk Organization auto-provisions as Pilot in ProcuLink
   (`TenantResolutionMiddleware`), so set the org's plan to Enterprise (admin limit
   override and/or Stripe/manual) as part of onboarding, or the customer is stuck on
   Pilot limits.
2. **Clerk Dashboard → Enterprise Connections → New connection → SAML**, scoped to
   the customer's Clerk **Organization** (organization-level, not instance-level).
3. **Exchange SAML metadata with the customer's IdP** (Okta / Entra ID / Google
   Workspace / OneLogin / generic SAML 2.0):
   - Give the customer ProcuLink's (Clerk-hosted) **ACS / Reply URL** and **SP
     Entity ID / Audience URI** shown in the Clerk connection screen.
   - Enter the customer's **IdP metadata**: either upload the IdP metadata XML/URL,
     or paste the **IdP SSO URL**, **IdP Entity ID**, and the **signing
     certificate**.
4. **Set the email domain** for the connection (e.g. `customer.com`). Clerk enforces
   SSO per domain within the Organization. Gotcha: a domain used for SSO **cannot
   also be a verified domain** on the org — if the customer's domain is already
   verified, remove/adjust that first or setup is blocked (capture in the runbook).
5. **Attribute mapping** — map IdP assertions to Clerk user fields:
   `email` (required), `firstName`, `lastName`. Defaults are usually correct for
   Okta/Entra; verify the email claim resolves, because ProcuLink keys tenancy off
   the Clerk Organization the user lands in, and identity off `email`.
6. **JIT provisioning** — confirm "create user on first sign-in" is on. The
   JIT-provisioned user receives the Organization's **default role**. Set that
   default role deliberately (today ProcuLink has no role-based authz, so any member
   has full org access; if/when RBAC lands, the default role gates SSO users' access).
7. **Activate** the connection and run one real end-to-end sign-in from the
   customer's IdP. Verify: the user lands in the correct Clerk Organization → the
   session JWT carries `org_id`/`org_slug` → ProcuLink's `TenantResolutionMiddleware`
   resolves that tenant → upload→transform→deliver still works for that org. This is
   the proof that "no backend change needed" holds before any SSO copy goes live.

Until step 7 passes for a given customer, that customer's SSO is **not** working —
keep the UI on "available on Enterprise / contact us".

---

## 5. Admin-UX spec — frontend "Single sign-on (SAML)" Settings section (spec only)

Frontend is owned separately (NOT implemented in this branch). Spec:

**Placement.** Add a `Single sign-on` tab to the Settings shell
(`project-proculink/src/app/(app)/settings/page.tsx`): extend the `SettingsTab`
union, the `TABS` array, and the content switch. Use a `ShieldCheck` / `KeyRound`
lucide icon, consistent with the existing tabs (Organization, Billing, Email, SFTP,
S3, API keys, Connectors).

**Data source.** Read `billing.plan` and the new `billing.ssoAvailable` from
`getBillingStatus()` (already fetched on the Settings page and used to drive other
gate notices). No new API client method required for the MVP.

**Two states, gated on `ssoAvailable`:**

- **`ssoAvailable === false` (Pilot / Growth / Operations / Integration /
  Distributor) — upsell card.** Headline "Single sign-on (SAML/OIDC)", one line on
  what it is ("Let your team sign in through your identity provider — Okta, Entra ID,
  Google Workspace, OneLogin, or any SAML 2.0 IdP"), and a primary CTA "Available on
  Enterprise — contact sales" linking to the Enterprise contact-sales flow. No
  configuration controls. Do not imply it is one click away.

- **`ssoAvailable === true` (Enterprise) — available/status card.** Headline +
  short explainer + a "Contact us to set up SAML" CTA (white-glove; ops configures
  the Clerk connection per §4). Make explicit that SSO is configured by ProcuLink
  with the customer's IdP team. Do **not** render this as "SSO is active" unless a
  real connection exists — `ssoAvailable` is a plan flag, not a connection-status
  signal. A precise live status requires the optional Phase 4 read-only
  `GET /api/settings/sso` Clerk-Backend-API probe (§6) — until then, copy stays
  "available / contact us".

**Offer⇔works prerequisite (must land first or in lockstep).** The marketing
security page already over-claims: `project-proculink/src/app/(marketing)/security/page.tsx`
says "Role-based access, SSO via SAML/OIDC on **Scale**" — both the SSO claim (not
live) and the plan name (`Scale` is not a real plan; the ladder is
Pilot/Growth/Operations/Integration/Distributor/Enterprise) are wrong. Fix to
"SAML/OIDC SSO available on Enterprise" (or remove the SSO claim until a connection
is live) and correct `Scale` → `Enterprise`. Optionally add an "SSO (SAML/OIDC)"
line to the Enterprise column in `lib/plans.ts` / pricing once it is real.

---

## 6. Optional later increments (NOT in scope now)

- **Phase 4 live status probe.** A read-only `GET /api/settings/sso` that calls
  Clerk's Backend API to report whether the org has an active Enterprise Connection,
  turning the Settings card from "available" into a true configured/not-configured
  status. Backend would need the Clerk secret key + a Clerk Backend API client; keep
  org-scoped, read-only, Enterprise-gated.
- **Self-serve management.** Embed Clerk `<OrganizationProfile />` so customer org
  admins manage the SAML connection themselves (gate on Clerk org admin role +
  Enterprise). Only if Clerk's plan/add-on supports self-serve enterprise-connection
  management.
- **`Organisation.SsoEnforced` column** (additive migration) only if ProcuLink wants
  its own audit record of SSO-managed orgs; not needed for the capability.
- **SCIM / Directory Sync** (auto-deprovisioning) — separate, larger increment;
  explicitly FROZEN with RBAC per the current roadmap until paying customers.

---

## 7. Risks / open questions (carried from the program design)

- **Over-claiming.** The security page already advertises SSO that isn't live.
  Gate all SSO UI behind "available on Enterprise / contact us" until a real Clerk
  connection exists per customer.
- **Clerk pricing.** Enterprise Connections (SAML) — and especially self-serve
  management / Directory Sync — may require a higher Clerk tier/add-on with
  per-connection cost. Confirm before promising self-serve SSO; it affects margin on
  the €2,500+ Enterprise tier.
- **Prod Clerk cutover.** SAML must be configured on the production Clerk instance,
  not the dev `golden-alpaca-43` instance. The cutover is a prerequisite and a known
  gotcha (see memory: deployment-topology).
- **Per-domain enforcement.** A domain used for SSO can't double as a verified
  domain; mis-onboarding blocks setup.
- **JIT default role + Pilot auto-provisioning.** New SSO orgs auto-provision as
  Pilot; set Enterprise on the org and set the Organization default role deliberately
  during onboarding.
- **Is SAML actually required by the first Enterprise prospect**, or anticipatory?
  The roadmap freezes non-customer-winning features — confirm a contracted Enterprise
  customer is driving this before building beyond the Phase 0 copy fix + the Phase 1
  config proof + this plan-gate.
