using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;

namespace ProcuLink.Api.Services;

/// <summary>
/// Internal sub-service of <see cref="OrderService"/> owning the transform-to-artifact
/// path (generate output document, upload, advance to ready_to_deliver). Method moved
/// verbatim from the original God-class; only the host type and the shared-helper call
/// site changed (audit W1/B1 decomposition).
///
/// <para><b>Idempotency:</b> <see cref="TransformAsync"/> atomically claims the order
/// (ready/transforming → transforming) before generating anything; a duplicated Hangfire
/// run or a concurrent enqueue on an already-transformed order returns a
/// <see cref="TransformResponse.Skipped"/> no-op instead of uploading a duplicate
/// artifact and re-triggering delivery.</para>
/// </summary>
internal sealed class OrderTransformService
{
    /// <summary>
    /// Serializer for the supplier-promoted output mapping's provenance digest descriptor. CamelCase
    /// matches how <c>PoMappingService</c> persists <c>SupplierPoMapping.ConfigJson</c>, so the
    /// digested text is a stable representation of the stored supplier mapping.
    /// </summary>
    private static readonly JsonSerializerOptions SupplierOutputSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Deserializer for a pinned revision's <c>output_mapping_json</c> snapshot (launch batch 7).
    /// Mirrors <c>ReplayService.DeserializeOutputConfig</c> so the live transform and the replay
    /// engine read the SAME snapshot identically.
    /// </summary>
    private static readonly JsonSerializerOptions RevisionOutputSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ProcuLinkDbContext              _db;
    private readonly IFileStorageService             _fileStorage;
    private readonly IEnumerable<ITransformService>  _transformers;
    private readonly ILogger<OrderService>           _logger;
    private readonly IPoMappingService               _poMappings;
    private readonly OrderServiceShared              _shared;
    private readonly IEffectiveConnectionConfigResolver? _effectiveConfig;
    private readonly ICxmlCredentialResolver?        _cxmlResolver;
    private readonly IAcceptanceGate                 _acceptanceGate;

    public OrderTransformService(
        ProcuLinkDbContext             db,
        IFileStorageService            fileStorage,
        IEnumerable<ITransformService> transformers,
        ILogger<OrderService>          logger,
        IPoMappingService              poMappings,
        OrderServiceShared             shared,
        IAcceptanceGate                acceptanceGate,
        IEffectiveConnectionConfigResolver? effectiveConfig = null,
        ICxmlCredentialResolver?       cxmlResolver = null)
    {
        _db           = db;
        _fileStorage  = fileStorage;
        _transformers = transformers;
        _logger       = logger;
        _poMappings   = poMappings;
        // WP-17 — the server-side acceptance gate. NOT nullable on purpose: an optional gate that
        // defaults to null is an enforcement switch that turns itself off in any host that forgets
        // to register it, which is the exact failure this work package exists to close. OrderService
        // constructs a real one when DI supplies none, so there is no "gate absent" state to reason
        // about.
        _acceptanceGate = acceptanceGate;
        // Best-effort exception reconcile — the terminal-failure path opens the operator-workable
        // transform_failed exception through the SAME helper the parse-failure path uses.
        _shared       = shared;
        // Launch batch 7 — revision authority. Null (older positional ctors / unregistered
        // hosts) behaves exactly like flag-OFF: the live path drives everything.
        _effectiveConfig = effectiveConfig;
        // cXML network credentials. Null (older positional ctors / unregistered hosts) → the cXML
        // transform falls back to the legacy OrgId/SupplierId GUID identities.
        _cxmlResolver = cxmlResolver;
    }

    // ── TransformAsync ────────────────────────────────────────────────────────

    public async Task<Result<TransformResponse>> TransformAsync(
        Guid organisationId,
        Guid orderId,
        OutputFormat format,
        CancellationToken ct)
    {
        // Load with tracking — we will mutate status twice
        var entity = await _db.PurchaseOrders
            .Include(x => x.Lines)
            .Include(x => x.Supplier)
            .Include(x => x.SourceCapture)   // Phase 2: persisted token universe for SourceMap re-derive
            .Where(x => x.Id == orderId && x.OrgId == organisationId)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            return Result<TransformResponse>.Fail("Order not found.");

        // Pre-flight check: all lines must be resolved
        var unresolved = entity.Lines.Where(l => l.NeedsReview).Select(l => l.LineNumber).ToList();
        if (unresolved.Count > 0)
            return Result<TransformResponse>.Fail(
                $"Resolve all lines before transforming. Unresolved: {string.Join(", ", unresolved)}.");

        // ── Launch batch 7 — revision authority ────────────────────────────────
        // Resolve the pinned revision's effective bundle ONCE per transform. Live bundle when
        // the flag is off / the order is unpinned / the pin is orphaned — byte-identical to the
        // pre-batch-7 path in all of those cases.
        var effective = _effectiveConfig is null
            ? EffectiveConnectionConfig.Live
            : await _effectiveConfig.ResolveAsync(organisationId, entity.ConnectionRevisionId, ct);

        // Effective output format: [flag+pin] the revision's snapshotted format governs;
        // null/unparseable snapshot → the caller's format (which the controller already resolved
        // as explicit-request ?? live delivery-config format ?? safe default).
        var effectiveFormat = format;
        if (effective.IsRevision && !string.IsNullOrWhiteSpace(effective.OutputFormat))
        {
            if (Enum.TryParse<OutputFormat>(effective.OutputFormat, ignoreCase: true, out var revisionFormat))
            {
                if (revisionFormat != format)
                    _logger.LogInformation(
                        "Order {OrderId}: output format {RevisionFormat} taken from pinned {Source} (requested {Requested}).",
                        orderId, revisionFormat, effective.Source, format);
                effectiveFormat = revisionFormat;
            }
            else
            {
                _logger.LogWarning(
                    "Order {OrderId}: pinned {Source} output format '{Format}' is not recognised — using the requested format {Requested}.",
                    orderId, effective.Source, effective.OutputFormat, format);
            }
        }

        // ── cXML network credentials ────────────────────────────────────────────
        // Resolve the supplier's configured cXML From/To/Sender credentials ONLY when the effective
        // format is cXML (every other transformer ignores this argument). Null — no resolver, no
        // delivery-config row, or no cXML credentials — makes the cXML Header fall back to the legacy
        // OrgId/SupplierId GUID identities, byte-identical to the pre-feature output. Read from the
        // LIVE delivery-config row, the same source the controller used to pick the cXML format.
        CxmlCredentialConfig? cxmlCredentials = null;
        if (effectiveFormat == OutputFormat.CXml && _cxmlResolver is not null)
        {
            try
            {
                cxmlCredentials = await _cxmlResolver.ResolveAsync(organisationId, entity.SupplierId ?? Guid.Empty, ct);
            }
            catch (CredentialUnbindableException ex)
            {
                // This call sits before the idempotency claim below and outside every other
                // try/catch in this method (the first one starts ~300 lines down, at the
                // acceptance gate). Left unguarded, a throw here unwinds straight through
                // TransformOrderJob, Hangfire retries it 3x identically (the AAD mismatch is not
                // transient), then StuckOrderDetectionService — which by design NEVER fails a
                // 'transforming' strand — quietly resets the order to 'ready' with no error message.
                // That is a silent strand, not a fix. Route it through the SAME terminal-failure
                // helper every other unrecoverable transform failure in this method uses, so it is
                // visible (ops health, the exception row, the order's errorMessage) and recoverable
                // (transform_failed is re-claimable, same as every other terminal failure here).
                _logger.LogError(ex,
                    "Order {OrderId}: cXML shared secret for supplier {SupplierId} could not be decrypted ({Reason}) — failing the transform.",
                    orderId, entity.SupplierId, ex.Reason);

                const string error =
                    "The supplier's cXML shared secret could not be decrypted, so the order was not transformed.";
                await FailTransformAsync(entity, organisationId, orderId, error, ct);
                return Result<TransformResponse>.Fail(error);
            }
        }

        // heart-piece-flex flexible mapping: SIX transform modes, in precedence order.
        //   1. TEMPLATE MODE — order carries a non-blank whole-document OutputTemplate → render the
        //      ENTIRE document from that one Scriban template (ANY format, no fixed transformer needed).
        //   2. NATIVE OVERRIDE (CSV/JSON) — per-order output mapping + a format the override builder
        //      emits natively from the output-field rules.
        //   3. STRUCTURED OVERRIDE (XML/cXML/UBL/X12) — resolve the override's canonical-field changes
        //      into an EFFECTIVE entity, then hand it to the EXISTING fixed transform (no per-format
        //      re-implementation).
        //   4. REVISION-PINNED OUTPUT (launch batch 7, flag-gated) — the order is pinned to a
        //      published revision whose output_mapping_json snapshot is usable → wrap it as a
        //      synthetic override and reuse modes 2/3 verbatim. For a PINNED order the LIVE
        //      supplier-promoted mapping is intentionally NOT consulted (it is a mutable table and
        //      would break reproducibility); an unusable/null snapshot falls to the FIXED transformer.
        //   5. SUPPLIER-PROMOTED OUTPUT (launch batch 4A — unpinned/flag-off only) — the supplier
        //      carries a promoted PoMappingConfig.Output ("Save mappings for this supplier") →
        //      wrap it as a synthetic override and reuse modes 2/3 verbatim. The per-order
        //      override always stays the higher-priority seam.
        //   6. FIXED TRANSFORMER — the default; byte-for-byte identical to today when nothing above applies.
        // An override with only custom fields or an empty output config never diverts the transform.
        var mappingOverride = OrderMappingOverrideReader.Read(entity.CanonicalJson);
        // HIGHEST-precedence (Phase B): a structured OutputNode tree renders the WHOLE document via
        // OutputTemplateEmitter (arbitrary nesting/arrays/attributes). Everything below gates on
        // !useOutputNode (as they already gate on !useTemplate), so a null OutputTree is byte-for-byte
        // identical to today.
        //
        // WS-12 exception: an OutputTree in a format the emitter cannot render does NOT route to it —
        // the emitter REFUSES those (a generic node tree can't carry a valid cXML Header/DOCTYPE, a
        // Peppol UBL CustomizationID or an X12 ISA/GS envelope; it throws). For cXML/X12 the tree
        // exists only to carry the per-connection EnvelopeConfig (OutputNodeTemplate.Envelope) into the
        // dedicated fixed transformer below, so we keep the fixed-transformer path and read the
        // envelope off the tree (see `envelope` resolution further down).
        var perOrderTree    = mappingOverride?.OutputTree;
        var outputTree      = perOrderTree;
        var useOutputNode   = TreeDrivesTheDocument(perOrderTree, effectiveFormat, orderId, "the order's own");
        // The override the emitter resolves custom fields against. Normally the order's own; when the
        // tree arrives from the supplier's promoted config (WP-12, below) it becomes the synthetic
        // supplier override, which already carries this order's custom fields.
        var treeOverride    = mappingOverride;
        var useTemplate     = !useOutputNode && OrderMappingOverrideReader.HasUsableTemplate(mappingOverride);
        var hasUsableOverride =
            !useOutputNode
            && !useTemplate
            && OrderMappingOverrideReader.HasUsableOutput(mappingOverride)
            && MappedTransformService.SupportsOverrideFormat(effectiveFormat);
        var useNativeOverride = hasUsableOverride && MappedTransformService.SupportsOverride(effectiveFormat);

        // Revision-pinned output vs supplier-promoted output — mutually exclusive by construction:
        // a pinned order consults ONLY its revision snapshot; an unpinned/flag-off order consults
        // ONLY the live supplier-promoted mapping (read ONCE per transform). Defensive: a missing /
        // malformed / unusable mapping on either side yields null and the fixed transformer stays
        // in control (logged, never a throw). supplierOutputJson is the camelCase serialization of
        // the promoted Output config, used for the provenance ConfigDigest below.
        OrderMappingOverride? revisionOverride   = null;
        OrderMappingOverride? supplierOverride   = null;
        string?               supplierOutputJson = null;
        if (!useOutputNode && !useTemplate && !hasUsableOverride && MappedTransformService.SupportsOverrideFormat(effectiveFormat))
        {
            if (effective.IsRevision)
                revisionOverride = TryBuildRevisionOutputOverride(effective, mappingOverride, orderId);
            else
                (supplierOverride, supplierOutputJson) =
                    await TryReadSupplierPromotedOutputAsync(organisationId, entity.SupplierId ?? Guid.Empty, mappingOverride, ct);
        }
        // The FLAT builder runs at a promoted layer only when that layer actually carries a flat
        // output config. A layer that contributed only a tree must never reach it: MappedTransformService
        // throws "Override has no output mapping config" for a null Output, which the catch below turns
        // into a TERMINAL transform_failed — failing an order the fixed transformer would have delivered.
        var useRevisionOutput  = OrderMappingOverrideReader.HasUsableOutput(revisionOverride);
        var useRevisionNative  = useRevisionOutput && MappedTransformService.SupportsOverride(effectiveFormat);
        var useSupplierMapping = OrderMappingOverrideReader.HasUsableOutput(supplierOverride);
        var useSupplierNative  = useSupplierMapping && MappedTransformService.SupportsOverride(effectiveFormat);

        // ── WP-12 — the supplier-PROMOTED output tree ─────────────────────────────
        // "Save this layout for the supplier" copies the designed OutputNode tree onto
        // PoMappingConfig.OutputTree. Consume it HERE, in the supplier-promoted layer: below every
        // per-order seam (this block only runs when none of them applied) and above the fixed
        // transformer. Within the layer the tree OUTRANKS the promoted flat Output — the tree is the
        // richer concept, mirroring the per-order ladder at the top of this method.
        //
        // Promoting the tree also promotes its cXML/X12 Envelope: `envelope` below reads it, so a
        // supplier's required sender/receiver identity survives promotion for exactly the same reason
        // the structure does. The WS-12 exception still holds — a cXML/X12 tree does not route to the
        // emitter, it only carries that identity.
        //
        // TWO precedence rules govern this block, and both were violated by assigning the promoted
        // tree unconditionally:
        //
        //   1. THE PER-ORDER TREE ALWAYS WINS — in EVERY format, including when it carries nothing but
        //      an Envelope. A per-order cXML/X12 tree sets useOutputNode = false (the WS-12 exception),
        //      which is NOT the same thing as "this order has no tree": overwriting it replaced a
        //      per-order sender identity with the supplier's, and a per-order cXML tree paired with a
        //      promoted JSON tree flipped the whole transform onto the emitter, writing JSON bytes into
        //      an artifact row that recorded 'cxml'. So a promoted tree is adopted ONLY when the order
        //      has no per-order tree at all.
        //   2. A PROMOTED TREE THE EMITTER CANNOT RENDER CONTRIBUTES AT MOST ITS ENVELOPE. cXML/X12
        //      hand their identity to the dedicated fixed transformer; UBL / Peppol / X12-850 / EDIFACT
        //      contribute nothing at all. Neither may drive the document, and neither may suppress or
        //      replace the flat output config at its own layer — the suppression below is therefore
        //      reached only by a tree that actually renders THIS connection's format.
        //
        // A pinned order takes its tree from the revision snapshot; an unpinned one from the live
        // promoted config. The two are mutually exclusive by construction (only one of the two reads
        // above ran), so this reads whichever produced a tree.
        var useSupplierTree = false;
        var useRevisionTree = false;
        OutputNodeTemplate? promotedTree = null;

        if (perOrderTree is null)
        {
            var promotedSource = supplierOverride?.OutputTree is not null ? supplierOverride
                               : revisionOverride?.OutputTree is not null ? revisionOverride
                               : null;
            promotedTree = promotedSource?.OutputTree;

            if (TreeDrivesTheDocument(promotedTree, effectiveFormat, orderId, "the supplier's promoted"))
            {
                useSupplierTree = ReferenceEquals(promotedSource, supplierOverride);
                useRevisionTree = !useSupplierTree;
                outputTree      = promotedTree;
                useOutputNode   = true;
                treeOverride    = promotedSource;

                // A tree-driven layer must not ALSO run the flat builder for that same layer — the tree
                // already describes the whole document, and running both would emit the flat layout.
                if (useSupplierTree) { useSupplierMapping = false; useSupplierNative = false; }
                else                 { useRevisionOutput  = false; useRevisionNative = false; }
            }
        }

        // Locate the fixed transformer (Xml/Csv/Json/...). Required EXCEPT for template mode and the
        // native CSV/JSON override path; resolved up-front so a missing transformer fails before status
        // mutation. NOTE: the revision-pinned and supplier-promoted paths deliberately do NOT relax
        // this requirement — the fixed transformer must exist so the defensive fallback below is
        // always possible.
        var transformer = _transformers.FirstOrDefault(t => t.CanTransform(effectiveFormat));
        if (!useOutputNode && !useTemplate && !useNativeOverride && transformer is null)
            return Result<TransformResponse>.Fail($"No transform service registered for format '{effectiveFormat}'.");

        // ── WS-12 — per-connection EDI/cXML envelope identity ───────────────────
        // The supplier's required X12 ISA/GS identity (sender/receiver qualifier+id, version, usage,
        // delimiters) and cXML From/To/Sender party identity ride on the output template as DATA
        // (OutputNodeTemplate.Envelope), pinned into the order's override JSON exactly like every other
        // output mode — so a PINNED revision delivers under the SAME envelope it was published with.
        // Resolved ONLY when the effective format is X12/cXML (every other transformer has no envelope
        // overload and ignores it). Null — no override, no OutputTree, or a tree without an Envelope —
        // makes the fixed transform fall back to its legacy baked identity, BYTE-FOR-BYTE identical to
        // the pre-WS-12 output. The X12 transform ignores cxmlCredentials and cXML ignores nothing here;
        // see RunFixedTransform / MergeCxmlIdentity below for how the two cXML identity sources (live
        // delivery-config credentials vs the envelope) compose without dropping the shared secret.
        //
        // Precedence mirrors the ladder above: the ORDER's own tree owns the identity when it has one,
        // and a promoted tree fills in only when the order carries no tree at all (`promotedTree` is
        // null in every other case, by the guard above).
        //
        // The promoted half additionally requires identity FOR THIS FORMAT. A cXML-format tree
        // carrying only an Envelope.Cxml, delivered on an X12 connection, reaches
        // `X12TransformService: var env = envelope?.X12;` as null — the output is byte-identical to
        // having no envelope at all. Recording it anyway appended `|envelope:{…}` to the provenance
        // descriptor, so two byte-identical artifacts carried different digests (and the mirror case
        // for an X12-only envelope on cXML).
        EnvelopeConfig? envelope = null;
        var envelopeFromPromotedTree = false;
        if (OutputTreeFormats.ReadsEnvelopeIdentity(effectiveFormat))
        {
            envelope = perOrderTree?.Envelope;
            if (envelope is null
                && promotedTree?.Envelope is { } promotedEnvelope
                && OrderMappingOverrideReader.HasEnvelopeIdentityFor(promotedEnvelope, effectiveFormat))
            {
                envelope                 = promotedEnvelope;
                envelopeFromPromotedTree = true;
            }
        }

        // ── Idempotency / concurrency guard ────────────────────────────────────
        // Atomically claim the order by flipping ready → transforming only while it is
        // still claimable (mirrors the parse path's "only parse while parsing" guard).
        // "transforming" is also claimable so a Hangfire retry can re-run a crashed
        // attempt; a COMPLETED transform (ready_to_deliver and beyond) affects 0 rows
        // and short-circuits below — re-running it would upload a duplicate artifact
        // and re-enqueue delivery, double-sending the same PO to the supplier.
        //
        // "transform_failed" is claimable for the same reason "transforming" is — it is a
        // failure state, not a completed transform, and it holds NO artifact. It is the
        // RECOVERY door: the user fixes the broken template/mapping and re-transforms. A
        // mapping edit does NOT reset the status for us here (OrderMappingOverrideService's
        // MV-1 reset only fires for post-artifact states, which transform_failed is not), so
        // if this claim rejected it the order would be permanently stuck — trading the silent
        // strand this status exists to expose for a worse, louder one.
        //
        // "rejected_by_supplier" is the SAME recovery door for the other kind of correction
        // (WP-19), and for the same three reasons: it is a failure state, it holds no artifact
        // anyone may ship (no delivery claim set admits it, so the refused bytes can never be
        // re-sent in place), and refusing it here left the status with no exit at all — an order
        // the operator could only move with a database edit.
        //
        // The status list itself lives in OrderStatusMachine.ClaimableForTransformFrom because it
        // is written TWICE below (relational + InMemory), which is precisely how the five
        // delivery-claim lists drifted apart four times, each time silently.
        var claimedAt = DateTime.UtcNow;
        int claimed;
        // Parameterised as `= ANY(@p)` rather than inlined as `IN ('…', …)` — same reason as
        // DeliveryClaim: it keeps the claim's SQL text (and therefore its Postgres plan) stable
        // no matter what the set contains.
        var claimableStatuses = OrderStatusMachine.ClaimableForTransformFrom.ToArray();
        if (_db.Database.IsRelational())
        {
            claimed = await _db.PurchaseOrders
                .Where(x => x.Id == orderId && x.OrgId == organisationId
                         && claimableStatuses.Contains(x.Status))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.Status, OrderStatusConstants.Transforming)
                    .SetProperty(o => o.UpdatedAt, claimedAt), ct);
        }
        else
        {
            // EF InMemory test provider cannot translate ExecuteUpdateAsync — emulate the
            // same transition through the change tracker (tests are single-threaded there).
            claimed = OrderStatusMachine.ClaimableForTransformFrom.Contains(entity.Status) ? 1 : 0;
            if (claimed == 1)
            {
                entity.Status    = OrderStatusConstants.Transforming;
                entity.UpdatedAt = claimedAt;
                await _db.SaveChangesAsync(ct);
            }
        }

        if (claimed == 0)
        {
            // Already transformed (or not in a transformable state): report the latest
            // existing artifact as a benign no-op so a duplicated job neither re-uploads
            // nor re-enqueues delivery.
            // WP-35: DELIVERABLE only. This id is handed back to TransformOrderJob, which decides
            // whether to enqueue a delivery for it — so reporting a re-processed preview here would
            // turn a duplicated transform job into an unattended send of a document nobody approved.
            var existing = await _db.OutboundArtifacts.AsNoTracking()
                .Where(a => a.OrderId == orderId && a.OrgId == organisationId)
                .Deliverable()
                .OrderByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync(ct);

            _logger.LogInformation(
                "Order {OrderId} transform skipped (status={Status}, existing artifact={ArtifactId}) — already in flight or done.",
                orderId, entity.Status, existing?.Id);

            return Result<TransformResponse>.Ok(new TransformResponse(
                existing?.Id ?? Guid.Empty,
                existing?.Format ?? effectiveFormat.ToString().ToLowerInvariant(),
                existing?.CreatedAt ?? claimedAt,
                Skipped: true));
        }

        // Sync the tracked entity with the row just claimed (the ExecuteUpdateAsync above
        // bypasses the change tracker): both CURRENT and ORIGINAL values must say
        // 'transforming', otherwise the failure paths' revert to "ready" would diff as
        // a no-op against a stale original and never be written — stranding the order.
        entity.Status    = OrderStatusConstants.Transforming;
        entity.UpdatedAt = claimedAt;
        if (_db.Database.IsRelational())
        {
            var entry = _db.Entry(entity);
            entry.Property(x => x.Status).OriginalValue    = OrderStatusConstants.Transforming;
            entry.Property(x => x.UpdatedAt).OriginalValue = claimedAt;
        }

        // ── WP-17 — the server-side acceptance gate ────────────────────────────
        // The supplier-profile UI promises that an error-severity acceptance rule BLOCKS delivery.
        // Until now that promise was kept only in the browser: ValidateOrderAsync had exactly two
        // production callers and both were HTTP controllers, so an order the profile refused still
        // went out through inbox bulk-send, REST ingress, inbound email, or any auto-drive. This is
        // the one place the promise is now kept, and because TransformAsync is the SINGLE
        // server-side transform door (TransformOrderJob is the only enqueuer, and it is the only
        // caller of IOrderService.TransformAsync), every one of those paths inherits the answer.
        //
        // WHY AFTER THE CLAIM, NOT BEFORE IT — this placement is deliberate:
        //   • OrdersController.Transform ALREADY flipped ready → transforming before enqueueing this
        //     job. Refusing before the claim and returning Fail would leave the order sitting in
        //     'transforming' with no job to pick it up — the exact strand the claim exists to avoid,
        //     and one nothing sweeps.
        //   • Behind the claim exactly ONE runner evaluates the gate for an order, so a duplicated
        //     Hangfire run cannot write two refusals or race a concurrent transform.
        //   • It still runs BEFORE any document generation, upload, or artifact row, so a refused
        //     order costs one read and produces nothing that could later be delivered.
        // The refusal goes through the SAME FailTransformAsync the terminal template/mapping failures
        // use, so it lands in transform_failed: visible in ops health, opens the operator-workable
        // exception row, surfaces its message as the order's errorMessage, and stays re-claimable so
        // a fix (or an override) plus another Send re-drives it.
        //
        // WHAT IF THE GATE ITSELF FAILS — refuse, visibly. EvaluateAsync runs two reads (the
        // effective acceptance profile, the latest override audit row) and it sits AFTER the claim
        // and OUTSIDE the try/catch below, so a throw here — a DB blip, a malformed pin — unwound
        // straight through the Hangfire job and left the order in 'transforming': no artifact, no
        // transform_failed, no sentence, and no sweep looking for it. A document we could not check
        // against the supplier's rules is not one to send, so the failure is caught and routed
        // through the SAME FailTransformAsync as every other terminal failure. That is recoverable
        // by construction: transform_failed is re-claimable, and TransformOrderJob's own
        // AutomaticRetry re-drives it, so a transient lookup failure heals itself on the next run.
        // Cancellation is NOT swallowed — a cancelled request is not a refusal.
        AcceptanceGateDecision? gate;
        try
        {
            gate = await _acceptanceGate.EvaluateAsync(organisationId, orderId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Order {OrderId} (org {OrgId}): the supplier acceptance gate could not be evaluated — refusing the "
              + "transform rather than sending a document nobody checked. The order is marked transform_failed and "
              + "stays re-claimable, so a retry re-drives it.",
                orderId, organisationId);

            const string reason =
                "This order couldn't be checked against the supplier's rules, so it wasn't sent. "
              + "Try sending it again in a moment.";

            _db.AuditEvents.Add(OrderServiceShared.BuildAuditEvent(
                organisationId, orderId, AcceptanceGateAudit.BlockedAction, new
                {
                    blockers = Array.Empty<object>(),
                    stage    = "transform",
                    // The gate did not refuse this order — it could not answer. Recorded distinctly
                    // so an operator reading the trail is never told the supplier rejected it.
                    error    = "acceptance_gate_unavailable",
                }));

            await FailTransformAsync(entity, organisationId, orderId, reason, ct);
            return Result<TransformResponse>.Fail(reason);
        }

        if (gate is { Blocked: true })
        {
            var reason = string.IsNullOrWhiteSpace(gate.Reason)
                ? "This order wasn't sent because it doesn't meet what the supplier accepts."
                : gate.Reason!;

            _db.AuditEvents.Add(OrderServiceShared.BuildAuditEvent(
                organisationId, orderId, AcceptanceGateAudit.BlockedAction, new
                {
                    blockers = gate.Blockers.Select(b => new { code = b.Code, line = b.LineNumber, message = b.Message }).ToList(),
                    stage    = "transform",
                }));

            await FailTransformAsync(entity, organisationId, orderId, reason, ct);
            return Result<TransformResponse>.Fail(reason);
        }

        if (gate is { Overridden: true })
        {
            // The guarantee was waived in PRACTICE, not merely granted — a distinct, more
            // interesting audit fact than the override itself. Queued on the change tracker and
            // committed by whichever SaveChanges finishes this transform (the artifact commit, or
            // FailTransformAsync if generation then fails for an unrelated reason): either way an
            // attempt was admitted under the override, which is what the row records.
            _db.AuditEvents.Add(AcceptanceGate.BuildOverrideUsedEvent(organisationId, orderId, gate));
            _logger.LogWarning(
                "Order {OrderId}: transforming despite {Count} blocking supplier acceptance rule(s) — operator override by {Actor}.",
                orderId, gate.Blockers.Count, gate.OverriddenBy);
        }

        // Generate the document.
        //  • Native CSV/JSON override → the override builder emits the document.
        //  • Structured-format override → resolve an effective entity (canonical-field overrides
        //    applied to the typed columns) and feed it to the EXISTING fixed transform.
        //  • Supplier-promoted output (no per-order override) → same two mechanisms, driven by the
        //    synthetic supplier override; any unexpected failure falls back to the fixed transform.
        //  • No override → the fixed transform on the original entity, byte-for-byte unchanged.
        //
        // Phase 2: rebuild the addressable source-token universe from the persisted capture so
        // SourceMap rules resolve at delivery time even after the source blob is purged
        // (SourceFilePurgedAt). Empty when there is no capture → byte-identical to the no-token path.
        var sourceTokens = ProcuLink.Transform.Output.SourceTokenSerialization
            .FromTokensJson(entity.SourceCapture?.TokensJson);

        // Phase 2: batch-load this supplier's catalog ONCE (org+supplier scoped, never cross-tenant)
        // so the {{ catalog.* }} template accessor resolves without an N+1. Empty dict when the
        // supplier has no catalog → byte-identical to the no-catalog path (empty catalog object).
        var catalogLookup = await OrderServiceShared.BuildCatalogLookupAsync(
            _db, organisationId, entity.SupplierId ?? Guid.Empty, ct);

        TransformResult transformResult;
        try
        {
            if (useOutputNode)
            {
                // Phase B: render the supplier's exact required STRUCTURE from the OutputNode tree.
                // The emitter reuses the same value machinery + the same unresolved-lines guard, so a
                // broken/unresolved order fails the same way (TransformValidationException → revert).
                try
                {
                    transformResult = new OutputTemplateEmitter().Emit(
                        outputTree!, entity, treeOverride!, sourceTokens, catalogLookup);
                }
                catch (Exception ex) when (ex is not TransformValidationException and not TransformTemplateException)
                {
                    // TRUST: a CONFIGURED output tree that throws at emit time (malformed node name/prefix,
                    // an unsupported tree format like cXML/UBL) must FAIL LOUDLY — revert to a retryable
                    // state and return Fail — never strand the order in `transforming` through Hangfire
                    // retries or silently deliver. Mirrors the revision/supplier mapping branches below.
                    _logger.LogError(ex,
                        "Output tree emit failed for order {OrderId}; failing the transform (no silent fallback, no stuck order).",
                        orderId);
                    throw new TransformValidationException(
                        $"The output structure for this connection could not be rendered, so the order was not delivered: {ex.Message}");
                }
            }
            else if (useTemplate)
            {
                transformResult = new ScribanTemplateTransformService().Build(entity, mappingOverride!, catalogLookup);
            }
            else if (useNativeOverride)
            {
                transformResult = new MappedTransformService().Build(entity, mappingOverride!, effectiveFormat, sourceTokens: sourceTokens, catalogLookup: catalogLookup);
            }
            else if (hasUsableOverride)
            {
                var effectiveEntity = EffectiveEntityResolver.Resolve(entity, mappingOverride!);
                transformResult = await RunFixedTransform(transformer!, effectiveEntity, effectiveFormat, cxmlCredentials, envelope, ct);
            }
            else if (useRevisionOutput)
            {
                try
                {
                    transformResult = useRevisionNative
                        ? new MappedTransformService().Build(entity, revisionOverride!, effectiveFormat, sourceTokens: sourceTokens, catalogLookup: catalogLookup)
                        : await RunFixedTransform(
                              transformer!, EffectiveEntityResolver.Resolve(entity, revisionOverride!), effectiveFormat, cxmlCredentials, envelope, ct);
                }
                catch (Exception ex) when (ex is not TransformValidationException and not TransformTemplateException)
                {
                    // TRUST: a CONFIGURED output mapping that throws at transform time must FAIL LOUDLY,
                    // never silently deliver the default document. Surface it like a validation failure
                    // (revert to a retryable state, return Fail) so the order is held for review and the
                    // mapping can be fixed — instead of shipping a document the published mapping did
                    // not produce.
                    _logger.LogError(ex,
                        "Pinned revision output mapping failed for order {OrderId} ({Source}); failing the transform (no silent fallback to the default document).",
                        orderId, effective.Source);
                    throw new TransformValidationException(
                        $"The published output mapping for this connection could not be applied, so the order was not delivered: {ex.Message}");
                }
            }
            else if (useSupplierMapping)
            {
                try
                {
                    transformResult = useSupplierNative
                        ? new MappedTransformService().Build(entity, supplierOverride!, effectiveFormat, sourceTokens: sourceTokens, catalogLookup: catalogLookup)
                        : await RunFixedTransform(
                              transformer!, EffectiveEntityResolver.Resolve(entity, supplierOverride!), effectiveFormat, cxmlCredentials, envelope, ct);
                }
                catch (Exception ex) when (ex is not TransformValidationException and not TransformTemplateException)
                {
                    // TRUST: a promoted supplier output mapping that throws at transform time must FAIL
                    // LOUDLY, never silently deliver the default document. Surface it like a validation
                    // failure (revert to a retryable state, return Fail) so the order is held for review.
                    _logger.LogError(ex,
                        "Supplier-promoted output mapping failed for order {OrderId} (supplier {SupplierId}); failing the transform (no silent fallback to the default document).",
                        orderId, entity.SupplierId);
                    throw new TransformValidationException(
                        $"The supplier's saved output mapping could not be applied, so the order was not delivered: {ex.Message}");
                }
            }
            else
            {
                transformResult = await RunFixedTransform(transformer!, entity, effectiveFormat, cxmlCredentials, envelope, ct);
            }
        }
        catch (Exception ex) when (ex is TransformTemplateException or TransformValidationException)
        {
            // TERMINAL transform failure — the order is never delivered from a template that did not
            // render, or from an output mapping that could not be applied. Neither can be fixed by
            // retrying the SAME inputs: the config itself is broken, so a Hangfire retry is hopeless
            // and the only cure is a human editing the template/mapping.
            //
            // This lands in `transform_failed`, NOT back in `ready`. Reverting to `ready` (the old
            // behaviour) is indistinguishable from "never transformed", which made the failure
            // INVISIBLE: OpsHealthService's TransformFailed count was structurally always 0, so it
            // contributed 0 to TotalProblemOrders and /operations/health showed a green "All clear"
            // while a broken supplier template silently stranded every order for that supplier.
            // Writing the real status lights up the plumbing that was already waiting for it — the
            // health tile, the transform_failed exception row, and blob/data retention.
            //
            // Recovery is deliberate, not accidental: the claim above accepts transform_failed, so a
            // re-transform after the fix works (see the transition maps, which document both the edge
            // into this status and the edges back out).
            await FailTransformAsync(entity, organisationId, orderId, ex.Message, ct);
            return Result<TransformResponse>.Fail(ex.Message);
        }

        // Buffer the generated bytes once: the SAME byte sequence UploadAsync would have read
        // from the stream's current position is both uploaded and hashed for provenance, so the
        // stored artifact and its recorded SHA-256 can never diverge.
        byte[] artifactBytes;
        await using (var content = transformResult.Content)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            artifactBytes = buffer.ToArray();
        }

        // Upload artifact to R2
        var artifactId  = Guid.NewGuid();
        var artifactKey = $"{organisationId}/{orderId}/artifacts/{artifactId}{transformResult.FileExtension}";

        await _fileStorage.UploadAsync(
            new MemoryStream(artifactBytes, writable: false), artifactKey, transformResult.ContentType, ct);

        _logger.LogInformation("Uploaded artifact to R2: {Key}", artifactKey);

        // ── Provenance (best-effort; must NEVER fail the transform) ────────────────
        // ConfigDigest = SHA-256 of the EFFECTIVE config that drove this transform: the order's
        // per-order mappingOverride JSON (template / native / structured override modes), else
        // "revision:{revisionId}:{outputMappingJson}" when the pinned revision's output snapshot
        // drove it (launch batch 7), else "supplier:{outputJson}" when the supplier-promoted
        // output mapping drove it, else the marker "fixed:{format}" for the fixed-transformer
        // path — the prefixes keep the four sources distinguishable. ArtifactSha256 = SHA-256 of
        // the exact generated bytes. ConnectionRevisionId = the order's ingest-time pin.
        string? configDigest = null;
        string? artifactSha  = null;
        try
        {
            artifactSha = ProvenanceHash.TrySha256Hex(artifactBytes);

            // WP-12: a PROMOTED tree also sets useOutputNode, but the config that drove it belongs to
            // the supplier or the pinned revision, not to this order — so it must digest under that
            // source's prefix. The descriptor names the EXACT surface that produced these bytes:
            //   • a promoted TREE digests THE TREE. Digesting the revision's OutputMappingJson instead
            //     was worthless — a tree-only revision has none, so every layout on that revision
            //     collapsed to the constant SHA256("revision:{id}:").
            //   • a promoted FLAT config digests the flat config, even when a tree is also stored. The
            //     tree only outranks it when the tree actually renders; digesting a tree that changed
            //     nothing gave byte-identical artifacts different digests.
            var configDescriptor =
                ((useOutputNode && !useSupplierTree && !useRevisionTree) || useTemplate || useNativeOverride || hasUsableOverride)
                    ? OrderMappingOverrideReader.ReadRawJson(entity.CanonicalJson)
                : useRevisionTree
                    ? $"revision:{effective.RevisionId}:{SerializeForDigest(revisionOverride!.OutputTree)}"
                : useRevisionOutput
                    ? $"revision:{effective.RevisionId}:{effective.OutputMappingJson}"
                : useSupplierTree
                    ? $"supplier:{SerializeForDigest(supplierOverride!.OutputTree)}"
                : useSupplierMapping
                    ? $"supplier:{supplierOutputJson}"
                    : $"fixed:{effectiveFormat.ToString().ToLowerInvariant()}";

            // A cXML/X12 promoted tree contributes ONLY its envelope, which is invisible in every
            // descriptor above yet changes the delivered sender/receiver identity. Name it, or two
            // connections that differ solely by identity would share one digest.
            if (envelopeFromPromotedTree && !useOutputNode && !useTemplate)
                configDescriptor = $"{configDescriptor}|envelope:{SerializeForDigest(envelope)}";

            configDigest = ProvenanceHash.TrySha256HexUtf8(configDescriptor);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Provenance capture failed for order {OrderId} (non-fatal; artifact saved without digests).",
                orderId);
        }

        // Persist artifact row + update order status + audit — one SaveChanges
        var now      = DateTime.UtcNow;
        var artifact = new OutboundArtifact
        {
            Id                   = artifactId,
            OrderId              = orderId,
            OrgId                = organisationId,
            Format               = effectiveFormat.ToString().ToLowerInvariant(),
            FileKey              = artifactKey,
            CreatedAt            = now,
            ConnectionRevisionId = entity.ConnectionRevisionId,
            ConfigDigest         = configDigest,
            ArtifactSha256       = artifactSha,
        };

        _db.OutboundArtifacts.Add(artifact);

        entity.Status    = OrderStatusConstants.ReadyToDeliver;
        entity.UpdatedAt = now;

        _db.AuditEvents.Add(OrderServiceShared.BuildAuditEvent(organisationId, orderId, "Transformed", new
        {
            format     = artifact.Format,
            artifactId = artifactId,
            fileKey    = artifactKey
        }));

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Order {OrderId} transformed to {Format}, artifact {ArtifactId}",
            orderId, effectiveFormat, artifactId);

        return Result<TransformResponse>.Ok(new TransformResponse(artifactId, artifact.Format, now));
    }

    // ── Terminal transform failure (visible, recoverable) ─────────────────────

    /// <summary>
    /// Commits a TERMINAL transform failure so it is VISIBLE to an operator, mirroring the shape of
    /// the parse-failure path (status + audit event in one SaveChanges, then a best-effort exception
    /// reconcile). Three surfaces depend on this and all three were dead while the failure reverted
    /// to <c>ready</c>:
    /// <list type="bullet">
    ///   <item><description><c>OpsHealthService.TransformFailed</c> — counts the status, and feeds
    ///     <c>TotalProblemOrders</c>, so a failed transform now breaks the "All clear" gate instead of
    ///     hiding under it.</description></item>
    ///   <item><description>The <c>"TransformFailed"</c> audit action, whose <c>error</c> payload key
    ///     <c>OrdersController</c> reads to populate the order's <c>errorMessage</c> (the string the
    ///     workshop shows the user).</description></item>
    ///   <item><description><c>OrderExceptionService</c>'s <c>transform_failed</c> row, opened by the
    ///     reconcile below — the operator-workable exception.</description></item>
    /// </list>
    ///
    /// <para>The reconcile is best-effort ON PURPOSE (<c>SafeReconcileExceptionsAsync</c> swallows and
    /// logs): the status + audit event are the durable record, and losing the derived exception row must
    /// never turn a reported failure into an unhandled one. Nothing here re-resolves the exception on a
    /// later success — a successful re-transform enqueues delivery, and DeliveryService reconciles on
    /// every successful attempt, which auto-resolves the row once the condition no longer holds.</para>
    /// </summary>
    private async Task FailTransformAsync(
        PurchaseOrderEntity entity,
        Guid                organisationId,
        Guid                orderId,
        string              error,
        CancellationToken   ct)
    {
        entity.Status    = OrderStatusConstants.TransformFailed;
        entity.UpdatedAt = DateTime.UtcNow;

        _db.AuditEvents.Add(OrderServiceShared.BuildAuditEvent(organisationId, orderId, "TransformFailed", new
        {
            error,
            stage = "transform",
        }));

        await _db.SaveChangesAsync(ct);

        _logger.LogError(
            "Order {OrderId} (org {OrgId}) TRANSFORM FAILED terminally: {Error}. The order is marked transform_failed " +
            "(visible in ops health + exceptions) and needs a template/mapping fix before it can be re-transformed.",
            orderId, organisationId, error);

        await _shared.SafeReconcileExceptionsAsync(organisationId, orderId, ct);
    }

    // ── WS-12 — fixed-transformer call with the per-connection envelope ────────

    /// <summary>
    /// Runs the resolved fixed <see cref="ITransformService"/>, threading the per-connection
    /// <see cref="EnvelopeConfig"/> into the dedicated X12 / cXML transforms via their concrete
    /// <c>envelope:</c> overloads. Every non-X12/cXML transformer keeps the unchanged
    /// 4-argument interface call, so a null envelope (or any other format) is BYTE-FOR-BYTE
    /// identical to the pre-WS-12 path.
    ///
    /// <list type="bullet">
    ///   <item><description><b>X12</b> with an envelope → the <see cref="X12TransformService"/>
    ///     overload drives ISA/GS identity + delimiters. X12 ignores cXML credentials, so nothing
    ///     is lost.</description></item>
    ///   <item><description><b>cXML</b> → the LIVE delivery-config credentials
    ///     (<paramref name="cxmlCredentials"/>) stay AUTHORITATIVE because they alone carry the
    ///     decrypted Sender <c>SharedSecret</c>; the envelope only FILLS IN any From/To/Sender
    ///     identity the live credentials left blank. This composes the two identity sources without
    ///     dropping the secret and keeps the existing credential path byte-identical when no envelope
    ///     is set.</description></item>
    /// </list>
    /// </summary>
    private static Task<TransformResult> RunFixedTransform(
        ITransformService    transformer,
        PurchaseOrderEntity  order,
        OutputFormat         format,
        CxmlCredentialConfig? cxmlCredentials,
        EnvelopeConfig?      envelope,
        CancellationToken    ct)
    {
        // No envelope → the legacy interface call, byte-for-byte unchanged (covers every non-X12/cXML
        // format, which never has an envelope, and an unconfigured X12/cXML connection).
        if (envelope is null)
            return transformer.TransformAsync(order, format, ct, cxmlCredentials);

        switch (format)
        {
            case OutputFormat.X12 when transformer is X12TransformService x12:
                // X12 has no cXML Header; its overload ignores cxmlCredentials entirely.
                return x12.TransformAsync(order, format, ct, envelope);

            case OutputFormat.CXml when transformer is CxmlTransformService cxml:
                // Compose: live credentials win per-credential (and own the shared secret); the
                // envelope fills only the gaps. A null merge result keeps the legacy GUID identities.
                return cxml.TransformAsync(order, format, ct, MergeCxmlIdentity(cxmlCredentials, envelope.Cxml));

            default:
                // Some other transformer happened to be registered for this format, or the concrete
                // type isn't the expected one — fall back to the interface call (never throw on the
                // delivery path; the legacy identity is correct-but-unconfigured, not broken output).
                return transformer.TransformAsync(order, format, ct, cxmlCredentials);
        }
    }

    /// <summary>
    /// Merges the LIVE cXML delivery-config credentials with the per-connection envelope identity for
    /// the cXML Header. Per-credential precedence: the live config value wins when present, the envelope
    /// fills the gap, and the decrypted Sender <c>SharedSecret</c> always comes from the live config
    /// (the envelope never carries a secret — it is a reference). Returns null only when NEITHER source
    /// has anything, so the cXML transform keeps its legacy <c>OrgId</c>/<c>SupplierId</c> identities.
    /// </summary>
    private static CxmlCredentialConfig? MergeCxmlIdentity(CxmlCredentialConfig? live, CxmlEnvelope? env)
    {
        if (env is null) return live; // byte-identical to the pre-WS-12 credential-only path.

        static string? Prefer(string? primary, string? fallback) =>
            string.IsNullOrWhiteSpace(primary) ? fallback : primary;

        return new CxmlCredentialConfig(
            FromDomain:         Prefer(live?.FromDomain,     env.FromDomain),
            FromIdentity:       Prefer(live?.FromIdentity,   env.FromIdentity),
            ToDomain:           Prefer(live?.ToDomain,       env.ToDomain),
            ToIdentity:         Prefer(live?.ToIdentity,     env.ToIdentity),
            SenderDomain:       Prefer(live?.SenderDomain,   env.SenderDomain),
            SenderIdentity:     Prefer(live?.SenderIdentity, env.SenderIdentity),
            SenderSharedSecret: live?.SenderSharedSecret) // secret only ever comes from live config.
        {
            // The configurable DOCTYPE (T7) composes with the SAME precedence: the live delivery-config
            // DTD wins per-field; the pinned-revision envelope fills the gap. Null/blank both → no DOCTYPE.
            DtdSystemId = Prefer(live?.DtdSystemId, env.DtdSystemId),
            DtdPublicId = Prefer(live?.DtdPublicId, env.DtdPublicId),
        };
    }

    // ── Revision-pinned output mapping (launch batch 7) ───────────────────────

    /// <summary>
    /// Builds the synthetic override the PINNED revision's <c>output_mapping_json</c> snapshot
    /// would apply, reusing the existing override machinery verbatim (mirrors
    /// <c>ReplayService.BuildRevisionOverride</c>: the order's custom fields are preserved so
    /// output rules referencing them still resolve; the per-order SourceMap / template are NOT
    /// carried). Returns null — meaning "the FIXED transformer drives the output" — for a
    /// null/blank snapshot, an empty snapshot (no header AND no line rules; matches a backfilled
    /// rev-1), or a malformed snapshot (logged). Never throws.
    /// </summary>
    private OrderMappingOverride? TryBuildRevisionOutputOverride(
        EffectiveConnectionConfig effective, OrderMappingOverride? mappingOverride, Guid orderId)
    {
        // WP-12 — the structured output tree rides INSIDE the revision's InputMappingJson, which is a
        // byte-identical snapshot of the whole serialized PoMappingConfig and therefore already
        // carries the additive OutputTree member (ConnectionBackfillService: `InputMappingJson =
        // poMapping?.ConfigJson`). No new revision column, no migration.
        //
        // This matters in production specifically: Connections:RevisionAuthority is ON there, so a
        // pinned order resolves ONLY its revision bundle. Without this read, promoting a designed
        // layout would work in every test and do nothing for any pinned order — the exact
        // "green locally, inert live" shape this packet exists to remove.
        // BOTH halves of the pinned bundle are read, and BOTH ride on the returned override. Returning
        // as soon as a tree was found DISCARDED a working published flat snapshot: for a cXML/X12 tree
        // — which never renders anything — that left an override with a null Output, and the flat
        // builder then threw, turning an order that had been delivering correctly into transform_failed.
        // Which of the two actually drives is the caller's decision (see the WP-12 block in
        // TransformAsync); this method's job is only to report what the revision snapshotted.
        var pinnedTree   = TryReadPinnedOutputTree(effective, orderId);
        var pinnedOutput = TryReadPinnedOutputConfig(effective, orderId);

        if (pinnedTree is null && pinnedOutput is null)
            return null;

        if (pinnedTree is not null)
            _logger.LogInformation(
                "Order {OrderId}: output structure taken from pinned {Source}.", orderId, effective.Source);

        if (pinnedOutput is not null)
            _logger.LogInformation(
                "Order {OrderId}: output mapping taken from pinned {Source}.", orderId, effective.Source);

        return new OrderMappingOverride
        {
            CustomFields = mappingOverride?.CustomFields ?? new List<CustomField>(),
            Output       = pinnedOutput,
            OutputTree   = pinnedTree,
        };
    }

    /// <summary>
    /// Reads the FLAT <see cref="OutputMappingConfig"/> out of a pinned revision's
    /// <c>output_mapping_json</c> snapshot. Returns null — meaning "this half contributes nothing" —
    /// for a blank, empty (no header AND no line rules; matches a backfilled rev-1) or malformed
    /// snapshot. Logged, never thrown.
    /// </summary>
    private OutputMappingConfig? TryReadPinnedOutputConfig(EffectiveConnectionConfig effective, Guid orderId)
    {
        if (string.IsNullOrWhiteSpace(effective.OutputMappingJson))
            return null;

        try
        {
            var output = JsonSerializer.Deserialize<OutputMappingConfig>(
                effective.OutputMappingJson, RevisionOutputSerializerOptions);

            return output is null || (output.Header.Count == 0 && output.Lines.Count == 0)
                ? null // empty snapshot — the fixed transformer stays in control
                : output;
        }
        catch (Exception ex)
        {
            // Deliberately NOT `catch (JsonException)`: the deserializer is only one of the ways a
            // poisoned snapshot can fail, and this method sits behind no other guard — anything that
            // escapes here escapes TransformAsync itself, BEFORE the status claim, leaving no
            // transform_failed row and no exception row while Hangfire retries forever.
            _logger.LogWarning(ex,
                "Order {OrderId}: pinned {Source} output mapping is malformed — using the fixed transformer.",
                orderId, effective.Source);
            return null;
        }
    }

    /// <summary>
    /// THE adoption gate: may <paramref name="tree"/> render the document this connection delivers?
    /// Two conditions, and both are load-bearing.
    ///
    /// <list type="number">
    ///   <item><description><b>The emitter must render the tree's format at all</b> — asked of the
    ///     shared <see cref="OutputTreeFormats.IsRenderable"/>, the same source
    ///     <c>OutputTemplateEmitter.Emit</c> dispatches on. Asking "is this NOT cXML/X12?" instead
    ///     adopted Ubl / UblOrder / X12_850 / EdifactOrders trees, the emitter threw, and the order
    ///     ended in a TERMINAL transform_failed. Promoted once, that killed every future order for the
    ///     supplier.</description></item>
    ///   <item><description><b>The tree's format must BE the connection's format.</b> The bytes come
    ///     from <c>tree.Format</c>, but <c>artifact.Format</c> is <c>effectiveFormat</c> and (post-#77)
    ///     delivery derives the content type and the file name from THAT — so a Json tree on a cXML
    ///     connection shipped JSON bytes as <c>application/xml</c> named <c>PO-x.xml</c>, recorded as
    ///     <c>cxml</c>. The CONNECTION's format wins: it is what the supplier's system was configured
    ///     to accept, what the artifact row records, and what delivery announces. A mismatched tree is
    ///     dropped (loudly) and the flat/fixed path below delivers a valid document.</description></item>
    /// </list>
    /// </summary>
    private bool TreeDrivesTheDocument(
        OutputNodeTemplate? tree, OutputFormat effectiveFormat, Guid orderId, string source)
    {
        if (tree is null) return false;

        if (!OrderMappingOverrideReader.CanRenderTree(tree))
            return false;   // cXML/X12 contribute an envelope (below); the rest contribute nothing.

        if (tree.Format != effectiveFormat)
        {
            _logger.LogWarning(
                "Order {OrderId}: {Source} output structure is designed as {TreeFormat} but this connection " +
                "delivers {EffectiveFormat} — the structure was NOT applied (delivering {EffectiveFormat} would " +
                "have shipped {TreeFormat} bytes under a {EffectiveFormat} content type and file name).",
                orderId, source, tree.Format, effectiveFormat, effectiveFormat, tree.Format, effectiveFormat);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads the promoted <see cref="PoMappingConfig.OutputTree"/> out of a pinned revision's
    /// <c>InputMappingJson</c> snapshot (WP-12). Returns null — meaning "fall through to the flat
    /// output snapshot, then the fixed transformer" — for a blank snapshot, a snapshot with no tree,
    /// an unusable tree (an empty root emits an empty document, strictly worse than the complete
    /// fixed one), or a malformed snapshot. Logged, never thrown: a bad snapshot must not brick a
    /// pinned order's delivery.
    /// </summary>
    private OutputNodeTemplate? TryReadPinnedOutputTree(EffectiveConnectionConfig effective, Guid orderId)
    {
        if (string.IsNullOrWhiteSpace(effective.InputMappingJson))
            return null;

        try
        {
            var config = JsonSerializer.Deserialize<PoMappingConfig>(
                effective.InputMappingJson, RevisionOutputSerializerOptions);

            return OrderMappingOverrideReader.HasUsablePromotedOutputTree(config)
                ? config!.OutputTree
                : null;
        }
        catch (Exception ex)
        {
            // Same reason as TryReadPinnedOutputConfig: this is the LAST guard on the pinned path, so
            // it must catch what the unpinned path catches (TryReadSupplierPromotedOutputAsync catches
            // Exception). A JsonException-only filter let a NullReferenceException from a
            // `"root": null` snapshot escape TransformAsync entirely.
            _logger.LogWarning(ex,
                "Order {OrderId}: pinned {Source} input mapping snapshot is malformed — no output structure taken from it.",
                orderId, effective.Source);
            return null;
        }
    }

    /// <summary>
    /// Serializes an output-config fragment for the provenance descriptor, using the SAME camelCase
    /// shape <c>PoMappingService</c> persists, so the digested text is a stable representation of the
    /// stored config rather than an incidental one.
    /// </summary>
    private static string SerializeForDigest(object? value) =>
        JsonSerializer.Serialize(value, SupplierOutputSerializerOptions);

    // ── Supplier-promoted output mapping (launch batch 4A) ────────────────────

    /// <summary>
    /// Reads the supplier's reusable <see cref="PoMappingConfig"/> and, when it carries a USABLE
    /// promoted output mapping (<see cref="OrderMappingOverrideReader.HasUsablePromotedOutput"/>),
    /// wraps it as a synthetic per-order-shaped <see cref="OrderMappingOverride"/> so the EXISTING
    /// override machinery (native CSV/JSON builder + structured effective-entity resolver) is reused
    /// verbatim. The order's custom fields are preserved so promoted output rules referencing them
    /// still resolve; the per-order SourceMap / template are intentionally NOT carried over (mirrors
    /// <c>ReplayService.BuildRevisionOverride</c>). Defensive: any failure (e.g. malformed
    /// <c>SupplierPoMapping.ConfigJson</c>) logs a warning and returns null so the caller falls
    /// through to the fixed transformer — never a throw.
    /// </summary>
    private async Task<(OrderMappingOverride? Override, string? OutputJson)> TryReadSupplierPromotedOutputAsync(
        Guid organisationId, Guid supplierId, OrderMappingOverride? mappingOverride, CancellationToken ct)
    {
        try
        {
            var supplierConfig = await _poMappings.GetAsync(organisationId, supplierId, ct);

            var hasFlat = OrderMappingOverrideReader.HasUsablePromotedOutput(supplierConfig);
            var hasTree = OrderMappingOverrideReader.HasUsablePromotedOutputTree(supplierConfig);
            if (!hasFlat && !hasTree)
                return (null, null);

            var synthetic = new OrderMappingOverride
            {
                CustomFields = mappingOverride?.CustomFields ?? new List<CustomField>(),
                Output       = hasFlat ? supplierConfig!.Output     : null,
                OutputTree   = hasTree ? supplierConfig!.OutputTree : null,
            };

            // Provenance descriptor for the FLAT half only — byte-identical to the pre-WP-12 value.
            // Whether the flat config or the tree actually drives the bytes is decided later (a
            // cXML/X12 tree never does), so the caller picks the descriptor; deciding it here made
            // the digest describe a tree that had changed nothing.
            var descriptor = hasFlat
                ? JsonSerializer.Serialize(supplierConfig!.Output, SupplierOutputSerializerOptions)
                : null;

            return (synthetic, descriptor);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read the supplier-promoted output mapping for supplier {SupplierId}; using the fixed transformer.",
                supplierId);
            return (null, null);
        }
    }
}
