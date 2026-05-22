using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Canonical;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Repositories;

public class EfSupplierProfileRepository : ISupplierProfileRepository
{
    private readonly ProcuLinkDbContext _db;
    private readonly ICurrentTenantService _tenant;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public EfSupplierProfileRepository(ProcuLinkDbContext db, ICurrentTenantService tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SupplierProfile>> ListAsync(CancellationToken ct = default)
    {
        var orgId = _tenant.OrganisationId;

        var rows = await _db.SupplierProfiles
            .AsNoTracking()
            .Include(x => x.Supplier)
            .Where(x => x.OrgId == orgId)
            .OrderBy(x => x.Supplier.Name)
            .ToListAsync(ct);

        return rows.Select(ToCanonical).ToList();
    }

    public async Task<SupplierProfile?> GetByNameAsync(string supplierName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(supplierName))
            return null;

        var orgId = _tenant.OrganisationId;
        var lower = supplierName.Trim().ToLowerInvariant();

        var row = await _db.SupplierProfiles
            .AsNoTracking()
            .Include(x => x.Supplier)
            .Where(x => x.OrgId == orgId
                     && x.Supplier.Name.ToLower() == lower)
            .FirstOrDefaultAsync(ct);

        return row is null ? null : ToCanonical(row);
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public async Task<SupplierProfile> UpsertAsync(SupplierProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.SupplierName))
            throw new ArgumentException("SupplierName is required.", nameof(profile));

        var orgId = _tenant.OrganisationId;
        var supplier = await GetOrCreateSupplierAsync(profile.SupplierName, orgId, ct);

        var existing = await _db.SupplierProfiles
            .Where(x => x.OrgId == orgId && x.SupplierId == supplier.Id)
            .FirstOrDefaultAsync(ct);

        var requiredFieldsJson = JsonDocument.Parse(
            JsonSerializer.Serialize(new SupplierProfileFlags
            {
                RequiresSupplierItemCode = profile.RequiresSupplierItemCode,
                SupportsPartialAutomation = profile.SupportsPartialAutomation,
                Fields = profile.RequiredFields
            }, JsonOpts));

        var now = DateTime.UtcNow;

        if (existing is null)
        {
            var entity = new SupplierProfileEntity
            {
                Id = Guid.NewGuid(),
                SupplierId = supplier.Id,
                OrgId = orgId,
                AcceptedFormats = profile.AcceptedFormats,
                RequiredFields = requiredFieldsJson,
                OutputFormat = profile.AcceptedFormats.FirstOrDefault() ?? string.Empty,
                DestinationType = "download",
                CreatedAt = now,
                UpdatedAt = now
            };
            _db.SupplierProfiles.Add(entity);
        }
        else
        {
            existing.AcceptedFormats = profile.AcceptedFormats;
            existing.RequiredFields = requiredFieldsJson;
            existing.OutputFormat = profile.AcceptedFormats.FirstOrDefault() ?? existing.OutputFormat;
            existing.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
        return profile;
    }

    public async Task<bool> DeleteAsync(string supplierName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(supplierName))
            return false;

        var orgId = _tenant.OrganisationId;
        var lower = supplierName.Trim().ToLowerInvariant();

        var row = await _db.SupplierProfiles
            .Include(x => x.Supplier)
            .Where(x => x.OrgId == orgId
                     && x.Supplier.Name.ToLower() == lower)
            .FirstOrDefaultAsync(ct);

        if (row is null)
            return false;

        _db.SupplierProfiles.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private async Task<Supplier> GetOrCreateSupplierAsync(
        string supplierName, Guid orgId, CancellationToken ct)
    {
        var supplier = await _db.Suppliers
            .Where(s => s.OrgId == orgId && s.Name == supplierName)
            .FirstOrDefaultAsync(ct);

        if (supplier is null)
        {
            supplier = new Supplier
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                Name = supplierName,
                CreatedAt = DateTime.UtcNow
            };
            _db.Suppliers.Add(supplier);
        }

        return supplier;
    }

    private static SupplierProfile ToCanonical(SupplierProfileEntity e)
    {
        var flags = new SupplierProfileFlags();

        if (e.RequiredFields is not null)
        {
            try
            {
                flags = JsonSerializer.Deserialize<SupplierProfileFlags>(
                    e.RequiredFields.RootElement.GetRawText(), JsonOpts)
                    ?? flags;
            }
            catch (JsonException) { /* tolerate corrupt data */ }
        }

        return new SupplierProfile
        {
            SupplierName = e.Supplier?.Name ?? string.Empty,
            RequiresSupplierItemCode = flags.RequiresSupplierItemCode,
            SupportsPartialAutomation = flags.SupportsPartialAutomation,
            RequiredFields = flags.Fields,
            AcceptedFormats = e.AcceptedFormats
        };
    }

    /// <summary>
    /// Private DTO stored in the required_fields jsonb column to preserve
    /// the Canonical SupplierProfile flags that have no dedicated columns.
    /// </summary>
    private sealed class SupplierProfileFlags
    {
        public bool RequiresSupplierItemCode { get; set; }
        public bool SupportsPartialAutomation { get; set; }
        public List<string> Fields { get; set; } = new();
    }
}
