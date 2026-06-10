using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Group V2 — REPLAY / impact testing. Verifies the non-mutating replay produces output diffs,
/// validation pass→fail flips, effective-value changes, that it writes NOTHING to the DB, and that
/// it is org-scoped. Uses the real <see cref="ProcuLinkDbContext"/> on the EF InMemory provider and
/// the real transform services (CSV/JSON/XML), like the other Api.Tests services.
/// </summary>
public class ReplayServiceTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static IEnumerable<ITransformService> Transformers() => new ITransformService[]
    {
        new CsvTransformService(),
        new JsonTransformService(),
        new XmlTransformService(),
    };

    // ── seed helpers ───────────────────────────────────────────────────────

    private static async Task<(Guid orgId, Guid supplierId, Guid connectionId, Guid revisionId)>
        SeedConnectionWithRevisionAsync(
            ProcuLinkDbContext db,
            string? outputMappingJson = null,
            string? outputFormat = "csv",
            string revisionStatus = "draft",
            Guid? acceptanceProfileId = null,
            bool setAsActive = false)
    {
        var orgId        = Guid.NewGuid();
        var supplierId   = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var revisionId   = Guid.NewGuid();
        var now          = DateTime.UtcNow;

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme OÜ", CreatedAt = now });
        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = connectionId, OrgId = orgId, SupplierId = supplierId, Name = "Acme OÜ",
            ActiveRevisionId = setAsActive ? revisionId : null, CreatedAt = now, UpdatedAt = now,
        });
        db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
        {
            Id = revisionId, ConnectionId = connectionId, OrgId = orgId, SupplierId = supplierId,
            VersionNo = 1, Status = revisionStatus, CreatedAt = now,
            OutputMappingJson = outputMappingJson, OutputFormat = outputFormat,
            AcceptanceProfileId = acceptanceProfileId,
            AcceptanceVersionNo = acceptanceProfileId is null ? null : 1,
        });
        await db.SaveChangesAsync();
        return (orgId, supplierId, connectionId, revisionId);
    }

    private static async Task<Guid> SeedOrderAsync(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId,
        string poNumber = "PO-1", JsonDocument? canonical = null,
        (string supplierCode, decimal qty, decimal price)? line = null)
    {
        var orderId = Guid.NewGuid();
        var l = line ?? ("SUP-1", 2m, 5m);
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId, PoNumber = poNumber,
            Currency = "EUR", OrderDate = new DateOnly(2026, 1, 1), Status = "ready",
            CanonicalJson = canonical, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Lines = new List<PurchaseOrderLineEntity>
            {
                new()
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "BUY-1", SupplierItemCode = l.supplierCode,
                    Description = "Widget", Quantity = l.qty, Unit = "EA", UnitPrice = l.price,
                    NeedsReview = false,
                },
            },
        });
        await db.SaveChangesAsync();
        return orderId;
    }

    /// <summary>A canonical_json document carrying a per-order mapping override (the order's CURRENT output config).</summary>
    private static JsonDocument CanonicalWithOverride(OrderMappingOverride @override)
    {
        var root = new Dictionary<string, object?>
        {
            ["buyerName"] = "Buyer Co",
            [OrderMappingOverrideReader.CanonicalKey] = @override,
        };
        return JsonDocument.Parse(JsonSerializer.Serialize(root, CamelCase));
    }

    private static string OutputMappingJson(OutputMappingConfig config) =>
        JsonSerializer.Serialize(config, CamelCase);

    // ── 1. Output diff: a draft with a changed output mapping shows the output diff ──

    [Fact]
    public async Task Replay_DraftWithChangedOutputMapping_ShowsOutputDiff()
    {
        var db = MakeDb();

        // Draft revision: a JSON output mapping that emits a single header field "po" = PoNumber and a
        // line field "sku" = SupplierItemCode. The current orders have NO per-order override → their
        // current output is the FIXED CSV transform, so the output text must differ.
        var draftConfig = new OutputMappingConfig
        {
            Header = { ["po"] = new OutputFieldRule { OutputPath = "po", CanonicalField = "PoNumber" } },
            Lines  = { ["sku"] = new OutputFieldRule { OutputPath = "sku", CanonicalField = "SupplierItemCode" } },
        };
        var (orgId, supplierId, connId, revId) = await SeedConnectionWithRevisionAsync(
            db, outputMappingJson: OutputMappingJson(draftConfig), outputFormat: "json");

        await SeedOrderAsync(db, orgId, supplierId, "PO-100"); // no override → fixed transformer (current)

        var svc = new ReplayService(db, Transformers());
        var result = await svc.ReplayAsync(orgId, connId, revId, new ReplayRequest(), CancellationToken.None);

        Assert.NotNull(result);
        var diff = Assert.Single(result!.Orders);
        Assert.Equal("PO-100", diff.PoNumber);
        Assert.Equal("Json", diff.OutputFormat);
        Assert.True(diff.OutputChanged);            // draft JSON output ≠ current CSV output
        Assert.Null(diff.OutputError);
        Assert.NotNull(diff.CurrentOutput);
        Assert.NotNull(diff.DraftOutput);
        Assert.Contains("PO-100", diff.DraftOutput!); // header rule emitted the PO number
        Assert.Contains("SUP-1", diff.DraftOutput!);  // line rule emitted the supplier code
    }

    // ── 2. Validation flip: a draft tightening a rule shows a pass→fail flip ──

    [Fact]
    public async Task Replay_DraftTighteningRule_ShowsValidationPassToFailFlip()
    {
        var db = MakeDb();
        var now = DateTime.UtcNow;

        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        // CURRENT active profile: a lenient rule that passes (currency required, which is set).
        var currentProfileId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme", CreatedAt = now });
        db.SupplierAcceptanceProfiles.Add(new SupplierAcceptanceProfile
        {
            Id = currentProfileId, OrgId = orgId, SupplierId = supplierId, VersionNo = 1,
            Status = "active", CreatedAt = now,
            Rules = new List<SupplierAcceptanceRule>
            {
                new() { Id = Guid.NewGuid(), Scope = "order", FieldPath = "currency", Operator = "required",
                        Severity = "error", BlockOnFail = true },
            },
        });

        // DRAFT-bound profile: a STRICTER rule on the same field that FAILS (currency must equal "USD",
        // but the order is "EUR"). Same code key (currency.equals) is fine; the flip is detected by status.
        var draftProfileId = Guid.NewGuid();
        db.SupplierAcceptanceProfiles.Add(new SupplierAcceptanceProfile
        {
            Id = draftProfileId, OrgId = orgId, SupplierId = supplierId, VersionNo = 2,
            Status = "draft", CreatedAt = now,
            Rules = new List<SupplierAcceptanceRule>
            {
                new() { Id = Guid.NewGuid(), Scope = "order", FieldPath = "currency", Operator = "equals",
                        ExpectedValue = "USD", Severity = "error", BlockOnFail = true },
            },
        });

        var connectionId = Guid.NewGuid();
        var revisionId   = Guid.NewGuid();
        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = connectionId, OrgId = orgId, SupplierId = supplierId, Name = "Acme",
            ActiveRevisionId = null, CreatedAt = now, UpdatedAt = now,
        });
        db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
        {
            Id = revisionId, ConnectionId = connectionId, OrgId = orgId, SupplierId = supplierId,
            VersionNo = 2, Status = "draft", CreatedAt = now, OutputFormat = "csv",
            AcceptanceProfileId = draftProfileId, AcceptanceVersionNo = 2,
        });
        await db.SaveChangesAsync();

        await SeedOrderAsync(db, orgId, supplierId, "PO-VAL"); // currency EUR

        var svc = new ReplayService(db, Transformers());
        var result = await svc.ReplayAsync(orgId, connectionId, revisionId, new ReplayRequest(), CancellationToken.None);

        Assert.NotNull(result);
        var diff = Assert.Single(result!.Orders);

        Assert.True(diff.CurrentValidation.Passed);   // lenient "required" passes (EUR present)
        Assert.False(diff.DraftValidation.Passed);     // strict "equals USD" fails (EUR ≠ USD)
        Assert.True(diff.ValidationChanged);
        Assert.NotEmpty(diff.ValidationFlips);

        var flip = diff.ValidationFlips.First(f => f.DraftStatus == "fail");
        Assert.Equal("fail", flip.DraftStatus);
    }

    // ── 3. Replay writes NOTHING (assert no DB changes) ──

    [Fact]
    public async Task Replay_WritesNothing_NoDbChanges()
    {
        var db = MakeDb();
        var draftConfig = new OutputMappingConfig
        {
            Header = { ["po"] = new OutputFieldRule { OutputPath = "po", CanonicalField = "PoNumber" } },
        };
        var (orgId, supplierId, connId, revId) = await SeedConnectionWithRevisionAsync(
            db, outputMappingJson: OutputMappingJson(draftConfig), outputFormat: "json");
        await SeedOrderAsync(db, orgId, supplierId, "PO-A");
        await SeedOrderAsync(db, orgId, supplierId, "PO-B");

        // Snapshot row counts of every table replay touches.
        var ordersBefore       = db.PurchaseOrders.Count();
        var linesBefore        = db.PurchaseOrderLines.Count();
        var artifactsBefore    = db.OutboundArtifacts.Count();
        var validationsBefore  = db.OrderValidationResults.Count();
        var revisionsBefore    = db.SupplierConnectionRevisions.Count();
        var connectionsBefore  = db.SupplierConnections.Count();
        var attemptsBefore     = db.DeliveryAttempts.Count();
        var auditBefore        = db.AuditEvents.Count();

        var svc = new ReplayService(db, Transformers());
        var result = await svc.ReplayAsync(orgId, connId, revId, new ReplayRequest(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result!.OrderCount);

        // Nothing persisted, nothing pending in the change tracker.
        var dirty = db.ChangeTracker.Entries()
            .Where(e => e.State != EntityState.Unchanged && e.State != EntityState.Detached)
            .ToList();
        Assert.Empty(dirty);
        Assert.Equal(ordersBefore,      db.PurchaseOrders.Count());
        Assert.Equal(linesBefore,       db.PurchaseOrderLines.Count());
        Assert.Equal(artifactsBefore,   db.OutboundArtifacts.Count());
        Assert.Equal(validationsBefore, db.OrderValidationResults.Count());
        Assert.Equal(revisionsBefore,   db.SupplierConnectionRevisions.Count());
        Assert.Equal(connectionsBefore, db.SupplierConnections.Count());
        Assert.Equal(attemptsBefore,    db.DeliveryAttempts.Count());
        Assert.Equal(auditBefore,       db.AuditEvents.Count());
    }

    // ── 4. Org-scoping ──

    [Fact]
    public async Task Replay_CrossTenant_ReturnsNull()
    {
        var db = MakeDb();
        var (orgA, supplierA, connId, revId) = await SeedConnectionWithRevisionAsync(db);
        await SeedOrderAsync(db, orgA, supplierA, "PO-A");

        var svc = new ReplayService(db, Transformers());

        // Org B cannot replay org A's revision.
        var result = await svc.ReplayAsync(Guid.NewGuid(), connId, revId, new ReplayRequest(), CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task Replay_OnlyReplaysOrgScopedOrders()
    {
        var db = MakeDb();
        var (orgA, supplierA, connId, revId) = await SeedConnectionWithRevisionAsync(db);
        await SeedOrderAsync(db, orgA, supplierA, "PO-A1");

        // An order in another org for the SAME supplier id must never be replayed.
        var orgB = Guid.NewGuid();
        await SeedOrderAsync(db, orgB, supplierA, "PO-B1");

        var svc = new ReplayService(db, Transformers());
        var result = await svc.ReplayAsync(orgA, connId, revId, new ReplayRequest(), CancellationToken.None);

        Assert.NotNull(result);
        var diff = Assert.Single(result!.Orders);
        Assert.Equal("PO-A1", diff.PoNumber);
    }

    // ── 5. Not found ──

    [Fact]
    public async Task Replay_UnknownRevision_ReturnsNull()
    {
        var db = MakeDb();
        var (orgId, _, connId, _) = await SeedConnectionWithRevisionAsync(db);

        var svc = new ReplayService(db, Transformers());
        var result = await svc.ReplayAsync(orgId, connId, Guid.NewGuid(), new ReplayRequest(), CancellationToken.None);
        Assert.Null(result);
    }

    // ── 6. Published/archived revision is replayable (read-only) ──

    [Fact]
    public async Task Replay_PublishedRevision_IsReplayable()
    {
        var db = MakeDb();
        var (orgId, supplierId, connId, revId) = await SeedConnectionWithRevisionAsync(
            db, outputFormat: "csv", revisionStatus: "published", setAsActive: true);
        await SeedOrderAsync(db, orgId, supplierId, "PO-PUB");

        var svc = new ReplayService(db, Transformers());
        var result = await svc.ReplayAsync(orgId, connId, revId, new ReplayRequest(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("published", result!.RevisionStatus);
        Assert.Single(result.Orders);
    }

    // ── 7. Effective-value diff (SourceMap fixed value changes a canonical field) ──

    [Fact]
    public async Task Replay_NoOutputMappingChange_NoOutputDiff()
    {
        var db = MakeDb();
        // Draft revision has NO output mapping → fixed transformer on the draft side too. Orders also
        // have no override → fixed transformer on the current side. Same format ⇒ identical output.
        var (orgId, supplierId, connId, revId) = await SeedConnectionWithRevisionAsync(
            db, outputMappingJson: null, outputFormat: "csv");
        await SeedOrderAsync(db, orgId, supplierId, "PO-SAME");

        var svc = new ReplayService(db, Transformers());
        var result = await svc.ReplayAsync(orgId, connId, revId, new ReplayRequest(), CancellationToken.None);

        Assert.NotNull(result);
        var diff = Assert.Single(result!.Orders);
        Assert.False(diff.OutputChanged);
        Assert.Equal(diff.CurrentOutput, diff.DraftOutput);
        Assert.Empty(diff.EffectiveValueChanges);
    }

    // ── 8. Effective-value diff: a draft output rule that overrides a recognized canonical field ──

    [Fact]
    public async Task Replay_DraftOverridesCanonicalField_ShowsEffectiveValueChange()
    {
        var db = MakeDb();
        // Draft revision: an XML output mapping whose line rule REWRITES the SupplierItemCode canonical
        // field to a fixed literal. For a structured format (XML) the override is applied by resolving an
        // effective entity (EffectiveEntityResolver), so the effective line value changes vs. current.
        var draftConfig = new OutputMappingConfig
        {
            Lines =
            {
                ["SupplierItemCode"] = new OutputFieldRule
                {
                    OutputPath = "SupplierItemCode", FixedValue = "REMAPPED-SKU",
                },
            },
        };
        var (orgId, supplierId, connId, revId) = await SeedConnectionWithRevisionAsync(
            db, outputMappingJson: OutputMappingJson(draftConfig), outputFormat: "xml");
        await SeedOrderAsync(db, orgId, supplierId, "PO-EFF"); // current supplier code SUP-1

        var svc = new ReplayService(db, Transformers());
        var result = await svc.ReplayAsync(orgId, connId, revId, new ReplayRequest(), CancellationToken.None);

        Assert.NotNull(result);
        var diff = Assert.Single(result!.Orders);

        var change = Assert.Single(diff.EffectiveValueChanges, c => c.Field == "SupplierItemCode");
        Assert.Equal("line", change.Scope);
        Assert.Equal(1, change.LineNumber);
        Assert.Equal("SUP-1", change.CurrentValue);
        Assert.Equal("REMAPPED-SKU", change.DraftValue);
        Assert.True(diff.OutputChanged); // the remapped value appears in the XML output too
    }

    // ── 9. Bounded: recent window respects the limit ──

    [Fact]
    public async Task Replay_RecentWindow_RespectsLimit()
    {
        var db = MakeDb();
        var (orgId, supplierId, connId, revId) = await SeedConnectionWithRevisionAsync(db, outputFormat: "csv");
        for (var i = 0; i < 5; i++)
            await SeedOrderAsync(db, orgId, supplierId, $"PO-{i}");

        var svc = new ReplayService(db, Transformers());
        var result = await svc.ReplayAsync(orgId, connId, revId, new ReplayRequest(RecentLimit: 3), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(3, result!.OrderCount);
    }
}
