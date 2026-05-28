# ProcuLink — Claude Code Model Routing Policy

_Last updated: 2026-05-28_

## Purpose

Use the **cheapest model that can reliably complete the current task** while keeping ProcuLink professional, production-ready, and safe.

Do **not** default to Opus for everything. Opus should be used for architecture, unclear multi-system reasoning, high-risk changes, and final review of critical work — not for routine implementation, styling, documentation, or mechanical fixes.

This file is intended to be read together with `CLAUDE.md`, `AGENTS.md`, `STATUS.md`, and the task-specific design/system docs.

---

## Current ProcuLink context

ProcuLink is a B2B outbound procurement bridge:

```text
Parse → Normalize → Validate → Review exceptions → Transform → Deliver → Learn
```

Current strategic focus:

- First ICP: **buyer/procurement teams sending purchase orders out to many suppliers**
- Current phase: **Phase 5 — production hardening**
- Immediate priority: **Group J live end-to-end QA + deployment hardening**
- Group I UI polish is effectively complete enough to stop adding broad UI work unless a specific defect is found
- Do not start broad new engines before the live upload → parse → review → transform → deliver loop works in production

Tech stack:

- Backend: ASP.NET Core 8, EF Core 8, PostgreSQL, Hangfire, Railway
- Worker: `ProcuLink.Worker`
- Frontend: Next.js 15 App Router, TypeScript, Tailwind, shadcn/ui, TanStack Query, Vercel
- Auth: Clerk via `@clerk/nextjs`
- Billing: Stripe
- Storage: Cloudflare R2
- AI mapping: provider-neutral interface, OpenAI structured outputs first
- Package manager: `bun` only for frontend

Hard rules:

- No Lovable-generated code
- No Vite patterns
- No `react-router-dom`
- No `@clerk/clerk-react`
- No raw SQL
- No EF query without `OrganisationId` scope
- No non-idempotent Hangfire jobs
- No secrets in git
- Read `STATUS.md` before planning

---

# Model routing principle

## Default model

Use **Sonnet-level model** as the default for almost all implementation work.

Use **Opus-level model** only when the task needs unusually strong reasoning, architectural judgment, or high-risk review.

Use a **cheap/fast model** for simple edits, documentation cleanup, summaries, mechanical refactors, and checklist work.

Model names may change. Treat the tiers below as capability levels:

| Capability tier | Use for |
|---|---|
| Cheap / fast model | Docs, copy, simple renames, formatting, small config edits, summarizing logs |
| Sonnet-level model | Default coding, normal debugging, UI polish, tests, API endpoints, forms |
| Opus-level model | Architecture, risky changes, multi-repo reasoning, billing/security/tenancy, failed-debug escalation |

---

# Task classification

## Tier 0 — Cheap / fast model

Use a cheap/fast model when the task is low-risk and mostly mechanical.

Examples:

- Update `.md` files
- Summarize `STATUS.md`
- Add or clean comments
- Rename labels or copy
- Adjust button text
- Create a checklist
- Update `.env.example`
- Write a simple prompt for another agent
- Format JSON examples
- Explain a known error message
- Generate outreach text or landing page copy variants

Do **not** use Opus.

Expected behavior:

```text
Read only the relevant file.
Make the smallest possible change.
Do not launch subagents.
Do not inspect unrelated docs.
Do not run broad searches.
```

---

## Tier 1 — Sonnet low/medium

Use Sonnet for simple implementation tasks touching 1–2 files.

Examples:

- Fix a TypeScript type mismatch
- Fix a broken import
- Add a small loading/error state
- Add one API client method
- Add one backend DTO
- Add one frontend form field
- Add a missing route link
- Fix a responsive layout issue in one component
- Add a straightforward unit test

Do **not** use Opus unless the task affects billing, auth, tenancy, data deletion, encryption, or production deployment behavior.

Expected behavior:

```text
Start with git status --short.
Read STATUS.md only if the task may affect project phase/status.
Read only files directly involved.
Implement.
Run the narrowest relevant build/test command.
Return a concise diff summary.
```

---

## Tier 2 — Sonnet medium/high

Use Sonnet for normal professional feature work touching 3–8 files when the implementation pattern is already known.

Examples:

- Add a new delivery retry button
- Add JSON output transformer using existing transform pattern
- Add a new validation rule UI
- Wire frontend page to an existing backend endpoint
- Add a Hangfire job using existing job conventions
- Add Sentry/logging improvements
- Add a small backend service with tests
- Add a new settings panel
- Improve onboarding checklist
- Fix live QA defects after deployment

Use `/superpowers:brainstorm` or a short written plan before coding if the task touches 3+ files.

Do **not** use Opus by default. Use Opus only for planning or review if the change is cross-cutting or risky.

Expected behavior:

```text
1. git status --short
2. git diff --stat
3. Read STATUS.md
4. Read only task-relevant docs
5. Write short plan
6. Implement in small commits/patches
7. Run focused tests/builds
8. Update STATUS.md if the task changes project status
```

---

## Tier 3 — Opus for planning/review, Sonnet for implementation

Use Opus only for the thinking-heavy part. Then hand implementation to Sonnet.

Examples:

- Multi-tenant authorization model
- Clerk organization roles
- Billing limit enforcement changes
- Stripe webhook correctness
- Delivery encryption or credential handling
- EF migration that changes important data shape
- Production deployment failure with several possible causes
- Worker/API/Railway/Vercel integration issue
- Re-architecting mapping or transformation engine
- Peppol/UBL design
- Security/compliance/trust architecture
- Major codebase cleanup across many files

Recommended split:

```text
Opus:
- Analyze architecture
- Identify risks
- Produce implementation plan
- Define acceptance tests

Sonnet:
- Implement the plan
- Run tests
- Fix normal compile/runtime errors

Opus:
- Final review only if the change is security/billing/tenancy/data-critical
```

Do **not** let Opus do long mechanical edits if Sonnet can follow a clear plan.

---

## Tier 4 — Opus only when the answer is not yet knowable

Use Opus when the task requires judgment, ambiguity handling, and deep trade-off reasoning.

Examples:

- “Is this product direction right?”
- “Should we sell to buyers or suppliers first?”
- “What is the best architecture for Peppol support?”
- “Analyze why this product might fail.”
- “Review the whole codebase and tell me what blocks launch.”
- “Design the next 90-day roadmap.”
- “Find the root cause after two failed debugging attempts.”
- “Decide whether to pivot the ICP.”

Even here, do not run implementation on Opus unless the task is small.

---

# Escalation rules

Start lower. Escalate only when justified.

Escalate from cheap/fast model to Sonnet if:

- Code must be edited
- Tests must be written
- The change touches runtime behavior
- The task requires reading several related files

Escalate from Sonnet to Opus if:

- Two focused Sonnet attempts failed
- The root cause is still unclear after logs/tests
- The change touches auth, billing, tenancy, encryption, data loss, or production deployment
- The task affects both backend and frontend architecture
- The task requires deciding between multiple product/technical strategies
- The issue could silently break customer data, payments, or delivery
- The task needs a high-confidence final review before launch

Do **not** escalate to Opus just because:

- The task feels important
- There are many files, but the pattern is repetitive
- The user asks for polish
- The build failed once
- A TypeScript type is annoying
- A layout needs adjustment
- Documentation is long

---

# De-escalation rules

Downgrade from Opus to Sonnet/cheap model when the work becomes mechanical.

Examples:

- Applying a plan already written by Opus
- Repeating an existing backend service pattern
- Adding tests following existing examples
- Updating copy across pages
- Fixing lint/build errors after a clear cause is found
- Creating `.md` docs
- Moving UI components without new design decisions
- Adding route loading skeletons using existing pattern

---

# Subagent policy

Subagents can burn usage quickly. Use them only when they create real leverage.

## Do not use subagents for:

- Simple bugs
- Copy edits
- One-page UI changes
- Adding one endpoint
- Updating one config file
- Mechanical refactors
- “Check everything” without a narrow question

## Use subagents for:

- Parallel review of backend + frontend after a risky feature
- Security review of auth/billing/tenancy changes
- UI review against the design system
- Product/market analysis
- Large codebase audit
- Test coverage audit
- Deployment failure investigation where logs point to multiple layers

## Recommended subagent model routing

| Subagent role | Model |
|---|---|
| Implementation agent | Sonnet |
| UI polish agent | Sonnet + `/frontend-design` guidance |
| Test writer | Sonnet |
| Documentation agent | Cheap/fast model |
| Product/market analyst | Sonnet or Opus depending on depth |
| Architecture reviewer | Opus |
| Security/billing/tenancy reviewer | Opus |
| Final launch-readiness reviewer | Opus |

Never run multiple Opus agents in parallel unless explicitly approved.

---

# Current ProcuLink task routing

## Group J — live QA and deployment hardening

Default: **Sonnet high**

Use Sonnet for:

- Setting env var checklists
- Railway/Vercel config fixes
- CORS issues
- `/health` failures with clear logs
- Clerk login flow issues
- Upload/parse/transform bugs with clear stack traces
- HTTP delivery test-fire defects
- IMAP test mailbox defects
- Sentry setup warnings
- Worker startup issues with clear logs

Use Opus for:

- Production incident root cause after two failed Sonnet attempts
- Cross-service deployment failure involving API + Worker + DB + Vercel
- Stripe webhook correctness if money/account state is affected
- Clerk organization/tenant authorization design
- Delivery encryption or credential persistence design
- Any change that could expose another organisation's data

---

## UI/UX polish

Default: **Sonnet medium**

Use Sonnet for:

- Responsive fixes
- Empty/loading/error states
- Form layout issues
- Status badges
- Timeline/tables/cards
- In-app onboarding checklist
- Accessibility fixes
- Copy alignment with product-selling-points

Use Opus only if:

- A new design direction is being considered
- The current information architecture is being redesigned
- A full product UX audit is requested

For ProcuLink, do **not** introduce a new design direction. Use the existing Bridge Layer design system.

---

## Standards and engine hardening

Default: **Sonnet high**

Use Sonnet for:

- JSON output transformer
- cXML test expansion
- Parser edge-case fixes
- Output format selector
- Webhook JSON payload delivery
- Replay endpoint
- SFTP dispatcher following existing `IDeliveryDispatcher` pattern

Use Opus for:

- Peppol/UBL architecture
- Canonical model redesign
- Full standards matrix strategy
- Long-term interoperability design
- Deciding whether to use a library vs custom parser

---

## Billing, security, tenancy

Default: **Opus plan/review + Sonnet implementation**

Use Opus for:

- Billing state machine design
- Stripe webhook correctness review
- Read-only account behavior
- Clerk organization/role design
- Multi-org/workspace separation
- Security/privacy/trust pages if making claims
- Encryption/key rotation design

Use Sonnet for:

- Implementing the already-approved plan
- Writing tests
- Updating UI after backend contract is clear

---

## Documentation and project memory

Default: **cheap/fast model or Sonnet low**

Use cheap/fast model for:

- Updating `STATUS.md`
- Writing handoff notes
- Creating model-routing docs
- Summarizing session changes
- Adding checklists
- Cleaning README sections

Use Sonnet if the documentation requires reading code or reconciling several files.

Do not use Opus for documentation unless the document is strategic architecture or investor/product analysis.

---

# Prompt prefixes for the user

The user may prefix tasks to force routing discipline.

## `[FAST]`

Use cheapest adequate model. No broad reading. No subagents.

Good for:

```text
[FAST] Update STATUS.md with this session summary.
[FAST] Fix this typo in pricing copy.
[FAST] Write a commit message.
```

## `[STANDARD]`

Use Sonnet. Normal implementation quality. Focused reading only.

Good for:

```text
[STANDARD] Add a retry button for failed delivery attempts.
[STANDARD] Fix upload selected-file state.
[STANDARD] Add tests for CxmlOrderParser edge cases.
```

## `[CAREFUL]`

Use Sonnet high, write a plan first, run tests.

Good for:

```text
[CAREFUL] Wire JSON output format into DeliveryService and the frontend selector.
[CAREFUL] Fix live Stripe checkout webhook mapping.
```

## `[OPUS-PLAN]`

Use Opus only to produce the plan. Implementation must be done later by Sonnet unless explicitly approved.

Good for:

```text
[OPUS-PLAN] Design multi-org workspace separation with Clerk roles.
[OPUS-PLAN] Decide the right Peppol/UBL architecture.
```

## `[OPUS-REVIEW]`

Use Opus only to review a completed change.

Good for:

```text
[OPUS-REVIEW] Review this billing change for data/account-state risks.
[OPUS-REVIEW] Review the tenant-scoping of these new endpoints.
```

---

# Required Claude Code startup behavior

For every non-trivial ProcuLink task:

```text
1. Run: git status --short
2. Run: git diff --stat
3. Read STATUS.md
4. Read CLAUDE.md or AGENTS.md only if workflow/tooling is relevant
5. Read design docs only for UI tasks
6. Read standards docs only for parser/transform/EDI/cXML/UBL tasks
7. Do not read the whole repo unless the task requires it
8. Do not run full git diff unless asked
9. Do not launch subagents unless the task is Tier 3+
10. Choose model tier according to this file
```

---

# Required planning behavior

If a task touches 3+ files:

```text
Use /superpowers:brainstorm or write a concise plan before coding.
```

If a task is medium or larger:

```text
Use /superpowers:write-plan before implementation.
```

If a bug survives one focused fix attempt:

```text
Use /superpowers:debug.
```

At the end of a task group:

```text
Use /code-review before marking the group complete.
```

---

# Context discipline

Token usage is not only model choice. It is also context discipline.

Do:

- Read only the files needed for the current task
- Prefer `git diff --stat` over full diff
- Search for exact symbols instead of opening huge files
- Use existing patterns instead of rediscovering architecture
- Keep summaries short
- Run targeted tests first
- Use full builds only at meaningful checkpoints

Do not:

- Read all docs automatically
- Re-open every design file for small UI fixes
- Run broad codebase scans for one-file bugs
- Start several agents “just in case”
- Ask Opus to rewrite mechanical code across many files
- Use screenshots repeatedly unless the UI task needs visual QA
- Keep stale branches or unrelated diffs in context

---

# Test/build routing

Use the narrowest test first.

Backend:

```bash
dotnet test ProcuLink.Transform.Tests/ProcuLink.Transform.Tests.csproj --no-restore
dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --no-restore
dotnet test ProcuLink.slnx --no-restore
dotnet build ProcuLink.slnx --no-restore
```

Frontend:

```bash
bun run build
bunx tsc --noEmit
```

Use full solution tests/builds before declaring major backend work done.

Use frontend build before declaring production-facing UI work done.

---

# Model decision tree

```text
Is it only docs/copy/checklist?
  → Cheap/fast model

Does it touch 1–2 files and follow an existing pattern?
  → Sonnet low/medium

Does it touch 3–8 files but the design is clear?
  → Sonnet medium/high + short plan

Does it affect auth, billing, tenancy, encryption, customer data, production deploy, or money?
  → Opus for plan/review, Sonnet for implementation

Has Sonnet failed twice and the root cause is unclear?
  → Escalate to Opus debug/architecture

Is it a broad product, market, architecture, or investor-level analysis?
  → Opus

Is the Opus output now a clear implementation checklist?
  → Downgrade to Sonnet for coding
```

---

# Definition of done by task type

## Simple docs/copy

- File updated
- No unrelated changes
- Summary returned

## Simple code fix

- Minimal patch
- Focused test/build run if relevant
- No unrelated refactor
- Diff summary returned

## Feature work

- Plan written
- Backend/frontend contracts aligned
- Tests added or updated
- UI states covered: loading, empty, error, success
- Build/test passed
- `STATUS.md` updated if project status changed

## Production hardening

- Live behavior verified or clearly marked as not verified
- Logs/errors checked
- Secrets not printed
- Failure mode documented
- Rollback path considered
- User-facing error state exists

## Billing/security/tenancy

- Opus plan or review used
- Organisation scoping checked
- Tests cover forbidden/wrong-org path where possible
- No secret leakage
- No raw SQL
- No silent account-state transitions
- Manual QA checklist updated

---

# Practical recommendation for Dim

Running everything on **Opus 4.7 Max** is overkill for ProcuLink.

Recommended default setup:

```text
70–80% of tasks: Sonnet
10–20% of tasks: cheap/fast model
5–10% of tasks: Opus
```

Use Opus mainly as:

```text
architect → debugger after failed attempts → final reviewer for risky changes
```

Use Sonnet as:

```text
daily builder → test writer → UI fixer → normal debugger
```

Use cheap/fast model as:

```text
documentation assistant → checklist writer → copy cleaner → summarizer
```

The best cost/performance pattern is:

```text
Opus thinks.
Sonnet builds.
Cheap model documents.
Opus reviews only when risk justifies it.
```
