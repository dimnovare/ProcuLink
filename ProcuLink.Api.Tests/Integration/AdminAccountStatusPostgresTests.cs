using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// The 2026-07-24 product gap proven closed on REAL Postgres: an org frozen by a Stripe cancel
/// (<c>account_status = read_only</c>, plan reverted to Pilot, subscription id nulled) can be
/// lifted back to <c>trialing</c> through <c>POST /api/admin/organisations/{id}/account-status</c>
/// instead of a raw production UPDATE.
///
/// <para>Real Postgres matters for two things InMemory cannot prove: the status write must be
/// visible from a FRESH DbContext (a genuine round trip through Npgsql, not the shared in-memory
/// change tracker), and the accountability row must persist with its <c>jsonb</c> payload intact
/// — <c>audit_events.payload</c> is a real jsonb column, and InMemory stores the
/// <see cref="System.Text.Json.JsonDocument"/> as an object reference, which would mask a
/// serialization failure entirely.</para>
///
/// <para>Docker-gated; skips where Docker is absent.</para>
/// </summary>
/// <summary>
/// ONE postgres:16 + migrated schema shared by every test in
/// <see cref="AdminAccountStatusPostgresTests"/>.
///
/// <para>xUnit builds a fresh instance of a test class per test method, so the usual
/// <c>IAsyncLifetime</c>-on-the-test-class pattern starts (and migrates) a container PER TEST.
/// Three cold starts plus three full migrations is exactly the load the
/// <see cref="PostgresContainerCollection"/> comment warns about — under a busy Docker the
/// second and third reliably die with Npgsql "Timeout during reading attempt" inside
/// <c>InitializeAsync</c>, before a line of the code under test runs. A class fixture starts
/// it once. Sharing is safe here because every test seeds its OWN organisation under a fresh
/// <see cref="Guid"/> and filters on it — no test can observe another's rows.</para>
/// </summary>
public sealed class AdminAccountStatusPostgresFixture(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;

    /// <summary>Null when Docker is unavailable — the Docker-gated facts skip before touching it.</summary>
    public DbContextOptions<ProcuLinkDbContext>? Options { get; private set; }

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_acctstatus");

        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_databaseConnectionString)
        {
            Pooling = false,
            // Both timeouts are about the Docker HOST's load, never about anything under test:
            // opening the first connection to a cold container, and then running the whole
            // migration chain over it, both outlive the 15 s / 30 s defaults on a busy machine.
            Timeout = 60,
            CommandTimeout = 180,
        }.ConnectionString;

        Options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            // The full migration chain on a cold container comfortably outlives Npgsql's 30 s
            // default when the Docker host is busy; the timeout is about the HOST's load, not
            // about anything this test measures.
            .UseNpgsql(connectionString, npgsql => npgsql.CommandTimeout(180))
            .Options;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }
}

[Collection("postgres-container")]
public sealed class AdminAccountStatusPostgresTests : IClassFixture<AdminAccountStatusPostgresFixture>
{
    private readonly AdminAccountStatusPostgresFixture _fixture;

    public AdminAccountStatusPostgresTests(AdminAccountStatusPostgresFixture fixture) => _fixture = fixture;

    private ProcuLinkDbContext NewContext() => new(_fixture.Options!);

    [DockerRequiredFact]
    public async Task ReadOnlyToTrialing_RoundTripsThroughPostgres_AndLeavesAnAuditTrail()
    {
        // A frozen-Pilot org in exactly the shape both cancel paths leave behind
        // (BillingController.HandleSubscriptionDeletedAsync / reconciliation DowngradeAsync):
        // read_only + Pilot + customer id kept + subscription id nulled.
        var orgId = await SeedFrozenPilotOrgAsync(trialStartedAt: DateTime.UtcNow.AddDays(-3));

        await using (var db = NewContext())
        {
            var ctrl = BuildAdminController(db, sub: "user_founder", email: "founder@proculink.eu");
            var result = await ctrl.SetOrganisationAccountStatus(
                orgId, new SetOrgAccountStatusRequest(AccountStatusConstants.Trialing), CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var dto = Assert.IsType<OrgAccountStatusResponse>(ok.Value);
            Assert.Equal(AccountStatusConstants.ReadOnly, dto.PreviousAccountStatus);
            Assert.Equal(AccountStatusConstants.Trialing, dto.AccountStatus);
            Assert.False(dto.RevertedByTrialWindow);
        }

        // FRESH context — the write must be in Postgres, not merely in a change tracker.
        await using (var verify = NewContext())
        {
            var org = await verify.Organisations.AsNoTracking().SingleAsync(o => o.Id == orgId);
            Assert.Equal(AccountStatusConstants.Trialing, org.AccountStatus);
            Assert.Equal(PlanConstants.Pilot, org.Plan);
            Assert.Null(org.StripeSubscriptionId);

            var audit = await verify.AuditEvents.AsNoTracking()
                .SingleAsync(e => e.OrgId == orgId && e.Action == "admin.org.account_status_changed");
            Assert.Equal("Organisation", audit.EntityType);
            Assert.Equal(orgId, audit.EntityId);

            // The jsonb payload must survive the real column round trip.
            var root = audit.Payload!.RootElement;
            Assert.Equal("user_founder", root.GetProperty("actor").GetProperty("sub").GetString());
            Assert.Equal("founder@proculink.eu", root.GetProperty("actor").GetProperty("email").GetString());
            var detail = root.GetProperty("detail");
            Assert.Equal(AccountStatusConstants.ReadOnly, detail.GetProperty("from").GetString());
            Assert.Equal(AccountStatusConstants.Trialing, detail.GetProperty("to").GetString());
        }
    }

    [DockerRequiredFact]
    public async Task LapsedTrialWindow_PersistsTheArbitersVerdict_NotTheRequestedStatus()
    {
        // The endpoint hands the final say to MarkPilotExpiredIfNeededAsync. For an org whose
        // Pilot window elapsed long ago, what must land in Postgres is trial_expired — the
        // response says so, and so does the database. (This is the founder-org shape: cancelled
        // months after the original trial ended.)
        var orgId = await SeedFrozenPilotOrgAsync(
            trialStartedAt: DateTime.UtcNow.AddDays(-400),
            trialEndsAt:    DateTime.UtcNow.AddDays(-386));

        await using (var db = NewContext())
        {
            var ctrl = BuildAdminController(db, sub: "user_founder", email: "founder@proculink.eu");
            var result = await ctrl.SetOrganisationAccountStatus(
                orgId, new SetOrgAccountStatusRequest(AccountStatusConstants.Trialing), CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var dto = Assert.IsType<OrgAccountStatusResponse>(ok.Value);
            Assert.True(dto.RevertedByTrialWindow);
            Assert.Equal(AccountStatusConstants.TrialExpired, dto.AccountStatus);
            Assert.Contains("limits", dto.Note);
        }

        await using (var verify = NewContext())
        {
            var org = await verify.Organisations.AsNoTracking().SingleAsync(o => o.Id == orgId);
            Assert.Equal(AccountStatusConstants.TrialExpired, org.AccountStatus);

            // The audit row records the EFFECTIVE outcome, not the wish.
            var audit = await verify.AuditEvents.AsNoTracking()
                .SingleAsync(e => e.OrgId == orgId && e.Action == "admin.org.account_status_changed");
            var detail = audit.Payload!.RootElement.GetProperty("detail");
            Assert.Equal(AccountStatusConstants.Trialing, detail.GetProperty("requested").GetString());
            Assert.Equal(AccountStatusConstants.TrialExpired, detail.GetProperty("to").GetString());
        }
    }

    [DockerRequiredFact]
    public async Task RefusedTransition_WritesNothingToPostgres()
    {
        var orgId = await SeedFrozenPilotOrgAsync(trialStartedAt: DateTime.UtcNow.AddDays(-3));

        await using (var db = NewContext())
        {
            var ctrl = BuildAdminController(db, sub: "user_founder", email: "founder@proculink.eu");
            var result = await ctrl.SetOrganisationAccountStatus(
                orgId, new SetOrgAccountStatusRequest(AccountStatusConstants.Active), CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result);
        }

        await using (var verify = NewContext())
        {
            var org = await verify.Organisations.AsNoTracking().SingleAsync(o => o.Id == orgId);
            Assert.Equal(AccountStatusConstants.ReadOnly, org.AccountStatus);
            Assert.Empty(await verify.AuditEvents.AsNoTracking()
                .Where(e => e.OrgId == orgId && e.Action == "admin.org.account_status_changed")
                .ToListAsync());
        }
    }

    private async Task<Guid> SeedFrozenPilotOrgAsync(DateTime trialStartedAt, DateTime? trialEndsAt = null)
    {
        var orgId = Guid.NewGuid();
        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id                       = orgId,
            ClerkOrgId               = $"org_as_{orgId:N}",
            Name                     = "Frozen Pilot Org",
            Slug                     = $"as-{orgId:N}",
            Plan                     = PlanConstants.Pilot,
            AccountStatus            = AccountStatusConstants.ReadOnly,
            CreatedAt                = DateTime.UtcNow,
            TrialStartedAt           = trialStartedAt,
            TrialEndsAt              = trialEndsAt,
            StripeCustomerId         = "cus_kept_for_portal",   // both cancel paths KEEP this
            StripeSubscriptionId     = null,                    // ...and null this
            StripeSubscriptionStatus = "canceled",
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    private static AdminController BuildAdminController(ProcuLinkDbContext db, string sub, string email)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())   // no Stripe:SecretKey
            .Build();

        var billing = new StripeBillingService(
            db, config, NullLogger<StripeBillingService>.Instance, new FakeAnalyticsService());

        var ctrl = new AdminController(
            db, billing, config, NullLogger<AdminController>.Instance, new NoopErasureService(),
            new ProcuLink.Infrastructure.Services.ItemMappingService(db));

        var identity = new ClaimsIdentity(
            new[] { new Claim("sub", sub), new Claim("email", email) }, authenticationType: "Test");
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
        return ctrl;
    }

    private sealed class NoopErasureService : ProcuLink.Core.Services.IDataErasureService
    {
        public Task<ProcuLink.Core.Services.OrderErasureResult> EraseOrderAsync(Guid org, Guid orderId, CancellationToken ct)
            => Task.FromResult(new ProcuLink.Core.Services.OrderErasureResult(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));

        public Task<ProcuLink.Core.Services.BulkOrderErasureResult> BulkEraseOrdersAsync(
            Guid org, ProcuLink.Core.Services.BulkEraseFilter filter, CancellationToken ct)
            => Task.FromResult(new ProcuLink.Core.Services.BulkOrderErasureResult(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
    }
}
