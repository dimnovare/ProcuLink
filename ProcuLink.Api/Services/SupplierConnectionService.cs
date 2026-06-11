using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Conformance;

namespace ProcuLink.Api.Services;

/// <summary>
/// Group V1 lifecycle service for the versioned Supplier Connection. Generalises the
/// <see cref="SupplierAcceptanceService"/> versioning precedent (version_no + status +
/// effective_from/to, archive-prior-on-activate) to the whole connection bundle, and adds
/// the connection-level <c>active_revision_id</c> pointer the acceptance precedent lacked.
/// All queries are org-scoped.
///
/// <para>Launch batch 3 adds the lifecycle minimum: a REAL test pack
/// (<see cref="RunTestPackAsync"/> — replay leg + conformance leg, evidence stored on the
/// revision), an evidence-gated <see cref="PublishAsync"/>, and <see cref="RollbackAsync"/>
/// (clone a previously-published revision into a new published one and move the pointer).</para>
/// </summary>
public sealed class SupplierConnectionService : ISupplierConnectionService
{
    /// <summary>Recent-order window the test pack replays (bounded, prompt-specified).</summary>
    public const int TestPackRecentOrders = 5;

    private static readonly JsonSerializerOptions SummaryJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ProcuLinkDbContext  _db;
    private readonly IReplayService      _replay;
    private readonly IConformanceService _conformance;

    public SupplierConnectionService(
        ProcuLinkDbContext db, IReplayService replay, IConformanceService conformance)
    {
        _db          = db;
        _replay      = replay;
        _conformance = conformance;
    }

    public async Task<IReadOnlyList<SupplierConnection>> ListAsync(Guid orgId, CancellationToken ct) =>
        await _db.SupplierConnections
            .Where(c => c.OrgId == orgId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

    public async Task<SupplierConnection?> GetAsync(Guid orgId, Guid connectionId, CancellationToken ct) =>
        await _db.SupplierConnections
            .Include(c => c.Revisions)
            .Where(c => c.OrgId == orgId && c.Id == connectionId)
            .FirstOrDefaultAsync(ct);

    public async Task<SupplierConnectionRevision?> GetRevisionAsync(
        Guid orgId, Guid connectionId, Guid revisionId, CancellationToken ct) =>
        await _db.SupplierConnectionRevisions
            .Include(r => r.ItemMappings)
            .Include(r => r.TestCases)
            .Where(r => r.OrgId == orgId && r.ConnectionId == connectionId && r.Id == revisionId)
            .FirstOrDefaultAsync(ct);

    public async Task<SupplierConnection?> EnsureConnectionAsync(
        Guid orgId, Guid supplierId, string? createdBy, CancellationToken ct)
    {
        var existing = await _db.SupplierConnections
            .FirstOrDefaultAsync(c => c.OrgId == orgId && c.SupplierId == supplierId, ct);
        if (existing is not null) return existing;

        // Supplier must belong to the org (cross-tenant guard).
        var supplier = await _db.Suppliers
            .FirstOrDefaultAsync(s => s.Id == supplierId && s.OrgId == orgId, ct);
        if (supplier is null) return null;

        var now = DateTime.UtcNow;
        var connection = new SupplierConnection
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            SupplierId = supplierId,
            Name       = supplier.Name,
            CreatedBy  = createdBy,
            CreatedAt  = now,
            UpdatedAt  = now,
        };
        _db.SupplierConnections.Add(connection);
        await _db.SaveChangesAsync(ct);
        return connection;
    }

    public async Task<SupplierConnectionRevision?> CreateDraftAsync(
        Guid orgId, Guid connectionId, ConnectionRevisionDraftInput? input,
        bool cloneFromActive, string? createdBy, CancellationToken ct)
    {
        var connection = await _db.SupplierConnections
            .FirstOrDefaultAsync(c => c.OrgId == orgId && c.Id == connectionId, ct);
        if (connection is null) return null;

        var maxVersion = await _db.SupplierConnectionRevisions
            .Where(r => r.ConnectionId == connectionId)
            .Select(r => (int?)r.VersionNo)
            .MaxAsync(ct);
        var nextVersion = (maxVersion ?? 0) + 1;

        var now = DateTime.UtcNow;
        var revisionId = Guid.NewGuid();
        var draft = new SupplierConnectionRevision
        {
            Id           = revisionId,
            ConnectionId = connectionId,
            OrgId        = orgId,
            SupplierId   = connection.SupplierId,
            VersionNo    = nextVersion,
            Status       = "draft",
            CreatedAt    = now,
            CreatedBy    = createdBy,
            CatalogMode  = "live",
        };

        // Clone-from-active takes precedence: snapshot the published revision's bundle into the draft.
        SupplierConnectionRevision? source = null;
        if (cloneFromActive && connection.ActiveRevisionId is not null)
        {
            source = await _db.SupplierConnectionRevisions
                .Include(r => r.ItemMappings)
                .FirstOrDefaultAsync(r => r.Id == connection.ActiveRevisionId, ct);
        }

        if (source is not null)
        {
            draft.InputMappingJson    = source.InputMappingJson;
            draft.OutputMappingJson   = source.OutputMappingJson;
            draft.OutputFormat        = source.OutputFormat;
            draft.DeliveryProtocol    = source.DeliveryProtocol;
            draft.DeliveryConfigJson  = source.DeliveryConfigJson;
            draft.DeliveryAutoDeliver = source.DeliveryAutoDeliver;
            draft.CredentialsRef      = source.CredentialsRef;
            draft.AcceptanceProfileId = source.AcceptanceProfileId;
            draft.AcceptanceVersionNo = source.AcceptanceVersionNo;
            draft.CatalogMode         = source.CatalogMode;
            draft.ItemMappings        = source.ItemMappings.Select(m => CloneMapping(revisionId, m)).ToList();
        }
        else if (input is not null)
        {
            ApplyInput(draft, revisionId, input);
        }

        _db.SupplierConnectionRevisions.Add(draft);
        await _db.SaveChangesAsync(ct);
        return draft;
    }

    public async Task<bool?> UpdateDraftAsync(
        Guid orgId, Guid connectionId, Guid revisionId, ConnectionRevisionDraftInput input, CancellationToken ct)
    {
        var rev = await _db.SupplierConnectionRevisions
            .FirstOrDefaultAsync(r => r.OrgId == orgId && r.ConnectionId == connectionId && r.Id == revisionId, ct);
        if (rev is null) return null;

        // Immutability: only draft/test revisions may be edited; publish is the freeze line.
        if (rev.Status is not ("draft" or "test")) return false;

        ApplyScalars(rev, input);

        // Content changed: stamp the content-update time and VOID any prior test evidence —
        // the evidence-gated publish requires a test run AFTER the last content update.
        rev.UpdatedAt      = DateTime.UtcNow;
        rev.TestResultJson = null;
        rev.TestedAt       = null;
        rev.TestPassed     = null;

        // Replace child item mappings via the DbSet directly (no Include navigation). Delete the
        // old rows in their own SaveChanges first, then insert the new ones — separate units of
        // work avoid the InMemory "update/delete an entity that does not exist" concurrency error
        // a combined remove+reinsert can trigger.
        var oldMappings = await _db.ConnectionRevisionItemMappings
            .Where(m => m.RevisionId == revisionId)
            .ToListAsync(ct);
        if (oldMappings.Count > 0)
        {
            _db.ConnectionRevisionItemMappings.RemoveRange(oldMappings);
            await _db.SaveChangesAsync(ct);
        }
        var newMappings = (input.ItemMappings ?? Array.Empty<ConnectionItemMappingInput>())
            .Select(m => NewMapping(revisionId, m))
            .ToList();
        if (newMappings.Count > 0)
            _db.ConnectionRevisionItemMappings.AddRange(newMappings);

        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<ConnectionTestEvidence?> RunTestPackAsync(
        Guid orgId, Guid connectionId, Guid revisionId, CancellationToken ct)
    {
        var rev = await _db.SupplierConnectionRevisions
            .FirstOrDefaultAsync(r => r.OrgId == orgId && r.ConnectionId == connectionId && r.Id == revisionId, ct);
        if (rev is null) return null;

        var now = DateTime.UtcNow;
        bool passed;
        string summaryJson;
        try
        {
            (passed, summaryJson) = await ExecuteTestPackAsync(orgId, connectionId, revisionId, rev, ct);
        }
        catch (Exception ex)
        {
            // Failures are stored honestly, never thrown: an exploding test pack is itself a FAIL.
            passed = false;
            summaryJson = JsonSerializer.Serialize(new TestPackSummary(
                Replay: null,
                Conformance: null,
                Error: $"Test pack execution failed: {ex.Message}"), SummaryJsonOptions);
        }

        rev.TestResultJson = summaryJson;
        rev.TestedAt       = now;
        rev.TestPassed     = passed;
        await _db.SaveChangesAsync(ct);

        return new ConnectionTestEvidence(passed, now, summaryJson);
    }

    public async Task<ConnectionTestOutcome> MarkTestAsync(
        Guid orgId, Guid connectionId, Guid revisionId, CancellationToken ct)
    {
        var rev = await _db.SupplierConnectionRevisions
            .FirstOrDefaultAsync(r => r.OrgId == orgId && r.ConnectionId == connectionId && r.Id == revisionId, ct);
        if (rev is null) return new ConnectionTestOutcome(ConnectionTestStatus.NotFound, null);
        if (rev.Status is not ("draft" or "test"))
            return new ConnectionTestOutcome(ConnectionTestStatus.InvalidStatus, null);

        // Run the REAL test pack and store the evidence (pass or fail, stored honestly).
        var evidence = await RunTestPackAsync(orgId, connectionId, revisionId, ct);

        rev.Status = "test";
        await _db.SaveChangesAsync(ct);
        return new ConnectionTestOutcome(ConnectionTestStatus.Completed, evidence);
    }

    public async Task<ConnectionPublishOutcome> PublishAsync(
        Guid orgId, Guid connectionId, Guid revisionId, string? publishedBy, CancellationToken ct)
    {
        var connection = await _db.SupplierConnections
            .FirstOrDefaultAsync(c => c.OrgId == orgId && c.Id == connectionId, ct);
        if (connection is null) return ConnectionPublishOutcome.NotFound;

        var revisions = await _db.SupplierConnectionRevisions
            .Where(r => r.ConnectionId == connectionId)
            .ToListAsync(ct);
        var target = revisions.FirstOrDefault(r => r.Id == revisionId);
        if (target is null) return ConnectionPublishOutcome.NotFound;

        // Only draft/test may be published; published/archived are frozen. Pre-existing published
        // revisions (incl. the V1 backfilled rev-1 rows) are therefore UNTOUCHED by the evidence
        // gate below — it only applies to NEW publish attempts.
        if (target.Status is not ("draft" or "test")) return ConnectionPublishOutcome.InvalidStatus;

        // Evidence gate: require a PASSING test-pack run that is at least as new as the revision's
        // last content update (UpdatedAt is stamped by UpdateDraftAsync; null = never edited after
        // creation, in which case any passing evidence suffices).
        var evidenceFresh = target.TestPassed == true
            && target.TestedAt is not null
            && (target.UpdatedAt is null || target.TestedAt >= target.UpdatedAt);
        if (!evidenceFresh) return ConnectionPublishOutcome.EvidenceRequired;

        var now = DateTime.UtcNow;
        // Archive the prior published revision (one published per connection — acceptance precedent).
        foreach (var r in revisions)
        {
            if (r.Status == "published" && r.Id != target.Id)
            {
                r.Status = "archived";
                r.EffectiveTo = now;
            }
        }

        target.Status = "published";
        target.EffectiveFrom = now;
        target.EffectiveTo = null;
        target.PublishedAt = now;
        target.PublishedBy = publishedBy;

        connection.ActiveRevisionId = target.Id;
        connection.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        return ConnectionPublishOutcome.Published;
    }

    public async Task<ConnectionRollbackOutcome> RollbackAsync(
        Guid orgId, Guid connectionId, Guid targetRevisionId, string? requestedBy, CancellationToken ct)
    {
        var connection = await _db.SupplierConnections
            .FirstOrDefaultAsync(c => c.OrgId == orgId && c.Id == connectionId, ct);
        if (connection is null)
            return new ConnectionRollbackOutcome(ConnectionRollbackStatus.NotFound, null, null);

        var target = await _db.SupplierConnectionRevisions
            .Include(r => r.ItemMappings)
            .FirstOrDefaultAsync(r => r.OrgId == orgId && r.ConnectionId == connectionId && r.Id == targetRevisionId, ct);
        if (target is null)
            return new ConnectionRollbackOutcome(ConnectionRollbackStatus.NotFound, null, null);

        // The target must be a PREVIOUSLY-PUBLISHED, now archived/superseded revision of THIS
        // connection. Draft/test revisions were never proven live; the currently-published
        // revision is a no-op target.
        if (target.Status != "archived" || target.PublishedAt is null)
            return new ConnectionRollbackOutcome(
                ConnectionRollbackStatus.InvalidTarget, null,
                "Rollback target must be a previously published (now archived) revision of this connection.");

        var maxVersion = await _db.SupplierConnectionRevisions
            .Where(r => r.ConnectionId == connectionId)
            .Select(r => (int?)r.VersionNo)
            .MaxAsync(ct);
        var nextVersion = (maxVersion ?? 0) + 1;

        var now = DateTime.UtcNow;
        var actor = $"rollback:{(string.IsNullOrWhiteSpace(requestedBy) ? "unknown" : requestedBy)}";
        var cloneId = Guid.NewGuid();
        var clone = new SupplierConnectionRevision
        {
            Id            = cloneId,
            ConnectionId  = connectionId,
            OrgId         = orgId,
            SupplierId    = connection.SupplierId,
            VersionNo     = nextVersion,
            Status        = "published",
            EffectiveFrom = now,
            EffectiveTo   = null,
            PublishedAt   = now,
            CreatedAt     = now,
            CreatedBy     = actor,
            PublishedBy   = actor,
            // Full bundle clone from the target revision.
            InputMappingJson    = target.InputMappingJson,
            OutputMappingJson   = target.OutputMappingJson,
            OutputFormat        = target.OutputFormat,
            DeliveryProtocol    = target.DeliveryProtocol,
            DeliveryConfigJson  = target.DeliveryConfigJson,
            DeliveryAutoDeliver = target.DeliveryAutoDeliver,
            CredentialsRef      = target.CredentialsRef,
            AcceptanceProfileId = target.AcceptanceProfileId,
            AcceptanceVersionNo = target.AcceptanceVersionNo,
            CatalogMode         = target.CatalogMode,
            // Test evidence is NOT carried over — it belongs to the revision it was taken on.
            // Rollback bypasses the evidence gate by design: the bundle was already published.
            ItemMappings = target.ItemMappings.Select(m => CloneMapping(cloneId, m)).ToList(),
        };

        // Circular-FK lesson (connection.active_revision_id → revisions): the clone row must
        // EXIST before the pointer can reference it — save the new revision FIRST, then move
        // the pointer in a second SaveChanges.
        _db.SupplierConnectionRevisions.Add(clone);
        await _db.SaveChangesAsync(ct);

        // Archive every other currently-published revision (one published per connection) and
        // move the active pointer to the clone.
        var published = await _db.SupplierConnectionRevisions
            .Where(r => r.ConnectionId == connectionId && r.Status == "published" && r.Id != cloneId)
            .ToListAsync(ct);
        foreach (var r in published)
        {
            r.Status = "archived";
            r.EffectiveTo = now;
        }

        connection.ActiveRevisionId = cloneId;
        connection.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        return new ConnectionRollbackOutcome(ConnectionRollbackStatus.Completed, clone, null);
    }

    public async Task<bool?> ArchiveAsync(Guid orgId, Guid connectionId, Guid revisionId, CancellationToken ct)
    {
        var connection = await _db.SupplierConnections
            .FirstOrDefaultAsync(c => c.OrgId == orgId && c.Id == connectionId, ct);
        if (connection is null) return null;

        var rev = await _db.SupplierConnectionRevisions
            .FirstOrDefaultAsync(r => r.OrgId == orgId && r.ConnectionId == connectionId && r.Id == revisionId, ct);
        if (rev is null) return null;

        var now = DateTime.UtcNow;
        rev.Status = "archived";
        rev.EffectiveTo = now;

        if (connection.ActiveRevisionId == rev.Id)
        {
            connection.ActiveRevisionId = null;
            connection.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
        return true;
    }

    // ── test pack internals ──────────────────────────────────────────────────

    /// <summary>Serializable summary stored in <c>test_result_json</c> (camelCase).</summary>
    private sealed record TestPackSummary(ReplayLeg? Replay, ConformanceLeg? Conformance, string? Error);
    private sealed record ReplayLeg(bool Passed, int OrderCount, int OutputErrors, int OutputChanged, int ValidationChanged, string? Note);
    private sealed record ConformanceLeg(bool Skipped, bool? Passed, string? Profile, int Errors, int Warnings, string? Note);

    /// <summary>
    /// Runs the two test-pack legs. (a) REPLAY: the revision is replayed over the most recent
    /// <see cref="TestPackRecentOrders"/> orders via the existing replay engine (non-mutating,
    /// never delivers). 0 orders = pass-with-note. With orders, the leg passes when at least one
    /// order rendered (i.e. the revision can actually produce output); per-order render errors
    /// are counted honestly in the summary. (b) CONFORMANCE: a replayed output is validated
    /// against its NAMED standards profile where the revision's format has one; skip-with-note
    /// otherwise. NO delivery test-fire — side effects are out of bounds here.
    /// </summary>
    private async Task<(bool Passed, string SummaryJson)> ExecuteTestPackAsync(
        Guid orgId, Guid connectionId, Guid revisionId, SupplierConnectionRevision rev, CancellationToken ct)
    {
        var hasOrders = await _db.PurchaseOrders
            .AnyAsync(o => o.OrgId == orgId && o.SupplierId == rev.SupplierId, ct);

        if (!hasOrders)
        {
            var emptySummary = new TestPackSummary(
                new ReplayLeg(true, 0, 0, 0, 0, "No orders exist for this supplier yet — replay pass-with-note."),
                new ConformanceLeg(true, null, null, 0, 0, "No replayed output available to conformance-check."),
                Error: null);
            return (true, JsonSerializer.Serialize(emptySummary, SummaryJsonOptions));
        }

        var replay = await _replay.ReplayAsync(
            orgId, connectionId, revisionId,
            new ReplayRequest(OrderIds: null, RecentLimit: TestPackRecentOrders), ct);

        if (replay is null)
        {
            var missing = new TestPackSummary(
                new ReplayLeg(false, 0, 0, 0, 0, "Replay could not resolve the connection/revision."),
                new ConformanceLeg(true, null, null, 0, 0, "Skipped — replay produced no output."),
                Error: null);
            return (false, JsonSerializer.Serialize(missing, SummaryJsonOptions));
        }

        // Replay leg: with orders present, the revision must render at least ONE of them —
        // a revision that cannot produce any output must not publish. Per-order errors on
        // historically-bad orders are recorded, not fatal.
        var outputErrors      = replay.Orders.Count(o => o.OutputError is not null);
        var rendered          = replay.Orders.Count(o => o.DraftOutput is not null);
        var outputChanged     = replay.Orders.Count(o => o.OutputChanged);
        var validationChanged = replay.Orders.Count(o => o.ValidationChanged);
        var replayPassed      = replay.OrderCount == 0 || rendered > 0;
        var replayNote = replay.OrderCount == 0
            ? "No recent orders matched the replay window — pass-with-note."
            : outputErrors > 0
                ? $"{outputErrors} of {replay.OrderCount} replayed order(s) failed to render under this revision."
                : null;
        var replayLeg = new ReplayLeg(replayPassed, replay.OrderCount, outputErrors, outputChanged, validationChanged, replayNote);

        // Conformance leg: check the first successfully replayed output against the named
        // profile for the revision's output format, where one exists.
        ConformanceLeg conformanceLeg;
        var sample = replay.Orders.FirstOrDefault(o => o.DraftOutput is not null);
        var format = ParseFormat(rev.OutputFormat);
        if (sample is null)
        {
            conformanceLeg = new ConformanceLeg(true, null, null, 0, 0, "Skipped — no replayed output was produced to check.");
        }
        else if (format is null || !_conformance.SupportsFormat(format.Value))
        {
            conformanceLeg = new ConformanceLeg(
                true, null, null, 0, 0,
                $"Skipped — output format '{rev.OutputFormat ?? "(none)"}' has no named conformance profile.");
        }
        else
        {
            var report = _conformance.Check(sample.DraftOutput!, format.Value);
            conformanceLeg = new ConformanceLeg(
                false, report.OverallPass, report.ProfileName, report.ErrorCount, report.WarningCount,
                report.OverallPass ? null : "Replayed output failed its named standards profile.");
        }

        var passed = replayPassed && (conformanceLeg.Skipped || conformanceLeg.Passed == true);
        var summary = new TestPackSummary(replayLeg, conformanceLeg, Error: null);
        return (passed, JsonSerializer.Serialize(summary, SummaryJsonOptions));
    }

    private static OutputFormat? ParseFormat(string? format) =>
        Enum.TryParse<OutputFormat>(format, ignoreCase: true, out var f) ? f : null;

    // ── helpers ──────────────────────────────────────────────────────────────
    // Scalar-only assignment (used by both create and update); item mappings are handled
    // separately so the UPDATE path can mutate the TRACKED collection rather than reassign it
    // (reassigning while old children are marked Deleted trips an InMemory concurrency error).
    private static void ApplyScalars(SupplierConnectionRevision rev, ConnectionRevisionDraftInput input)
    {
        rev.InputMappingJson    = input.InputMappingJson;
        rev.OutputMappingJson   = input.OutputMappingJson;
        rev.OutputFormat        = input.OutputFormat;
        rev.DeliveryProtocol    = input.DeliveryProtocol;
        rev.DeliveryConfigJson  = input.DeliveryConfigJson;
        rev.DeliveryAutoDeliver = input.DeliveryAutoDeliver;
        rev.CredentialsRef      = input.CredentialsRef;
        rev.AcceptanceProfileId = input.AcceptanceProfileId;
        rev.AcceptanceVersionNo = input.AcceptanceVersionNo;
        rev.CatalogMode         = string.IsNullOrWhiteSpace(input.CatalogMode) ? "live" : input.CatalogMode;
    }

    // For a brand-new (untracked) draft entity it's safe to set the navigation collection directly.
    private static void ApplyInput(SupplierConnectionRevision rev, Guid revisionId, ConnectionRevisionDraftInput input)
    {
        ApplyScalars(rev, input);
        rev.ItemMappings = (input.ItemMappings ?? Array.Empty<ConnectionItemMappingInput>())
            .Select(m => NewMapping(revisionId, m)).ToList();
    }

    // Live-parity normalisation (launch-batch-7 review fix): the live ItemMappingService.UpsertAsync
    // TRIMS both codes, and BOTH resolvers (live ResolveManyAsync and the pinned-revision
    // ResolveFromSnapshot) match against TRIMMED requested codes — so a padded code written into a
    // revision snapshot could never resolve. Trim at every snapshot-write seam (draft input AND
    // clone) so snapshot rows behave exactly like live item-mapping rows. (The V1 backfill copy
    // stays verbatim by design: it mirrors live rows, which the live writer already trims.)
    private static ConnectionRevisionItemMapping NewMapping(Guid revisionId, ConnectionItemMappingInput m) => new()
    {
        Id               = Guid.NewGuid(),
        RevisionId       = revisionId,
        BuyerItemCode    = m.BuyerItemCode?.Trim() ?? string.Empty,
        SupplierItemCode = m.SupplierItemCode?.Trim() ?? string.Empty,
        Confidence       = m.Confidence,
        Source           = m.Source,
    };

    private static ConnectionRevisionItemMapping CloneMapping(Guid revisionId, ConnectionRevisionItemMapping m) => new()
    {
        Id               = Guid.NewGuid(),
        RevisionId       = revisionId,
        BuyerItemCode    = m.BuyerItemCode?.Trim() ?? string.Empty,
        SupplierItemCode = m.SupplierItemCode?.Trim() ?? string.Empty,
        Confidence       = m.Confidence,
        Source           = m.Source,
    };
}
