using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Ai;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// EF-backed implementation of <see cref="IAiUsageTracker"/>.
/// Persists token usage in <c>ai_usage_monthly</c> keyed by (OrgId, Year, Month).
/// </summary>
public sealed class AiUsageTracker : IAiUsageTracker
{
    /// <summary>Fallback default when the config key is missing or unparseable.</summary>
    internal const long DefaultMonthlyTokenLimit = 100_000;

    private readonly ProcuLinkDbContext _db;
    private readonly Func<DateTimeOffset> _clock;

    public AiUsageTracker(ProcuLinkDbContext db, IConfiguration configuration)
        : this(db, configuration, () => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>Test-only ctor: lets tests pin the clock to a deterministic instant.</summary>
    internal AiUsageTracker(
        ProcuLinkDbContext db,
        IConfiguration configuration,
        Func<DateTimeOffset> clock)
    {
        _db = db;
        _clock = clock;

        var raw = configuration["Ai:OpenAI:MonthlyTokenLimitPerOrg"];
        MonthlyLimit = long.TryParse(raw, out var parsed) && parsed > 0
            ? parsed
            : DefaultMonthlyTokenLimit;
    }

    public long MonthlyLimit { get; }

    public async Task<bool> IsAtOrOverLimitAsync(Guid organisationId, CancellationToken ct = default)
    {
        var (year, month) = CurrentYearMonth();
        var row = await _db.AiUsageMonthly
            .AsNoTracking()
            .FirstOrDefaultAsync(
                u => u.OrgId == organisationId && u.Year == year && u.Month == month,
                ct);

        if (row is null) return false;
        return row.TokensUsed >= MonthlyLimit;
    }

    public async Task IncrementAsync(Guid organisationId, long tokens, CancellationToken ct = default)
    {
        if (tokens <= 0) return;

        var (year, month) = CurrentYearMonth();
        var now = _clock();

        var row = await _db.AiUsageMonthly.FirstOrDefaultAsync(
            u => u.OrgId == organisationId && u.Year == year && u.Month == month,
            ct);

        if (row is null)
        {
            _db.AiUsageMonthly.Add(new AiUsageMonthly
            {
                OrgId      = organisationId,
                Year       = year,
                Month      = month,
                TokensUsed = tokens,
                UpdatedAt  = now,
            });
        }
        else
        {
            row.TokensUsed += tokens;
            row.UpdatedAt   = now;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<AiUsageSnapshot> GetCurrentAsync(Guid organisationId, CancellationToken ct = default)
    {
        var (year, month) = CurrentYearMonth();
        var row = await _db.AiUsageMonthly
            .AsNoTracking()
            .FirstOrDefaultAsync(
                u => u.OrgId == organisationId && u.Year == year && u.Month == month,
                ct);

        return new AiUsageSnapshot(organisationId, year, month, row?.TokensUsed ?? 0, MonthlyLimit);
    }

    private (int year, int month) CurrentYearMonth()
    {
        var now = _clock().UtcDateTime;
        return (now.Year, now.Month);
    }
}
