using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Ai;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// EF-backed implementation of <see cref="IAiUsageTracker"/>.
/// Persists token usage in <c>ai_usage_monthly</c> keyed by (OrgId, Year, Month).
///
/// <para>The monthly limit is PLAN-AWARE and resolved per org at check time:
/// the global config override <c>Ai:OpenAI:MonthlyTokenLimitPerOrg</c> (when
/// set and &gt; 0) beats EVERY plan — preserved as the production emergency
/// lever. Otherwise the org's plan maps to its default via
/// <see cref="PlanConstants.GetAiMonthlyTokenLimit"/>; a missing org row or an
/// unrecognised plan falls back to the Pilot value (fail-safe).</para>
/// </summary>
public sealed class AiUsageTracker : IAiUsageTracker
{
    private readonly ProcuLinkDbContext _db;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// Global config override (<c>Ai:OpenAI:MonthlyTokenLimitPerOrg</c>).
    /// Null when the key is missing, unparseable, or non-positive — in which
    /// case the per-plan defaults apply.
    /// </summary>
    private readonly long? _configOverrideLimit;

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
        _configOverrideLimit = long.TryParse(raw, out var parsed) && parsed > 0
            ? parsed
            : null;
    }

    public async Task<bool> IsAtOrOverLimitAsync(Guid organisationId, CancellationToken ct = default)
    {
        var (year, month) = CurrentYearMonth();
        var row = await _db.AiUsageMonthly
            .AsNoTracking()
            .FirstOrDefaultAsync(
                u => u.OrgId == organisationId && u.Year == year && u.Month == month,
                ct);

        // Zero usage can never be at/over a positive limit — skip the plan lookup.
        if (row is null) return false;

        var limit = await ResolveLimitAsync(organisationId, ct);
        return row.TokensUsed >= limit;
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

        var limit = await ResolveLimitAsync(organisationId, ct);
        return new AiUsageSnapshot(organisationId, year, month, row?.TokensUsed ?? 0, limit);
    }

    /// <summary>
    /// Resolves the org's monthly token limit. PRECEDENCE: the explicit config
    /// override (set and &gt; 0) beats ALL plans; otherwise the org's plan
    /// default. A missing org row or unknown plan resolves to the Pilot value
    /// (fail-safe).
    /// </summary>
    private async Task<long> ResolveLimitAsync(Guid organisationId, CancellationToken ct)
    {
        if (_configOverrideLimit is { } configured) return configured;

        var plan = await _db.Organisations
            .AsNoTracking()
            .Where(o => o.Id == organisationId)
            .Select(o => o.Plan)
            .FirstOrDefaultAsync(ct);

        return PlanConstants.GetAiMonthlyTokenLimit(plan);
    }

    private (int year, int month) CurrentYearMonth()
    {
        var now = _clock().UtcDateTime;
        return (now.Year, now.Month);
    }
}
