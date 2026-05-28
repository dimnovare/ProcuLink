# Group L — Go-live Playbook (Founder Configuration)

> Sequential checklist for the 8 external/configuration actions that take ProcuLink from "Group L code shipped on `main`" to "fully functional in production". All code is already on `main` in both repos; this playbook only covers env vars, third-party signup flows, and operational workflows.

**Estimated total time:** 4-6 hours active work, spread however you like. Most actions are independent — see the dependency graph below.

**Last updated:** 2026-05-28

---

## Dependency graph

```
   Action 3 (Frontend URL) ─┐
                            ├──► Action 7 (SMTP)  ──► all support form testing
   Action 1 (PostHog) ──────┤
                            └──► Action 2 (Clerk redirect)  → so the welcome page captures `signup` analytics

   Action 4 (Status page)         independent
   Action 5 (Loom video)          independent
   Action 6 (Cal.com)             independent
   Action 8 (DPA + subprocessor)  independent, operational only
```

You can complete the four "independent" actions in any order, even in parallel. The four with arrows have soft ordering — they still work without dependencies, but verification is cleaner if you follow the arrows.

---

## Recommended provider defaults

If you don't already have preferences, these are the defaults this playbook assumes:

| Concern | Provider | Why |
|---|---|---|
| Product analytics | **PostHog Cloud EU** | EU residency for GDPR, generous free tier, real funnels |
| Status page | **Instatus** | Fast setup, ~€20/mo, EU region |
| Demo booking | **Cal.com** | Open-source, EU-friendly, free tier sufficient |
| Transactional email | **Postmark** | Best deliverability for transactional, €15/mo |
| Walkthrough video | **Loom** | Embed-friendly, free tier covers one short video |

If you prefer alternatives (BetterStack instead of Instatus, Resend instead of Postmark, Calendly instead of Cal.com), the env var names stay the same — only the signup flow steps change.

---

## Action 1 — PostHog Cloud EU project + API keys

**Why this matters:** The backend `IAnalyticsService` and frontend `posthog-js` SDKs are already wired and no-op silently until keys are set. Without this action, you cannot see the signup → first-delivery funnel, first-supplier rate, or billing-upgrade conversions.

**Prerequisites:** A work email address.

### Sign up + project creation

1. Go to **https://eu.posthog.com/signup** (the EU instance — not `us.posthog.com`).
2. Sign up. Use a work email so the org name resolves to ProcuLink.
3. After login, create a new project: name it `ProcuLink Production`, region `EU`.
4. Navigate to **Project settings → Project API Key**. Copy the value — it starts with `phc_`.

### Set keys on Vercel (frontend)

5. Open **https://vercel.com/dashboard** → `project-proculink` → **Settings → Environment Variables**.
6. Add three rows (apply each to Production + Preview + Development):

   | Key | Value |
   |---|---|
   | `NEXT_PUBLIC_POSTHOG_KEY` | `phc_...` (from step 4) |
   | `NEXT_PUBLIC_POSTHOG_HOST` | `https://eu.posthog.com` |

7. Go to **Deployments** → click the three-dot menu on the latest production deployment → **Redeploy**.

### Set keys on Railway (backend API + Worker)

8. Open **https://railway.app/dashboard** → ProcuLink project → click the **API** service → **Variables** tab.
9. Add two rows. Railway uses literal env var names; .NET's `ConfigurationBuilder` translates double-underscore `__` to colon `:`. So:

   | Key | Value |
   |---|---|
   | `Analytics__PostHog__ApiKey` | `phc_...` (same key as step 4 — backend and frontend share it) |
   | `Analytics__PostHog__Host` | `https://eu.posthog.com` |

10. Click **Deploy** (or wait for auto-redeploy).
11. Repeat steps 8-10 for the **Worker** service — same two variables, same values.

### Verify

- Open the marketing site in an incognito tab. Accept analytics cookies.
- Sign up, complete the wizard, upload a sample order.
- In PostHog → **Activity → Events**, search for `signup`, `org_created`, `wizard_opened`, `first_supplier_added`. You should see them within ~30 seconds.
- If you see zero events: confirm the Vercel redeploy actually picked up the new vars (Vercel → Deployments → ... → "Use existing build cache: No" then redeploy), and check Railway logs for the PostHog client init line.

**Time:** 30-45 minutes.

---

## Action 2 — Clerk post-signup redirect to `/welcome`

**Why this matters:** Without this, new sign-ups land on `/bridge` (the dashboard) and skip the welcome funnel — they don't see the 4-step preview or the post-Checkout upgrade callout.

**Prerequisites:** Clerk dashboard admin access. The `golden-alpaca-43` dev instance is already configured; you may also need a production Clerk instance.

### Configure dev instance (`golden-alpaca-43`)

1. Open **https://dashboard.clerk.com** and select the `golden-alpaca-43` instance.
2. Navigate to **Paths** in the left nav.
3. Find **After sign-up URL** and set the value to `/welcome`.
4. Click **Save**.

### Configure production instance (if not yet created)

5. From the Clerk dashboard top bar, click **Create application** → name it `ProcuLink Production`.
6. Choose authentication methods: **Email + password** plus **Google** (recommended for B2B).
7. After the instance is created, go to **Paths → After sign-up URL** and set `/welcome`. Save.
8. In Clerk → **API Keys**, copy:
   - **Publishable key** (`pk_live_...`)
   - **Secret key** (`sk_live_...`)

### Set production keys on Vercel

9. Vercel env vars (apply to Production only):

   | Key | Value |
   |---|---|
   | `NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY` | `pk_live_...` |
   | `CLERK_SECRET_KEY` | `sk_live_...` |

### Set production authority on Railway

10. Railway → API service → Variables. Add:

    | Key | Value |
    |---|---|
    | `Clerk__Authority` | The JWT issuer URL from Clerk dashboard → **JWT Templates → Default → Discovery URL** (drop the `/.well-known/openid-configuration` suffix) |

11. Redeploy both Vercel and Railway.

### Verify

- On the production URL, click **Sign up** in an incognito tab.
- Complete the email + password (or Google) flow.
- Confirm you land at `/welcome` (NOT `/bridge` or `/dashboard`).
- If you don't see `/welcome`: open browser DevTools → Network → look for the Clerk session redirect target. If it's something else, the Clerk dashboard "After sign-up URL" wasn't saved — try again.

**Time:** 15-30 minutes for dev, plus 30-60 minutes if creating a fresh production instance.

---

## Action 3 — Set production frontend URL

**Why this matters:** The backend reads `Frontend:Url` to compose Stripe Checkout `success_url`, future password reset links, and any absolute-URL email content. Without it, Stripe redirects to a broken URL after payment.

**Prerequisites:** Your production frontend URL — either a custom domain like `https://proculink.com` or the Vercel-issued URL.

### Set the env var

1. Railway → API service → Variables. Add:

   | Key | Value |
   |---|---|
   | `Frontend__Url` | `https://proculink.com` (no trailing slash) |

2. Click **Deploy**.

### Verify

- This is verified as part of Action 7 (Stripe Checkout end-to-end test). Skip standalone verification for now.

**Time:** 5 minutes.

---

## Action 4 — Status page

**Why this matters:** The marketing footer has a "Status" link gated by `NEXT_PUBLIC_STATUS_URL`. Without it, the link is hidden — no broken link visible, but customers can't self-check uptime.

**Prerequisites:** Decide on a provider (Instatus recommended; BetterStack and Statuspage.io are alternatives).

### Sign up (Instatus)

1. Go to **https://instatus.com** → **Get started for free**.
2. Sign up. Workspace name: `ProcuLink`.
3. Create a status page: name it `ProcuLink Status`, default subdomain `proculink.instatus.com`, or set a custom domain `status.proculink.com` (requires DNS CNAME — see Instatus docs).

### Configure monitors

4. From the status page admin → **Components** → add:
   - `API` (web check on `https://api.proculink.com/health` — adjust to your actual Railway URL)
   - `Frontend` (web check on `https://proculink.com`)
   - `Worker / Background jobs` (manual updates only — no automated check yet)
   - `Database` (manual updates only)
5. From the status page admin → **Notifications** → enable email subscriptions for the public to subscribe to incident updates.

### Set the env var on Vercel

6. Vercel env vars (Production + Preview):

   | Key | Value |
   |---|---|
   | `NEXT_PUBLIC_STATUS_URL` | `https://status.proculink.com` (or your Instatus subdomain) |

7. Redeploy frontend.

### Verify

- Open `https://proculink.com` (or any marketing page) → scroll to the footer.
- A new "Status" link should appear in the link row. Click it → Instatus page loads.
- If the link is missing: confirm Vercel redeployed with the new env var.

**Time:** 30-60 minutes including custom domain setup.

---

## Action 5 — Record walkthrough Loom

**Why this matters:** `/watch` shows a "video is being recorded" placeholder until `NEXT_PUBLIC_WALKTHROUGH_LOOM_URL` is set. This is the single most useful asset for outbound sales emails — every "see how it works" CTA on the landing page assumes this video exists.

**Prerequisites:** Loom account (free tier covers one short video).

### Record the video

1. Sign in at **https://loom.com**.
2. Record a 60-90 second walkthrough. Suggested structure:

   | Time | What to show | Script |
   |---|---|---|
   | 0:00-0:10 | Marketing landing page | "ProcuLink turns the POs you send into the exact format each supplier needs — and delivers them." |
   | 0:10-0:30 | `/upload` → click **Try with sample order** | "Pick a file, or try with our sample order. ProcuLink parses CSV, XLSX, and PDF." |
   | 0:30-0:55 | Review screen — point to parsed lines + AI suggestions | "We extract the lines, suggest item code mappings, and show you exactly what needs review." |
   | 0:55-1:15 | Configure HTTP delivery → test-fire to webhook.site | "Configure how to deliver — HTTP, ERP, or download — and ProcuLink handles the rest." |
   | 1:15-1:30 | End frame | "Built for procurement teams sending POs out. Start at proculink.com." |

3. After recording, click **Share** → toggle privacy to **Public**.
4. Copy the **embed URL** — should look like `https://www.loom.com/embed/abc123def456`.

### Set the env var on Vercel

5. Vercel env vars (Production + Preview):

   | Key | Value |
   |---|---|
   | `NEXT_PUBLIC_WALKTHROUGH_LOOM_URL` | `https://www.loom.com/embed/abc123def456` |

6. Redeploy frontend.

### Verify

- Open `https://proculink.com/watch` in an incognito tab.
- The Loom player should embed in a 16:9 frame (was a dashed-border placeholder before).
- Click play — video plays.

**Time:** 1-2 hours including retakes.

---

## Action 6 — Cal.com / Calendly demo slot

**Why this matters:** The Pilot "Book a 15-min demo" CTA on `/upload` and Billing settings is hidden until `NEXT_PUBLIC_BOOK_DEMO_URL` is set.

**Prerequisites:** Decide on Cal.com (recommended — free, EU-friendly) vs Calendly.

### Sign up (Cal.com)

1. Go to **https://cal.com** → Sign up. Username: e.g. `dim-novare` or `proculink`.
2. Click **Event Types → + New Event Type**.
3. Configure:
   - **Title:** `ProcuLink — 15-min demo`
   - **URL slug:** `proculink-15-min-demo`
   - **Duration:** 15 minutes
   - **Location:** Google Meet (or Zoom)
   - **Availability:** weekdays 10:00-18:00 EET (adjust)
4. **Advanced → Booking questions:** add "What's your company?" and "How many POs do you send per month?" — these qualify the lead before the call.
5. Click **Save**. Copy the public URL — should look like `https://cal.com/dim-novare/proculink-15-min-demo`.

### Set the env var on Vercel

6. Vercel env vars (Production + Preview):

   | Key | Value |
   |---|---|
   | `NEXT_PUBLIC_BOOK_DEMO_URL` | `https://cal.com/dim-novare/proculink-15-min-demo` |

7. Redeploy frontend.

### Verify

- Sign in to ProcuLink with a Pilot account (any newly-created org defaults to Pilot).
- Open `/upload` — a "Book a 15-min demo" CTA card should appear above the file-drop area.
- Click the CTA → Cal.com booking page opens in a new tab.
- Same check on `/settings` → Billing tab — Pilot accounts should see the same CTA.

**Time:** 15-30 minutes.

---

## Action 7 — SMTP credentials for support form

**Why this matters:** `POST /api/support/contact` accepts requests but routes them to `ConsoleEmailSender` (logs to backend stdout) unless SMTP env vars are set. Support requests are then silently lost.

**Prerequisites:** Decide on a provider (Postmark recommended; Resend, Mailgun, SendGrid, Amazon SES are alternatives).

### Sign up (Postmark)

1. Go to **https://postmarkapp.com** → Sign up.
2. **Verify your sending domain:**
   - Click **Sender Signatures → Add Domain** → enter `proculink.com`.
   - Postmark provides three DNS records (SPF, DKIM, return-path). Add them to your domain registrar.
   - Click **Verify** — propagation can take up to 48 hours but usually completes within 30 minutes.
3. **Create a server:** **Servers → + Server** → name `ProcuLink Transactional`. The Color is just visual.
4. In the new server → **API Tokens** → copy the **Server API Token** (32 chars).

### Set env vars on Railway (API service)

5. Railway → API service → Variables. Add five rows:

   | Key | Value |
   |---|---|
   | `Smtp__Host` | `smtp.postmarkapp.com` |
   | `Smtp__Port` | `587` |
   | `Smtp__Username` | The Server API Token from step 4 (Postmark uses it as username AND password) |
   | `Smtp__Password` | The same Server API Token |
   | `Smtp__From` | `support@proculink.com` (MUST match a verified sender signature in Postmark) |

6. Click **Deploy**.

### Verify

- Submit a test request via `https://proculink.com/support` — fill the contact form with category `general`, subject `Postmark smoke test`, message `If you see this, SMTP works`.
- Within 30 seconds, you should receive the email at `support@proculink.com` with subject `[support][general] Postmark smoke test`.
- In Postmark dashboard → **Activity → Outbound**, see the sent message.
- Also verify the `support_form_submitted` PostHog event fired (Activity → Events filter).
- If the email never arrives: check Postmark **Activity → Bounces / Suppressions** + Railway logs for SMTP errors. Most common issue is a wrong `Smtp__From` that doesn't match a verified sender.

**Time:** 30-60 minutes including DNS propagation.

---

## Action 8 — Operational workflows (DPA + subprocessor notifications)

**Why this matters:** Pages `/dpa` and `/subprocessors` commit you to specific customer-facing workflows. No code involved — these are inboxes + lists you set up and personally honour.

### DPA counter-signature workflow (commitment: 5 business days)

1. **Set up the inbox:** create `legal@proculink.com` (Google Workspace, Microsoft 365, or any email forwarder).
2. **Tracking list:** create a Notion table, Airtable, or Linear list called `DPA Requests` with columns:
   - Customer org name
   - Date received
   - Date signed and returned
   - Notes
   - Signed PDF (link to file storage)
3. **Signing tool:** sign up for **DocuSign** (€10-25/mo) or **PandaDoc** (free tier OK for low volume), or use **Tilki.app** for a lightweight free option.
4. **Inbox rule:** any email to `legal@proculink.com` with subject containing "DPA" or "Data Processing" → label "DPA Pending" + create a tracking row.
5. **Promise:** when a DPA arrives, sign and return within 5 business days as `/dpa` page commits.

### Subprocessor change-notification subscriber list

6. **Set up the inbox:** create `privacy@proculink.com`.
7. **Subscriber list:** create a Notion/Airtable list called `Subprocessor Notification Subscribers` with columns:
   - Email
   - Date subscribed
   - Last notified date
8. **Inbox rule:** auto-reply to any email with subject "Subprocessor notifications":
   > "Thanks — you're subscribed to ProcuLink subprocessor change notifications. We'll email you at least 30 days before adding or replacing any subprocessor listed at https://proculink.com/subprocessors."
   Manually add the sender's email to the list.
9. **Change-management process:** before editing `(marketing)/subprocessors/page.tsx`:
   - Send an email blast to all subscribers 30 days before the planned change.
   - Subject line: `[ProcuLink] Subprocessor change notification — {effective date}`.
   - Then make the code change and deploy on the effective date.

### DPA inbox & abuse reporting

10. **Also set up:** `abuse@proculink.com` (referenced on `/aup`) and `security@proculink.com` (referenced on `/security`). These can be aliases to the same human inbox initially.

**Time:** 1-2 hours to set up inboxes, tooling, and the tracking lists.

---

## Final end-to-end smoke test

Once all 8 actions are done, run this 20-minute test to confirm everything is connected.

### Funnel test (single browser session, incognito)

1. Open the production marketing URL → accept analytics cookies.
2. Click **Sign up** → complete Clerk → confirm landing on `/welcome`.
3. PostHog check: `signup` + `welcome_viewed` events visible within 30s.
4. Click **Open the dashboard** → wizard opens → `wizard_opened` event.
5. Step 1: add a supplier `Smoke Test Supplier` → `wizard_step_completed` + `first_supplier_added`.
6. Step 2: upload your own CSV OR click **Try with sample order** from `/upload` → `first_upload_started`.
7. After parse → review screen renders → confirm any AI suggestions → `first_upload_parsed` + `first_mapping_resolved`.
8. Click **Send to supplier** → `first_transform_succeeded`.
9. Configure HTTP delivery against `https://webhook.site/<your-unique-id>` → test-fire → confirm `first_delivery_succeeded`.

### Sales/trust paths

10. Open `/watch` in a new tab → Loom video embeds and plays.
11. Open `/customers` → placeholder cards visible.
12. Open `/dpa`, `/subprocessors`, `/aup` → all four legal pages render with `ProcuLink OÜ` entity.
13. Open `/support` → fill the contact form → email arrives at `support@proculink.com` within 30s.
14. Click "Status" in the marketing footer → Instatus page loads.

### Billing path

15. Open `/settings` → Billing tab while signed in as the Pilot account.
16. Confirm the "Book a 15-min demo" CTA card is visible.
17. Click **Upgrade to Growth** → Stripe Checkout in test mode → use card `4242 4242 4242 4242` → complete payment.
18. Confirm redirect lands at `https://proculink.com/welcome?upgraded=growth&session_id=cs_test_...`.
19. PostHog check: `billing_upgraded` event with `from_plan=pilot`, `to_plan=growth`.

### Acceptance

If all 19 steps pass → **Group L is live in production**. Update `STATUS.md` accordingly (the "waiting on founder configuration" table can be deleted).

If any step fails → check the corresponding Action section above's "Verify" subsection for troubleshooting.

---

## Cost estimate (monthly recurring)

| Service | Plan | Cost |
|---|---|---|
| PostHog Cloud EU | Free tier (up to 1M events/mo) | €0 |
| Instatus | Starter | €20 |
| Cal.com | Free tier | €0 |
| Postmark | 10K emails/mo | €15 |
| Loom | Free tier (one video, public) | €0 |
| Clerk | Free dev + Hobby production (5K MAU) | €0 |
| **Total Group-L third-party cost** | | **~€35/mo** |

Plus Railway (~€20-50 depending on traffic), Vercel (~€20 Pro), Stripe (% of revenue only), Cloudflare R2 (~€5).

---

## Optional next-up suggestions (not blocking)

After the 8 actions, consider:

- **Monitor alerts:** Configure Sentry alerts to email or Slack on backend 5xx errors. Sentry is already wired — just add alert rules.
- **PostHog dashboards:** Build a "First-paying-customer funnel" dashboard in PostHog with steps `signup → org_created → first_supplier_added → first_upload_parsed → first_delivery_succeeded → billing_upgraded`.
- **Sentry release tags:** Vercel and Railway both support setting `SENTRY_RELEASE` per deploy — improves error grouping across releases.
- **Stripe webhook retry config:** verify Stripe is set to retry failed webhooks (default is on).
