using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Conformance;
using ProcuLink.Transform.Output;

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
    private readonly bool                _revisionAuthority;

    public SupplierConnectionService(
        ProcuLinkDbContext db, IReplayService replay, IConformanceService conformance,
        IConfiguration? configuration = null)
    {
        _db          = db;
        _replay      = replay;
        _conformance = conformance;
        // Same flag the resolver gates delivery routing on — when OFF, the live config governs
        // delivery directly, so there is nothing to route into a versioned revision. Read through
        // the resolver's own IsEnabled so "same flag" is literally true: a second hand-rolled parse
        // that disagreed (say, treating "1" as on) would republish revisions the resolver then
        // ignored. ON in production on both Railway services — see RevisionAuthorityHosts.
        _revisionAuthority = EffectiveConnectionConfigResolver.IsEnabled(configuration);
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

        // Captured BEFORE ApplyScalars, which assigns rev.DeliveryConfigJson = input.DeliveryConfigJson.
        // Reading it afterwards would compare the incoming blob against itself and grandfather
        // everything, including a credential header the caller just introduced.
        var storedConfigJson = rev.DeliveryConfigJson;

        ApplyScalars(rev, input, storedConfigJson);

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
                "You can only roll back to a version that was live before. Pick one of the archived " +
                "versions in the revision history — the current live version and any drafts can't be rollback targets.");

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

    // ── honest + route-to-versioned (live delivery edit → new published revision) ──

    public async Task<DeliveryRepublishOutcome> RepublishLiveDeliveryAsync(
        Guid orgId, Guid supplierId, string? publishedBy, CancellationToken ct)
    {
        // Flag off → the live config governs delivery directly; nothing to route.
        if (!_revisionAuthority)
            return new DeliveryRepublishOutcome(DeliveryRepublishStatus.NotGoverned, null);

        var connection = await _db.SupplierConnections
            .FirstOrDefaultAsync(c => c.OrgId == orgId && c.SupplierId == supplierId, ct);
        // No connection / no published active revision → live config already governs (unpinned).
        if (connection?.ActiveRevisionId is null)
            return new DeliveryRepublishOutcome(DeliveryRepublishStatus.NotGoverned, null);

        var active = await _db.SupplierConnectionRevisions
            .Include(r => r.ItemMappings)
            .FirstOrDefaultAsync(r => r.Id == connection.ActiveRevisionId
                                   && r.OrgId == orgId && r.Status == "published", ct);
        if (active is null)
            return new DeliveryRepublishOutcome(DeliveryRepublishStatus.NotGoverned, null);

        // Deterministic single read of the live delivery config (defensive OrderBy — there is a
        // UNIQUE(org_id, supplier_id) index, so at most one row exists).
        var live = await _db.SupplierDeliveryConfigs
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync(x => x.OrgId == orgId && x.SupplierId == supplierId, ct);
        if (live is null)
            return new DeliveryRepublishOutcome(DeliveryRepublishStatus.NotGoverned, null);

        // Idempotent: a no-op save must not spawn an identical version.
        if (DeliverySnapshotMatches(active, live))
            return new DeliveryRepublishOutcome(DeliveryRepublishStatus.Unchanged, null);

        var maxVersion = await _db.SupplierConnectionRevisions
            .Where(r => r.ConnectionId == connection.Id)
            .Select(r => (int?)r.VersionNo)
            .MaxAsync(ct);
        var nextVersion = (maxVersion ?? 0) + 1;

        var now   = DateTime.UtcNow;
        var actor = $"live-delivery-edit:{(string.IsNullOrWhiteSpace(publishedBy) ? "api" : publishedBy)}";
        var cloneId = Guid.NewGuid();
        var clone = new SupplierConnectionRevision
        {
            Id            = cloneId,
            ConnectionId  = connection.Id,
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
            // Non-delivery bundle cloned from the active revision …
            InputMappingJson    = active.InputMappingJson,
            OutputMappingJson   = active.OutputMappingJson,
            AcceptanceProfileId = active.AcceptanceProfileId,
            AcceptanceVersionNo = active.AcceptanceVersionNo,
            CatalogMode         = active.CatalogMode,
            // … delivery channel taken from the operator's current live edit.
            DeliveryProtocol    = live.Protocol,
            DeliveryConfigJson  = live.ConfigJson,
            DeliveryAutoDeliver = live.AutoDeliver,
            CredentialsRef      = string.IsNullOrEmpty(live.EncryptedCredentials) ? null : live.EncryptedCredentials,
            OutputFormat        = live.OutputFormat ?? active.OutputFormat,
            ItemMappings        = active.ItemMappings.Select(m => CloneMapping(cloneId, m)).ToList(),
        };

        // Circular-FK lesson (connection.active_revision_id → revisions): the clone must EXIST
        // before the pointer can reference it — save it FIRST, move the pointer second.
        _db.SupplierConnectionRevisions.Add(clone);
        await _db.SaveChangesAsync(ct);

        var priorPublished = await _db.SupplierConnectionRevisions
            .Where(r => r.ConnectionId == connection.Id && r.Status == "published" && r.Id != cloneId)
            .ToListAsync(ct);
        foreach (var r in priorPublished)
        {
            // Status + effective_to only — the immutability trigger allows archiving (no content
            // column changes), exactly as PublishAsync/RollbackAsync do.
            r.Status = "archived";
            r.EffectiveTo = now;
        }

        connection.ActiveRevisionId = cloneId;
        connection.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        return new DeliveryRepublishOutcome(DeliveryRepublishStatus.Republished, nextVersion);
    }

    public async Task<DeliveryGovernanceInfo> DescribeDeliveryGovernanceAsync(
        Guid orgId, Guid supplierId, CancellationToken ct)
    {
        var none = new DeliveryGovernanceInfo(false, null, null);
        if (!_revisionAuthority)
            return none;

        var connection = await _db.SupplierConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.OrgId == orgId && c.SupplierId == supplierId, ct);
        if (connection?.ActiveRevisionId is null)
            return none;

        var active = await _db.SupplierConnectionRevisions
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == connection.ActiveRevisionId
                                   && r.OrgId == orgId && r.Status == "published", ct);
        if (active is null)
            return none;

        var live = await _db.SupplierDeliveryConfigs
            .AsNoTracking()
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync(x => x.OrgId == orgId && x.SupplierId == supplierId, ct);

        // Governed, but no live row to compare against (delivery configured only via the versioned
        // path) → can't say in/out of sync.
        bool? matches = live is null ? null : DeliverySnapshotMatches(active, live);

        return new DeliveryGovernanceInfo(
            RevisionGoverned:         true,
            ActiveVersionNo:          active.VersionNo,
            LiveMatchesActiveDelivery: matches);
    }

    /// <summary>True when the active revision's delivery snapshot already equals the live config —
    /// the no-op guard that keeps a repeated save from spawning identical revisions.</summary>
    private static bool DeliverySnapshotMatches(SupplierConnectionRevision active, SupplierDeliveryConfig live)
    {
        var liveCreds      = string.IsNullOrEmpty(live.EncryptedCredentials) ? null : live.EncryptedCredentials;
        var effectiveFormat = live.OutputFormat ?? active.OutputFormat;
        return string.Equals(active.DeliveryProtocol ?? "", live.Protocol ?? "", StringComparison.OrdinalIgnoreCase)
            && active.DeliveryAutoDeliver == live.AutoDeliver
            && string.Equals(active.CredentialsRef ?? "", liveCreds ?? "", StringComparison.Ordinal)
            && string.Equals(active.OutputFormat ?? "", effectiveFormat ?? "", StringComparison.OrdinalIgnoreCase)
            && JsonEquals(active.DeliveryConfigJson, live.ConfigJson);
    }

    /// <summary>Semantic jsonb equality (key order / whitespace insensitive); falls back to ordinal
    /// string compare when either side is not parseable JSON.</summary>
    private static bool JsonEquals(string? a, string? b)
    {
        if (a is null && b is null) return true;
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return string.Equals(a, b, StringComparison.Ordinal);
        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(a), JsonNode.Parse(b));
        }
        catch (JsonException)
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }
    }

    // ── test pack internals ──────────────────────────────────────────────────

    /// <summary>Serializable summary stored in <c>test_result_json</c> (camelCase).</summary>
    private sealed record TestPackSummary(ReplayLeg? Replay, ConformanceLeg? Conformance, string? Error, ParseLegSummary? ParseLeg = null);
    private sealed record ReplayLeg(bool Passed, int OrderCount, int OutputErrors, int OutputChanged, int ValidationChanged, string? Note);
    private sealed record ConformanceLeg(bool Skipped, bool? Passed, string? Profile, int Errors, int Warnings, string? Note);
    /// <summary>Replay flip A — parse-from-source leg evidence: how many orders re-parsed / would parse differently / failed / were skipped.</summary>
    private sealed record ParseLegSummary(bool Passed, int OrdersReParsed, int ParseChanges, int Failures, int Skipped, string? Note);

    /// <summary>
    /// Runs the test-pack legs. (a) REPLAY: the revision is replayed over the most recent
    /// <see cref="TestPackRecentOrders"/> orders via the existing replay engine (non-mutating,
    /// never delivers). 0 orders = pass-with-note. With orders, the leg passes when at least one
    /// order rendered (i.e. the revision can actually produce output); per-order render errors
    /// are counted honestly in the summary. (b) CONFORMANCE: a replayed output is validated
    /// against its NAMED standards profile where the revision's format has one; skip-with-note
    /// otherwise. (c) PARSE-FROM-SOURCE (replay flip A): orders with a stored source file are
    /// re-parsed IN MEMORY under the revision's input-mapping + item-mapping snapshots. The leg
    /// runs UNCONDITIONALLY (not just when InputMappingJson differs from the previous published
    /// revision's): the pack is bounded at ≤<see cref="TestPackRecentOrders"/> orders, the leg is
    /// deterministic-only (PDF/AI sources skip-with-note) and in-memory, so it is cheap — and an
    /// always-on leg also catches ITEM-MAPPING snapshot drift plus the no-previous-revision case
    /// that an input-mapping-diff trigger would miss. Pass criterion: when any order with a
    /// source file exists, at least ONE must re-parse successfully; parse DIFFERENCES are
    /// informational (a mapping change SHOULD change parsing), only failures gate. NO delivery
    /// test-fire — side effects are out of bounds here.
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
                Error: null,
                new ParseLegSummary(true, 0, 0, 0, 0, "No orders exist for this supplier yet — nothing to re-parse."));
            return (true, JsonSerializer.Serialize(emptySummary, SummaryJsonOptions));
        }

        var replay = await _replay.ReplayAsync(
            orgId, connectionId, revisionId,
            new ReplayRequest(OrderIds: null, RecentLimit: TestPackRecentOrders, IncludeParseLeg: true), ct);

        if (replay is null)
        {
            var missing = new TestPackSummary(
                new ReplayLeg(false, 0, 0, 0, 0, "Replay could not resolve the connection/revision."),
                new ConformanceLeg(true, null, null, 0, 0, "Skipped — replay produced no output."),
                Error: null,
                new ParseLegSummary(true, 0, 0, 0, 0, "Skipped — replay produced no orders to re-parse."));
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
                ? $"{outputErrors} of {replay.OrderCount} recent order(s) couldn't be rebuilt with this version's " +
                  "mapping (usually an unmapped or unresolved field). Open them from the inbox to see which field, " +
                  "fix the mapping, then re-run the test."
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
                $"Standards check skipped — '{rev.OutputFormat ?? "(none)"}' is a flexible supplier format with no " +
                $"published standard to check it against (standards-checked formats: {StandardsCheckedFormatsForMessage}). " +
                "Your output still delivers normally.");
        }
        else
        {
            var report = _conformance.Check(sample.DraftOutput!, format.Value);
            conformanceLeg = new ConformanceLeg(
                false, report.OverallPass, report.ProfileName, report.ErrorCount, report.WarningCount,
                report.OverallPass ? null : "Replayed output failed its named standards profile.");
        }

        // Parse-from-source leg (replay flip A): aggregate the per-order parse legs the replay
        // produced. Skips (no stored file / AI-extracted PDF source / host without storage) are
        // honest non-evidence; with ≥1 eligible order, at least one must re-parse successfully.
        // Parse DIFFERENCES are informational only — they never fail the pack.
        var parseLegs     = replay.Orders.Select(o => o.ParseLeg).OfType<ReplayParseLegDto>().ToList();
        var reParsed      = parseLegs.Count(p => p.Status == "reparsed");
        var parseChanges  = parseLegs.Count(p => p.ParseChanged);
        var parseFailures = parseLegs.Count(p => p.Status == "failed");
        var parseSkipped  = parseLegs.Count(p => p.Status == "skipped");
        var parseEligible = reParsed + parseFailures;
        var parsePassed   = parseEligible == 0 || reParsed > 0;
        var parseNote = parseEligible == 0
            ? "No replayed order had a re-parsable stored source file — parse leg skip-with-note."
            : parseFailures > 0
                ? $"{parseFailures} of {parseEligible} order(s) with source files failed to re-parse under this revision's input mapping."
                : parseChanges > 0
                    ? $"{parseChanges} order(s) would parse differently under this revision (informational, not a failure)."
                    : null;
        var parseLegSummary = new ParseLegSummary(parsePassed, reParsed, parseChanges, parseFailures, parseSkipped, parseNote);

        var passed = replayPassed && (conformanceLeg.Skipped || conformanceLeg.Passed == true) && parsePassed;
        var summary = new TestPackSummary(replayLeg, conformanceLeg, Error: null, parseLegSummary);
        return (passed, JsonSerializer.Serialize(summary, SummaryJsonOptions));
    }

    private static OutputFormat? ParseFormat(string? format) =>
        Enum.TryParse<OutputFormat>(format, ignoreCase: true, out var f) ? f : null;

    /// <summary>
    /// The formats a revision can actually be PUBLISHED with that also have a named standards profile,
    /// as an operator-facing list.
    ///
    /// <para>This sentence used to be typed out as "only cXML, UBL, X12, and EDIFACT are
    /// standards-checked", which told a customer ProcuLink emits EDIFACT. It does not: EDIFACT is
    /// inbound-only (<c>EdifactOrderParser</c> reads it, no <c>ITransformService</c> writes it), and
    /// <see cref="OutputFormat.EdifactOrders"/> exists solely to name a conformance profile. Frontend
    /// PR #125 removed the same claim from every user-facing surface it appeared on; this copy was
    /// still being served from the API.</para>
    ///
    /// <para>Derived rather than typed, from the two facts that decide it: what
    /// <see cref="OutputTransformRegistry.Catalog"/> can build, and what <c>_conformance</c> has a
    /// profile for. A transform or a profile added on either side changes this sentence with no edit
    /// here — which is what stops it going stale a second time.</para>
    /// </summary>
    private string StandardsCheckedFormatsForMessage =>
        _standardsCheckedFormats ??= BuildStandardsCheckedFormats();

    private string? _standardsCheckedFormats;

    private string BuildStandardsCheckedFormats()
    {
        var checkable = OutputTransformRegistry.Catalog.Buildable
            .Where(_conformance.SupportsFormat)
            .Select(OutputFormatCatalog.Token)
            .ToList();

        // Honest empty case rather than a dangling "formats: )". Unreachable with the real
        // ConformanceService; reachable with a stub that supports nothing.
        return checkable.Count == 0 ? "none" : string.Join(", ", checkable);
    }

    /// <summary>
    /// Refuses a caller-supplied revision delivery endpoint that would send the purchase order — and
    /// the credentials that authenticate it — over plain http, or that carries a username/password
    /// in the URL itself.
    ///
    /// <para>Deliberately the SAME primitive the live delivery-config save path runs
    /// (<c>DeliveryConfigService.ValidateTransportSecurity</c>): both reach the verdict through
    /// <see cref="DeliveryConfigTransport.InspectEndpoint"/>, which judges every url-keyed value in
    /// the blob with <see cref="OutboundUrlPolicy"/>. A published revision is what a pinned order actually
    /// delivers through, so a second, hand-rolled check here would be a second security rule that
    /// could drift from the first — which is precisely how this gap existed: the URL was refused on
    /// one of the two write paths and unread on the other.</para>
    ///
    /// <para><b>Only caller-supplied input.</b> The clone-from-active, rollback, republish-from-live
    /// and publish paths carry a bundle that is ALREADY live. Refusing those would turn a security
    /// weakness into an outage — an operator could not publish a mapping fix for a supplier whose
    /// endpoint predates enforcement, and rollback (whose whole purpose is restoring a version that
    /// was live before) would fail. Those keep working; the read path reports
    /// <c>InsecureTransportWarning</c> and the dispatchers log on every attempt, so no such config is
    /// silent.</para>
    /// </summary>
    private static void ValidateTransportSecurity(string? protocol, string? configJson)
    {
        // InspectEndpoint judges EVERY url-keyed value in the blob, not just the one the
        // deserializer happens to bind, and returns Allow when the protocol carries no URL at all.
        var verdict = DeliveryConfigTransport.InspectEndpoint(protocol, configJson);
        if (!verdict.Allowed)
            throw new OutboundUrlPolicyException(
                verdict, nameof(ConnectionRevisionDraftInput.DeliveryConfigJson));
    }

    /// <summary>
    /// Refuses a credential written into a caller-supplied revision's extra-headers map. Every entry
    /// of that map is applied to the outbound request by <c>HttpDeliveryDispatcher</c>, and
    /// <c>config_json</c> is a cleartext column, so a token typed there is stored in the clear and
    /// copied into the snapshot a pinned order delivers through.
    ///
    /// <para>Deliberately the SAME primitive the live delivery-config save path runs
    /// (<c>DeliveryConfigService.ValidateCredentialHeaders</c>): both reach
    /// <see cref="DeliveryConfigTransport.FindCredentialHeaders"/>. A second hand-rolled name check
    /// here would be a second security rule free to drift from the first.</para>
    ///
    /// <para><b>Grandfathered on UPDATE, flat on CREATE.</b> <paramref name="storedConfigJson"/> is
    /// the row's CURRENTLY-STORED blob, so an identical <c>(name, value)</c> pair already persisted
    /// is not treated as a write of a secret; adding a header, or rotating the value of one, is
    /// still refused. <c>UpdateDraftAsync</c> passes the draft's own stored blob; <c>ApplyInput</c>
    /// (create) passes nothing and so grandfathers nothing — a create has no stored predecessor, and
    /// an identical header on some OTHER revision must not license one here.</para>
    ///
    /// <para><b>Why the update leg cannot be flat.</b> A draft acquires a credential header without
    /// ever passing through <c>ApplyScalars</c> — clone-from-active, republish-from-live, rollback
    /// and the V1 backfill all copy a bundle in wholesale, and all four are allowed by design. The
    /// frontend then creates its editable draft with <c>cloneFromActive: true</c>, and because the
    /// PUT replaces the WHOLE bundle, every mapping save echoes the delivery config straight back
    /// (deliberately: without it a mapping save would wipe the draft's delivery channel). A flat
    /// refusal here would therefore 400 every mapping autosave for exactly the pre-enforcement
    /// customers this rule must not strand, with no headers field anywhere in the UI to clear the
    /// fault with. Same unmanaged-key round-trip that shaped the live delivery-config path; the
    /// principle is unchanged — refuse what the caller INTRODUCES, not what they merely echo back.</para>
    /// </summary>
    private static void ValidateCredentialHeaders(string? configJson, string? storedConfigJson)
    {
        var offending = DeliveryConfigTransport.FindCredentialHeaders(configJson, storedConfigJson);
        if (offending.Count > 0)
            throw new CredentialHeaderInConfigException(offending);
    }

    /// <summary>
    /// Refuses a caller-supplied encrypted-credential reference. See
    /// <see cref="ClientSuppliedCredentialsRefException"/> for why a ciphertext taken off a request
    /// body is a credential the caller never proved they own, and for the route that replaces it.
    ///
    /// <para>Null is NOT a refusal — it is "no change", which the partial-update contract depends
    /// on.</para>
    /// </summary>
    private static void ValidateNoClientSuppliedCredentials(string? credentialsRef)
    {
        if (credentialsRef is not null)
            throw new ClientSuppliedCredentialsRefException();
    }

    /// <summary>
    /// Refuses a caller-supplied output format no registered transform can build, and normalises an
    /// accepted one to its persisted lowercase token.
    ///
    /// <para><b>Why this has to be here.</b> With <c>Connections:RevisionAuthority</c> on — which it
    /// is in production on both Railway services — a PUBLISHED revision is the authority that decides
    /// what a pinned order is transformed and delivered as. <c>OutputFormat</c> carries three values
    /// no transform in this solution can build (<c>UblOrder</c>, <c>X12_850</c>,
    /// <c>EdifactOrders</c> — they name conformance PROFILES, and EDIFACT is inbound-only), and
    /// <c>Enum.TryParse(ignoreCase: true)</c> re-hydrates every one of them. This line used to be a
    /// bare <c>rev.OutputFormat = input.OutputFormat</c>, so a revision could be written naming a
    /// format nothing could produce; nothing noticed until an order reached
    /// <c>OrderTransformService</c>'s "No transform service registered for format '…'" and died there
    /// terminally. Published revision content is frozen by
    /// <c>proculink_block_published_revision_content_update</c>, so by then it could not even be
    /// edited back.</para>
    ///
    /// <para>Deliberately the SAME primitive the live delivery-config save path runs
    /// (<c>DeliveryConfigService.NormalizeOutputFormat</c>): both reach
    /// <see cref="OutputTransformRegistry.Catalog"/>, whose allow-list is DERIVED from the registered
    /// <c>ITransformService</c> set. Same reasoning as <see cref="ValidateTransportSecurity"/> — a
    /// second, hand-rolled list here would be a second rule free to drift from the first, which is
    /// exactly how this gap existed.</para>
    ///
    /// <para><b>Only caller-supplied input</b>, for the same reason the transport check is: the
    /// clone-from-active, rollback, republish-from-live and V1-backfill paths copy a bundle that is
    /// ALREADY stored and do not pass through <see cref="ApplyScalars"/>. Refusing those would turn a
    /// write-time guard into an outage for whoever already has such a revision.</para>
    /// </summary>
    private static string? ValidateOutputFormat(string? outputFormat) =>
        OutputTransformRegistry.Catalog.Normalize(outputFormat);

    // ── helpers ──────────────────────────────────────────────────────────────
    // Scalar-only assignment (used by both create and update); item mappings are handled
    // separately so the UPDATE path can mutate the TRACKED collection rather than reassign it
    // (reassigning while old children are marked Deleted trips an InMemory concurrency error).
    // storedConfigJson is the row's CURRENTLY-STORED delivery blob, or null when there is no
    // predecessor. It grandfathers an unchanged credential header on UPDATE only — see
    // ValidateCredentialHeaders. The create leg leaves it at the default and so refuses flat.
    private static void ApplyScalars(
        SupplierConnectionRevision rev, ConnectionRevisionDraftInput input, string? storedConfigJson = null)
    {
        // Before ANY assignment: this is the only place a caller-supplied delivery endpoint,
        // credential blob or output format enters a revision, and a half-applied bundle behind a
        // refusal would be worse than the refusal.
        ValidateTransportSecurity(input.DeliveryProtocol, input.DeliveryConfigJson);
        ValidateCredentialHeaders(input.DeliveryConfigJson, storedConfigJson);
        ValidateNoClientSuppliedCredentials(input.CredentialsRef);
        var outputFormat = ValidateOutputFormat(input.OutputFormat);

        rev.InputMappingJson    = input.InputMappingJson;
        rev.OutputMappingJson   = input.OutputMappingJson;
        // Normalised to the persisted lowercase token, the same value the live delivery-config row
        // stores — so the two representations a drift check compares are the same spelling, not two
        // casings of it.
        rev.OutputFormat        = outputFormat;
        rev.DeliveryProtocol    = input.DeliveryProtocol;
        rev.DeliveryConfigJson  = input.DeliveryConfigJson;
        rev.DeliveryAutoDeliver = input.DeliveryAutoDeliver;
        // CredentialsRef is left ENTIRELY alone here, in both directions.
        //
        // Null still means "no change": the positional DTO cannot distinguish "omitted" from
        // "explicit null", so a mapping-only partial update deserializes credentialsRef to null, and
        // wiping on that would silently lose the supplier's delivery credentials at the next publish.
        //
        // Non-null is now REFUSED rather than written (see ValidateNoClientSuppliedCredentials).
        // The value is the AES-GCM ciphertext the dispatchers decrypt to authenticate the outbound
        // request, and it is encrypted with no associated data — bound to the deployment key and to
        // nothing else — so any blob that decrypts, decrypts for every tenant. Taking one off a
        // request body let a caller nominate credentials they never proved they own. Credentials
        // still reach a revision the way they do in production: saved on the supplier delivery
        // config, encrypted server-side, then copied forward by clone-from-active, rollback and
        // republish-from-live — none of which pass through this input.
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
