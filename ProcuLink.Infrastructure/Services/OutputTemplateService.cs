using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

public sealed class OutputTemplateService : IOutputTemplateService
{
    private readonly ProcuLinkDbContext _db;

    public OutputTemplateService(ProcuLinkDbContext db) { _db = db; }

    public async Task<IReadOnlyList<OutputTemplate>> ListAsync(Guid orgId, CancellationToken ct)
    {
        return await _db.OutputTemplates
            .AsNoTracking()
            .Where(t => t.OrgId == orgId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<OutputTemplate> CreateAsync(Guid orgId, CreateTemplateRequest req, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var template = new OutputTemplate
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            Name       = req.Name,
            Format     = req.Format,
            Version    = req.Version,
            ConfigJson = req.ConfigJson,
            CreatedAt  = now,
            UpdatedAt  = now,
        };
        _db.OutputTemplates.Add(template);
        await _db.SaveChangesAsync(ct);
        return template;
    }

    public async Task<OutputTemplate?> UpdateAsync(Guid orgId, Guid id, CreateTemplateRequest req, CancellationToken ct)
    {
        var existing = await _db.OutputTemplates
            .Where(t => t.Id == id && t.OrgId == orgId)
            .FirstOrDefaultAsync(ct);

        if (existing is null) return null;

        existing.Name      = req.Name;
        existing.Format    = req.Format;
        existing.Version   = req.Version;
        existing.ConfigJson?.Dispose();
        existing.ConfigJson = req.ConfigJson;
        existing.UpdatedAt  = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid orgId, Guid id, CancellationToken ct)
    {
        var existing = await _db.OutputTemplates
            .Where(t => t.Id == id && t.OrgId == orgId)
            .FirstOrDefaultAsync(ct);

        if (existing is null) return false;

        _db.OutputTemplates.Remove(existing);
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
