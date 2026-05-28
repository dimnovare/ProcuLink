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
