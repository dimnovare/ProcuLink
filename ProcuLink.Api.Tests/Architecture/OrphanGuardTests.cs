using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// <b>The orphan guard.</b> Rule R1 of the V1 plan is "no new surface without a consumer".
/// Three navigable surfaces reached production writing to stores that nothing ever read back;
/// the rule needed teeth, and this is them. It is meant to be permanent.
///
/// <para><b>What it asserts.</b> Every <c>DbSet&lt;T&gt;</c> on <see cref="ProcuLinkDbContext"/>
/// has at least one reader OUTSIDE its own CRUD service/controller — or is recorded in
/// <see cref="KnownWriteOnlyStores"/> with a WRITTEN reason. The DbSet inventory is discovered
/// reflectively on purpose: a hand-maintained list of tables would defeat the whole point,
/// because the failure being guarded is a table nobody remembered to wire up.</para>
///
/// <para><b>How "reader" is decided</b> — structurally, not by naming convention, because
/// naming conventions are exactly what a new orphan will not follow:</para>
/// <list type="number">
///   <item><description><b>Who touches the store.</b> A file <i>reads</i> it via
///     <c>x.Widgets.Where(…)</c>, <c>x.Set&lt;Widget&gt;().Any(…)</c> (namespace-qualified or
///     not), or an <c>Include</c>/<c>ThenInclude</c> of an EF navigation whose target type is
///     the entity — that last one is how child tables such as <c>InvoiceLines</c> are
///     legitimately read. A file <i>writes</i> it via
///     <c>Add</c>/<c>Remove</c>/<c>Update</c>/<c>Attach</c>/<c>ExecuteDelete</c>/<c>ExecuteUpdate</c>.
///     </description></item>
///   <item><description><b>An independent reader discharges the obligation.</b> A file that
///     reads the store WITHOUT writing it is consuming data some other code path put there.
///     That is a consumer, controller or not.</description></item>
///   <item><description><b>Otherwise the store is self-contained</b> — every file that reads it
///     also maintains it — so we take one hop out: is any owner referenced by something that is
///     not (a) a DI composition root, (b) the owner's own interface/class declaration file, or
///     (c) the store's own CRUD service/controller? If nothing does, the store is navigable,
///     writable, and read by nobody. Orphan.</description></item>
/// </list>
///
/// <para><b>Why step 3 matters more than it looks.</b> Registering a service in the container
/// is not consumption, and neither is an <c>&lt;see cref="IFooService"/&gt;</c> in a doc comment
/// (<c>PurchaseOrderEntity.cs</c> mentions <c>ISchemaFingerprintService</c> in prose — comments
/// are stripped before any matching for exactly this reason). Both look like use and prove
/// nothing.</para>
///
/// <para><b>Deliberate limits.</b> This is a floor, not a proof. It is textual, so a reader
/// reached through an indirection it cannot see is a false negative. And it only reads THIS
/// repo: a store whose real consumer is the frontend, reached through its own CRUD controller,
/// is flagged even though it is perfectly alive. <c>CanonicalFieldDef</c> is exactly that — the
/// mapper in <c>project-proculink</c> consumes it — and the entry on the allowlist says so.
/// Before recording anything here as dead, go and look in the other repo. When the guard is
/// wrong, the fix is a <see cref="KnownWriteOnly"/> entry that says WHY in prose, with the
/// evidence. Never a silent skip.</para>
/// </summary>
public sealed class OrphanGuardTests
{
    /// <summary>
    /// Stores knowingly read by nobody inside this repo. An entry is a confession, not an
    /// exemption: the reason is prose a reviewer can disagree with, and the tests below delete
    /// the hiding place as soon as the store is fixed or removed.
    /// </summary>
    /// <remarks>
    /// Every reason below was settled by going and looking, in BOTH repos at <c>origin/main</c> on
    /// 2026-07-30 — this backend and <c>project-proculink</c> — rather than by inferring from the
    /// guard's output. That mattered: <c>CanonicalFieldDef</c> looked like a dead surface and is
    /// not one. "Appears unused" is not a reason. Name the reader you failed to find, and where
    /// you looked for it.
    /// </remarks>
    private static readonly IReadOnlyList<KnownWriteOnly> KnownWriteOnlyStores = new[]
    {
        // The two entries that sat here — the retired output-template and validation-rule stores —
        // were deleted when Wave 1 removed those entities (#75). That is the allowlist working as
        // designed: it is shrink-only, it named the orphans while they still existed, and it emptied
        // itself as they were retired. Do not re-add an entry for a store that no longer exists.

        // ── Read by code that is not in this repo ────────────────────────────────────────
        new KnownWriteOnly("DataProtectionKey",
            "2026-07-30, not ours to read: ASP.NET Core Data Protection owns this table via "
            + "AddDataProtection().PersistKeysToDbContext<ProcuLinkDbContext>() in ProcuLink.Api/Program.cs. "
            + "The reader is framework code outside this repo, so no in-repo reader can exist. "
            + "Permanent entry — do not 'fix'."),
        new KnownWriteOnly("CanonicalFieldDef",
            "2026-07-30 — flagged by this guard but NOT a dead surface. Corrected after checking the "
            + "other repo: the only in-repo reader is its own CanonicalFieldsController, which is all a "
            + "backend-only scan can see, but the real consumer is the mapper in project-proculink "
            + "(origin/main src/components/bridge/mapper/useMapperModel.ts:249 fetches it through "
            + "src/lib/api/canonical-fields.ts, and mergeCanonicalNodes places a custom field alongside "
            + "the system spine so it can be wired exactly like a built-in — the resulting wire lands in "
            + "the mapping tables the engine DOES read). Recorded as a limit of the guard, not as a "
            + "cleanup target. Do not delete this on the strength of this entry."),

        // ── WP-04 findings, 2026-07-30. Recorded, NOT fixed: each belongs to another packet.
        new KnownWriteOnly("Buyer",
            "WP-04 finding 2026-07-30 — FOUNDER CALL. Both repos checked at origin/main before writing "
            + "this. The surface is live: project-proculink src/app/(app)/library/buyers/page.tsx, full "
            + "CRUD at src/lib/api-client.ts L1917-L1946 against /api/buyers. But no row of this table "
            + "reaches the engine: PurchaseOrderEntity has NO BuyerId foreign key — BuyerService.ListAsync "
            + "joins buyers to orders on the denormalised buyer_name STRING, for display counts only — and "
            + "IBuyerService is referenced by nothing but BuyersController and the DI roots. Buyers is "
            + "therefore a directory the user maintains that no parse / mapping / transform / delivery / "
            + "billing path reads. Whether the concept should exist at all is a product decision, which is "
            + "why this one is a founder question and the others are not."),
        new KnownWriteOnly("MappingCorrection",
            "WP-04 finding 2026-07-30: written by ItemMappingService (~L120) when a user changes a "
            + "mapping's supplier item code, and read by NOTHING — no reader in any of the five "
            + "production projects, no endpoint anywhere in ProcuLink.Api, and no route referencing it "
            + "in project-proculink at origin/main. The correction trail is captured and discarded; "
            + "fingerprint learning runs off SchemaFingerprint, not this table."),
        new KnownWriteOnly("Membership",
            "WP-04 finding 2026-07-30, judged INERT rather than write-mostly — checked before calling it "
            + "dead, including the Clerk hypothesis. No reader AND no writer in app code; no route in "
            + "ProcuLink.Api; no frontend call (the OrgSwitcher / UserChipMenu / select-organization hits "
            + "in project-proculink are Clerk's OWN memberships, not this table). Tenancy resolves from "
            + "Clerk organisation claims. The comment at OrdersController.cs:1936 — 'org membership lives "
            + "in Membership' — describes an intent that was never wired. Mapped and migrated, never "
            + "populated. Keep-or-drop decision owed; do not build on it in the meantime."),
        new KnownWriteOnly("RetentionAuditLog",
            "WP-04 finding 2026-07-30, judged LEGITIMATELY write-mostly: an append-only evidence trail "
            + "written by BlobRetentionService (~L225) with a DetailsJson payload, so that 'what did the "
            + "retention sweep delete, and when' can be answered later. It deliberately has no in-app "
            + "reader — no endpoint in ProcuLink.Api exposes it and no frontend route references it at "
            + "origin/main — because the consumer is a human answering a compliance question over SQL. "
            + "An audit trail the application can read back is a weaker audit trail. Expected to stay."),
    };

    /// <summary>
    /// The allowlist as landed, 2026-07-30. <b>SHRINK-ONLY.</b> An entry may be deleted — when the
    /// store gains a real consumer, or is removed — but nothing may ever be added: a NEW orphan has
    /// to be fixed, not excused, and this array is what makes that non-negotiable.
    ///
    /// <para>It is also Wave 1's checklist. When it is empty, R1 holds without exceptions.</para>
    ///
    /// <para>Editing this array to make a build green is the one thing this whole file exists to
    /// prevent. If a genuinely new write-only store is unavoidable, that is a conversation with a
    /// reviewer, in the PR, in the open — not a line quietly added here.</para>
    /// </summary>
    private static readonly IReadOnlySet<string> AllowlistBaselineAt_2026_07_30 =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Buyer",
            "CanonicalFieldDef",
            "DataProtectionKey",
            "MappingCorrection",
            "Membership",
            "OutputTemplate",
            "RetentionAuditLog",
            "ValidationRule",
        };

    // The repo scan is the expensive part; every test below shares one.
    private static readonly Lazy<RepoScan> Scan = new(RepoScan.Run, isThreadSafe: true);

    // ── The guard ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryDbSet_HasAReaderOutsideItsOwnCrudService()
    {
        var scan = Scan.Value;

        Assert.NotEmpty(scan.DbSets);
        Assert.True(scan.Sources.Count > 100,
            $"only {scan.Sources.Count} production source files found under {scan.RepoRoot} — the scan is broken");

        var allowed = KnownWriteOnlyStores.Select(k => k.Entity).ToHashSet(StringComparer.Ordinal);
        var unexplained = scan.Orphans.Where(f => !allowed.Contains(f.EntityName)).ToList();

        if (unexplained.Count > 0)
        {
            Assert.Fail(OrphanDetector.Render(unexplained, scan.Sources.Count));
        }
    }

    /// <summary>
    /// The projects to scan are derived from ProcuLink.slnx, not typed out here — a hand-maintained
    /// project list is the same anti-pattern as a hand-maintained DbSet list, one level up. A new
    /// production project must be scanned the day it is added to the solution.
    /// </summary>
    [Fact]
    public void ScanCoverage_IsDerivedFromTheSolution_AndExcludesTests()
    {
        var scan = Scan.Value;

        Assert.Equal(
            new[]
            {
                "ProcuLink.Api", "ProcuLink.Core", "ProcuLink.Infrastructure",
                "ProcuLink.Transform", "ProcuLink.Worker",
            },
            scan.ProjectDirectories.ToArray());

        Assert.DoesNotContain(scan.Sources, f => f.RelativePath.Contains(".Tests", StringComparison.Ordinal));
        Assert.DoesNotContain(scan.Sources, f => f.RelativePath.Contains(".claude", StringComparison.Ordinal));
        Assert.DoesNotContain(scan.Sources, f => f.RelativePath.Contains("Migrations", StringComparison.Ordinal));
    }

    // ── The allowlist cannot rot ─────────────────────────────────────────────────────────

    /// <summary>
    /// Once an orphan is actually deleted — which is the point of recording it — its confession
    /// must go too, or the next reader inherits a list of ghosts and stops trusting any of it.
    /// </summary>
    [Fact]
    public void KnownWriteOnly_EntriesStillCorrespondToRealDbSets()
    {
        var entityNames = Scan.Value.DbSets.Select(d => d.EntityType.Name).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var entry in KnownWriteOnlyStores)
        {
            if (!entityNames.Contains(entry.Entity))
            {
                problems.Add($"  • '{entry.Entity}' is no longer a DbSet on ProcuLinkDbContext — delete its "
                             + "KnownWriteOnly entry; the store it excused is gone.");
            }

            if (string.IsNullOrWhiteSpace(entry.Reason))
            {
                problems.Add($"  • '{entry.Entity}' has no written reason. A bare name is not a reason.");
            }
        }

        problems.AddRange(KnownWriteOnlyStores
            .GroupBy(k => k.Entity, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"  • '{g.Key}' is listed {g.Count()} times."));

        if (problems.Count > 0)
        {
            Assert.Fail("KnownWriteOnly allowlist has rotted:" + Environment.NewLine
                        + string.Join(Environment.NewLine, problems));
        }
    }

    /// <summary>
    /// The other half of "cannot rot": an entry that is no longer an orphan is a stale excuse, and
    /// a stale excuse is how a list like this stops meaning anything. Deleting the line is the fix.
    /// This is what kept the guard honest about the two stores Wave 1 retired: while they existed
    /// they were still detected as orphans every run — recorded, not forgiven — and when the
    /// entities were deleted this test is what demanded their entries go too.
    /// </summary>
    [Fact]
    public void KnownWriteOnly_EntriesAreStillOrphans()
    {
        var stillOrphaned = Scan.Value.Orphans.Select(o => o.EntityName).ToHashSet(StringComparer.Ordinal);

        var stale = KnownWriteOnlyStores
            .Where(k => !stillOrphaned.Contains(k.Entity))
            .Select(k => $"  • '{k.Entity}' now has a real consumer — delete its KnownWriteOnly entry.")
            .ToList();

        if (stale.Count > 0)
        {
            Assert.Fail("KnownWriteOnly allowlist has stale entries:" + Environment.NewLine
                        + string.Join(Environment.NewLine, stale));
        }
    }

    /// <summary>
    /// Shrink-only. The guard has to protect against NEW orphans from the day it lands, which it
    /// cannot do if the allowlist is a place to put them.
    /// </summary>
    [Fact]
    public void KnownWriteOnly_MayOnlyEverShrink()
    {
        var added = KnownWriteOnlyStores
            .Select(k => k.Entity)
            .Where(e => !AllowlistBaselineAt_2026_07_30.Contains(e))
            .ToList();

        if (added.Count > 0)
        {
            Assert.Fail(
                "The KnownWriteOnly allowlist may only ever SHRINK. These entries are new since the "
                + "2026-07-30 baseline:" + Environment.NewLine
                + string.Join(Environment.NewLine, added.Select(a => $"  • {a}")) + Environment.NewLine
                + Environment.NewLine
                + "A new write-only store is the defect this guard exists to catch. Give it a consumer, "
                + "or delete it. If it genuinely must be write-only, say so in the PR and move the "
                + "baseline deliberately — do not slip it in.");
        }

        Assert.True(KnownWriteOnlyStores.Count <= AllowlistBaselineAt_2026_07_30.Count,
            $"allowlist grew from {AllowlistBaselineAt_2026_07_30.Count} to {KnownWriteOnlyStores.Count}");
    }

    // ── Regressions locked against a previously-refuted implementation of this guard ─────

    /// <summary>
    /// An earlier build of this guard let <c>CanonicalFieldDefs</c> pass with
    /// <c>CanonicalFieldsController</c> as its only reader, because its name heuristics did not
    /// connect "CanonicalFields" to the entity "CanonicalFieldDef". The store is caught here
    /// structurally instead — the controller both reads and writes it, and nothing references the
    /// controller — so the catch does not depend on the two names resembling each other.
    ///
    /// <para>This asserts the DETECTOR still sees the shape, not that the store is dead: this
    /// particular one is consumed by the frontend mapper, which is written up on its allowlist
    /// entry. The shape — a table whose only reader is the controller that maintains it — is what
    /// has to keep being visible, because the next one will not have a frontend behind it.</para>
    /// </summary>
    [Fact]
    public void Guard_CatchesAStore_WhoseOnlyReaderIsItsOwnController()
    {
        var finding = Scan.Value.Orphans.SingleOrDefault(o => o.EntityName == "CanonicalFieldDef");

        Assert.True(finding is not null,
            "CanonicalFieldDef is no longer detected as an orphan. If it genuinely gained a consumer, "
            + "delete its KnownWriteOnly entry and this test. If the DETECTOR stopped seeing it, the "
            + "guard has regressed to the false-green it was built to avoid.");

        Assert.Contains(finding!.Owners, o => o.EndsWith("CanonicalFieldsController.cs", StringComparison.Ordinal));
    }

    /// <summary>
    /// The ingress dedupe ledgers are read through <c>_db.Set&lt;ImportedSftpFile&gt;()</c> and
    /// <c>_db.Set&lt;Core.Entities.ImportedS3Object&gt;()</c>, never through the DbSet property. A
    /// guard that only looks for <c>.ImportedSftpFiles</c> reports them as orphans — they are not;
    /// SftpIngressService / S3IngressService read the claim back to stay idempotent, and
    /// DataErasureService reads both to tombstone them. Losing this makes the guard cry wolf.
    /// </summary>
    [Fact]
    public void Guard_SeesReadsMadeThroughSetOfT()
    {
        var orphans = Scan.Value.Orphans.Select(o => o.EntityName).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("ImportedSftpFile", orphans);
        Assert.DoesNotContain("ImportedS3Object", orphans);
    }

    // ── The detector itself, on synthetic input ──────────────────────────────────────────

    /// <summary>
    /// Proves the guard can actually see an orphan, through the same detection function the real
    /// guard runs. A guard that is green because it detects nothing is worse than no guard, and
    /// "it is red on two known orphans today" does not prove it will be red on the next one.
    ///
    /// <para>The fixture is a synthetic DbContext plus a synthetic source corpus. It is
    /// deliberately NOT a fake DbSet bolted onto the production context: that would put a lie in
    /// the production model in order to test the test.</para>
    ///
    /// <para>Run under both path separators, because the answer must not depend on which OS is
    /// asking. Windows dev, Linux CI: this repo scans with <c>\</c> locally and <c>/</c> on the
    /// runner, and a stem-splitter that only understands one of them fails in exactly one place
    /// — the one-hop reference lookup — where the symptom is a false ORPHAN, not a crash.</para>
    /// </summary>
    [Theory]
    [InlineData('\\')]
    [InlineData('/')]
    public void Detector_FlagsASyntheticOrphan_AndSparesAConsumedStore(char separator)
    {
        var dbSets = OrphanDetector.DbSetsOf(typeof(SyntheticDbContext));

        Assert.Equal(
            new[] { "WidgetOrders", "WidgetTelemetries" },
            dbSets.Select(d => d.DbSetName).ToArray());

        SourceFile At(string path, string text) => Source(path.Replace('\\', separator), text);

        var corpus = new[]
        {
            // The orphan: a CRUD service and its own controller, and nothing else.
            At(@"Widgets\Services\WidgetTelemetryService.cs", """
                public class WidgetTelemetryService : IWidgetTelemetryService
                {
                    public Task<List<WidgetTelemetry>> ListAsync(Guid organisationId) =>
                        _db.WidgetTelemetries.Where(x => x.OrganisationId == organisationId).ToListAsync();

                    public void Record(WidgetTelemetry t) => _db.WidgetTelemetries.Add(t);
                }
                """),
            At(@"Widgets\Controllers\WidgetTelemetriesController.cs", """
                public class WidgetTelemetriesController : ControllerBase
                {
                    public WidgetTelemetriesController(IWidgetTelemetryService telemetry) => _telemetry = telemetry;
                }
                """),
            At(@"Widgets\Program.cs", """
                builder.Services.AddScoped<IWidgetTelemetryService, WidgetTelemetryService>();
                builder.Services.AddScoped<IWidgetOrderService, WidgetOrderService>();
                """),

            // The healthy store: also a CRUD service, but a real path downstream consumes it.
            At(@"Widgets\Services\WidgetOrderService.cs", """
                public class WidgetOrderService : IWidgetOrderService
                {
                    public Task<WidgetOrder?> GetAsync(Guid organisationId, Guid id) =>
                        _db.WidgetOrders.FirstOrDefaultAsync(x => x.OrganisationId == organisationId && x.Id == id);

                    public void Save(WidgetOrder o) => _db.WidgetOrders.Add(o);
                }
                """),
            At(@"Widgets\Jobs\ShipWidgetJob.cs", """
                public class ShipWidgetJob
                {
                    public ShipWidgetJob(IWidgetOrderService orders) => _orders = orders;
                }
                """),
        };

        var findings = OrphanDetector.FindOrphans(dbSets, corpus);

        Assert.Equal(new[] { "WidgetTelemetry" }, findings.Select(f => f.EntityName).ToArray());
        Assert.Contains(
            $"Widgets{separator}Services{separator}WidgetTelemetryService.cs",
            findings[0].Owners);

        // An unreadable failure gets suppressed, so the rendered message must name the store.
        Assert.Contains("WidgetTelemetries", OrphanDetector.Render(findings, corpus.Length));
    }

    /// <summary>
    /// Negative control for the fixture above: strip the only consumer and the previously healthy
    /// store must start failing. Without this, the fixture cannot tell "the detector works" apart
    /// from "the detector happens to flag exactly one thing".
    /// </summary>
    [Fact]
    public void Detector_FlagsAStore_WhenItsLastConsumerIsRemoved()
    {
        var dbSets = OrphanDetector.DbSetsOf(typeof(SyntheticDbContext))
            .Where(d => d.EntityType == typeof(WidgetOrder))
            .ToList();

        var corpus = new[]
        {
            Source(@"Widgets\Services\WidgetOrderService.cs", """
                public class WidgetOrderService : IWidgetOrderService
                {
                    public Task<WidgetOrder?> GetAsync(Guid organisationId, Guid id) =>
                        _db.WidgetOrders.FirstOrDefaultAsync(x => x.OrganisationId == organisationId && x.Id == id);

                    public void Save(WidgetOrder o) => _db.WidgetOrders.Add(o);
                }
                """),
            Source(@"Widgets\Controllers\WidgetOrdersController.cs", """
                public class WidgetOrdersController : ControllerBase
                {
                    public WidgetOrdersController(IWidgetOrderService orders) => _orders = orders;
                }
                """),
        };

        Assert.Equal(new[] { "WidgetOrder" }, OrphanDetector.FindOrphans(dbSets, corpus).Select(f => f.EntityName));
    }

    /// <summary>
    /// A write is not a read. This is the entire defect class: <c>Add</c> without a query that
    /// reads it back is what "writes to a store nothing reads" means.
    /// </summary>
    [Fact]
    public void Detector_DoesNotCountAWriteAsARead()
    {
        var dbSets = OrphanDetector.DbSetsOf(typeof(SyntheticDbContext))
            .Where(d => d.EntityType == typeof(WidgetTelemetry))
            .ToList();

        var corpus = new[]
        {
            Source(@"Widgets\Services\WidgetPipeline.cs", """
                public class WidgetPipeline
                {
                    public void Trace(WidgetTelemetry t)
                    {
                        _db.WidgetTelemetries.Add(t);
                        _db.WidgetTelemetries.RemoveRange(_old);
                    }
                }
                """),
            Source(@"Widgets\Jobs\RunPipelineJob.cs", """
                public class RunPipelineJob
                {
                    public RunPipelineJob(WidgetPipeline pipeline) => _pipeline = pipeline;
                }
                """),
        };

        var findings = OrphanDetector.FindOrphans(dbSets, corpus);

        Assert.Equal(new[] { "WidgetTelemetry" }, findings.Select(f => f.EntityName));

        // Writer-only files are not "owners" — nobody reads this store at all, and the report
        // has to say so, because "no reader anywhere" and "only its own service reads it" are
        // different problems with different fixes.
        Assert.Empty(findings[0].Owners);
    }

    /// <summary>
    /// Prose is not consumption. An earlier build of this guard matched raw file text, so a single
    /// <c>&lt;see cref="IWidgetTelemetryService"/&gt;</c> in a doc comment marked the store consumed.
    /// </summary>
    [Fact]
    public void Detector_DoesNotCountADocCommentMentionAsConsumption()
    {
        var dbSets = OrphanDetector.DbSetsOf(typeof(SyntheticDbContext))
            .Where(d => d.EntityType == typeof(WidgetTelemetry))
            .ToList();

        var corpus = new[]
        {
            Source(@"Widgets\Services\WidgetTelemetryService.cs", """
                public class WidgetTelemetryService : IWidgetTelemetryService
                {
                    public Task<List<WidgetTelemetry>> ListAsync(Guid organisationId) =>
                        _db.WidgetTelemetries.Where(x => x.OrganisationId == organisationId).ToListAsync();

                    public void Record(WidgetTelemetry t) => _db.WidgetTelemetries.Add(t);
                }
                """),
            Source(@"Widgets\Entities\WidgetOrder.cs", """
                public class WidgetOrder
                {
                    /// <summary>
                    /// Set once the order has been traced by <see cref="IWidgetTelemetryService"/>.
                    /// See also WidgetTelemetryService, which owns the trace table.
                    /// </summary>
                    public string? TelemetryHash { get; set; }

                    /* WidgetTelemetryService also stamps this on retry. */
                    public int Attempts { get; set; }
                }
                """),
        };

        Assert.Equal(
            new[] { "WidgetTelemetry" },
            OrphanDetector.FindOrphans(dbSets, corpus).Select(f => f.EntityName));
    }

    /// <summary>
    /// Child tables loaded through their parent's <c>Include</c> are read, even though their DbSet
    /// is never touched. Without this the guard would flag every line table in the schema and be
    /// switched off within a week.
    /// </summary>
    [Fact]
    public void Detector_CreditsAChildTableReadThroughAnInclude()
    {
        var dbSets = OrphanDetector.DbSetsOf(typeof(SyntheticParentChildDbContext));

        var corpus = new[]
        {
            Source(@"Widgets\Services\CrateReader.cs", """
                public class CrateReader
                {
                    public Task<Crate?> LoadAsync(Guid organisationId, Guid id) =>
                        _db.Crates.Include(c => c.Items)
                            .FirstOrDefaultAsync(c => c.OrganisationId == organisationId && c.Id == id);
                }
                """),
        };

        var findings = OrphanDetector.FindOrphans(dbSets, corpus).Select(f => f.EntityName).ToArray();

        Assert.DoesNotContain("CrateItem", findings);
        Assert.DoesNotContain("Crate", findings);
    }

    /// <summary>
    /// …but only a navigation the file actually traverses. <c>Items</c> is one of the most common
    /// identifiers in any codebase, so a guard that credits the bare name is crediting a
    /// coincidence.
    ///
    /// <para>Two details are load-bearing, both found by mutating the detector rather than by
    /// reading it (2026-07-30). The fixture DOES call <c>Include</c>, just not to reach this
    /// entity — with no <c>Include</c> at all, the cheap pre-filter rejects the file before the
    /// traversal regex ever runs. And the FULL DbSet list is passed in, not just
    /// <c>CrateItem</c> — navigations are discovered from the set of entities, so narrowing the
    /// list to one entity silently empties the navigation map and the test passes no matter what
    /// the traversal match does. Either mistake leaves a test that asserts nothing about the
    /// thing it is named after.</para>
    /// </summary>
    [Fact]
    public void Detector_DoesNotCreditANavigationTheFileNeverTraverses()
    {
        var dbSets = OrphanDetector.DbSetsOf(typeof(SyntheticParentChildDbContext));

        var corpus = new[]
        {
            Source(@"Widgets\Services\CrateReader.cs", """
                public class CrateReader
                {
                    public Task<Crate?> LoadAsync(Guid organisationId, Guid id) =>
                        _db.Crates.Include(c => c.Owner)
                            .FirstOrDefaultAsync(c => c.OrganisationId == organisationId && c.Id == id);

                    private static readonly string[] Items = { "unrelated", "local", "array" };

                    public int Count() => Items.Length;
                }
                """),
        };

        Assert.Equal(
            new[] { "CrateItem" },
            OrphanDetector.FindOrphans(dbSets, corpus).Select(f => f.EntityName));
    }

    // ── Fixtures. Never instantiated; only reflected over. ───────────────────────────────

    private static SourceFile Source(string relativePath, string text) =>
        new(relativePath, OrphanDetector.StripComments(text));

    private sealed class SyntheticDbContext : DbContext
    {
        public DbSet<WidgetTelemetry> WidgetTelemetries => Set<WidgetTelemetry>();
        public DbSet<WidgetOrder> WidgetOrders => Set<WidgetOrder>();
    }

    private sealed class SyntheticParentChildDbContext : DbContext
    {
        public DbSet<Crate> Crates => Set<Crate>();
        public DbSet<CrateItem> CrateItems => Set<CrateItem>();
    }

    internal sealed class WidgetTelemetry
    {
        public Guid Id { get; set; }
        public Guid OrganisationId { get; set; }
    }

    internal sealed class WidgetOrder
    {
        public Guid Id { get; set; }
        public Guid OrganisationId { get; set; }
    }

    internal sealed class Crate
    {
        public Guid Id { get; set; }
        public Guid OrganisationId { get; set; }
        public List<CrateItem> Items { get; set; } = new();
    }

    internal sealed class CrateItem
    {
        public Guid Id { get; set; }
        public Guid CrateId { get; set; }
    }
}

/// <summary>A store knowingly read by nobody, and the prose that justifies it.</summary>
public sealed record KnownWriteOnly(string Entity, string Reason);

/// <summary>One production source file, comment-stripped, with a repo-relative path.</summary>
public sealed record SourceFile(string RelativePath, string Text);

/// <summary>A <c>DbSet&lt;T&gt;</c> discovered reflectively on a DbContext.</summary>
public sealed record DbSetRef(string DbSetName, Type EntityType);

/// <summary>
/// A store with no reader outside its own CRUD surface. <paramref name="Owners"/> is the files
/// that both read and write it — empty means literally nothing reads the table anywhere.
/// </summary>
public sealed record OrphanFinding(string DbSetName, string EntityName, IReadOnlyList<string> Owners);

/// <summary>One scan of the repository, shared by every test in this file.</summary>
public sealed record RepoScan(
    string RepoRoot,
    IReadOnlyList<string> ProjectDirectories,
    IReadOnlyList<DbSetRef> DbSets,
    IReadOnlyList<SourceFile> Sources,
    IReadOnlyList<OrphanFinding> Orphans)
{
    public static RepoScan Run()
    {
        var root = OrphanDetector.FindRepoRoot();
        var projects = OrphanDetector.ProductionProjectDirectories(root);
        var dbSets = OrphanDetector.DbSetsOf(typeof(ProcuLinkDbContext));
        var sources = OrphanDetector.LoadSources(root, projects);
        return new RepoScan(root, projects, dbSets, sources, OrphanDetector.FindOrphans(dbSets, sources));
    }
}

/// <summary>
/// The detection function. Pure with respect to its inputs (DbSet list + source corpus) so the
/// synthetic fixtures exercise exactly the code the real guard runs.
/// </summary>
public static class OrphanDetector
{
    // Longest-first so an alternation cannot settle on a prefix (OrderBy vs OrderByDescending).
    private const string ReadOps =
        "AsNoTrackingWithIdentityResolution|AsAsyncEnumerable|OrderByDescending|DefaultIfEmpty|" +
        "SingleOrDefault|FirstOrDefault|LastOrDefault|AsNoTracking|ForEachAsync|ToDictionary|" +
        "SelectMany|AsQueryable|AsEnumerable|ToHashSet|LongCount|Intersect|ElementAt|OrderBy|" +
        "GroupBy|Distinct|Contains|Average|Reverse|ToArray|ToList|Single|Select|ThenBy|Except|" +
        "Include|Where|First|Last|Count|Union|Find|Load|Join|Take|Skip|Sum|Min|Max|Any|All";

    private const string WriteOps =
        "ExecuteDelete|ExecuteUpdate|RemoveRange|UpdateRange|AttachRange|AddRange|Attach|Remove|Update|Add";

    private const string ContextFileStem = "ProcuLinkDbContext";

    private static readonly Regex LineComment = new(@"(?<!:)//[^\r\n]*", RegexOptions.Compiled);
    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex RoleSuffix = new(
        @"(?:Service|Controller|Repository|Store|Manager|Job|Provider|Resolver|Tracker|Sender|Dispatcher|Handler|Client|Factory)$",
        RegexOptions.Compiled);

    // ── Reflection: the DbSet inventory is discovered, never listed ──────────────────────

    public static IReadOnlyList<DbSetRef> DbSetsOf(Type dbContextType) =>
        dbContextType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.PropertyType.IsGenericType
                        && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(p => new DbSetRef(p.Name, p.PropertyType.GetGenericArguments()[0]))
            .OrderBy(d => d.DbSetName, StringComparer.Ordinal)
            .ToList();

    // ── Source corpus ───────────────────────────────────────────────────────────────────

    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProcuLink.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"could not find ProcuLink.slnx walking up from {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// Derived from ProcuLink.slnx, minus the test projects — a test reading a table is not a
    /// consumer of it, which is precisely how an orphan hides from a naive scan.
    /// </summary>
    public static IReadOnlyList<string> ProductionProjectDirectories(string repoRoot)
    {
        var solution = XDocument.Load(Path.Combine(repoRoot, "ProcuLink.slnx"));

        return solution.Descendants("Project")
            .Select(p => (string?)p.Attribute("Path"))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetDirectoryName(path!.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar)))
            .Where(dir => !string.IsNullOrEmpty(dir))
            .Select(dir => dir!)
            .Where(dir => !dir.EndsWith(".Tests", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(dir => dir, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Migrations are excluded because a migration names every table by construction.
    /// <c>.claude/worktrees/</c> holds full copies of this repo — reading them would mix another
    /// session's code into the answer — and is excluded structurally: only
    /// <c>&lt;root&gt;/&lt;project&gt;</c> is ever enumerated, never the repository root itself.
    /// </summary>
    public static IReadOnlyList<SourceFile> LoadSources(string repoRoot, IReadOnlyList<string> projectDirectories)
    {
        var files = new List<SourceFile>();

        foreach (var project in projectDirectories)
        {
            var projectDir = Path.Combine(repoRoot, project);
            if (!Directory.Exists(projectDir))
            {
                throw new InvalidOperationException($"solution lists {project}, but {projectDir} does not exist");
            }

            foreach (var path in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(repoRoot, path);
                if (IsExcluded(relative))
                {
                    continue;
                }

                files.Add(new SourceFile(relative, StripComments(File.ReadAllText(path))));
            }
        }

        return files;
    }

    private static bool IsExcluded(string relativePath)
    {
        var sep = Path.DirectorySeparatorChar;
        var p = relativePath.Replace('/', sep).Replace('\\', sep);

        return p.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase)
               || p.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase)
               || p.Contains($"{sep}Migrations{sep}", StringComparison.OrdinalIgnoreCase)
               || p.Contains(".claude", StringComparison.OrdinalIgnoreCase)
               || p.Contains(".Tests", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Comments are stripped before ANY matching. A doc comment naming a service is prose, not a
    /// call; treating it as consumption is how a guard like this goes quietly false-green.
    /// The <c>(?&lt;!:)</c> keeps <c>https://…</c> inside string literals intact.
    /// </summary>
    public static string StripComments(string text) =>
        LineComment.Replace(BlockComment.Replace(text, string.Empty), string.Empty);

    // ── Detection ───────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<OrphanFinding> FindOrphans(
        IReadOnlyList<DbSetRef> dbSets,
        IReadOnlyList<SourceFile> sources)
    {
        var navigationsTo = NavigationsByTargetEntity(dbSets);
        var findings = new List<OrphanFinding>();

        foreach (var dbSet in dbSets)
        {
            var usage = Usage(dbSet, sources, navigationsTo);

            // Someone reads what someone else wrote. That is a consumer, controller or not.
            if (usage.IndependentReaders.Count > 0)
            {
                continue;
            }

            if (HasConsumerBeyondItsOwnCrud(dbSet, usage.Owners, sources))
            {
                continue;
            }

            findings.Add(new OrphanFinding(dbSet.DbSetName, dbSet.EntityType.Name, usage.Owners));
        }

        return findings;
    }

    private static (List<string> Owners, List<string> IndependentReaders) Usage(
        DbSetRef dbSet,
        IReadOnlyList<SourceFile> sources,
        IReadOnlyDictionary<Type, HashSet<string>> navigationsTo)
    {
        var entity = dbSet.EntityType.Name;

        // Reached either through the DbSet property or through Set<T>(), which may be namespace
        // qualified (DataErasureService uses _db.Set<Core.Entities.ImportedSftpFile>()).
        var access = $@"(?:{Regex.Escape(dbSet.DbSetName)}\b|Set<(?:[\w\.]*\.)?{Regex.Escape(entity)}>\s*\(\s*\))";
        var readRegex = new Regex($@"[A-Za-z0-9_\)\]]\s*\.\s*{access}\s*\.\s*(?:{ReadOps})(?:Async)?\s*[\(<]");
        var writeRegex = new Regex($@"[A-Za-z0-9_\)\]]\s*\.\s*{access}\s*\.\s*(?:{WriteOps})(?:Async)?\s*[\(<]");

        Regex? navigationRegex = null;
        if (navigationsTo.TryGetValue(dbSet.EntityType, out var navigations) && navigations.Count > 0)
        {
            var alternation = string.Join('|', navigations.OrderBy(n => n, StringComparer.Ordinal).Select(Regex.Escape));

            // Must be an actual traversal — `x => x.Items` — never a bare mention of the name.
            navigationRegex = new Regex(
                $@"\.\s*(?:Include|ThenInclude)\s*\(\s*\w+\s*=>\s*[\w\.\!\?]*\.\s*(?:{alternation})\b");
        }

        var owners = new List<string>();
        var independentReaders = new List<string>();

        foreach (var file in sources)
        {
            // The DbContext declares and configures every store; it consumes none of them.
            if (StemOf(file.RelativePath).Equals(ContextFileStem, StringComparison.Ordinal))
            {
                continue;
            }

            var mightMention = file.Text.Contains(dbSet.DbSetName, StringComparison.Ordinal)
                               || file.Text.Contains(entity, StringComparison.Ordinal);

            var reads = (mightMention && readRegex.IsMatch(file.Text))
                        || (navigationRegex is not null
                            && file.Text.Contains("nclude", StringComparison.Ordinal)
                            && navigationRegex.IsMatch(file.Text));

            if (!reads)
            {
                continue;
            }

            if (mightMention && writeRegex.IsMatch(file.Text))
            {
                owners.Add(file.RelativePath);
            }
            else
            {
                independentReaders.Add(file.RelativePath);
            }
        }

        return (owners, independentReaders);
    }

    /// <summary>
    /// One hop out from a store that only its own maintainer reads. Three things deliberately do
    /// NOT count as consumption: DI registration (proves only that it can be resolved — exactly
    /// the illusion the orphaned surfaces were living inside), the owner's own interface or class
    /// declaration file, and the store's own CRUD service/controller.
    /// </summary>
    private static bool HasConsumerBeyondItsOwnCrud(
        DbSetRef dbSet,
        IReadOnlyList<string> owners,
        IReadOnlyList<SourceFile> sources)
    {
        var ownNames = new HashSet<string>(StringComparer.Ordinal)
        {
            Normalize(dbSet.DbSetName),
            Normalize(dbSet.EntityType.Name),
        };

        foreach (var owner in owners)
        {
            var stem = StemOf(owner);
            var declarationStems = new HashSet<string>(StringComparer.Ordinal) { stem, "I" + stem };
            if (stem.Length > 1 && stem[0] == 'I' && char.IsUpper(stem[1]))
            {
                declarationStems.Add(stem[1..]);
            }

            var referenceRegex = new Regex($@"\bI?{Regex.Escape(stem)}\b");

            foreach (var file in sources)
            {
                if (file.RelativePath.Equals(owner, StringComparison.Ordinal)
                    || owners.Contains(file.RelativePath))
                {
                    continue;
                }

                var fileStem = StemOf(file.RelativePath);
                if (fileStem.Equals(ContextFileStem, StringComparison.Ordinal)
                    || IsCompositionRoot(fileStem)
                    || declarationStems.Contains(fileStem)
                    || ownNames.Contains(Normalize(fileStem)))
                {
                    continue;
                }

                if (file.Text.Contains(stem, StringComparison.Ordinal) && referenceRegex.IsMatch(file.Text))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsCompositionRoot(string fileStem) =>
        fileStem.Equals("Program", StringComparison.Ordinal)
        || fileStem.Equals("Startup", StringComparison.Ordinal)
        || fileStem.Contains("ServiceCollection", StringComparison.Ordinal)
        || fileStem.Contains("DependencyInjection", StringComparison.Ordinal);

    /// <summary>
    /// EF navigations pointing AT each entity, so a child table loaded with
    /// <c>Include(x =&gt; x.Lines)</c> counts as read even though its DbSet is never touched.
    /// </summary>
    private static Dictionary<Type, HashSet<string>> NavigationsByTargetEntity(IReadOnlyList<DbSetRef> dbSets)
    {
        var entityTypes = dbSets.Select(d => d.EntityType).ToHashSet();
        var map = new Dictionary<Type, HashSet<string>>();

        foreach (var owner in entityTypes)
        {
            foreach (var property in owner.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var target = NavigationTarget(property.PropertyType);
                if (target is null || target == owner || !entityTypes.Contains(target))
                {
                    continue;
                }

                if (!map.TryGetValue(target, out var names))
                {
                    map[target] = names = new HashSet<string>(StringComparer.Ordinal);
                }

                names.Add(property.Name);
            }
        }

        return map;
    }

    private static Type? NavigationTarget(Type propertyType)
    {
        if (!propertyType.IsGenericType)
        {
            return propertyType;
        }

        if (propertyType.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            return null;
        }

        var arguments = propertyType.GetGenericArguments();
        return arguments.Length == 1 ? arguments[0] : null;
    }

    /// <summary>
    /// Separator-agnostic on purpose. <see cref="Path.GetFileNameWithoutExtension(string)"/> does
    /// not treat <c>\</c> as a separator on Linux, so on CI it would hand back a whole path as the
    /// "stem" and every one-hop reference lookup would silently miss. Windows dev, Linux CI.
    /// </summary>
    private static string StemOf(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var lastSlash = normalized.LastIndexOf('/');
        var fileName = lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized;
        var lastDot = fileName.LastIndexOf('.');
        return lastDot > 0 ? fileName[..lastDot] : fileName;
    }

    /// <summary>
    /// Folds a file stem or a DbSet/entity name onto the concept it is about, so
    /// <c>SupplierCatalogsController</c>, <c>ISupplierCatalogService</c>, <c>SupplierCatalogs</c> and
    /// <c>SupplierCatalog</c> all land on one key. <c>Entity</c> and <c>Def</c> are stripped because
    /// they are type-naming noise here (<c>PurchaseOrderEntity</c>, <c>CanonicalFieldDef</c>)
    /// rather than part of the concept's name.
    ///
    /// <para>This is a convenience, not the guard's backbone: it only ever narrows the one-hop
    /// step. A store whose maintainer is a controller is caught structurally regardless of what
    /// either is called.</para>
    /// </summary>
    private static string Normalize(string name)
    {
        var s = RoleSuffix.Replace(name, string.Empty);

        if (s.Length > 1 && s[0] == 'I' && char.IsUpper(s[1]))
        {
            s = s[1..];
        }

        if (s.EndsWith("ies", StringComparison.Ordinal))
        {
            s = string.Concat(s.AsSpan(0, s.Length - 3), "y");
        }
        else if (s.EndsWith("s", StringComparison.Ordinal) && !s.EndsWith("ss", StringComparison.Ordinal))
        {
            s = s[..^1];
        }

        if (s.EndsWith("Entity", StringComparison.Ordinal))
        {
            s = s[..^6];
        }
        else if (s.EndsWith("Def", StringComparison.Ordinal))
        {
            s = s[..^3];
        }

        return s.ToLowerInvariant();
    }

    // ── Reporting ───────────────────────────────────────────────────────────────────────

    public static string Render(IReadOnlyList<OrphanFinding> findings, int scannedFileCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ORPHAN GUARD — rule R1: no new surface without a consumer.");
        sb.AppendLine();
        sb.AppendLine($"{findings.Count} DbSet(s) on ProcuLinkDbContext are written to but read by nobody");
        sb.AppendLine($"outside their own CRUD service/controller ({scannedFileCount} production files scanned):");
        sb.AppendLine();

        foreach (var finding in findings.OrderBy(f => f.DbSetName, StringComparer.Ordinal))
        {
            sb.AppendLine($"  • {finding.DbSetName}  (entity {finding.EntityName})");

            if (finding.Owners.Count == 0)
            {
                sb.AppendLine("      NOTHING IN THE REPO READS THIS TABLE — it is written and never queried.");
            }
            else
            {
                sb.AppendLine("      read only by the code that also maintains it:");
                foreach (var owner in finding.Owners.OrderBy(o => o, StringComparer.Ordinal))
                {
                    sb.AppendLine($"        - {owner}");
                }

                sb.AppendLine("      and nothing references that, beyond its own controller and the DI roots.");
            }

            sb.AppendLine();
        }

        sb.AppendLine("Fix it one of two ways:");
        sb.AppendLine("  1. Give the store a real consumer — a parse / mapping / transform / delivery /");
        sb.AppendLine("     revision / billing / routing path that reads back what it writes; or");
        sb.AppendLine("  2. Delete it.");
        sb.AppendLine();
        sb.AppendLine("Adding a KnownWriteOnly entry is NOT one of the two ways: that allowlist is");
        sb.AppendLine("shrink-only and frozen at its 2026-07-30 baseline.");

        return sb.ToString();
    }
}
