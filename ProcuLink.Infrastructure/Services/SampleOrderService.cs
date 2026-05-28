using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Email;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// Creates a sample purchase order from the embedded onboarding fixture so a new user can run the
/// full parse → transform → deliver loop without their own data. The sample supplier (<c>__sample__</c>)
/// is created on first call and reused; the order is flagged <c>IsSample = true</c> so it is excluded
/// from billing quota (see <c>StripeBillingService.CountOrdersAsync</c>).
/// </summary>
public sealed class SampleOrderService : ISampleOrderService
{
    private const string SampleSupplierCode  = "__sample__";
    private const string SampleSupplierName  = "ProcuLink Sample Supplier";
    private const string FixtureResourceName = "ProcuLink.Infrastructure.Fixtures.sample-order.csv";
    private const string FixtureFileName     = "sample-order.csv";
    private const string FixturePoNumber     = "DEMO-2026-001";
    private const string FixtureCurrency     = "EUR";

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

        // 1. Idempotent: reuse existing __sample__ supplier or create one.
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
