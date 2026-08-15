using System.Text.RegularExpressions;
using ProcuLink.Api.Controllers;
using Xunit;
using static ProcuLink.Api.Tests.Architecture.EndpointReachabilityScanner;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// <b>The endpoint reachability guard.</b> Rule R1 — "no new surface without a consumer" — pointed
/// at the API surface itself.
///
/// <para><b>The hole it closes.</b> The v2 audit (2026-08-06, §3) found that every guard in these
/// two repositories is written from the shape of the defect that prompted it, and named this one
/// exactly: <i>"the backend orphan guard covers DbSets and the frontend route guard covers routes,
/// so an API endpoint with no caller is invisible to both — two documented recovery doors have zero
/// callers."</i> An HTTP endpoint could be written, documented, unit-tested and deployed while no
/// user could reach it, and every check in both repositories reported green.</para>
///
/// <para><b>What counts as a caller</b>, and why. An endpoint is reachable when something that can
/// issue a real request to the deployed API names its path:</para>
/// <list type="number">
///   <item><description><b>Frontend product code</b> — <c>project-proculink/src/**</c>. This is the
///     only way a customer reaches anything.</description></item>
///   <item><description><b>The live harnesses</b> — <c>project-proculink/scripts/**</c>. They issue
///     real HTTP against a real deployment; <c>ingest-harness.mjs</c> is the only caller of
///     <c>POST /api/ingress/{slug}/orders</c> in either repository, and
///     <c>inbound-email-harness.mjs</c> the only caller of the Postmark webhook.</description></item>
/// </list>
///
/// <para><b>And what deliberately does NOT count</b>, because each looks like a caller and is not:</para>
/// <list type="bullet">
///   <item><description><b>Comments.</b> Stripped from every file before matching.
///     <c>api-client.ts</c> documents <c>POST /api/suppliers/{id}/profiles</c> in a comment beside
///     two GET callers and never posts to it.</description></item>
///   <item><description><b>Documentation.</b> Markdown and MDX are not in the corpus at all.
///     "Documented and unreachable" is the defect, so a document cannot discharge it — at most it
///     is a justification, written down below.</description></item>
///   <item><description><b>Tests</b>, in either repository. A test is not a user path. Same stance
///     <see cref="OrphanGuardTests"/> takes on readers, for the same reason.</description></item>
///   <item><description><b>MSW handlers</b> (<c>src/mocks/</c>). They stand in FOR the API rather
///     than call it, and they have drifted — they answer a different base with no <c>/api</c>
///     prefix at all.</description></item>
///   <item><description><b>The backend's own source.</b> <c>AdminController</c> spells two admin
///     routes into operator-facing error messages and <c>PassportService</c> spells one into a
///     hint. To a text scanner those read exactly like call sites, and crediting them would let a
///     controller excuse its own endpoint. <c>Detector_DoesNotCreditARouteNamedInProse</c> pins
///     it.</description></item>
/// </list>
///
/// <para><b>Its relationship to the frontend's guard.</b> <c>src/test/endpoint-reachability.test.ts</c>
/// in <c>project-proculink</c> asks a version of this question from the other side, and is the
/// better half on caller extraction — it owns the call sites. It is not a substitute, for three
/// reasons, each of which is a direction this defect can arrive from that it cannot see:</para>
/// <list type="number">
///   <item><description>It checks <b>state-changing verbs only</b>. Sixteen of the findings below
///     are GETs — a read-only recovery or diagnostic door with no handle is invisible to it, and
///     that is the same defect rotated ninety degrees.</description></item>
///   <item><description>Its endpoint inventory is <b>regexed out of the C# source text</b>; this
///     one is read from ASP.NET Core's own <c>IActionDescriptorCollectionProvider</c>. See
///     <see cref="EndpointReachabilityScanner.Endpoints"/> for the four decisions a regex has to
///     re-implement and can get wrong.</description></item>
///   <item><description>It runs on the frontend's clock. <b>Endpoints are added here</b>, and this
///     guard fails in the build of the pull request that adds one.</description></item>
/// </list>
///
/// <para><b>It cannot skip.</b> The same audit named <c>backendMirror.test.ts</c> as a guard that
/// "has never run" — <c>skipIf(!BACKEND)</c>, with CI never supplying a backend. This guard has no
/// unable-to-check state: <see cref="EndpointReachabilityScanner.FindFrontendRoot"/> throws, and
/// <c>.github/workflows/ci.yml</c> checks the frontend out so CI supplies what it demands.</para>
///
/// <para><b>Limits that fail in the NOISY direction</b> — the endpoint reads as unreachable, which
/// costs a reasoned declaration and never hides anything: a computed final segment is not credited
/// against a literal route segment (see <see cref="EndpointReachabilityScanner.Matches"/>); a verb
/// held in a variable rather than written into the call's own options object reads as GET; and a
/// path assembled from a constant declared in another module has nothing local to resolve.</para>
///
/// <para><b>Limits that fail in the SILENT direction</b>, established by mutating a copy of the
/// frontend until this guard went green, and pinned as fixtures below rather than left as prose —
/// because a green suite is read as coverage, and this paragraph is exactly where a guard asserts a
/// limitation nobody checked:</para>
/// <list type="number">
///   <item><description><b>A dead branch is indistinguishable from a live call.</b> The sweep
///     proves a path is NAMED in a call-shaped context, never that the call executes.
///     <see cref="Detector_CannotTellADeadBranchFromALiveCall"/>.</description></item>
///   <item><description><b>A display-only URL credits a GET.</b> Reading the verb separates "shown
///     to the customer" from "called" only when the endpoint is not a GET — which is what keeps
///     <c>POST /api/ingress/{slug}/orders</c> honest, and what cannot help a GET.
///     <see cref="Detector_CreditsADisplayOnlyUrlWhenTheEndpointIsAGet"/>.</description></item>
///   <item><description><b>A route registered outside MVC is not in the inventory at all</b> — which
///     is why <see cref="NoEndpointArrivesThroughAMechanismTheInventoryCannotSee"/> exists to fail
///     on the mechanism instead.</description></item>
/// </list>
/// </summary>
public sealed class EndpointReachabilityGuardTests
{
    /// <summary>
    /// <b>"This is fine, and here is who calls it."</b> Endpoints whose caller is real and outside
    /// both repositories.
    ///
    /// <para>An entry may live here ONLY with a reason citing something a reviewer can open — a
    /// file path, a URL, or an ISO date. "Probably called by something" is not a reason.</para>
    /// </summary>
    private static readonly IReadOnlyList<AccountedEndpoint> MachineFacing =
    [
        // ── Machine-to-machine ingress, sold as an integration surface ────────────────────
        new("POST /api/ingress/{}/catalog/{}",
            "2026-08-13 — machine-to-machine catalog push. IngressController carries "
            + "[Authorize(AuthenticationSchemes = \"ApiKey\")], not Clerk. The URL is RENDERED for "
            + "the customer to paste into their own system at "
            + "project-proculink/src/components/bridge/SupplierDockProfile.tsx:1243 — this app never "
            + "posts to it, and a frontend caller would be the bug. Its sibling POST "
            + "/api/ingress/{slug}/orders needs no entry: scripts/live-matrix/ingest-harness.mjs:97 "
            + "really calls it."),
        new("GET /api/ingress/{}/ping",
            "2026-08-13 — the reachability probe a customer runs while wiring the ingress API up, "
            + "documented for them at "
            + "project-proculink/src/app/(marketing)/help/api-and-integrations/page.mdx:29 as "
            + "`GET /api/ingress/{your-slug}/ping`. Being callable by someone who is not us IS the "
            + "feature. Permanent entry."),

        // ── Provider webhooks. The caller is Stripe. ──────────────────────────────────────
        new("POST /api/billing/webhook",
            "2026-08-13 — Stripe calls this, not the browser. [AllowAnonymous] with signature "
            + "verification, and the URL is configured in the Stripe dashboard rather than in code. "
            + "The other half of the integration is ProcuLink.Api/Services/StripeBillingService.cs. "
            + "A frontend caller would be a bug. Permanent entry."),

        new("POST /api/inbound-email/postmark-bounce",
            "2026-08-15 — Postmark calls this, not the browser, and the URL is configured in the " + 
            "Postmark dashboard rather than in code (contract: " + 
            "https://postmarkapp.com/developer/webhooks/bounce-webhook). It is the OUTBOUND half of the " + 
            "provider relationship: Postmark POSTs here when a purchase order ProcuLink emailed " + 
            "to a supplier hard-bounces or is reported as spam, and the handler moves the order " + 
            "off 'delivered'. The URL is configured in the Postmark dashboard rather than in " + 
            "code, and authentication is the shared token, not a session — so a frontend caller " + 
            "would be a bug. The handler is " + 
            "ProcuLink.Infrastructure/Services/Delivery/DeliveryBounceHandler.cs and its sibling " + 
            "POST /api/inbound-email/postmark is the inbound half. " + 
            "Permanent entry."),

        // ── Operator tooling with a documented out-of-band caller ─────────────────────────
        new("POST /api/admin/organisations/{}/account-status",
            "2026-08-13 — run by hand from the admin runbook rather than from a screen: "
            + "project-proculink/src/app/(app)/admin/guides/onboard-a-new-client/content.mdx:82 "
            + "spells out the curl against `$API_BASE/api/admin/organisations/$ORG_ID/account-status`. "
            + "[AdminOnly] and cross-tenant. The other AdminController writes have NO such runbook "
            + "entry and are listed below as open questions, not excused here."),

        // ── Reached only in a local development topology ──────────────────────────────────
        new("GET /api/dev/files/{**}",
            "2026-08-13 — the local blob server. Nothing fetches this path from source; the URL is "
            + "MINTED at runtime by ProcuLink.Infrastructure/Storage/LocalFileStorageService.cs:33 "
            + "($\"http://localhost:5096/api/dev/files/{key}\") and handed to whatever asked for a "
            + "download link, so the caller is a browser following a URL this service built. R2 is "
            + "used in every deployed environment, so it has no production caller either."),

        // ── Called through a computed final segment, which the matcher refuses to credit ──
        new("POST /api/connections/{}/revisions/{}/publish",
            "2026-08-13 — genuinely called, through a segment the matcher will not credit: "
            + "project-proculink/src/lib/api-client.ts builds "
            + "`/api/connections/${connectionId}/revisions/${revisionId}/${action}` where `action` "
            + "is the union \"publish\" | \"archive\". Crediting a computed segment against a "
            + "literal route segment would let one template mark a whole controller reachable — see "
            + "EndpointReachabilityScanner.Matches for why that refusal is deliberate."),
        new("POST /api/connections/{}/revisions/{}/archive",
            "2026-08-13 — the other half of the same computed-segment call in "
            + "project-proculink/src/lib/api-client.ts; see the publish entry directly above for why "
            + "the matcher refuses to credit it structurally."),
    ];

    /// <summary>
    /// <b>"Nobody calls this, and nobody has decided what to do about it."</b>
    ///
    /// <para>This list is NOT the one above, and the two must never be merged. An entry here is an
    /// open question with a date on it, not a justification. Both lists pass — and that is a
    /// deliberate correction, taken from the frontend guard that reached it first: left failing,
    /// the pipeline could not go green until a product decision arrived, which trains people to
    /// stop reading CI and then masks the next real failure. So the gaps are tracked loudly instead
    /// — every entry reasoned, the whole set counted in the failure text, a new one failing, and an
    /// entry that gains a caller failing.</para>
    /// </summary>
    private static readonly IReadOnlyList<AccountedEndpoint> UncalledPendingDecision =
    [
        // ── The two recovery doors the audit named. See Guard_SeesTheTwoRecoveryDoors… ────
        new("POST /api/orders/{}/acceptance-gate/override",
            "PENDING A DECISION, 2026-08-13 — named by the v2 audit as a caller-less recovery door. "
            + "ProcuLink.Api/Controllers/OrderAcceptanceGateController.cs:70-78 documents it as the "
            + "administrator's authorisation to send an order despite the supplier's blocking rules, "
            + "with a required reason and a recorded identity — and no screen offers it. The GET "
            + "beside it IS called, so an operator can see exactly why an order is blocked and has "
            + "no way to act on it. Build the control or delete the endpoint."),
        new("DELETE /api/suppliers/{}/po-mapping/output-tree",
            "PENDING A DECISION, 2026-08-13 — the second recovery door named by the v2 audit. Its "
            + "own docstring at ProcuLink.Api/Controllers/SuppliersController.cs:583-587 calls it "
            + "\"the recovery door for a layout that cannot deliver this supplier's format\", and "
            + "the mapper offers no way to un-promote a layout. A door with no handle, in its own "
            + "words."),

        // ── Data-protection obligations with no operator surface ─────────────────────────
        new("DELETE /api/admin/organisations/{}/orders/{}",
            "PENDING A DECISION, 2026-08-13 — single-order erasure. A data-protection obligation "
            + "with no screen and, unlike account-status, no runbook curl either: "
            + "project-proculink/src/app/(app)/admin/guides/onboard-a-new-client/content.mdx "
            + "documents none."),
        new("POST /api/admin/organisations/{}/orders/bulk-erase",
            "PENDING A DECISION, 2026-08-13 — bulk erasure, the same gap as the single-order erase "
            + "above (AdminController.cs:697) and the same absence from "
            + "project-proculink/src/app/(app)/admin/guides/onboard-a-new-client/content.mdx. The "
            + "admin screens call only /limits and /invoices."),
        new("POST /api/admin/organisations/{}/retention",
            "PENDING A DECISION, 2026-08-13 — sets an organisation's retention window "
            + "(AdminController.cs:626). No control anywhere under "
            + "project-proculink/src/app/(app)/admin/page.tsx, and no curl in "
            + "project-proculink/src/app/(app)/admin/guides/onboard-a-new-client/content.mdx. Wire "
            + "it into the admin org view or retire it."),

        // ── Operator diagnostics that exist and cannot be looked at ──────────────────────
        new("GET /api/admin/job-failures",
            "PENDING A DECISION, 2026-08-13 — background-job failures "
            + "(ProcuLink.Api/Controllers/AdminController.cs:102), queryable and never queried: no "
            + "call site anywhere in project-proculink/src. Found by this guard and not by the "
            + "frontend's — it is a GET, and that sweep covers state-changing verbs only."),
        new("GET /api/admin/item-mapping-twins",
            "PENDING A DECISION, 2026-08-13 — duplicate item-mapping detection "
            + "(ProcuLink.Api/Controllers/AdminController.cs:144), with no reader in "
            + "project-proculink/src/app/(app)/admin/. Same class as job-failures above, and "
            + "likewise invisible to a state-changing-only sweep."),
        new("GET /api/auto-send/dry-runs",
            "PENDING A DECISION, 2026-08-13 — the audit records the per-supplier auto-send flag as "
            + "shipped with no indicator and no control; this is the evidence trail for it, and "
            + "nothing reads that either. ProcuLink.Api/Controllers/AutoSendDryRunController.cs:145 "
            + "exists in full, exercised by "
            + "ProcuLink.Api.Tests/Integration/AutoSendDryRunPostgresTests.cs, behind no surface."),
        new("GET /api/auto-send/dry-runs/summary",
            "PENDING A DECISION, 2026-08-13 — the aggregate of the list above "
            + "(ProcuLink.Api/Controllers/AutoSendDryRunController.cs:82); it stands or falls with "
            + "the same decision and is listed separately so neither is lost."),

        // ── Order-level reads with no screen ─────────────────────────────────────────────
        new("GET /api/orders/{}/status",
            "PENDING A DECISION, 2026-08-13 — a cheap poll-friendly status read. The review screen "
            + "polls the full order instead (project-proculink/src/lib/api-client.ts fetches "
            + "/api/orders/{id}), so this narrower endpoint is engine surface nobody adopted."),
        new("GET /api/orders/{}/ai-decisions",
            "PENDING A DECISION, 2026-08-13 — the AI decision trail for one order. "
            + "ProcuLink.Api/Services/PassportService.cs:168 tells the operator in prose that it is "
            + "\"available at GET /api/orders/{id}/ai-decisions\" — a sentence that promises a "
            + "surface no screen provides. Either the review screen shows the trail or both the "
            + "endpoint and the sentence go."),
        new("GET /api/orders/{}/delivery-attempts",
            "PENDING A DECISION, 2026-08-13 — per-order delivery attempts "
            + "(ProcuLink.Api/Controllers/DeliveriesController.cs:33). The review screen renders "
            + "delivery state from the order itself and the org-wide trail is GET /api/audit, which "
            + "project-proculink/src/components/bridge/CrossingsLog.tsx reads, so this per-order "
            + "view has no reader. Decide whether the order page grows an attempts panel."),
        new("POST /api/orders/{}/mark-rejected",
            "PENDING A DECISION, 2026-08-13 — marks an order rejected by the supplier. The review "
            + "screen RENDERS the resulting rejected_by_supplier status "
            + "(project-proculink/src/lib/orderStatusManifest.ts) but offers no way to set it, so "
            + "today the status can only arrive from a delivery response."),

        // ── The supplier order-confirmation flow: built, never surfaced ──────────────────
        new("POST /api/orders/{}/confirmation",
            "PENDING A DECISION, 2026-08-13 — records a supplier order confirmation. "
            + "ProcuLink.Api/Controllers/OrderConfirmationController.cs:45 is reachable only by an "
            + "operator with a REST client. Decide whether inbound confirmation is a product "
            + "surface."),
        new("POST /api/orders/{}/confirmation/upload",
            "PENDING A DECISION, 2026-08-13 — the file half of the confirmation flow above "
            + "(OrderConfirmationController.cs:88). Same decision, listed separately so neither is "
            + "lost."),
        new("GET /api/order-confirmations/{}",
            "PENDING A DECISION, 2026-08-13 — the read half of the same confirmation flow "
            + "(OrderConfirmationController.cs:132). A GET, so the frontend's state-changing sweep "
            + "never saw it; it makes the confirmation gap a whole feature rather than two writes."),

        // ── Engine surfaces the product never adopted ────────────────────────────────────
        new("POST /api/schema/infer",
            "PENDING A DECISION, 2026-08-13 — schema inference over an uploaded sample. No caller in "
            + "project-proculink/src/lib/api-client.ts; the mapper's suggestion path uses the "
            + "mapper-enrichment endpoints instead. Either the mapper adopts it or it is dead engine "
            + "surface."),
        new("POST /api/schema/propose-mapping",
            "PENDING A DECISION, 2026-08-13 — the second half of the inference pair above, uncalled "
            + "for the same reason (SchemaInferenceController.cs:96)."),
        new("POST /api/suppliers/{}/po-mapping/test",
            "PENDING A DECISION, 2026-08-13 — dry-runs a mapping against a sample without saving. "
            + "project-proculink/src/lib/api/mapping.ts calls PUT and DELETE on /po-mapping and "
            + "never /test, so the mapper's preview does not use it."),
        new("POST /api/suppliers/{}/profiles",
            "PENDING A DECISION, 2026-08-13 — upserts a supplier profile. "
            + "project-proculink/src/lib/api-client.ts:1300 documents the route in a COMMENT beside "
            + "two GET callers and never posts to it, which is precisely how a comment comes to look "
            + "like a caller. Decide whether profiles are editable in-product."),
        new("GET /api/rule-definitions",
            "PENDING A DECISION, 2026-08-13 — the org-wide validation-rule catalog. "
            + "project-proculink/src/lib/api-client.ts:3169 says so outright: \"No client for GET "
            + "/api/rule-definitions: the org-wide catalog page it fed had…\" — the page was deleted "
            + "(see src/lib/retired-routes.ts) and the endpoint was not. Delete it or give it a "
            + "page."),
        new("GET /api/rule-definitions/{}",
            "PENDING A DECISION, 2026-08-13 — one definition from the catalog above "
            + "(ProcuLink.Api/Controllers/RuleDefinitionsController.cs:42); same decision. The "
            + "supplier-scoped bindings endpoint at RuleDefinitionsController.cs:52 is separate and "
            + "IS called."),
        new("GET /api/suppliers/{}/acceptance-profile/versions",
            "PENDING A DECISION, 2026-08-13 — the version history of a supplier's acceptance "
            + "profile. api-client.ts calls GET and POST on /acceptance-profile and POST on "
            + "/{versionNo}/activate, so a user can create and activate a version and cannot list "
            + "what exists. Either the supplier screen grows a history panel or this goes."),
        new("GET /api/invoices/{}/validate-peppol",
            "PENDING A DECISION, 2026-08-13 — Peppol conformance for one invoice. The invoices "
            + "screen (project-proculink/src/app/(app)/inbound/invoices/page.tsx) offers no control "
            + "for it. This is adjacent to audit finding P0-3 on circular conformance claims: the "
            + "one endpoint that could answer the question honestly is the one nothing asks."),
        new("GET /api/billing/ai-usage",
            "PENDING A DECISION, 2026-08-13 — per-organisation AI spend "
            + "(ProcuLink.Api/Controllers/BillingController.cs:187). "
            + "project-proculink/src/lib/api/billing.ts:73 reads /api/billing/status and nothing "
            + "reads this, so the cost surface exists and is never shown. Decide whether AI usage "
            + "is customer-visible."),
        new("POST /api/billing/pilot/request-extension",
            "PENDING A DECISION, 2026-08-13 — a Pilot whose trial has ended can ask for an "
            + "extension, and no surface asks. The expiry copy in project-proculink/src/lib/plans.ts "
            + "offers only Upgrade, so either the billing screen grows the ask or this endpoint "
            + "goes."),
        // ── Infrastructure probes ────────────────────────────────────────────────────────
        new("GET /health",
            "PENDING A DECISION, 2026-08-13 — the liveness endpoint. Nothing in either repository "
            + "fetches it: the frontend's /operations/health is a PAGE route, and the recorded "
            + "checks in CLAUDE.md are hand-run curls against https://api.proculink.eu/health. Its "
            + "real callers are Railway and the uptime workflow, which are configuration rather than "
            + "code — so this entry is 'nobody has written down who probes us', which is a decision "
            + "owed rather than a fact established. Move it to MachineFacing with the platform "
            + "config cited, or point .github/workflows/uptime.yml at it in a way a reader can see."),
    ];

    /// <summary>Both lists, keyed. The union is what the sweep will not fail on.</summary>
    private static IReadOnlyDictionary<string, AccountedEndpoint> AccountedFor =>
        MachineFacing.Concat(UncalledPendingDecision).ToDictionary(e => e.Key, e => e, StringComparer.Ordinal);

    /// <summary>
    /// The union as landed, 2026-08-15 (was 2026-08-13; moved once, for the entry named below). <b>SHRINK-ONLY.</b> An entry may be deleted — when the
    /// endpoint gains a caller, or is removed — but nothing may ever be added: a NEW caller-less
    /// endpoint has to be fixed, not excused.
    ///
    /// <para>Editing this set to make a build green is the one thing this file exists to prevent.
    /// If a genuinely caller-less endpoint is unavoidable — another provider webhook, another
    /// machine-facing ingress — that is a conversation with a reviewer, in the PR, in the open.</para>
    /// </summary>
    private static readonly IReadOnlySet<string> BaselineAt_2026_08_15 =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "DELETE /api/admin/organisations/{}/orders/{}",
            "DELETE /api/suppliers/{}/po-mapping/output-tree",
            "GET /api/admin/item-mapping-twins",
            "GET /api/admin/job-failures",
            "GET /api/auto-send/dry-runs",
            "GET /api/auto-send/dry-runs/summary",
            "GET /api/billing/ai-usage",
            "GET /api/dev/files/{**}",
            "GET /api/ingress/{}/ping",
            // Moved into the baseline on 2026-08-15, deliberately and in the open, exactly as the
            // failure text asks: the Postmark bounce webhook is caller-less BY CONSTRUCTION — the
            // caller is the mail provider. It is the second provider webhook here, beside Stripe's.
            "POST /api/inbound-email/postmark-bounce",
            "GET /api/invoices/{}/validate-peppol",
            "GET /api/order-confirmations/{}",
            "GET /api/orders/{}/ai-decisions",
            "GET /api/orders/{}/delivery-attempts",
            "GET /api/orders/{}/status",
            "GET /api/rule-definitions",
            "GET /api/rule-definitions/{}",
            "GET /api/suppliers/{}/acceptance-profile/versions",
            "GET /health",
            "POST /api/admin/organisations/{}/account-status",
            "POST /api/admin/organisations/{}/orders/bulk-erase",
            "POST /api/admin/organisations/{}/retention",
            "POST /api/billing/pilot/request-extension",
            "POST /api/billing/webhook",
            "POST /api/connections/{}/revisions/{}/archive",
            "POST /api/connections/{}/revisions/{}/publish",
            "POST /api/ingress/{}/catalog/{}",
            "POST /api/orders/{}/acceptance-gate/override",
            "POST /api/orders/{}/confirmation",
            "POST /api/orders/{}/confirmation/upload",
            "POST /api/orders/{}/mark-rejected",
            "POST /api/schema/infer",
            "POST /api/schema/propose-mapping",
            "POST /api/suppliers/{}/po-mapping/test",
            "POST /api/suppliers/{}/profiles",
        };

    /// <summary>
    /// The endpoint-registering calls this service is allowed to make outside MVC, and who reaches
    /// each. The inventory above reads MVC actions; anything registered another way is invisible to
    /// it, so a NEW mechanism must fail rather than pass unnoticed.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> NonMvcEndpointMechanisms =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MapControllers"] =
                "The MVC endpoints themselves — the ones this guard inventories. Not an exception.",
            ["MapHealthChecks"] =
                "Program.cs registers /health/ready for the platform's readiness probe. Its caller "
                + "is Railway's deployment health check, which is configuration rather than code. "
                + "Named here because no MVC action backs it and nothing else would notice it.",
            ["MapScalarApiReference"] =
                "The API reference UI, registered only in the non-production branch of Program.cs. "
                + "Reached by a developer with a browser.",
        };

    /// <summary>
    /// Identifiers an <c>IEndpointRouteBuilder</c> is held in. A <c>Map*</c> call on one of these
    /// registers a route by definition, whatever it is called — which is how a mechanism nobody has
    /// met yet still fails this guard.
    /// </summary>
    private static readonly IReadOnlySet<string> EndpointRouteBuilderReceivers =
        new HashSet<string>(StringComparer.Ordinal) { "app", "endpoints", "routes", "builder", "group", "host" };

    /// <summary>
    /// ASP.NET's own endpoint-mapping names, flagged wherever they appear — including inside an
    /// extension method where the receiver is a parameter with some local name.
    /// </summary>
    private static readonly IReadOnlySet<string> KnownEndpointMapMethods =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch", "MapMethods", "MapGroup",
            "MapControllers", "MapControllerRoute", "MapDefaultControllerRoute", "MapAreaControllerRoute",
            "MapRazorPages", "MapHub", "MapHealthChecks", "MapFallback", "MapFallbackToFile",
            "MapGrpcService", "MapReverseProxy", "MapStaticAssets", "MapOpenApi", "MapSwagger",
            "MapScalarApiReference", "MapHangfireDashboard", "MapPrometheusScrapingEndpoint",
        };

    // The scan is the expensive part; every test in this file shares one.
    private static readonly Lazy<ReachabilityScan> Scan = new(ReachabilityScan.Run, isThreadSafe: true);

    // Anti-vacuity floors. Every one is a real, currently-true measurement, and every one is a
    // count of something the DETECTOR itself produced — not a count of files on disk. A prior
    // packet in this repo re-pointed a sweep at a project with no matches and its file-count floor
    // passed anyway; a file-count floor proves the directory exists, not that anything was found.
    private const int MinimumEndpoints = 140;          // 165 today
    private const int MinimumControllers = 30;         // 37 today
    private const int MinimumCallerFiles = 300;        // 418 today
    private const long MinimumCallerBytes = 2_000_000; // 3.30 MB today
    private const int MinimumCallsExtracted = 100;     // 159 today
    private const int MinimumDistinctCalledEndpoints = 80;

    // ── The guard ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryEndpoint_HasACaller_OrIsDeclared()
    {
        var scan = Scan.Value;
        AssertTheSweepReallySwept(scan);

        var accounted = AccountedFor;
        var unexplained = scan.Unreachable.Where(e => !accounted.ContainsKey(e.Key)).ToList();

        if (unexplained.Count > 0)
        {
            Assert.Fail(Render(unexplained, scan.Endpoints.Count, scan.CallerFiles.Count, scan.Calls.Count));
        }
    }

    /// <summary>
    /// The audit's own finding, pinned. These are the two endpoints §3 pointed at when it said "two
    /// documented recovery doors have zero callers", and they are the reason this file exists. If
    /// either gains a control, delete its declaration AND its line here — the deletion is the win.
    /// If the DETECTOR stops seeing them, the guard has regressed to the false green it replaced.
    /// </summary>
    [Fact]
    public void Guard_SeesTheTwoRecoveryDoorsTheAuditNamed()
    {
        var unreachable = Scan.Value.Unreachable.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("POST /api/orders/{}/acceptance-gate/override", unreachable);
        Assert.Contains("DELETE /api/suppliers/{}/po-mapping/output-tree", unreachable);
    }

    /// <summary>
    /// A sweep that found nothing would satisfy every assertion above by having compared nothing.
    /// Each floor here counts something a detector produced.
    /// </summary>
    private static void AssertTheSweepReallySwept(ReachabilityScan scan)
    {
        Assert.True(scan.Endpoints.Count >= MinimumEndpoints,
            $"the route table resolved only {scan.Endpoints.Count} endpoint(s) — the MVC inventory is "
            + $"broken, not the API (expected at least {MinimumEndpoints})");

        Assert.True(scan.CallerFiles.Count >= MinimumCallerFiles,
            $"only {scan.CallerFiles.Count} caller file(s) were read from {scan.FrontendRoot} "
            + $"(expected at least {MinimumCallerFiles}) — with a truncated corpus EVERY endpoint "
            + "looks uncalled");

        Assert.True(scan.CallerBytes >= MinimumCallerBytes,
            $"only {scan.CallerBytes} byte(s) of caller source were read (expected at least "
            + $"{MinimumCallerBytes}). A file count alone cannot catch a corpus that was found but "
            + "not read, or a comment stripper that swallowed its input.");

        Assert.True(scan.Calls.Count >= MinimumCallsExtracted,
            $"the extractor found only {scan.Calls.Count} API call(s) in {scan.CallerFiles.Count} "
            + $"files (expected at least {MinimumCallsExtracted}). This is the floor that matters: "
            + "a corpus can be present, large and completely unparsed.");

        var reached = scan.Endpoints.Count - scan.Unreachable.Count;
        Assert.True(reached >= MinimumDistinctCalledEndpoints,
            $"only {reached} endpoint(s) were matched to a caller (expected at least "
            + $"{MinimumDistinctCalledEndpoints}) — the MATCHER is broken, which would report the "
            + "whole API as unreachable and the whole API as a finding");
    }

    // ── The inventory's own coverage ─────────────────────────────────────────────────────

    [Fact]
    public void TheInventory_IsResolvedByRouting_AndCoversEveryController()
    {
        var scan = Scan.Value;

        var controllers = scan.Endpoints.Select(e => e.Site.Split('.')[0]).Distinct().ToList();
        Assert.True(controllers.Count >= MinimumControllers,
            $"only {controllers.Count} controller(s) contributed endpoints (expected at least "
            + $"{MinimumControllers}) — application-part discovery is broken");

        // Every action must have resolved to a real template. An action with none is conventionally
        // routed, which this service does not use and this scanner does not model.
        Assert.DoesNotContain(scan.Endpoints, e => e.Path == "/(NO-ATTRIBUTE-ROUTE)");

        // Absolute templates DISCARD the controller prefix. Three actions rely on it, and a
        // hand-rolled combiner is exactly where a source-text scanner gets this wrong: it would
        // report /api/dashboard/api/orders/summary, which nothing calls, as a finding.
        Assert.Contains(scan.Endpoints, e => e.Key == "GET /api/orders/summary" && e.Site == "DashboardController.GetSummary");
        Assert.Contains(scan.Endpoints, e => e.Key == "GET /health" && e.Site == "HealthController.Health");
    }

    /// <summary>
    /// The rotation this guard would otherwise be blind to: an endpoint added by something that is
    /// not an MVC action. <c>IActionDescriptorCollectionProvider</c> cannot see a minimal-API
    /// registration, so a route added with <c>app.MapPost(…)</c> would be unguarded from the day it
    /// shipped — the same "invisible to both" shape the audit named, one level down.
    ///
    /// <para>So the mechanisms are enumerated instead, and silence fails: any <c>Map*(</c> call in
    /// the API project that is not declared in <see cref="NonMvcEndpointMechanisms"/> fails this
    /// test, whether or not it registers an endpoint. Waving through the ones that do not is how
    /// the ones that do get waved through too.</para>
    /// </summary>
    [Fact]
    public void NoEndpointArrivesThroughAMechanismTheInventoryCannotSee()
    {
        var root = OrphanDetector.FindRepoRoot();
        var apiDirectory = Path.Combine(root, "ProcuLink.Api");

        // Two ways in, because either alone has a hole. Matching only the KNOWN names would miss a
        // mechanism nobody has met yet, which is the whole point. Matching every `Map*` call would
        // flag `StripeBillingMapping.MapPriceIdToPlan(…)` — a domain verb, not a route — and a
        // guard that cries wolf gets an exemption added and then stops being read. So: any `Map*`
        // on an endpoint-route-builder receiver, plus the ASP.NET mapping names wherever they
        // appear.
        var mapCall = new Regex(@"\b([A-Za-z_]\w*)\s*\.\s*(Map[A-Z][A-Za-z0-9_]*)\s*[\(<]");
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var filesScanned = 0;
        long bytesScanned = 0;

        foreach (var path in Directory.EnumerateFiles(apiDirectory, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path);
            if (relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = OrphanDetector.StripComments(File.ReadAllText(path));
            filesScanned++;
            bytesScanned += text.Length;

            foreach (Match m in mapCall.Matches(text))
            {
                var receiver = m.Groups[1].Value;
                var method = m.Groups[2].Value;

                if (EndpointRouteBuilderReceivers.Contains(receiver) || KnownEndpointMapMethods.Contains(method))
                {
                    found.TryAdd(method, relative);
                }
            }
        }

        Assert.True(filesScanned > 30, $"only {filesScanned} API source file(s) scanned — the sweep is broken");
        Assert.True(bytesScanned > 200_000, $"only {bytesScanned} byte(s) of API source read — the sweep is broken");

        // Anti-vacuity: the detector must be able to FIND the mechanisms that are really there. A
        // regex that matched nothing would otherwise report a clean sweep for ever.
        Assert.Contains("MapControllers", found.Keys);
        Assert.Contains("MapHealthChecks", found.Keys);

        var undeclared = found
            .Where(f => !NonMvcEndpointMechanisms.ContainsKey(f.Key))
            .Select(f => $"  • {f.Key}(…)  first seen in {f.Value}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        if (undeclared.Count > 0)
        {
            Assert.Fail(
                "ProcuLink.Api registers endpoints through a mechanism this guard's inventory cannot "
                + "see. The inventory reads MVC action descriptors; a minimal-API route, a hub, or a "
                + "reverse proxy mapped here would be reachable in production and absent from every "
                + "reachability check.\n\n"
                + string.Join(Environment.NewLine, undeclared) + "\n\n"
                + "Either register it as a controller action, or add it to NonMvcEndpointMechanisms "
                + "with a written reason naming who calls it — and say in the PR how the new "
                + "mechanism's endpoints are covered.");
        }
    }

    // ── The caller corpus's own coverage ─────────────────────────────────────────────────

    /// <summary>
    /// The exclusions are load-bearing and each one is a way this guard could go quietly false-green,
    /// so they are asserted rather than trusted to a comment.
    /// </summary>
    [Theory]
    [InlineData("src/test/endpoint-reachability.test.ts", true, "the frontend's own guard names every route it knows")]
    [InlineData("src/mocks/handlers.ts", true, "MSW stands in for the API rather than calling it")]
    [InlineData("src/lib/api-client.test.ts", true, "a test is not a user path")]
    [InlineData("src/components/bridge/Foo.spec.tsx", true, "same")]
    [InlineData("src/types/api.d.ts", true, "declarations call nothing")]
    [InlineData(".claude/worktrees/other-session/src/lib/api-client.ts", true, "a worktree is a COPY of this repo")]
    [InlineData("node_modules/whatever/index.ts", true, "not ours")]
    [InlineData("src/lib/api-client.ts", false, "the main caller")]
    [InlineData("src/lib/api/delivery.ts", false, "four endpoints, and the string /api/ zero times")]
    [InlineData("scripts/live-matrix/ingest-harness.mjs", false, "the only caller of the ingress API")]
    public void TheCorpus_ExcludesWhatOnlyLooksLikeACaller(string relativePath, bool excluded, string why)
    {
        Assert.True(IsExcludedCallerFile(relativePath) == excluded, why);
    }

    /// <summary>
    /// The corpus has to contain the files the caller definition claims it contains. A rename in
    /// the frontend that emptied one of the two roots would otherwise cost a wave of false findings
    /// with no explanation.
    /// </summary>
    [Fact]
    public void TheCorpus_HoldsBothRoots()
    {
        var files = Scan.Value.CallerFiles.Select(f => f.RelativePath.Replace('\\', '/')).ToList();

        Assert.Contains("src/lib/api-client.ts", files);
        Assert.Contains("src/lib/api/delivery.ts", files);
        Assert.True(files.Any(f => f.StartsWith("scripts/live-matrix/", StringComparison.Ordinal)),
            "the live-matrix harnesses are gone from the corpus — they are the only in-repo caller "
            + "of the ingress API and of the Postmark webhook, so losing them turns two working "
            + "integrations into findings");
    }

    // ── The declaration lists cannot rot ─────────────────────────────────────────────────

    [Fact]
    public void Declarations_AreNotEmpty_AndTheTwoListsStaySeparate()
    {
        // Non-empty is not decoration: an empty list plus a broken sweep is indistinguishable from
        // a clean API, and this is the assertion that tells them apart.
        Assert.NotEmpty(MachineFacing);
        Assert.NotEmpty(UncalledPendingDecision);

        var machine = MachineFacing.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var pending = UncalledPendingDecision.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(machine.Intersect(pending, StringComparer.Ordinal));

        var duplicates = MachineFacing.Concat(UncalledPendingDecision)
            .GroupBy(e => e.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    /// <summary>
    /// A reason has to carry a date AND cite something a reviewer can open. Both, not either: a
    /// date alone satisfies any regex that accepts one and says nothing about whether the claim was
    /// checked, and every entry here carries a date by convention, so "date or file" would be a bar
    /// nothing could fail. "Appears unused" is not a reason — name the caller you failed to find,
    /// and where you looked for it.
    /// </summary>
    [Fact]
    public void Declarations_CarryAReasonThatCitesSomething()
    {
        var date = new Regex(@"\d{4}-\d{2}-\d{2}");
        var openable = new Regex(@"\.(cs|ts|tsx|mjs|mdx|md|yml|json)\b|https?://");

        var thin = MachineFacing.Concat(UncalledPendingDecision)
            .Where(e => e.Reason.Length < 80 || !date.IsMatch(e.Reason) || !openable.IsMatch(e.Reason))
            .Select(e => $"  • {e.Key} — needs a date AND a file, path or URL a reviewer can open")
            .ToList();

        Assert.True(thin.Count == 0,
            "Declared endpoints with an unciteable reason:" + Environment.NewLine
            + string.Join(Environment.NewLine, thin));

        // Anti-vacuity: the citation pattern has to be capable of REJECTING something, or this
        // test passes by matching everything. A bare date is exactly the shape it must refuse.
        Assert.DoesNotMatch(openable, "PENDING A DECISION, 2026-08-13 — nobody calls it.");
    }

    /// <summary>
    /// Once an endpoint is deleted or renamed — which is one of the two good outcomes — its entry
    /// must go too, or the next reader inherits a list of ghosts and stops trusting any of it.
    /// </summary>
    [Fact]
    public void Declarations_StillNameRealEndpoints()
    {
        var scan = Scan.Value;
        AssertTheSweepReallySwept(scan);

        var real = scan.Endpoints.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

        var ghosts = MachineFacing.Concat(UncalledPendingDecision)
            .Where(e => !real.Contains(e.Key))
            .Select(e => $"  • '{e.Key}' is no longer an endpoint — delete its entry; the door it "
                         + "excused is gone.")
            .ToList();

        Assert.True(ghosts.Count == 0,
            "The declaration list has rotted:" + Environment.NewLine + string.Join(Environment.NewLine, ghosts));
    }

    /// <summary>
    /// The other half of "cannot rot": an entry that is no longer uncalled is a stale excuse, and a
    /// stale excuse is how a list like this stops meaning anything. Deleting the line is the fix,
    /// and it is also how the list reports progress.
    /// </summary>
    [Fact]
    public void Declarations_AreStillUncalled()
    {
        var scan = Scan.Value;
        AssertTheSweepReallySwept(scan);

        var stillUncalled = scan.Unreachable.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

        var stale = MachineFacing.Concat(UncalledPendingDecision)
            .Where(e => !stillUncalled.Contains(e.Key))
            .Select(e => $"  • '{e.Key}' now has a caller — delete its entry.")
            .ToList();

        Assert.True(stale.Count == 0,
            "The declaration list has stale entries. That is good news: each line below is an "
            + "endpoint that gained a way in." + Environment.NewLine
            + string.Join(Environment.NewLine, stale));
    }

    /// <summary>
    /// Shrink-only. The guard has to protect against NEW caller-less endpoints from the day it
    /// lands, which it cannot do if the declaration list is a place to put them.
    /// </summary>
    [Fact]
    public void Declarations_MayOnlyEverShrink()
    {
        var added = MachineFacing.Concat(UncalledPendingDecision)
            .Select(e => e.Key)
            .Where(key => !BaselineAt_2026_08_15.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        if (added.Count > 0)
        {
            Assert.Fail(
                "The declaration list may only ever SHRINK. These entries are new since the "
                + "2026-08-15 baseline:" + Environment.NewLine
                + string.Join(Environment.NewLine, added.Select(a => $"  • {a}")) + Environment.NewLine
                + Environment.NewLine
                + "A new endpoint nothing can reach is the defect this guard exists to catch. Give it "
                + "a caller, or delete it. If it genuinely must be caller-less — another provider "
                + "webhook, another machine-facing ingress — say so in the PR and move the baseline "
                + "deliberately. Do not slip it in.");
        }

        Assert.True(AccountedFor.Count <= BaselineAt_2026_08_15.Count,
            $"the declaration list grew from {BaselineAt_2026_08_15.Count} to {AccountedFor.Count}");
    }

    // ── The detector itself, on synthetic input ──────────────────────────────────────────

    private static ApiEndpoint Endpoint(string method, string template, string site = "FixtureController.Action") =>
        new(method, NormalisePath(template), template, site);

    private static IReadOnlyList<CallerReference> Calls(string source) =>
        ExtractCalls(StripJsComments(source), "fixture.ts");

    /// <summary>
    /// Proves the detector can see BOTH answers, through the same functions the real sweep runs. A
    /// guard that is green because it detects nothing is worse than no guard.
    /// </summary>
    [Fact]
    public void Detector_CreditsACalledEndpoint_AndFlagsTheOneNothingCalls()
    {
        var endpoints = new[]
        {
            Endpoint("GET", "api/widgets/{id:guid}"),
            Endpoint("DELETE", "api/widgets/{id:guid}/promoted-layout"),
        };

        var calls = Calls("""
            export async function getWidget(id: string) {
              return fetch(`${API_BASE_URL}/api/widgets/${id}`, { headers: await authHeader() });
            }
            """);

        var unreachable = Unreachable(endpoints, calls);

        Assert.Equal(["DELETE /api/widgets/{}/promoted-layout"], unreachable.Select(e => e.Key));
        Assert.Contains("promoted-layout", Render(unreachable, endpoints.Length, 1, calls.Count));
    }

    /// <summary>
    /// <b>The entire defect class, in one fixture.</b> A route named in prose is not a caller, and
    /// every shape here is real: <c>api-client.ts:1300</c> documents the supplier-profiles POST in
    /// a comment beside two GET callers, <c>PassportService.cs:168</c> puts an endpoint in an
    /// operator hint, and <c>AdminController.cs:539</c> puts two into error messages. To a text
    /// scanner all three read exactly like call sites.
    /// </summary>
    [Fact]
    public void Detector_DoesNotCreditARouteNamedInProse()
    {
        var endpoints = new[] { Endpoint("POST", "api/widgets/{id:guid}/promote") };

        var calls = Calls("""
            // POST /api/widgets/{id}/promote — promotes the layout. Not wired up yet.
            /**
             * See also `POST /api/widgets/${id}/promote`, which the operator can curl:
             *   await fetch(`${API_BASE_URL}/api/widgets/${id}/promote`, { method: "POST" });
             */
            export const NOTE = "there is no caller for this one";
            """);

        Assert.Empty(calls);
        Assert.Equal(["POST /api/widgets/{}/promote"], Unreachable(endpoints, calls).Select(e => e.Key));
    }

    /// <summary>
    /// A URL BUILT FOR DISPLAY is not a call. This is the shape that keeps the ingress endpoints
    /// honest: <c>settings/page.tsx:956</c> and <c>SupplierDockProfile.tsx:1243</c> assemble the
    /// customer's ingress URL so it can be shown and copied, never fetched. Reading the verb from
    /// the call's own options object is what tells the two apart — there are no options, so it
    /// scores GET, and the endpoint is a POST.
    /// </summary>
    [Fact]
    public void Detector_DoesNotCreditAUrlAssembledForDisplay()
    {
        var endpoints = new[] { Endpoint("POST", "api/ingress/{slug}/orders") };

        var calls = Calls("""
            export function IngressPanel({ slug }: { slug: string }) {
              const endpoint = slug ? `${API_BASE_URL}/api/ingress/${slug}/orders` : null;
              return <code>{endpoint ?? "not configured"}</code>;
            }
            """);

        Assert.Equal(["GET /api/ingress/{}/orders"], calls.Select(c => c.Key).ToArray());
        Assert.Equal(["POST /api/ingress/{}/orders"], Unreachable(endpoints, calls).Select(e => e.Key));
    }

    /// <summary>
    /// The comment stripper, on the two shapes that would let prose through: a doc comment naming a
    /// route with the right verb beside it, and a block comment that ends after the route.
    /// </summary>
    [Fact]
    public void Detector_DoesNotCreditACommentedOutCallSite()
    {
        var endpoints = new[] { Endpoint("POST", "api/widgets/{id:guid}/promote") };

        var calls = Calls("""
            export async function promote(id: string) {
              // await fetch(`${API_BASE_URL}/api/widgets/${id}/promote`, { method: "POST" });
              /* await fetch(`${API_BASE_URL}/api/widgets/${id}/promote`, { method: "POST" }); */
              throw new Error("not wired up");
            }
            """);

        Assert.Empty(calls);
        Assert.Equal(["POST /api/widgets/{}/promote"], Unreachable(endpoints, calls).Select(e => e.Key));
    }

    /// <summary>
    /// A computed segment does not satisfy a literal route segment. Without this refusal one
    /// template with a variable tail marks an entire controller reachable — the single loudest way
    /// this guard could go false-green.
    /// </summary>
    [Fact]
    public void Detector_DoesNotCreditAComputedSegmentAgainstALiteralRoute()
    {
        var endpoints = new[]
        {
            Endpoint("POST", "api/revisions/{id:guid}/publish"),
            Endpoint("POST", "api/revisions/{id:guid}/archive"),
            Endpoint("POST", "api/revisions/{id:guid}/rollback"),
        };

        var calls = Calls("""
            export async function act(id: string, action: "publish" | "archive") {
              return fetch(`${API_BASE_URL}/api/revisions/${id}/${action}`, { method: "POST" });
            }
            """);

        Assert.Equal(3, Unreachable(endpoints, calls).Count);
    }

    /// <summary>
    /// …but a route PARAMETER is satisfied by anything, including a literal. Losing this makes the
    /// guard cry wolf on every id-bearing endpoint, which is how a guard gets switched off.
    /// </summary>
    [Fact]
    public void Detector_CreditsARouteParameterFromEitherALiteralOrAComputedSegment()
    {
        var endpoints = new[] { Endpoint("GET", "api/widgets/{id:guid}") };

        Assert.Empty(Unreachable(endpoints, Calls("fetch(`${API_BASE_URL}/api/widgets/${id}`);")));
        Assert.Empty(Unreachable(endpoints, Calls("fetch(`${API_BASE_URL}/api/widgets/sample`);")));
    }

    /// <summary>
    /// The verb is read from the call's OWN options object. A window that ran on to the next call
    /// would let one POST mark every neighbouring GET as written, and vice versa.
    /// </summary>
    [Fact]
    public void Detector_ReadsEachCallsVerbFromItsOwnOptions()
    {
        var calls = Calls("""
            export async function two(id: string) {
              await fetch(`${API_BASE_URL}/api/widgets/${id}/promote`, { method: "POST" });
              await fetch(`${API_BASE_URL}/api/widgets/${id}/history`, { headers });
            }
            """);

        Assert.Equal(
            ["POST /api/widgets/{}/promote", "GET /api/widgets/{}/history"],
            calls.Select(c => c.Key).ToArray());
    }

    /// <summary>
    /// A module-private wrapper that prefixes the base itself. Its NAME is captured, not assumed:
    /// <c>mapping.ts</c> declares <c>apiFetch</c> AND <c>magicFetch</c>, and hard-coding the first
    /// silently drops the two endpoints reached through the second.
    /// </summary>
    [Fact]
    public void Detector_CreditsEndpointsReachedThroughAPrefixingWrapper()
    {
        var endpoints = new[]
        {
            Endpoint("DELETE", "api/suppliers/{id:guid}/po-mapping"),
            Endpoint("GET", "api/suppliers/{id:guid}/mapping/source-columns"),
        };

        var calls = Calls("""
            async function apiFetch<T>(path: string, init?: RequestInit): Promise<T> {
              const auth = await authHeader();
              const res = await fetch(`${API_BASE_URL}/api${path}`, { ...init, headers: auth });
              return res.json() as Promise<T>;
            }

            const MAGIC_TIMEOUT_MS = 8000;

            async function magicFetch(path: string, init?: RequestInit): Promise<Response> {
              const auth = await authHeader();
              const controller = new AbortController();
              const timer = setTimeout(() => controller.abort(), MAGIC_TIMEOUT_MS);
              return await fetch(`${API_BASE_URL}/api${path}`, { ...init, headers: auth });
            }

            export const clear = (id: string) =>
              apiFetch<void>(`/suppliers/${id}/po-mapping`, { method: "DELETE" });

            export const columns = (id: string) => magicFetch(`/suppliers/${id}/mapping/source-columns`);
            """);

        Assert.Empty(Unreachable(endpoints, calls));
    }

    /// <summary>
    /// One hop of PATH indirection, in both shapes the frontend uses it: as a bare argument and
    /// interpolated with a tail. The bare shape is how the catalog-source module writes its PUT and
    /// its DELETE, and it carries the verb in the same options object.
    /// </summary>
    [Fact]
    public void Detector_CreditsEndpointsReachedThroughAPathHelper()
    {
        var endpoints = new[]
        {
            Endpoint("PUT", "api/suppliers/{id:guid}/catalog/source"),
            Endpoint("DELETE", "api/suppliers/{id:guid}/catalog/source"),
            Endpoint("POST", "api/suppliers/{id:guid}/catalog/source/test-fetch"),
        };

        var calls = Calls("""
            const _mockSources: Record<string, unknown> = {};

            function basePath(supplierId: string): string {
              return `${API_BASE_URL}/api/suppliers/${supplierId}/catalog/source`;
            }

            export async function upsert(id: string, payload: unknown) {
              return fetchWithTimeout(basePath(id), { method: "PUT", body: JSON.stringify(payload) }, 30000);
            }

            export async function remove(id: string) {
              return fetchWithTimeout(basePath(id), { method: "DELETE" }, 30000);
            }

            export async function probe(id: string) {
              return fetch(`${basePath(id)}/test-fetch`, { method: "POST" });
            }
            """);

        Assert.Empty(Unreachable(endpoints, calls));
    }

    /// <summary>
    /// The concatenation form, which only <c>scripts/live-matrix/runner.js</c> uses. Both halves
    /// matter: the spliced middle segment, and the trailing <c>'…/' + id</c> that would otherwise
    /// leave the stump <c>/api/orders/</c> — a stump that credits <c>GET /api/orders</c> it never
    /// called while missing the endpoint it did.
    /// </summary>
    [Fact]
    public void Detector_CreditsAPathBuiltByConcatenation()
    {
        var endpoints = new[]
        {
            Endpoint("POST", "api/orders/{id:guid}/transform"),
            Endpoint("GET", "api/orders/{id:guid}"),
            Endpoint("GET", "api/orders"),
        };

        var calls = Calls("""
            var API_BASE = 'https://api.proculink.eu';
            await fetch(API_BASE + '/api/orders/' + orderId + '/transform?format=' + outFmt, { method: 'POST' });
            await fetch(API_BASE + '/api/orders/' + orderId, { headers: h });
            """);

        Assert.Equal(
            ["/api/orders/{}/transform", "/api/orders/{}"],
            calls.Select(c => c.Path).ToArray());

        // The stump must NOT have credited the collection endpoint.
        Assert.Equal(["GET /api/orders"], Unreachable(endpoints, calls).Select(e => e.Key));
    }

    /// <summary>
    /// A literal segment carrying an interpolated QUERY keeps its literal. Folding the whole
    /// segment to a wildcard turned <c>`…/api/exceptions${qs}`</c> into <c>/api/{}</c> — a call
    /// that reaches nothing, and two endpoints (<c>/api/exceptions</c>, <c>/api/ops/dead-letter</c>)
    /// reported unreachable while a screen was calling them.
    /// </summary>
    [Fact]
    public void Detector_KeepsALiteralSegmentThatCarriesAnInterpolatedQuery()
    {
        var endpoints = new[]
        {
            Endpoint("GET", "api/exceptions"),
            Endpoint("GET", "api/ops/dead-letter"),
        };

        var calls = Calls("""
            await fetch(`${API_BASE_URL}/api/exceptions${qs}`, { headers });
            await fetch(`${API_BASE_URL}/api/ops/dead-letter${qs}`, { headers });
            """);

        Assert.Empty(Unreachable(endpoints, calls));
    }

    /// <summary>
    /// A catch-all route swallows its tail. <c>api/dev/files/{**key}</c> is the only one, and a
    /// matcher that compared segment counts would report it unreachable for ever.
    /// </summary>
    [Fact]
    public void Detector_UnderstandsACatchAllRoute()
    {
        var endpoints = new[] { Endpoint("GET", "api/dev/files/{**key}") };

        Assert.Empty(Unreachable(endpoints, Calls("fetch(`${API_BASE_URL}/api/dev/files/orders/2026/x.csv`);")));
        Assert.NotEmpty(Unreachable(endpoints, Calls("fetch(`${API_BASE_URL}/api/dev/other/x.csv`);")));
    }

    // ── What this guard CANNOT see, pinned ───────────────────────────────────────────────

    /// <summary>
    /// <b>A dead branch reads as a live call.</b> Found by mutation, not by reading: deleting the
    /// only caller of <c>GET /api/ops/health</c> from a copy of the frontend turned this guard red
    /// on that exact endpoint; re-adding it as a comment, and as a call in a test file, and as an
    /// MSW handler, all kept it red — and re-adding it inside <c>if (false) { … }</c> turned it
    /// green.
    ///
    /// <para>That is the honest boundary of a textual sweep: it proves a path is NAMED in a
    /// call-shaped context, never that the call executes. Closing it would need reachability
    /// analysis over the frontend's own control flow, which is a different tool. Pinned here so the
    /// limit is a failing test away from being forgotten: if someone narrows the matcher and this
    /// starts failing, the paragraph above needs rewriting too.</para>
    /// </summary>
    [Fact]
    public void Detector_CannotTellADeadBranchFromALiveCall()
    {
        var endpoints = new[] { Endpoint("GET", "api/ops/health") };

        var calls = Calls("""
            export async function health() {
              if (FEATURE === "never") {
                return fetch(`${API_BASE_URL}/api/ops/health`, { headers });
              }
              throw new Error("the panel was removed");
            }
            """);

        Assert.Empty(Unreachable(endpoints, calls));
    }

    /// <summary>
    /// <b>A URL built only to be shown credits a GET.</b> The verb is what tells display apart from
    /// call — <c>Detector_DoesNotCreditAUrlAssembledForDisplay</c> shows it working, for a POST —
    /// and a GET endpoint has no such gap to fall through.
    ///
    /// <para>Tightening this would mean requiring the path literal to sit in argument position,
    /// and that is worse: <c>api-client.ts:1171</c> and <c>:2742</c> assign a URL to
    /// <c>const url</c> and pass the variable, so the rule would drop real callers to catch a rare
    /// display constant. Wrong trade, made deliberately, recorded here.</para>
    /// </summary>
    [Fact]
    public void Detector_CreditsADisplayOnlyUrlWhenTheEndpointIsAGet()
    {
        var endpoints = new[] { Endpoint("GET", "api/ops/health") };

        var calls = Calls("export const DOC_URL = `${API_BASE_URL}/api/ops/health`;");

        Assert.Empty(Unreachable(endpoints, calls));
    }

    // ── The comment stripper ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A template literal nested inside another template literal's <c>${…}</c> hole. This is not
    /// hypothetical — <c>api-client.ts:1171</c> builds a catalog query exactly this way — and a
    /// stripper that loses track of the nesting either eats live code or stops stripping.
    /// </summary>
    [Fact]
    public void StripJsComments_SurvivesATemplateLiteralNestedInAnInterpolation()
    {
        var stripped = StripJsComments("""
            const url = `${API_BASE_URL}/api/suppliers/${id}/catalog?take=${take}${q ? `&q=${enc(q)}` : ""}`;
            // gone
            const other = `${API_BASE_URL}/api/orders`;
            """);

        Assert.Contains("/api/suppliers/${id}/catalog", stripped, StringComparison.Ordinal);
        Assert.Contains("/api/orders", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("gone", stripped, StringComparison.Ordinal);
    }

    /// <summary>
    /// Comments must still actually be stripped — guards against "fixing" a literal bug by simply
    /// stripping less — and a URL inside a literal must survive, which is the failure that deleted
    /// 677 lines of the API composition root in the sibling guard.
    /// </summary>
    [Fact]
    public void StripJsComments_RemovesCommentsAndKeepsUrlsInsideLiterals()
    {
        var stripped = StripJsComments("""
            const origins = ["https://*.vercel.app"];
            const re = /https?:\/\//;
            await fetch(`${API_BASE_URL}/api/orders`); // gone-line
            /* gone-block
               still gone */
            """);

        Assert.Contains("https://*.vercel.app", stripped, StringComparison.Ordinal);
        Assert.Contains("/api/orders", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("gone-line", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("gone-block", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("still gone", stripped, StringComparison.Ordinal);
    }

    // ── Finding the frontend checkout ────────────────────────────────────────────────────

    /// <summary>
    /// <b>The resolver's own worktree-safety</b>, proven without needing a real worktree: a
    /// synthetic tree whose backend root sits at <c>&lt;sandbox&gt;/.claude/worktrees/&lt;name&gt;</c>,
    /// which is exactly where <c>git worktree</c> puts a checkout here, with the frontend beside
    /// <c>&lt;sandbox&gt;</c> where a developer really keeps it.
    ///
    /// <para>Only the immediate parent used to be tried, so from a worktree the sole candidate was
    /// <c>&lt;repo&gt;/.claude/worktrees/project-proculink</c> — a path that never exists. The
    /// resolver threw (it has no unable-to-check state, deliberately) and all six sweep-backed tests
    /// in this class failed on a clean checkout, in the workspace CLAUDE.md tells every parallel
    /// session to work in. That is how a real guard gets deleted rather than fixed.</para>
    ///
    /// <para>The configured path is passed in as <c>null</c> rather than cleared from the
    /// environment: <c>PROCULINK_FRONTEND_PATH</c> is process-global, CI sets it for the whole job
    /// (see the frontend-checkout step in <c>.github/workflows/ci.yml</c>), and a test that wrote it
    /// would answer for every class running beside it — the hazard
    /// <see cref="Meta.ProcessGlobalStateIsSerializedTests"/> exists for.</para>
    /// </summary>
    [Fact]
    public void TheResolver_FindsTheFrontendWhenTheCheckoutItselfSitsUnderDotClaudeWorktrees()
    {
        var sandbox = NewSandbox();

        try
        {
            var frontend = Path.Combine(sandbox, "project-proculink");
            WriteSyntheticFrontend(frontend);

            var backendRoot = Path.Combine(sandbox, ".claude", "worktrees", "pensive-chebyshev");
            Directory.CreateDirectory(backendRoot);

            Assert.Equal(
                Path.GetFullPath(frontend),
                FindFrontendRoot(backendRoot, configuredPath: null));
        }
        finally
        {
            Delete(sandbox);
        }
    }

    /// <summary>
    /// Widening the candidate list must not have weakened the gate on each candidate. A directory
    /// named <c>project-proculink</c> that is not the frontend — here a stale checkout with a
    /// <c>package.json</c> and no <c>src/lib/api-client.ts</c> — must be walked past rather than
    /// accepted, and the search must go on to the real one further up.
    ///
    /// <para>Accepting it would be worse than the original defect: the sweep would load an empty
    /// caller corpus and report every endpoint in the API unreachable, and the fix for THAT reads
    /// as "the guard is noisy, declare them all".</para>
    /// </summary>
    [Fact]
    public void TheResolver_WalksPastADirectoryNamedLikeTheFrontendThatIsNot()
    {
        var sandbox = NewSandbox();

        try
        {
            var frontend = Path.Combine(sandbox, "project-proculink");
            WriteSyntheticFrontend(frontend);

            var decoy = Path.Combine(sandbox, ".claude", "worktrees", "project-proculink");
            Directory.CreateDirectory(decoy);
            File.WriteAllText(Path.Combine(decoy, "package.json"), "{}\n");

            var backendRoot = Path.Combine(sandbox, ".claude", "worktrees", "pensive-chebyshev");
            Directory.CreateDirectory(backendRoot);

            Assert.Equal(
                Path.GetFullPath(frontend),
                FindFrontendRoot(backendRoot, configuredPath: null));
        }
        finally
        {
            Delete(sandbox);
        }
    }

    /// <summary>
    /// <b>There is still no unable-to-check state.</b> The whole point of this resolver is that it
    /// refuses to shrug — <c>backendMirror.test.ts</c> was <c>skipIf(!BACKEND)</c> and reported
    /// green for months having compared nothing. A wider search is one edit away from "…and if we
    /// still cannot find it, carry on", so the throw is pinned rather than left to prose.
    ///
    /// <para>The backend root is the filesystem root, which is the one input with NO ancestors and
    /// therefore no candidates at all. A temp-directory sandbox would have been the natural fixture
    /// and is the wrong one: the walk climbs past the sandbox to the drive, so a developer who
    /// happens to keep a project-proculink checkout in their home directory — above
    /// <c>%TEMP%</c> — would see this test go red for a reason that has nothing to do with the
    /// code. Producing a false red in one workspace is the defect this whole change removes from
    /// another.</para>
    /// </summary>
    [Fact]
    public void TheResolver_StillThrowsWhenThereIsNowhereLeftToLook()
    {
        var filesystemRoot = Path.GetPathRoot(Path.GetTempPath())!;

        Assert.Null(Directory.GetParent(filesystemRoot));

        var thrown = Assert.Throws<InvalidOperationException>(
            () => FindFrontendRoot(filesystemRoot, configuredPath: null));

        Assert.Contains("PROCULINK_FRONTEND_PATH", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>A private temp root, so the ancestor walk cannot reach another test's tree.</summary>
    private static string NewSandbox() =>
        Path.Combine(Path.GetTempPath(), $"plk-frontendroot-{Guid.NewGuid():N}");

    /// <summary>The two files <c>LooksLikeTheFrontend</c> demands, and nothing else.</summary>
    private static void WriteSyntheticFrontend(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "src", "lib"));
        File.WriteAllText(Path.Combine(root, "package.json"), """{ "name": "project-proculink" }""");
        File.WriteAllText(Path.Combine(root, "src", "lib", "api-client.ts"), "// synthetic\n");
    }

    private static void Delete(string sandbox)
    {
        if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────

    /// <summary>An endpoint with no in-repo caller, and the prose that accounts for it.</summary>
    private sealed record AccountedEndpoint(string Key, string Reason);
}

/// <summary>One sweep of both repositories, shared by every test in the guard.</summary>
public sealed record ReachabilityScan(
    string FrontendRoot,
    IReadOnlyList<ApiEndpoint> Endpoints,
    IReadOnlyList<CallerFile> CallerFiles,
    IReadOnlyList<CallerReference> Calls,
    IReadOnlyList<ApiEndpoint> Unreachable)
{
    public long CallerBytes => CallerFiles.Sum(f => (long)f.Text.Length);

    public static ReachabilityScan Run()
    {
        var frontendRoot = EndpointReachabilityScanner.FindFrontendRoot(OrphanDetector.FindRepoRoot());
        var endpoints = EndpointReachabilityScanner.Endpoints(typeof(OrdersController).Assembly);
        var files = EndpointReachabilityScanner.LoadCallerCorpus(frontendRoot);

        var calls = files
            .SelectMany(f => EndpointReachabilityScanner.ExtractCalls(f.Text, f.RelativePath))
            .ToList();

        return new ReachabilityScan(
            frontendRoot, endpoints, files, calls,
            EndpointReachabilityScanner.Unreachable(endpoints, calls));
    }
}
