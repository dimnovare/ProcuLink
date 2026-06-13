using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// V9 — proves the confidence-calibration grouped/filtered aggregate runs correctly on REAL
/// Postgres (not just EF InMemory). The InMemory provider can mask LINQ-translation issues, and
/// the calibration query filters on the free-form Decision string + a nullable double + an empty
/// string + an org scope — exactly the kind of predicate worth pinning against Npgsql.
/// Also reconfirms tenant isolation at the database level. Docker-gated.
/// </summary>
[Collection("postgres-container")]
public sealed class ConfidenceCalibrationPostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_cal_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
        {
            Pooling = false,
        }.ConnectionString;

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var migrateDb = new ProcuLinkDbContext(_options);
        await migrateDb.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    [DockerRequiredFact]
    public async Task Calibration_AggregatesAndIsOrgScoped_OnRealPostgres()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var now  = DateTime.UtcNow;

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            // FKs are enforced on Postgres → seed the org parents first (the InMemory-masks-FK lesson).
            db.Organisations.Add(NewOrg(orgA, "Calib A"));
            db.Organisations.Add(NewOrg(orgB, "Calib B"));
            await db.SaveChangesAsync();

            // Org A — an over-confident [0.85,0.95) bucket: 10 accepted / 10 rejected at raw 0.90.
            db.AiSuggestionDecisions.AddRange(MakeMany(orgA, 0.90, accepted: 10, rejected: 10, now));
            // Noise that must be excluded from A's curve: manual (empty code) + null-confidence rows.
            db.AiSuggestionDecisions.AddRange(MakeNoise(orgA, now));
            // Org B — a different, rich bucket that must never leak into A.
            db.AiSuggestionDecisions.AddRange(MakeMany(orgB, 0.60, accepted: 30, rejected: 0, now));
            await db.SaveChangesAsync();
        }

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var svc = new ConfidenceCalibrationService(db, new MemoryCache(new MemoryCacheOptions()));

            // Org A: raw 0.90 → empirical 10/20 accepted → smoothed (10+1)/(20+2) = 0.5, calibrated.
            var aResult = await svc.CalibrateAsync(orgA, 0.90);
            aResult.IsCalibrated.Should().BeTrue();
            aResult.SampleSize.Should().Be(20, "manual + null-confidence rows are excluded by the query");
            aResult.CalibratedConfidence.Should().BeApproximately(0.5, 1e-9);

            var aSummary = await svc.GetCalibrationSummaryAsync(orgA);
            aSummary.TotalDecisions.Should().Be(20);
            aSummary.IsActive.Should().BeTrue();
            aSummary.Buckets[3].Accepted.Should().Be(10);
            aSummary.Buckets[3].Rejected.Should().Be(10);
            // Org B's [0.5,0.7) bucket is empty for A.
            aSummary.Buckets[1].Total.Should().Be(0);

            // Org B: its own data calibrates; A's never appears.
            var bSummary = await svc.GetCalibrationSummaryAsync(orgB);
            bSummary.TotalDecisions.Should().Be(30);
            bSummary.Buckets[1].Accepted.Should().Be(30);
            bSummary.Buckets[3].Total.Should().Be(0, "org A's bucket-3 history must never leak into org B");
        }
    }

    private static Organisation NewOrg(Guid id, string name) => new()
    {
        Id            = id,
        ClerkOrgId    = $"org_cal_{id:N}",
        Name          = name,
        Slug          = $"cal-{id:N}",
        Plan          = "operations",
        AccountStatus = "active",
        CreatedAt     = DateTime.UtcNow,
    };

    private static IEnumerable<AiSuggestionDecision> MakeMany(
        Guid orgId, double confidence, int accepted, int rejected, DateTime now)
    {
        for (var i = 0; i < accepted; i++)
            yield return Make(orgId, i, confidence, AiSuggestionDecisionKind.Accepted, "SUP-A", "SUP-A", now);
        for (var i = 0; i < rejected; i++)
            yield return Make(orgId, 100_000 + i, confidence, AiSuggestionDecisionKind.Rejected, "SUP-A", "SUP-B", now);
    }

    private static IEnumerable<AiSuggestionDecision> MakeNoise(Guid orgId, DateTime now)
    {
        // manual rows (empty suggested code) — no AI suggestion to judge.
        for (var i = 0; i < 15; i++)
            yield return Make(orgId, 200_000 + i, confidence: null, AiSuggestionDecisionKind.Manual, "", "SUP-X", now);
        // null-confidence "accepted" rows — would skew the curve if not excluded.
        for (var i = 0; i < 15; i++)
            yield return Make(orgId, 300_000 + i, confidence: null, AiSuggestionDecisionKind.Accepted, "SUP-Y", "SUP-Y", now);
    }

    private static AiSuggestionDecision Make(
        Guid orgId, int lineNumber, double? confidence, string decision,
        string suggested, string? chosen, DateTime now) => new()
    {
        Id                        = Guid.NewGuid(),
        OrgId                     = orgId,
        OrderId                   = Guid.NewGuid(),
        LineNumber                = lineNumber,
        SuggestedSupplierItemCode = suggested,
        ChosenSupplierItemCode    = chosen,
        Confidence                = confidence,
        ModelVersion              = "gpt-5-mini",
        Decision                  = decision,
        DecidedBy                 = "user",
        DecidedAt                 = now,
    };
}
