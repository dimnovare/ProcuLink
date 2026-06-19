using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Email;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// Creates a sample purchase order from the embedded onboarding fixture so a new user can run the
/// full parse → transform → deliver loop without their own data. The sample supplier (<c>__sample__</c>)
/// is created on first call and reused; the order is flagged <c>IsSample = true</c> so it is excluded
/// from billing quota (see <c>StripeBillingService.CountOrdersAsync</c>) and from every
/// onboarding-status milestone (see <c>OnboardingController</c> — samples teach, never certify).
/// The supplier is pre-wired with a small catalog and mappings for fixture lines 1–2 only, leaving
/// line 3 as the user's one deliberate manual resolution rep (see the pinned-code comment below).
/// </summary>
public sealed class SampleOrderService : ISampleOrderService
{
    private const string SampleSupplierCode  = "__sample__";
    private const string SampleSupplierName  = "ProcuLink Sample Supplier";
    private const string FixtureResourceName = "ProcuLink.Infrastructure.Fixtures.sample-order.csv";
    private const string FixtureFileName     = "sample-order.csv";
    private const string FixturePoNumber     = "DEMO-2026-001";
    private const string FixtureCurrency     = "EUR";

    // ── Seeded sample-supplier data — pinned to ProcuLink.Api\Fixtures\sample-order.csv ──
    // (embedded into this assembly as ProcuLink.Infrastructure.Fixtures.sample-order.csv).
    // Fixture buyer codes, verbatim:
    //   line 1: ACME-WIDGET-A   "Widget A 10mm"   12 × 4.50 EUR
    //   line 2: ACME-WIDGET-B   "Widget B 20mm"    6 × 8.25 EUR
    //   line 3: ACME-BRACKET-S  "Bracket short"   24 × 1.95 EUR
    // ItemMappings cover lines 1 and 2 ONLY — the DELIBERATE GAP: on first parse two lines
    // resolve automatically ("ProcuLink remembered these") and line 3 is the user's one
    // manual rep on the review screen. Its supplier code (SMP-BRACKET-S) IS seeded in the
    // catalog so manual resolution / catalog-grounded AI can find it. Do not map line 3.
    private const string FixtureLine1BuyerCode = "ACME-WIDGET-A";
    private const string FixtureLine2BuyerCode = "ACME-WIDGET-B";
    private const string Line1SupplierCode     = "SMP-WIDGET-A-10";
    private const string Line2SupplierCode     = "SMP-WIDGET-B-20";
    private const string Line3SupplierCode     = "SMP-BRACKET-S";

    /// <summary>(Code, Name, Unit, Price) for the sample supplier's seeded catalog.</summary>
    private static readonly (string Code, string Name, string Unit, decimal Price)[] SampleCatalog =
    {
        (Line1SupplierCode, "Widget A 10mm",  "PCS",  4.50m),
        (Line2SupplierCode, "Widget B 20mm",  "PCS",  8.25m),
        (Line3SupplierCode, "Bracket short",  "PCS",  1.95m),
        ("SMP-WIDGET-C-30", "Widget C 30mm",  "PCS", 12.40m),
        ("SMP-CLAMP-M",     "Clamp medium",   "PCS",  3.10m),
    };

    /// <summary>Buyer → supplier mappings seeded for fixture lines 1 and 2 ONLY (see gap note above).</summary>
    private static readonly (string BuyerCode, string SupplierCode)[] SampleMappings =
    {
        (FixtureLine1BuyerCode, Line1SupplierCode),
        (FixtureLine2BuyerCode, Line2SupplierCode),
    };

    private readonly ProcuLinkDbContext _db;
    private readonly IParseJobEnqueuer  _enqueuer;
    private readonly IFileStorageService _files;
    private readonly IAnalyticsService  _analytics;

    public SampleOrderService(
        ProcuLinkDbContext db,
        IParseJobEnqueuer enqueuer,
        IFileStorageService files,
        IAnalyticsService analytics)
    {
        _db        = db;
        _enqueuer  = enqueuer;
        _files     = files;
        _analytics = analytics;
    }

    public async Task<Guid> CreateAndEnqueueAsync(Guid organisationId, string? createdByUserId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // 1. Idempotent: reuse existing __sample__ supplier or create one. The lookup is
        //    INTENTIONALLY unfiltered by DeletedAt — a prior org purge soft-deletes suppliers,
        //    and the unique (org_id, code) index still covers the soft-deleted row, so a
        //    DeletedAt==null filter would force an INSERT that collides with it. Instead reuse
        //    the row and re-activate it.
        var supplier = await _db.Suppliers
            .FirstOrDefaultAsync(s => s.OrgId == organisationId && s.Code == SampleSupplierCode, ct);
        if (supplier is null)
        {
            supplier = new Supplier
            {
                Id        = Guid.NewGuid(),
                OrgId     = organisationId,
                Name      = SampleSupplierName,
                Code      = SampleSupplierCode,
                IsSample  = true,
                CreatedAt = now,
            };
            _db.Suppliers.Add(supplier);
        }
        else if (supplier.DeletedAt is not null)
        {
            // Re-activate a sample supplier a previous purge soft-deleted, so the new sample
            // order links to a LIVE supplier (and isn't hidden from supplier lists).
            supplier.DeletedAt = null;
        }

        // 1b. Seed the sample supplier's catalog + the 2-of-3 item mappings (idempotent,
        //     existence-checked per row — safe to re-run on every call / Hangfire retry).
        await SeedSampleSupplierDataAsync(organisationId, supplier.Id, now, ct);

        // 2. Load the embedded CSV fixture from ProcuLink.Api.dll (loaded into the same AppDomain at runtime).
        var fixtureBytes = await ReadFixtureBytesAsync(ct);

        // 3. Upload to file storage so the existing ParseOrderJob can consume it via SourceFileKey.
        var storageKey = $"sample/{organisationId}/{Guid.NewGuid()}.csv";
        using (var ms = new MemoryStream(fixtureBytes))
        {
            await _files.UploadAsync(ms, storageKey, "text/csv", ct);
        }

        // 4. Stub the PurchaseOrder with IsSample = true so quota counts skip it.
        var order = new PurchaseOrderEntity
        {
            Id            = Guid.NewGuid(),
            OrgId         = organisationId,
            SupplierId    = supplier.Id,
            PoNumber      = FixturePoNumber,
            OrderDate     = DateOnly.FromDateTime(now),
            Currency      = FixtureCurrency,
            Status        = "parsing",
            SourceFileKey = storageKey,
            IsSample      = true,
            CreatedAt     = now,
            UpdatedAt     = now,
        };
        _db.PurchaseOrders.Add(order);

        await _db.SaveChangesAsync(ct);

        // 5. Enqueue parse — ParseOrderJob already chains transform + delivery on success.
        await _enqueuer.EnqueueAsync(order.Id, organisationId, ct);

        // 6. Analytics: sample_order_started (per docs/analytics-event-taxonomy.md).
        await _analytics.CaptureAsync(
            organisationId: organisationId,
            userId:         createdByUserId,
            eventName:      "sample_order_started",
            properties:     new Dictionary<string, object?>
            {
                ["order_id"]    = order.Id,
                ["supplier_id"] = supplier.Id,
                ["po_number"]   = FixturePoNumber,
            },
            ct: ct);

        return order.Id;
    }

    /// <summary>
    /// Idempotently seeds the <c>__sample__</c> supplier with ~5 catalog rows (including the
    /// code for fixture line 3) and ItemMappings for fixture lines 1–2 ONLY. Existence-checked
    /// per row by (SupplierId, Code) / (SupplierId, BuyerItemCode) so a re-run — including a
    /// partial earlier save or a Hangfire retry — adds only what is missing, never duplicates.
    /// Rows are added to the SAME change-set as the supplier itself; EF orders the inserts by
    /// FK dependency, so this is safe on real Postgres for a brand-new supplier too.
    /// </summary>
    private async Task SeedSampleSupplierDataAsync(
        Guid organisationId, Guid supplierId, DateTime now, CancellationToken ct)
    {
        var existingCatalogCodes = await _db.SupplierProducts
            .Where(p => p.OrgId == organisationId && p.SupplierId == supplierId)
            .Select(p => p.Code)
            .ToListAsync(ct);

        foreach (var (code, name, unit, price) in SampleCatalog)
        {
            if (existingCatalogCodes.Contains(code))
                continue;

            _db.SupplierProducts.Add(new SupplierProduct
            {
                Id         = Guid.NewGuid(),
                OrgId      = organisationId,
                SupplierId = supplierId,
                Code       = code,
                Name       = name,
                Unit       = unit,
                Price      = price,
                Currency   = FixtureCurrency,
                IsActive   = true,
                CreatedAt  = now,
                UpdatedAt  = now,
            });
        }

        var existingMappedBuyerCodes = await _db.ItemMappings
            .Where(m => m.OrgId == organisationId && m.SupplierId == supplierId)
            .Select(m => m.BuyerItemCode)
            .ToListAsync(ct);

        foreach (var (buyerCode, supplierCode) in SampleMappings)
        {
            if (existingMappedBuyerCodes.Contains(buyerCode))
                continue;

            _db.ItemMappings.Add(new ItemMapping
            {
                Id               = Guid.NewGuid(),
                OrgId            = organisationId,
                SupplierId       = supplierId,
                BuyerItemCode    = buyerCode,
                SupplierItemCode = supplierCode,
                Confidence       = 1.0f,
                Source           = "imported",
                CreatedAt        = now,
                UpdatedAt        = now,
            });
        }
    }

    private static async Task<byte[]> ReadFixtureBytesAsync(CancellationToken ct)
    {
        await using var stream = typeof(SampleOrderService).Assembly.GetManifestResourceStream(FixtureResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded sample fixture '{FixtureResourceName}' not found in ProcuLink.Infrastructure assembly. " +
                "Check the EmbeddedResource entry in ProcuLink.Infrastructure.csproj.");
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
