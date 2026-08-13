using System.Net.Mail;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Catalog;
using ProcuLink.Core.Constants;
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
///
/// <para>
/// WP-27: the loop above is only "full" if the sample supplier can actually RECEIVE something.
/// Before WP-27 nothing seeded a <see cref="SupplierDeliveryConfig"/>, so every practice run ended
/// at "no delivery is set up" and the docstring promised a loop the code did not close. When the
/// caller supplies an address (normally the signed-in user's own) and this deployment has an email
/// provider, the sample supplier is now seeded with an <c>email</c> delivery setup — the one
/// offered channel that needs ZERO cooperation from a real supplier, because mail leaves from
/// ProcuLink's own verified sender (see <c>DeliveryProtocolConstants.Email</c>).
/// </para>
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

    /// <summary>Output the practice order is delivered in — the format a coordinator can open.</summary>
    private const string SampleOutputFormat = "csv";

    private readonly ProcuLinkDbContext _db;
    private readonly IParseJobEnqueuer  _enqueuer;
    private readonly IFileStorageService _files;
    private readonly IAnalyticsService  _analytics;
    private readonly IEmailApiClient    _email;

    public SampleOrderService(
        ProcuLinkDbContext db,
        IParseJobEnqueuer enqueuer,
        IFileStorageService files,
        IAnalyticsService analytics,
        IEmailApiClient email)
    {
        _db        = db;
        _enqueuer  = enqueuer;
        _files     = files;
        _analytics = analytics;
        _email     = email;
    }

    public async Task<SampleOrderResult> CreateAndEnqueueAsync(
        Guid organisationId,
        string? createdByUserId,
        string? deliverToEmail,
        CancellationToken ct)
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

        // 1c. Seed the delivery setup that CLOSES the loop (WP-27). Skipped — leaving the
        //     pre-WP-27 "no delivery is set up" ending intact — when the caller gave us no usable
        //     address or this deployment has no email provider. Never promise a send we cannot make.
        var practiceDelivery =
            await SeedSampleDeliveryConfigAsync(organisationId, supplier.Id, deliverToEmail, now, ct);

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
                ["order_id"]            = order.Id,
                ["supplier_id"]         = supplier.Id,
                ["po_number"]           = FixturePoNumber,
                ["delivery_configured"] = practiceDelivery == PracticeDeliveryState.EmailedToYou,
                ["practice_delivery"]   = practiceDelivery,
            },
            ct: ct);

        return new SampleOrderResult(order.Id, practiceDelivery);
    }

    /// <summary>
    /// Idempotently seeds an <c>email</c> delivery setup on the sample supplier so pressing "send"
    /// on the practice order really reaches <c>delivered</c>.
    ///
    /// <para>
    /// <b>Why email.</b> It is the only offered protocol whose far end needs nothing installed,
    /// configured, or agreed: mail is sent FROM ProcuLink's provider-verified sender and the
    /// recipient is whatever address the user typed — normally their own
    /// (<c>DeliveryProtocolConstants.Email</c>). HTTP/SFTP/FTPS/ERP all need another company's
    /// endpoint and credentials, which is precisely why first run used to dead-end here.
    /// </para>
    ///
    /// <para>
    /// <b>AutoDeliver is false on purpose.</b> The fixture deliberately leaves line 3 unmapped, so
    /// an auto-run would only 422 at transform; and the explicit "send" press is the moment the
    /// practice run exists to teach. The user's own action drives transform → deliver.
    /// </para>
    ///
    /// <para>
    /// No credentials are written: the email API dispatcher takes none per supplier
    /// (<c>EmailApiDeliveryDispatcher</c>), so <see cref="SupplierDeliveryConfig.EncryptedCredentials"/>
    /// stays empty and the cleartext-<c>ConfigJson</c> invariant is respected — the recipient
    /// address is connection metadata, not a secret.
    /// </para>
    /// </summary>
    /// <returns>
    /// One of <see cref="PracticeDeliveryState"/> — what pressing "send" will actually do.
    /// </returns>
    private async Task<string> SeedSampleDeliveryConfigAsync(
        Guid organisationId, Guid supplierId, string? deliverToEmail, DateTime now, CancellationToken ct)
    {
        // Read FIRST, before either guard. Both used to return early without ever looking, so the
        // answer was "did I write one just now" while every consumer read it as "does this supplier
        // have a delivery target". Those are the same answer except in one case, and that case —
        // a config already sitting there that this run cannot replace — is WP-39 §4.5: the screen
        // said the run would stop at "no delivery is set up", and pressing send POSTed to it.
        var existing = await _db.SupplierDeliveryConfigs
            .FirstOrDefaultAsync(c => c.OrgId == organisationId && c.SupplierId == supplierId, ct);

        string CannotSeed() =>
            existing is null ? PracticeDeliveryState.NotSetUp : PracticeDeliveryState.ExistingTarget;

        var recipient = NormaliseEmail(deliverToEmail);
        if (recipient is null) return CannotSeed();

        // A deployment with no email-API token cannot send at all — EmailApiDeliveryDispatcher
        // would answer "Email delivery is not configured on this deployment". Seeding here would
        // turn today's honest "delivery not set up" ending into a delivery_failed, which is worse.
        if (!_email.IsConfigured) return CannotSeed();

        var configJson = JsonSerializer.Serialize(new
        {
            toAddresses     = recipient,
            // Placeholders are SINGLE-brace — EmailApiDeliveryDispatcher.BuildFromTemplate
            // replaces "{poNumber}" and "{fileName}" literally. "{{…}}" would ship verbatim.
            subjectTemplate = "Purchase order {poNumber} — ProcuLink practice order",
            bodyTemplate    =
                "This is the ProcuLink practice order.\n\n" +
                "Attached ({fileName}) is the supplier-ready file exactly as a real supplier would " +
                "receive it — same builder, same item codes, same layout.\n\n" +
                "Nothing was sent to a real supplier.",
        });

        if (existing is null)
        {
            _db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
            {
                Id                   = Guid.NewGuid(),
                OrgId                = organisationId,
                SupplierId           = supplierId,
                Protocol             = DeliveryProtocolConstants.Email,
                AutoDeliver          = false,
                ConfigJson           = configJson,
                EncryptedCredentials = string.Empty,
                OutputFormat         = SampleOutputFormat,
                CreatedAt            = now,
                UpdatedAt            = now,
            });
            return PracticeDeliveryState.EmailedToYou;
        }

        // Re-run (the user started the practice order again, possibly with a different address):
        // point it at the new address rather than inserting a second config for the same supplier.
        existing.Protocol             = DeliveryProtocolConstants.Email;
        existing.AutoDeliver          = false;
        existing.ConfigJson           = configJson;
        existing.EncryptedCredentials = string.Empty;
        existing.OutputFormat         = SampleOutputFormat;
        existing.UpdatedAt            = now;
        return PracticeDeliveryState.EmailedToYou;
    }

    /// <summary>
    /// Trims and validates one recipient address. Returns null for null/blank/unparseable input —
    /// the caller then skips the seeding rather than writing a config that can only fail at send.
    /// </summary>
    private static string? NormaliseEmail(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        // One address only. A comma-separated list is a real feature of the email protocol, but the
        // practice order is a single-recipient teaching run and accepting a list here would let an
        // unvalidated blob through this guard.
        if (trimmed.Contains(',') || trimmed.Contains(';')) return null;
        try
        {
            var parsed = new MailAddress(trimmed);
            return parsed.Address;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
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
        // The idempotency probe uses the SHARED item-code rule, not a plain List.Contains (which is
        // Ordinal). Re-seeding an org whose catalog already holds "WIDGET-1" must not add a second
        // "widget-1" row: the unique index is case-sensitive so the insert succeeds, and the
        // case-insensitive catalog lookup then has two candidates for one code.
        var existingCatalogCodes = (await _db.SupplierProducts
            .Where(p => p.OrgId == organisationId && p.SupplierId == supplierId)
            .Select(p => p.Code)
            .ToListAsync(ct))
            .ToHashSet(ItemCodeComparison.Comparer);

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

        // Same shared rule for the learned mappings — see the catalog probe above.
        var existingMappedBuyerCodes = (await _db.ItemMappings
            .Where(m => m.OrgId == organisationId && m.SupplierId == supplierId)
            .Select(m => m.BuyerItemCode)
            .ToListAsync(ct))
            .ToHashSet(ItemCodeComparison.Comparer);

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
                // Seeded, not scored. This wrote 1.0f while ItemMappingService wrote 0.8f for the
                // same "imported" source — two writers, one column, two different fictions.
                Confidence       = null,
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
