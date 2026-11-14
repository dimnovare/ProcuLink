using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Ai;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// EF-backed implementation of <see cref="IAiUsageTracker"/>.
/// Persists token usage in <c>ai_usage_monthly</c> keyed by (OrgId, Year, Month).
///
/// <para>The monthly limit follows the documents the org reads: the billing units
/// read this month or being read now, through
/// <see cref="PlanConstants.GetAiMonthlyTokenAllowance"/>, capped only by the
/// emergency brake <c>Ai:OpenAI:EmergencyMonthlyTokenBrake</c>.</para>
/// </summary>
public sealed class AiUsageTracker : IAiUsageTracker
{
    private readonly ProcuLinkDbContext _db;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary><c>Ai:OpenAI:EmergencyMonthlyTokenBrake</c> when positive, else
    /// <see cref="PlanConstants.AiEmergencyBrakeTokens"/>.</summary>
    private readonly long _brake;

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

        _brake = long.TryParse(configuration["Ai:OpenAI:EmergencyMonthlyTokenBrake"], out var parsed) && parsed > 0
            ? parsed
            : PlanConstants.AiEmergencyBrakeTokens;
    }

    public async Task<bool> IsAtOrOverLimitAsync(Guid organisationId, CancellationToken ct = default)
    {
        var (year, month) = CurrentYearMonth();
        var row = await _db.AiUsageMonthly
            .AsNoTracking()
            .FirstOrDefaultAsync(
                u => u.OrgId == organisationId && u.Year == year && u.Month == month,
                ct);

        // Zero usage can never be at/over a positive limit — skip the allowance lookup.
        if (row is null) return false;

        var limit = await ResolveLimitAsync(organisationId, ct);
        return row.TokensUsed >= limit;
    }

    public async Task IncrementAsync(Guid organisationId, long tokens, CancellationToken ct = default)
    {
        if (tokens <= 0) return;

        var (year, month) = CurrentYearMonth();
        var now = _clock();

        if (!_db.Database.IsRelational())
        {
            // EF InMemory test provider cannot translate ExecuteUpdateAsync — emulate the
            // increment through the change tracker (tests are single-threaded there).
            await IncrementViaTrackerAsync(organisationId, year, month, tokens, now, ct);
            return;
        }

        // Atomic RELATIVE increment (TokensUsed = TokensUsed + tokens) — a read-modify-write
        // absolute UPDATE loses concurrent increments from parallel Hangfire workers,
        // silently under-counting the billing/quota meter.
        var updated = await _db.AiUsageMonthly
            .Where(u => u.OrgId == organisationId && u.Year == year && u.Month == month)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.TokensUsed, u => u.TokensUsed + tokens)
                .SetProperty(u => u.UpdatedAt, now), ct);
        if (updated > 0) return;

        // First usage this month — insert the row.
        _db.AiUsageMonthly.Add(new AiUsageMonthly
        {
            OrgId      = organisationId,
            Year       = year,
            Month      = month,
            TokensUsed = tokens,
            UpdatedAt  = now,
        });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent worker won the first-insert race (Postgres 23505 on the
            // (OrgId, Year, Month) key). Detach the stale Added entity and fall back to
            // the atomic update against the winner's row (mirrors
            // SchemaFingerprintService.RecoverFromConcurrentInsertAsync).
            var stale = _db.ChangeTracker.Entries<AiUsageMonthly>()
                .FirstOrDefault(e => e.State == EntityState.Added);
            if (stale is not null) stale.State = EntityState.Detached;

            var recovered = await _db.AiUsageMonthly
                .Where(u => u.OrgId == organisationId && u.Year == year && u.Month == month)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.TokensUsed, u => u.TokensUsed + tokens)
                    .SetProperty(u => u.UpdatedAt, now), ct);
            if (recovered == 0) throw; // not the insert race — surface the original failure
        }
    }

    /// <summary>Tracker-based increment for the non-relational (InMemory) test provider.</summary>
    private async Task IncrementViaTrackerAsync(
        Guid organisationId, int year, int month, long tokens, DateTimeOffset now, CancellationToken ct)
    {
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
    /// The org's monthly token limit: the allowance for the billing units it read this
    /// month or is reading now (samples excluded) and its account status, capped by the brake.
    /// </summary>
    private async Task<long> ResolveLimitAsync(Guid organisationId, CancellationToken ct)
    {
        var status = await _db.Organisations
            .AsNoTracking()
            .Where(o => o.Id == organisationId)
            .Select(o => o.AccountStatus)
            .FirstOrDefaultAsync(ct);

        var now = _clock().UtcDateTime;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var units = await _db.FlowDocuments
            .Where(d => d.OrgId == organisationId && !d.IsSample
                && ((d.ReadAt != null && d.ReadAt >= monthStart) || d.Status == DocumentStatus.Reading))
            .SumAsync(d => (long)d.BillingUnits, ct);

        return Math.Min(PlanConstants.GetAiMonthlyTokenAllowance(units, status), _brake);
    }

    private (int year, int month) CurrentYearMonth()
    {
        var now = _clock().UtcDateTime;
        return (now.Year, now.Month);
    }
}
