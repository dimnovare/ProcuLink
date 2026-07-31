using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Hangfire;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Detection;
using ProcuLink.Infrastructure.Services.Email;
using ProcuLink.Infrastructure.Services.Ingress;
using ProcuLink.Infrastructure.Services.Security;
using ProcuLink.Worker.Jobs;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace ProcuLink.Api.Tests.Integration;

// ════════════════════════════════════════════════════════════════════════════════════════
//  THE SUPPLIER-ROUTING MATRIX
//
//  One question, asked once per ingress channel: "does an order that arrives HERE end up
//  pointed at the RIGHT supplier — and when no supplier can be determined, does it park for
//  a human instead of being dropped or mis-routed?"
//
//  Every channel is answered in the same two columns, so the whole product's routing story
//  is one readable table:
//
//   cell  channel            scenario                                    outcome
//   ────  ─────────────────  ──────────────────────────────────────────  ─────────────────
//   1a    manual upload      explicit supplierId (CSV)                   routed
//   1b    manual upload      supplierId = Guid.Empty                     rejected 400
//   1c    manual upload      explicit supplierId (XLSX)                  routed
//   2a    REST ingress       SupplierId = GUID                           routed
//   2b    REST ingress       SupplierId = name, wrong case               routed
//   2c    REST ingress       SupplierId = unknown name                   rejected 400
//   2d    REST ingress       SupplierId = another org's supplier GUID    rejected 400
//   3a    inbound email      org default supplier set                    routed (to default)
//   3b    inbound email      no default, 2 suppliers                     parked unrouted (200)
//   3c    inbound email      no supplier at all                          parked unrouted (200)
//   3d    inbound email      only a soft-deleted supplier                parked unrouted (200)
//   3e    inbound email      prose-only body NLP, no supplier            parked unrouted (200)
//   4a    SFTP pull          source default supplier (CSV)               routed
//   4b    SFTP pull          default supplier NULL                       parked unrouted
//   4c    SFTP pull          default supplier soft-deleted               parked unrouted
//   4d    SFTP pull          default supplier NULL (XLSX)                parked unrouted
//   4e    S3 pull            source default supplier                     routed
//   4f    S3 pull            default supplier NULL                       parked unrouted
//   4g    S3 pull            default supplier soft-deleted               parked unrouted
//   4h    IMAP poll          source default supplier                     routed
//   4i    IMAP poll          default supplier NULL                       parked unrouted
//   4j    IMAP poll          default supplier soft-deleted               parked unrouted
//   5a    assign-supplier    unrouted order + valid supplier             routed + revision pinned
//   5b    assign-supplier    order already routed (ready)                rejected 409, untouched
//   6a    learning           assign then re-parse binds the layout       fingerprint bound
//   6b    learning           2nd same-layout doc, supplier-less          parked unrouted (no auto-bind)
//   7a    ASN / DESADV       EDIFACT DESADV upload                       rejected 501
//
//  WHY REAL POSTGRES. Three of these cells cannot be answered anywhere else:
//  2b is a case-insensitive name match that EF translates to Postgres <c>lower()</c>;
//  5a's claim and 6a's fingerprint bump are <c>ExecuteUpdateAsync</c>, which EF InMemory
//  cannot translate; and every "parked unrouted" cell asserts a real NULL
//  <c>supplier_id</c> column write against the fully migrated schema.
//
//  WHAT THIS SUITE PROVES, EXACTLY. Each cell drives the REAL producer for its channel —
//  the actual controller, router, ingress service or job that decides routing in
//  production — against real Postgres, and asserts the supplier that decision produced.
//  The leaf stub-creator is a recorder that persists precisely the supplier id the producer
//  passed it (see <see cref="RoutingRecorder"/>): that is the assertion surface, because the
//  supplier a channel CHOOSES is the whole question here.
//
//  WHAT IT DOES NOT PROVE, AND WHO DOES. The <c>parsing → unrouted</c> park is the parse
//  job's single write (<c>OrderIngestionService</c>,
//  <c>if (entity.SupplierId is null) newStatus = Unrouted</c>); driving the real parse needs
//  storage plus parsers, so the cells apply that same transition through
//  <see cref="ApplyParseJobParkAsync"/> and assert the row accepts it. Its persistence is
//  owned by <see cref="UnroutedOrderNullSupplierPersistencePostgresTests"/>. Likewise these
//  siblings own their own depth and are referenced, not repeated here:
//  <see cref="AssignSupplierPostgresTests"/> (404 / cross-tenant 400 variants of cell 5),
//  <see cref="InboundEmailUnroutedPostgresTests"/> (cell 3's audit trail),
//  <see cref="SchemaFingerprintLearnsOnAssignPostgresTests"/> (cell 6's count-only guard),
//  and the SFTP/S3/IMAP claim-first suites (dedupe and resume, not routing).
//
//  Each cell prints its id into the test output and stamps it into every assertion message,
//  so a red run names the cell that broke without anyone reading the harness.
// ════════════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// What a cell asserts about where the order ended up.
/// </summary>
internal enum RoutingOutcome
{
    /// <summary>An order exists, carries the expected supplier, and is NOT parked for routing.</summary>
    Routed,

    /// <summary>An order exists with a NULL supplier and parks <c>unrouted</c> for a human.</summary>
    ParkedUnrouted,

    /// <summary>No order is created at all — the channel refuses the document up front.</summary>
    Rejected,
}

/// <summary>One row of the routing matrix.</summary>
/// <param name="Id">Stable cell id (e.g. <c>"2b"</c>) — printed in output and in every assertion.</param>
/// <param name="Channel">The ingress channel under test, in product language.</param>
/// <param name="Scenario">The routing input being varied.</param>
/// <param name="Outcome">What must happen to the order.</param>
/// <param name="ExpectedHttpStatus">For <see cref="RoutingOutcome.Rejected"/> cells, the status the caller must see.</param>
internal sealed record RoutingCell(
    string Id,
    string Channel,
    string Scenario,
    RoutingOutcome Outcome,
    int? ExpectedHttpStatus = null)
{
    public string Label => $"cell {Id} [{Channel}] {Scenario}";
}

/// <summary>What actually happened when a cell ran.</summary>
/// <param name="SupplierId">The supplier on the created order; null when unrouted or no order exists.</param>
/// <param name="ParkedStatus">The status after the parse job's park is applied; null when no order exists.</param>
/// <param name="HttpStatus">The status code the caller saw, where the channel is an HTTP endpoint.</param>
/// <param name="OrderCount">Orders belonging to the cell's org afterwards.</param>
internal sealed record CellActual(
    Guid? SupplierId,
    string? ParkedStatus,
    int? HttpStatus,
    int OrderCount);

/// <summary>
/// One Postgres container for the WHOLE matrix. xUnit builds a fresh test-class instance per
/// theory case, so the repo's usual per-class <see cref="IAsyncLifetime"/> container would start
/// and migrate a container PER CELL — nearly thirty of them, which is exactly the Docker-host
/// overload that makes these suites flake ("Timeout during reading attempt"). A class fixture is
/// created once and shared by every case.
/// </summary>
public sealed class SupplierRoutingMatrixPostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;

    /// <summary>Null when Docker is unavailable — the Docker-gated theory skips before touching it.</summary>
    public DbContextOptions<ProcuLinkDbContext>? Options { get; private set; }

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_routing_matrix_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
        {
            Pooling = false,
        }.ConnectionString;

        Options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var migrateDb = new ProcuLinkDbContext(Options);
        await migrateDb.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }
}

[Collection("postgres-container")]
public sealed class SupplierRoutingMatrixPostgresTests
    : IClassFixture<SupplierRoutingMatrixPostgresFixture>
{
    private static readonly string[] LayoutHeaders = { "po_number", "supplier_code", "sku", "qty", "price" };

    private static readonly byte[] CsvBytes  = Encoding.UTF8.GetBytes("po_number,sku,qty,price\r\nPO-1,SKU-1,5,9.99\r\n");
    private static readonly byte[] XlsxBytes = Encoding.UTF8.GetBytes("PK not-a-real-workbook — routing never reads the body");

    private readonly SupplierRoutingMatrixPostgresFixture _fixture;
    private readonly ITestOutputHelper _output;

    public SupplierRoutingMatrixPostgresTests(
        SupplierRoutingMatrixPostgresFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output  = output;
    }

    private ProcuLinkDbContext NewDb() => new(_fixture.Options!);

    // ── The matrix ──────────────────────────────────────────────────────────────────────

    private static readonly RoutingCell[] Matrix =
    {
        new("1a", "manual upload",   "explicit supplierId (CSV)",                 RoutingOutcome.Routed,         200),
        new("1b", "manual upload",   "supplierId = Guid.Empty",                   RoutingOutcome.Rejected,       400),
        new("1c", "manual upload",   "explicit supplierId (XLSX)",                RoutingOutcome.Routed,         200),

        new("2a", "REST ingress",    "SupplierId = GUID",                         RoutingOutcome.Routed,         200),
        new("2b", "REST ingress",    "SupplierId = name, wrong case",             RoutingOutcome.Routed,         200),
        new("2c", "REST ingress",    "SupplierId = unknown name",                 RoutingOutcome.Rejected,       400),
        new("2d", "REST ingress",    "SupplierId = another org's supplier GUID",  RoutingOutcome.Rejected,       400),

        new("3a", "inbound email",   "org default supplier set",                  RoutingOutcome.Routed,         200),
        new("3b", "inbound email",   "no default, 2 suppliers (never guesses)",   RoutingOutcome.ParkedUnrouted, 200),
        new("3c", "inbound email",   "no supplier at all",                        RoutingOutcome.ParkedUnrouted, 200),
        new("3d", "inbound email",   "only a soft-deleted supplier",              RoutingOutcome.ParkedUnrouted, 200),
        new("3e", "inbound email",   "prose-only body NLP, no supplier",          RoutingOutcome.ParkedUnrouted, 200),

        new("4a", "SFTP pull",       "source default supplier (CSV)",             RoutingOutcome.Routed),
        new("4b", "SFTP pull",       "default supplier NULL",                     RoutingOutcome.ParkedUnrouted),
        new("4c", "SFTP pull",       "default supplier soft-deleted",             RoutingOutcome.ParkedUnrouted),
        new("4d", "SFTP pull",       "default supplier NULL (XLSX)",              RoutingOutcome.ParkedUnrouted),

        new("4e", "S3 pull",         "source default supplier",                   RoutingOutcome.Routed),
        new("4f", "S3 pull",         "default supplier NULL",                     RoutingOutcome.ParkedUnrouted),
        new("4g", "S3 pull",         "default supplier soft-deleted",             RoutingOutcome.ParkedUnrouted),

        new("4h", "IMAP poll",       "source default supplier",                   RoutingOutcome.Routed),
        new("4i", "IMAP poll",       "default supplier NULL",                     RoutingOutcome.ParkedUnrouted),
        new("4j", "IMAP poll",       "default supplier soft-deleted",             RoutingOutcome.ParkedUnrouted),

        new("5a", "assign-supplier", "unrouted order + valid supplier",           RoutingOutcome.Routed,         200),
        new("5b", "assign-supplier", "order already routed (ready)",              RoutingOutcome.Rejected,       409),

        new("6a", "learning",        "assign then re-parse binds the layout",     RoutingOutcome.Routed,         200),
        new("6b", "learning",        "2nd same-layout doc, supplier-less",        RoutingOutcome.ParkedUnrouted),

        new("7a", "ASN / DESADV",    "EDIFACT DESADV upload",                     RoutingOutcome.Rejected,       501),
    };

    public static TheoryData<string> CellIds
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var cell in Matrix) data.Add(cell.Id);
            return data;
        }
    }

    // ── The one assertion every cell shares ─────────────────────────────────────────────

    [DockerRequiredTheory]
    [MemberData(nameof(CellIds))]
    public async Task Order_routes_to_the_supplier_the_matrix_says(string cellId)
    {
        // The matrix IS the test: a theory runs once per ROW, so a row deleted from the table takes
        // its channel's whole routing proof away and the suite reports green — one fewer case simply
        // never runs, and nothing says so. Pinning the size makes a deletion deliberate.
        Matrix.Should().HaveCount(27,
            "the routing story documented at the top of this file is 27 cells; a row quietly dropped "
            + "removes a channel's only routing proof and every remaining case still passes");

        var cell = Matrix.Single(c => c.Id == cellId);
        _output.WriteLine($"▶ {cell.Label} → expected {cell.Outcome}");

        var (actual, expectedSupplierId) = await RunCellAsync(cell);

        _output.WriteLine(
            $"  actual: supplier={actual.SupplierId?.ToString() ?? "NULL"} " +
            $"status={actual.ParkedStatus ?? "(no order)"} http={actual.HttpStatus?.ToString() ?? "n/a"} " +
            $"orders={actual.OrderCount}");

        // BOTH halves are checked, so no cell reaches the outcome switch having examined nothing:
        // an HTTP-fronted cell must answer the status its row declares, and a pull channel has no
        // caller to answer at all — a status appearing there means the cell drove some producer
        // other than the one its row names.
        if (cell.ExpectedHttpStatus is { } expectedHttp)
        {
            actual.HttpStatus.Should().Be(expectedHttp,
                "{0} must answer HTTP {1}", cell.Label, expectedHttp);
        }
        else
        {
            actual.HttpStatus.Should().BeNull(
                "{0} is a pull channel with no HTTP caller, so a status code here means the cell "
                + "exercised a path the matrix does not describe", cell.Label);
        }

        switch (cell.Outcome)
        {
            case RoutingOutcome.Routed:
                actual.OrderCount.Should().Be(1, "{0} must create exactly one order", cell.Label);
                actual.SupplierId.Should().Be(expectedSupplierId,
                    "{0} must route to the supplier the channel resolved, not another one", cell.Label);
                actual.ParkedStatus.Should().NotBe(OrderStatusConstants.Unrouted,
                    "{0} resolved a supplier, so the parse job must not park it for routing", cell.Label);
                break;

            case RoutingOutcome.ParkedUnrouted:
                actual.OrderCount.Should().Be(1,
                    "{0} must PARK the order, never drop it", cell.Label);
                actual.SupplierId.Should().BeNull(
                    "{0} could not determine a supplier, so none may be invented", cell.Label);
                actual.ParkedStatus.Should().Be(OrderStatusConstants.Unrouted,
                    "{0} must park 'unrouted' so assign-supplier can resolve it", cell.Label);
                break;

            case RoutingOutcome.Rejected:
                actual.OrderCount.Should().Be(0,
                    "{0} is refused up front, so no order may be left behind", cell.Label);
                break;

            default:
                // A fourth outcome added to the enum would otherwise give its cells a switch with no
                // matching arm: they would run the channel, assert nothing about where the order
                // landed, and report Passed.
                throw new InvalidOperationException(
                    $"{cell.Label} declares outcome '{cell.Outcome}', which no arm asserts — give it "
                    + "one rather than letting the cell pass having examined nothing.");
        }
    }

    /// <summary>
    /// Dispatches a cell to the real producer for its channel and returns what happened,
    /// together with the supplier that cell expects to be routed to.
    /// </summary>
    private Task<(CellActual Actual, Guid? ExpectedSupplierId)> RunCellAsync(RoutingCell cell) => cell.Id switch
    {
        "1a" => UploadCellAsync(cell, useEmptySupplierId: false, "po.csv",  CsvBytes,  "text/csv"),
        "1b" => UploadCellAsync(cell, useEmptySupplierId: true,  "po.csv",  CsvBytes,  "text/csv"),
        "1c" => UploadCellAsync(cell, useEmptySupplierId: false, "po.xlsx", XlsxBytes, XlsxContentType),

        "2a" => IngressCellAsync(cell, IngressSupplierRef.OwnGuid),
        "2b" => IngressCellAsync(cell, IngressSupplierRef.NameWrongCase),
        "2c" => IngressCellAsync(cell, IngressSupplierRef.UnknownName),
        "2d" => IngressCellAsync(cell, IngressSupplierRef.OtherOrgGuid),

        "3a" => InboundEmailCellAsync(cell, EmailOrgShape.DefaultSupplierConfigured),
        "3b" => InboundEmailCellAsync(cell, EmailOrgShape.TwoSuppliersNoDefault),
        "3c" => InboundEmailCellAsync(cell, EmailOrgShape.NoSupplier),
        "3d" => InboundEmailCellAsync(cell, EmailOrgShape.OnlySoftDeletedSupplier),
        "3e" => InboundEmailCellAsync(cell, EmailOrgShape.NoSupplier, proseOnly: true),

        "4a" => SftpCellAsync(cell, DefaultSupplierShape.Valid,       "po.csv",  CsvBytes),
        "4b" => SftpCellAsync(cell, DefaultSupplierShape.Null,        "po.csv",  CsvBytes),
        "4c" => SftpCellAsync(cell, DefaultSupplierShape.SoftDeleted, "po.csv",  CsvBytes),
        "4d" => SftpCellAsync(cell, DefaultSupplierShape.Null,        "po.xlsx", XlsxBytes),

        "4e" => S3CellAsync(cell, DefaultSupplierShape.Valid),
        "4f" => S3CellAsync(cell, DefaultSupplierShape.Null),
        "4g" => S3CellAsync(cell, DefaultSupplierShape.SoftDeleted),

        "4h" => ImapCellAsync(cell, DefaultSupplierShape.Valid),
        "4i" => ImapCellAsync(cell, DefaultSupplierShape.Null),
        "4j" => ImapCellAsync(cell, DefaultSupplierShape.SoftDeleted),

        "5a" => AssignSupplierCellAsync(cell, startFromUnrouted: true),
        "5b" => AssignSupplierCellAsync(cell, startFromUnrouted: false),

        "6a" => LearningBindsOnAssignCellAsync(cell),
        "6b" => LearningDoesNotAutoRouteCellAsync(cell),

        "7a" => DesadvCellAsync(cell),

        _ => throw new InvalidOperationException($"{cell.Label} has no act — add one or drop the row."),
    };

    private const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // ── Cell 1: manual upload ───────────────────────────────────────────────────────────

    private async Task<(CellActual, Guid?)> UploadCellAsync(
        RoutingCell cell, bool useEmptySupplierId, string fileName, byte[] bytes, string contentType)
    {
        var (orgId, supplierId) = await SeedOrgWithSupplierAsync(cell.Id);
        var recorder = new RoutingRecorder(_fixture.Options!);

        await using var db = NewDb();
        var ctrl = BuildOrdersController(db, orgId, recorder);

        var result = await ctrl.Upload(
            FormFileFor(fileName, bytes, contentType),
            useEmptySupplierId ? Guid.Empty : supplierId,
            CancellationToken.None);

        return (await ObserveAsync(orgId, HttpStatusOf(result)), supplierId);
    }

    // ── Cell 2: REST ingress ────────────────────────────────────────────────────────────

    private enum IngressSupplierRef { OwnGuid, NameWrongCase, UnknownName, OtherOrgGuid }

    private async Task<(CellActual, Guid?)> IngressCellAsync(RoutingCell cell, IngressSupplierRef reference)
    {
        var (orgId, supplierId) = await SeedOrgWithSupplierAsync(cell.Id, supplierName: "Acme Supplies");
        var slug = await SlugOfAsync(orgId);

        var supplierRef = reference switch
        {
            IngressSupplierRef.OwnGuid       => supplierId.ToString(),
            // Mixed case on purpose: the match is Postgres lower() on both sides.
            IngressSupplierRef.NameWrongCase => "aCmE sUpPlIeS",
            IngressSupplierRef.UnknownName   => "Supplier That Does Not Exist",
            IngressSupplierRef.OtherOrgGuid  => (await SeedOrgWithSupplierAsync($"{cell.Id}-other")).SupplierId.ToString(),
            _ => throw new ArgumentOutOfRangeException(nameof(reference)),
        };

        var recorder = new RoutingRecorder(_fixture.Options!);

        await using var db = NewDb();
        var ctrl = BuildIngressController(db, orgId);

        var request = new IngressOrderRequest(
            OrderNumber: $"PO-{cell.Id}",
            OrderDate:   new DateOnly(2026, 7, 26),
            Currency:    "EUR",
            Notes:       null,
            SupplierId:  supplierRef,
            Lines: new[] { new IngressOrderLine("SKU-1", "A widget", 5m, "EA", 9.99m) });

        var result = await ctrl.ReceiveOrder(slug, request, recorder, recorder, CancellationToken.None);

        return (await ObserveAsync(orgId, HttpStatusOf(result)), supplierId);
    }

    // ── Cell 3: inbound email (Postmark webhook) ─────────────────────────────────────────

    private enum EmailOrgShape { DefaultSupplierConfigured, TwoSuppliersNoDefault, NoSupplier, OnlySoftDeletedSupplier }

    private async Task<(CellActual, Guid?)> InboundEmailCellAsync(
        RoutingCell cell, EmailOrgShape shape, bool proseOnly = false)
    {
        var orgId = Guid.NewGuid();
        var slug  = $"routing-{cell.Id}-{orgId:N}";
        var now   = DateTime.UtcNow;

        // Two suppliers, deliberately different ages. Age used to decide the outcome — the router
        // took the oldest when no default was configured. It no longer picks either, so the ages
        // now serve the opposite purpose: cell 3a's configured default is the NEWER supplier, so
        // a resurrected "oldest wins" fallback would route 3a to the wrong one and park 3b.
        var oldestSupplierId = Guid.NewGuid();
        var newerSupplierId  = Guid.NewGuid();
        Guid? expectedSupplierId = null;

        await using (var seed = NewDb())
        {
            var emailConfigJson = "{}";

            switch (shape)
            {
                case EmailOrgShape.DefaultSupplierConfigured:
                    seed.Suppliers.Add(NewSupplier(oldestSupplierId, orgId, "Oldest",     now.AddDays(-30)));
                    seed.Suppliers.Add(NewSupplier(newerSupplierId,  orgId, "Configured", now.AddDays(-1)));
                    // The configured default is the NEWER supplier — if precedence were broken and
                    // the fallback ran, this cell would route to "Oldest" and fail.
                    emailConfigJson = (EmailPollingConfig.Empty with { DefaultSupplierId = newerSupplierId }).ToJson();
                    expectedSupplierId = newerSupplierId;
                    break;

                case EmailOrgShape.TwoSuppliersNoDefault:
                    seed.Suppliers.Add(NewSupplier(oldestSupplierId, orgId, "Oldest", now.AddDays(-30)));
                    seed.Suppliers.Add(NewSupplier(newerSupplierId,  orgId, "Newer",  now.AddDays(-1)));
                    // expectedSupplierId stays null: owning suppliers is not the same as saying
                    // which one an unaddressed email belongs to, so this parks.
                    break;

                case EmailOrgShape.NoSupplier:
                    break;

                case EmailOrgShape.OnlySoftDeletedSupplier:
                    var retired = NewSupplier(Guid.NewGuid(), orgId, "Retired", now.AddDays(-30));
                    retired.DeletedAt = now.AddMinutes(-5);
                    seed.Suppliers.Add(retired);
                    break;
            }

            seed.Organisations.Add(new Organisation
            {
                Id = orgId, ClerkOrgId = $"org_routing_{orgId:N}", Name = $"Routing {cell.Id}",
                Slug = slug, Plan = "operations", AccountStatus = AccountStatusConstants.Active,
                EmailConfigJson = emailConfigJson, CreatedAt = now,
            });
            await seed.SaveChangesAsync();
        }

        var recorder = new RoutingRecorder(_fixture.Options!);

        // A prose-only message carries NO attachment; the body extractor is the only thing that
        // can yield an order (BE #60). With attachments present the extractor is never consulted.
        var payload = proseOnly
            ? new InboundEmailPayload(
                FromEmail: "buyer@example.com",
                ToEmail: $"orders@{slug}.proculink.eu",
                Subject: "PO in the body",
                Attachments: Array.Empty<InboundAttachment>(),
                Body: "Please supply 5 widgets, SKU-1, at 9.99 EUR each.")
            : new InboundEmailPayload(
                FromEmail: "buyer@example.com",
                ToEmail: $"orders@{slug}.proculink.eu",
                Subject: "PO attached",
                Attachments: new[] { new InboundAttachment("po.csv", "text/csv", CsvBytes) });

        await using var db = NewDb();
        var router = new InboundEmailRouter(
            db,
            recorder,
            new CountingParseEnqueuer(),
            proseOnly ? OneOrderBodyExtractor.Instance : NoOrderBodyExtractor.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
            NullLogger<InboundEmailRouter>.Instance);

        var result = await router.RouteAsync(payload, CancellationToken.None);

        // The webhook's contract is a status code: a parked message is 200 so Postmark stops
        // retrying (a non-200 would burn 10 retries and then drop the mail).
        var httpStatus = result.Success ? 200 : 422;

        return (await ObserveAsync(orgId, httpStatus), expectedSupplierId);
    }

    // ── Cell 4: the three pull channels ─────────────────────────────────────────────────

    private enum DefaultSupplierShape { Valid, Null, SoftDeleted }

    private async Task<(CellActual, Guid?)> SftpCellAsync(
        RoutingCell cell, DefaultSupplierShape shape, string fileName, byte[] bytes)
    {
        const string remoteDir = "/incoming";
        var remotePath = $"{remoteDir}/{fileName}";

        var (orgId, expectedSupplierId, configuredSupplierId) = await SeedPullOrgAsync(cell.Id, shape);

        await using (var seed = NewDb())
        {
            seed.Set<SftpIngressConfig>().Add(new SftpIngressConfig
            {
                Id = Guid.NewGuid(), OrgId = orgId, Host = "sftp.example.com", Port = 22, Username = "u",
                EncryptedPassword = Enc().Encrypt("pw"), RemoteDirectory = remoteDir,
                DefaultSupplierId = configuredSupplierId, IsEnabled = true,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var recorder = new RoutingStubRecorder(_fixture.Options!);

        await using var db = NewDb();
        var service = new SftpIngressService(
            db, recorder, new CountingParseEnqueuer(), Enc(),
            new OneFileSftpFactory(remotePath, bytes), AllowPrivateGuard(),
            NullLogger<SftpIngressService>.Instance);

        var imported = await service.PollAsync(orgId, CancellationToken.None);
        imported.Should().Be(1, "{0} must import the one file waiting on the source", cell.Label);

        return (await ObserveAsync(orgId, httpStatus: null), expectedSupplierId);
    }

    private async Task<(CellActual, Guid?)> S3CellAsync(RoutingCell cell, DefaultSupplierShape shape)
    {
        const string bucket = "routing-matrix-bucket";
        const string key    = "incoming/po.csv";

        var (orgId, expectedSupplierId, configuredSupplierId) = await SeedPullOrgAsync(cell.Id, shape);

        await using (var seed = NewDb())
        {
            seed.Set<S3IngressConfig>().Add(new S3IngressConfig
            {
                Id = Guid.NewGuid(), OrgId = orgId, BucketName = bucket, KeyPrefix = string.Empty,
                Region = "eu-west-1", ServiceUrl = null, AccessKeyId = "AKIA",
                EncryptedSecretKey = Enc().Encrypt("secret"), DefaultSupplierId = configuredSupplierId,
                IsEnabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var s3 = new Mock<IAmazonS3>(MockBehavior.Loose);
        s3.Setup(c => c.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new ListObjectsV2Response
          {
              S3Objects = new List<S3Object> { new() { Key = key, ETag = "\"etag-1\"" } },
              IsTruncated = false,
          });
        s3.Setup(c => c.GetObjectAsync(bucket, It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(() => new GetObjectResponse { ResponseStream = new MemoryStream(CsvBytes) });

        var recorder = new RoutingStubRecorder(_fixture.Options!);

        await using var db = NewDb();
        var service = new S3IngressService(
            db, recorder, new CountingParseEnqueuer(), Enc(),
            new SingleS3ClientFactory(s3.Object), AllowPrivateGuard(),
            NullLogger<S3IngressService>.Instance);

        var imported = await service.PollAsync(orgId, CancellationToken.None);
        imported.Should().Be(1, "{0} must import the one object waiting in the bucket", cell.Label);

        return (await ObserveAsync(orgId, httpStatus: null), expectedSupplierId);
    }

    private async Task<(CellActual, Guid?)> ImapCellAsync(RoutingCell cell, DefaultSupplierShape shape)
    {
        var (orgId, expectedSupplierId, configuredSupplierId) = await SeedPullOrgAsync(cell.Id, shape);

        var recorder = new RoutingStubRecorder(_fixture.Options!);

        await using var db = NewDb();
        var job = new EmailPollOrgJob(
            db, Enc(), recorder, new Mock<IBackgroundJobClient>().Object,
            new Mock<IBillingService>().Object, new Mock<IEmailSettingsService>().Object,
            AllowPrivateGuard(), NullLogger<EmailPollOrgJob>.Instance);

        // PollAsync needs a live IMAP server; these are the two steps it performs around it —
        // resolve the source's default supplier, then hand the message to the importer.
        var resolvedSupplierId = await job.ResolveDefaultSupplierIdAsync(
            orgId, configuredSupplierId, CancellationToken.None);

        var processed = await job.ProcessMessageAsync(
            orgId, resolvedSupplierId, MessageWithCsvAttachment($"<{cell.Id}@buyer.example.com>"),
            CancellationToken.None);

        processed.Should().BeTrue("{0} must fully handle the message so it can be flagged SEEN", cell.Label);

        return (await ObserveAsync(orgId, httpStatus: null), expectedSupplierId);
    }

    // ── Cell 5: resolution through assign-supplier ──────────────────────────────────────

    private async Task<(CellActual, Guid?)> AssignSupplierCellAsync(RoutingCell cell, bool startFromUnrouted)
    {
        var (orgId, supplierId) = await SeedOrgWithSupplierAsync(cell.Id);
        var revisionId = await SeedConnectionWithRevisionAsync(orgId, supplierId);

        var orderId = Guid.NewGuid();
        await using (var seed = NewDb())
        {
            seed.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = orderId, OrgId = orgId,
                // An already-routed order is the 409 case: it is not awaiting routing.
                SupplierId = startFromUnrouted ? null : supplierId,
                Status = startFromUnrouted ? OrderStatusConstants.Unrouted : OrderStatusConstants.Ready,
                PoNumber = $"PO-{cell.Id}", Currency = "EUR",
                OrderDate = new DateOnly(2026, 7, 26),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        int httpStatus;
        await using (var db = NewDb())
        {
            var result = await BuildOrdersController(db, orgId, new RoutingRecorder(_fixture.Options!))
                .AssignSupplier(orderId, new AssignSupplierRequest(supplierId), CancellationToken.None);
            httpStatus = HttpStatusOf(result);
        }

        await using var read = NewDb();
        var order = await read.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);

        if (startFromUnrouted)
        {
            order.Status.Should().Be(OrderStatusConstants.Parsing,
                "{0} must claim the order into 'parsing' so the re-parse resolves its lines", cell.Label);
            order.ConnectionRevisionId.Should().Be(revisionId,
                "{0} must pin the supplier's active connection revision, as ingest-time routing does", cell.Label);
        }
        else
        {
            order.Status.Should().Be(OrderStatusConstants.Ready,
                "{0} must leave a non-unrouted order untouched", cell.Label);
        }

        // The assign path leaves the order in 'parsing'; the park predicate then confirms a
        // supplier-bearing order is not parked back for routing.
        var parked = await ApplyParseJobParkAsync(orgId, orderId);
        var orderCount = await OrderCountAsync(orgId);

        // The 409 cell asserts "no order left behind" against an order that legitimately
        // pre-existed, so report it as an untouched non-creation.
        return (new CellActual(order.SupplierId, parked, httpStatus, startFromUnrouted ? orderCount : 0),
                startFromUnrouted ? supplierId : null);
    }

    // ── Cell 6: the learning cell ───────────────────────────────────────────────────────

    /// <summary>
    /// 6a — an operator's correction is the ONLY way a layout ever learns a supplier: the first
    /// parse of an unrouted document binds nothing, and the re-parse after assign-supplier binds
    /// the chosen supplier to the layout.
    ///
    /// <para>KNOWN LIMIT — read this before trusting a green here. Like its sibling
    /// <see cref="SchemaFingerprintLearnsOnAssignPostgresTests"/>, this cell calls
    /// <c>RecordParseSuccessAsync</c> DIRECTLY, so it proves the recorder binds the operator's
    /// choice on re-entry. It does NOT prove the real <c>ParseOrderJob</c> file-backed re-parse
    /// ever reaches that recorder, and the OPS-3 live pass (PR #64, finding F2) measured that it
    /// does not: the file-backed branch leaves Deleted phantom rows, the fingerprint is the next
    /// writer in the scope, its SaveChanges throws <c>DbUpdateConcurrencyException</c>, and
    /// <c>ParseOrderJob.cs:150-153</c> swallows it. So this cell can be green while production
    /// does not learn. It is the unit-level guard, not the end-to-end proof.</para>
    /// </summary>
    private async Task<(CellActual, Guid?)> LearningBindsOnAssignCellAsync(RoutingCell cell)
    {
        var (orgId, supplierId) = await SeedOrgWithSupplierAsync(cell.Id);

        var orderId = Guid.NewGuid();
        await using (var seed = NewDb())
        {
            seed.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = orderId, OrgId = orgId, SupplierId = null,
                Status = OrderStatusConstants.Unrouted, PoNumber = $"PO-{cell.Id}", Currency = "EUR",
                OrderDate = new DateOnly(2026, 7, 26),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        // 1. The parse job fingerprints the layout while the order is still unrouted.
        await using (var db = NewDb())
            await NewFingerprintService(db).RecordParseSuccessAsync(
                orgId, orderId, LayoutHeaders, "csv", CancellationToken.None);

        await using (var check = NewDb())
        {
            var fp = await check.SchemaFingerprints.AsNoTracking().SingleAsync(f => f.OrganisationId == orgId);
            fp.SupplierIdsCsv.Should().BeEmpty(
                "{0} has nothing to learn yet — the order had no supplier on its first parse", cell.Label);
        }

        // 2. The operator resolves the routing through the real endpoint.
        int httpStatus;
        await using (var db = NewDb())
        {
            var result = await BuildOrdersController(db, orgId, new RoutingRecorder(_fixture.Options!))
                .AssignSupplier(orderId, new AssignSupplierRequest(supplierId), CancellationToken.None);
            httpStatus = HttpStatusOf(result);
        }

        // 3. The re-enqueued parse job re-enters the recorder for the same order.
        await using (var db = NewDb())
            await NewFingerprintService(db).RecordParseSuccessAsync(
                orgId, orderId, LayoutHeaders, "csv", CancellationToken.None);

        await using var verify = NewDb();
        var learned = await verify.SchemaFingerprints.AsNoTracking().SingleAsync(f => f.OrganisationId == orgId);
        learned.SupplierIdsCsv.Should().Be(supplierId.ToString(),
            "{0} must bind the supplier the operator picked to this layout", cell.Label);

        var match = await NewFingerprintService(verify).LookupAsync(orgId, LayoutHeaders, CancellationToken.None);
        match.Should().NotBeNull("{0} must be able to recognise the layout again", cell.Label);
        match!.IsBoundTo(supplierId).Should().BeTrue(
            "{0} must record the supplier the operator picked against this layout", cell.Label);
        match.IsSharedLayout.Should().BeFalse(
            "{0} bound exactly one supplier, so the layout is not a collision", cell.Label);

        var parked = await ApplyParseJobParkAsync(orgId, orderId);
        var order = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);

        return (new CellActual(order.SupplierId, parked, httpStatus, await OrderCountAsync(orgId)), supplierId);
    }

    /// <summary>
    /// 6b — THE honest half of the learning story. A second document with the SAME layout,
    /// arriving with no supplier, still parks <c>unrouted</c>.
    ///
    /// <para>Be precise about what the learned binding buys today, because it is LESS than
    /// "suggest": the binding accumulates in <c>SupplierIdsCsv</c> and is readable through
    /// <c>ISchemaFingerprintService.LookupAsync</c> (asserted below), but NO production reader
    /// turns it into a supplier suggestion. The single consumer is
    /// <c>FormatDetectionController.cs:58</c>, and <c>FingerprintBoost.Apply</c> returns
    /// <c>detected with { Confidence, Reasoning, SeenCount }</c> — <c>match.SupplierIds</c> and
    /// <c>SampleSupplierName</c> are dropped, and <c>DetectedFormat.DetectedSupplier</c> is never
    /// populated from the match. The LAYOUT is recognised; the supplier is not offered.</para>
    ///
    /// <para>So this cell asserts the two things that ARE true — the order stays unrouted, and the
    /// binding exists at the service — and it fails the day anything starts auto-binding, which is
    /// exactly when a human must review that choice.</para>
    ///
    /// <para>Reachability note: this park no longer needs an org with no usable supplier. Inbound
    /// email used to fall back to the OLDEST active supplier, so an org holding at least one
    /// supplier could never reach it; that fallback was deleted 2026-07-26 (F1 of the routing-matrix
    /// live proof) and cell 3b now parks with two active suppliers present. The cell still arranges
    /// a supplier-less arrival explicitly, because what it is pinning is that the LEARNED binding
    /// does not auto-route — not how the order came to be unrouted.</para>
    /// </summary>
    private async Task<(CellActual, Guid?)> LearningDoesNotAutoRouteCellAsync(RoutingCell cell)
    {
        var (orgId, supplierId) = await SeedOrgWithSupplierAsync(cell.Id);

        // Arrange the learned state directly: the layout is already bound to a supplier by an
        // earlier correction (cell 6a proves that binding is real).
        var firstOrderId = Guid.NewGuid();
        await using (var seed = NewDb())
        {
            seed.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = firstOrderId, OrgId = orgId, SupplierId = supplierId,
                Status = OrderStatusConstants.PendingReview, PoNumber = $"PO-{cell.Id}-1", Currency = "EUR",
                OrderDate = new DateOnly(2026, 7, 26),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using (var db = NewDb())
            await NewFingerprintService(db).RecordParseSuccessAsync(
                orgId, firstOrderId, LayoutHeaders, "csv", CancellationToken.None);

        await using (var check = NewDb())
            (await check.SchemaFingerprints.AsNoTracking().SingleAsync(f => f.OrganisationId == orgId))
                .SupplierIdsCsv.Should().Be(supplierId.ToString(),
                    "{0} needs the layout already bound before the second document arrives", cell.Label);

        // A SECOND document, same layout, arriving supplier-less on a content-routed channel.
        var recorder = new RoutingRecorder(_fixture.Options!);
        var secondOrder = await recorder.CreateUnroutedStubAsync(
            orgId, new MemoryStream(CsvBytes), "po-again.csv", "text/csv", CancellationToken.None);
        secondOrder.IsSuccess.Should().BeTrue("{0} must be able to import the repeat document", cell.Label);

        var secondOrderId = secondOrder.Value!.Id;
        await using (var db = NewDb())
            await NewFingerprintService(db).RecordParseSuccessAsync(
                orgId, secondOrderId, LayoutHeaders, "csv", CancellationToken.None);

        var parked = await ApplyParseJobParkAsync(orgId, secondOrderId);

        await using var verify = NewDb();
        var reloaded = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == secondOrderId);

        // The binding IS readable at the service. Note what this does NOT say: no production
        // reader turns it into a supplier suggestion (FingerprintBoost.Apply drops SupplierIds).
        var match = await NewFingerprintService(verify).LookupAsync(orgId, LayoutHeaders, CancellationToken.None);
        match!.IsBoundTo(supplierId).Should().BeTrue(
            "{0} must still hold the layout's learned supplier at the service level", cell.Label);
        match.SeenCount.Should().BeGreaterThan(1,
            "{0} is the layout's second sighting, which is what raises detection confidence", cell.Label);

        // ...and it is only a suggestion. Auto-assign is NOT the shipped contract.
        reloaded.SupplierId.Should().BeNull(
            "{0} must NOT auto-bind from the fingerprint — v1 suggests, a human decides", cell.Label);

        // Only the second order is the cell's subject; the first is arranged history.
        return (new CellActual(reloaded.SupplierId, parked, null, OrderCount: 1), null);
    }

    // ── Cell 7: the untested format stays refused ───────────────────────────────────────

    /// <summary>
    /// Routing must never be the reason a document is accepted. ASN / EDIFACT DESADV has no
    /// parser (offer ⇔ works), so it is refused 501 with a supplier supplied — the honest answer
    /// rather than accepting and silently shelving a file that can never be parsed.
    /// </summary>
    private async Task<(CellActual, Guid?)> DesadvCellAsync(RoutingCell cell)
    {
        var (orgId, supplierId) = await SeedOrgWithSupplierAsync(cell.Id);

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var ctrl = new DesadvController(new Mock<IDesadvService>().Object, tenant.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = ctrl.Upload(FormFileFor("asn.edi", CsvBytes, "application/edifact"), supplierId);

        return (await ObserveAsync(orgId, HttpStatusOf(result)), null);
    }

    // ── Observation ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads back the single order the cell's org owns (if any) and applies the parse job's park,
    /// so every cell reports the same two columns: which supplier, and which status.
    /// </summary>
    private async Task<CellActual> ObserveAsync(Guid orgId, int? httpStatus)
    {
        await using var read = NewDb();
        var orders = await read.PurchaseOrders.AsNoTracking()
            .Where(o => o.OrgId == orgId)
            .Select(o => new { o.Id, o.SupplierId })
            .ToListAsync();

        if (orders.Count == 0)
            return new CellActual(null, null, httpStatus, 0);

        // More than one order is itself a routing failure; report it and let the cell's
        // OrderCount assertion name it.
        var order = orders[0];
        var parked = await ApplyParseJobParkAsync(orgId, order.Id);
        return new CellActual(order.SupplierId, parked, httpStatus, orders.Count);
    }

    /// <summary>
    /// Applies the parse job's park to a freshly created stub and returns the resulting status.
    /// This mirrors the single writer of <c>unrouted</c> — <c>OrderIngestionService</c>'s
    /// <c>if (entity.SupplierId is null) newStatus = Unrouted</c> — including its atomic
    /// still-in-'parsing' claim. Driving the real parse would need storage plus format parsers;
    /// what this pins is that each channel's row accepts exactly that transition, and its
    /// persistence is owned by <see cref="UnroutedOrderNullSupplierPersistencePostgresTests"/>.
    /// The routed branch's terminal status (review vs ready) is a line-resolution outcome, not a
    /// routing one, so cells only assert that a routed order is NOT parked.
    /// </summary>
    private async Task<string> ApplyParseJobParkAsync(Guid orgId, Guid orderId)
    {
        await using var db = NewDb();
        var order = await db.PurchaseOrders.AsNoTracking()
            .SingleAsync(o => o.Id == orderId && o.OrgId == orgId);

        if (order.Status != OrderStatusConstants.Parsing)
            return order.Status;   // already resolved (e.g. the 409 cell's untouched 'ready')

        var newStatus = order.SupplierId is null
            ? OrderStatusConstants.Unrouted
            : OrderStatusConstants.PendingReview;

        await db.PurchaseOrders
            .Where(o => o.Id == orderId && o.OrgId == orgId && o.Status == OrderStatusConstants.Parsing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, newStatus)
                .SetProperty(o => o.UpdatedAt, DateTime.UtcNow));

        return newStatus;
    }

    private async Task<int> OrderCountAsync(Guid orgId)
    {
        await using var db = NewDb();
        return await db.PurchaseOrders.CountAsync(o => o.OrgId == orgId);
    }

    private static int HttpStatusOf(IActionResult result) => result switch
    {
        OkObjectResult                => 200,
        OkResult                      => 200,
        BadRequestObjectResult        => 400,
        BadRequestResult              => 400,
        NotFoundObjectResult          => 404,
        NotFoundResult                => 404,
        ConflictObjectResult          => 409,
        ObjectResult o                => o.StatusCode ?? 0,
        StatusCodeResult s            => s.StatusCode,
        _                             => 0,
    };

    // ── Seeding ─────────────────────────────────────────────────────────────────────────

    private async Task<(Guid OrgId, Guid SupplierId)> SeedOrgWithSupplierAsync(
        string cellId, string supplierName = "Acme")
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = NewDb();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_routing_{orgId:N}", Name = $"Routing {cellId}",
            Slug = $"routing-{cellId}-{orgId:N}", Plan = "operations",
            AccountStatus = AccountStatusConstants.Active, EmailConfigJson = "{}", CreatedAt = now,
        });
        db.Suppliers.Add(NewSupplier(supplierId, orgId, supplierName, now));
        await db.SaveChangesAsync();
        return (orgId, supplierId);
    }

    /// <summary>
    /// Seeds a pull-ingress org and returns the supplier the channel should route to
    /// (null for the park cells) alongside the id to CONFIGURE on the source: absent for
    /// <see cref="DefaultSupplierShape.Null"/>, and a soft-deleted supplier's id for
    /// <see cref="DefaultSupplierShape.SoftDeleted"/> — the operator removed the destination
    /// after ingress was enabled, and the file must park rather than revive it.
    /// </summary>
    private async Task<(Guid OrgId, Guid? ExpectedSupplierId, Guid? ConfiguredSupplierId)> SeedPullOrgAsync(
        string cellId, DefaultSupplierShape shape)
    {
        var (orgId, supplierId) = await SeedOrgWithSupplierAsync(cellId);

        switch (shape)
        {
            case DefaultSupplierShape.Valid:
                return (orgId, supplierId, supplierId);

            case DefaultSupplierShape.Null:
                return (orgId, null, null);

            case DefaultSupplierShape.SoftDeleted:
                await using (var db = NewDb())
                {
                    var supplier = await db.Suppliers.SingleAsync(s => s.Id == supplierId);
                    supplier.DeletedAt = DateTime.UtcNow.AddMinutes(-5);
                    await db.SaveChangesAsync();
                }
                return (orgId, null, supplierId);

            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }
    }

    private async Task<Guid> SeedConnectionWithRevisionAsync(Guid orgId, Guid supplierId)
    {
        var now = DateTime.UtcNow;
        var connectionId = Guid.NewGuid();
        var revision = new SupplierConnectionRevision
        {
            Id = Guid.NewGuid(), ConnectionId = connectionId, OrgId = orgId, SupplierId = supplierId,
            VersionNo = 1, Status = "published", CreatedAt = now, PublishedAt = now,
        };

        await using var db = NewDb();

        // Three writes, not one: connection.active_revision_id and revision.connection_id point at
        // each other, and real Postgres rejects that pair inserted together (EF cannot order a
        // cycle — "circular dependency was detected"). Insert the connection unpinned, then the
        // revision, then pin it. InMemory accepts the single-write form, which is why the
        // InMemory-based revision-authority tests can get away with it.
        var connection = new SupplierConnection
        {
            Id = connectionId, OrgId = orgId, SupplierId = supplierId, Name = "conn",
            ActiveRevisionId = null, CreatedAt = now, UpdatedAt = now,
        };
        db.SupplierConnections.Add(connection);
        await db.SaveChangesAsync();

        db.SupplierConnectionRevisions.Add(revision);
        await db.SaveChangesAsync();

        connection.ActiveRevisionId = revision.Id;
        await db.SaveChangesAsync();

        return revision.Id;
    }

    private static Supplier NewSupplier(Guid id, Guid orgId, string name, DateTime createdAt) =>
        new() { Id = id, OrgId = orgId, Name = name, CreatedAt = createdAt };

    private async Task<string> SlugOfAsync(Guid orgId)
    {
        await using var db = NewDb();
        return await db.Organisations.AsNoTracking().Where(o => o.Id == orgId).Select(o => o.Slug).SingleAsync();
    }

    // ── Producer construction ───────────────────────────────────────────────────────────

    private static OrdersController BuildOrdersController(
        ProcuLinkDbContext db, Guid orgId, IOrderService orders)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.CheckOrderLimitAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new LimitCheckResult(Allowed: true, PilotExpired: false, Plan: "operations", Limit: 1000));

        var idempotency = new Mock<IIdempotencyService>();
        idempotency.Setup(i => i.TryGetExistingOrderIdAsync(
                       It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync((Guid?)null);

        return new OrdersController(
            orders,
            tenant.Object,
            new Mock<IBackgroundJobClient>().Object,
            db,
            NullLogger<OrdersController>.Instance,
            billing.Object,
            idempotency.Object,
            new Mock<IOrderExceptionService>().Object,
            new Mock<ISupplierAcceptanceService>().Object,
            new Mock<IOrderMappingOverrideService>().Object,
            new PromoteMappingService(db, new PoMappingService(db)),
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            Array.Empty<ITransformService>())
        {
            // Upload and ReceiveOrder both read the Idempotency-Key header off Request.
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static IngressController BuildIngressController(ProcuLinkDbContext db, Guid orgId)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        // The REAL idempotency service, not a mock: this suite runs on real Postgres, and the
        // controller's claim-first path (WP-22) needs an actual (org_id, key) row to claim. A mock
        // would have to fake ClaimAsync, which is precisely the behaviour these cells depend on.
        return new IngressController(
            db, new IdempotencyService(db), tenant.Object, TestDoubles.PermissiveBilling.Service(), NullLogger<IngressController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static SchemaFingerprintService NewFingerprintService(ProcuLinkDbContext db) =>
        new(db, NullLogger<SchemaFingerprintService>.Instance);

    private static DeliveryEncryptionService Enc()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            { ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]) })
            .Build();
        return new DeliveryEncryptionService(cfg);
    }

    private static OutboundRequestGuard AllowPrivateGuard() =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                { ["Delivery:AllowPrivateNetworkTargets"] = "true" })
                .Build(),
            NullLogger<OutboundRequestGuard>.Instance);

    private static IFormFile FormFileFor(string fileName, byte[] bytes, string contentType) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };

    private static MimeMessage MessageWithCsvAttachment(string messageId)
    {
        var msg = new MimeMessage { MessageId = messageId };
        msg.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        msg.To.Add(new MailboxAddress("Receiver", "po@buyer.example.com"));
        msg.Subject = "PO attached";

        var attachment = new MimePart("text", "csv")
        {
            Content = new MimeContent(new MemoryStream(CsvBytes)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = "po.csv",
        };
        msg.Body = new Multipart("mixed") { new TextPart("plain") { Text = "See attached." }, attachment };
        return msg;
    }

    // ── Doubles ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="IOrderService"/> recorder that self-commits the order row to the real database
    /// carrying EXACTLY the supplier the producer passed — a supplier id for the routed entry
    /// points, NULL for the unrouted siblings — both at status <c>parsing</c>, as
    /// <c>OrderIngestionService.CreateStubAsync</c> writes them.
    ///
    /// <para>The supplier is the assertion surface of this whole suite, which is why the existing
    /// <see cref="CountingOrderService"/> cannot be reused here: it writes
    /// <c>SupplierId = null</c> unconditionally (correct for the claim-first suites, which count
    /// orders), and every routed cell would pass vacuously against it. Members the routing paths
    /// never touch throw, so a silent extra call fails the cell instead of passing unnoticed.</para>
    /// </summary>
    private sealed class RoutingRecorder : IOrderService, IClaimedOrderCreator
    {
        private readonly DbContextOptions<ProcuLinkDbContext> _options;

        public RoutingRecorder(DbContextOptions<ProcuLinkDbContext> options) => _options = options;

        // ── IClaimedOrderCreator: the push channels create under a pre-generated id (WP-22
        // claim-first dedupe). The supplier recorded is still the assertion surface; honouring the
        // supplied id keeps the double consistent with the ledger claim that points at it.

        public Task<Result<PurchaseOrderEntity>> CreateClaimedStubAsync(
            Guid organisationId, Guid? supplierId, Guid orderId, Stream fileStream, string filename,
            string contentType, string? inboundSenderDomain, CancellationToken ct)
            => PersistAsync(organisationId, supplierId, filename, ct, orderId);

        public Task<Result<PurchaseOrderEntity>> CreateClaimedFromParsedOrderAsync(
            Guid organisationId, Guid? supplierId, Guid orderId, ExtractedOrder order, string source,
            string? inboundSenderDomain, CancellationToken ct)
            => PersistAsync(organisationId, supplierId, source, ct, orderId);

        public Task<Result<PurchaseOrderEntity>> CreateStubAsync(
            Guid organisationId, Guid supplierId, Stream fileStream, string filename, string contentType,
            CancellationToken ct, string? inboundSenderDomain = null)
            => PersistAsync(organisationId, supplierId, filename, ct);

        public Task<Result<PurchaseOrderEntity>> CreateUnroutedStubAsync(
            Guid organisationId, Stream fileStream, string filename, string contentType, CancellationToken ct,
            string? inboundSenderDomain = null)
            => PersistAsync(organisationId, supplierId: null, filename, ct);

        public Task<Result<PurchaseOrderEntity>> CreateStubFromParsedOrderAsync(
            Guid organisationId, Guid supplierId, ExtractedOrder order, string source, CancellationToken ct,
            string? inboundSenderDomain = null)
            => PersistAsync(organisationId, supplierId, source, ct);

        public Task<Result<PurchaseOrderEntity>> CreateUnroutedStubFromParsedOrderAsync(
            Guid organisationId, ExtractedOrder order, string source, CancellationToken ct,
            string? inboundSenderDomain = null)
            => PersistAsync(organisationId, supplierId: null, source, ct);

        private async Task<Result<PurchaseOrderEntity>> PersistAsync(
            Guid orgId, Guid? supplierId, string filename, CancellationToken ct, Guid? orderId = null)
        {
            var now = DateTime.UtcNow;
            var order = new PurchaseOrderEntity
            {
                Id = orderId is { } claimed && claimed != Guid.Empty ? claimed : Guid.NewGuid(),
                OrgId = orgId,
                SupplierId = supplierId,
                PoNumber = "PO-STUB",
                Currency = "EUR",
                Status = OrderStatusConstants.Parsing,
                OrderDate = DateOnly.FromDateTime(now),
                SourceFileKey = $"{orgId}/{Guid.NewGuid()}/{filename}",
                CreatedAt = now,
                UpdatedAt = now,
            };

            await using var db = new ProcuLinkDbContext(_options);
            db.PurchaseOrders.Add(order);
            await db.SaveChangesAsync(ct);

            return Result<PurchaseOrderEntity>.Ok(order);
        }

        public Task<Result<PurchaseOrderEntity>> CreateFromFileAsync(Guid organisationId, Guid supplierId, Stream fileStream, string filename, string contentType, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<ParsedFileOutput>> ParseStoredFileAsync(Guid organisationId, Guid orderId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> GetByIdAsync(Guid organisationId, Guid orderId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<IReadOnlyList<PurchaseOrderSummary>>> ListAsync(Guid organisationId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<(IReadOnlyList<PurchaseOrderSummary> Items, int TotalCount)>> ListPagedAsync(Guid organisationId, int page, int pageSize, string? status, Guid? supplierId, string? search, DateTime? dateFrom, DateTime? dateTo, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<(IReadOnlyList<PurchaseOrderSummary> Items, int TotalCount)>> ListWindowAsync(Guid organisationId, int skip, int take, string? status, Guid? supplierId, string? search, DateTime? dateFrom, DateTime? dateTo, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<TransformResponse>> TransformAsync(Guid organisationId, Guid orderId, OutputFormat format, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<DownloadUrl>> GetDownloadUrlAsync(Guid organisationId, Guid orderId, Guid artifactId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> ResolveAsync(Guid organisationId, Guid orderId, IReadOnlyList<ProcuLink.Core.Services.LineResolution> resolutions, bool saveMappings, CancellationToken ct, ResolveHeaderFields? header = null)
            => throw new NotImplementedException();
        public Task<Result<int>> AcceptAiSuggestionsAsync(Guid organisationId, Guid orderId, double minConfidence, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> MarkRejectedAsync(Guid organisationId, Guid orderId, string reason, CancellationToken ct)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// The pull channels' <see cref="IStubOrderCreator"/> twin of <see cref="RoutingRecorder"/>:
    /// same faithful persistence of whichever supplier the ingress resolved, under the
    /// caller-supplied primary key (find-or-create on it, like the real service).
    /// </summary>
    private sealed class RoutingStubRecorder : IStubOrderCreator
    {
        private readonly DbContextOptions<ProcuLinkDbContext> _options;

        public RoutingStubRecorder(DbContextOptions<ProcuLinkDbContext> options) => _options = options;

        public Task<Result<PurchaseOrderEntity>> CreateStubAsync(
            Guid organisationId, Guid supplierId, Guid orderId, Stream fileStream, string filename, string contentType, CancellationToken ct)
            => PersistAsync(organisationId, supplierId, orderId, filename, ct);

        public Task<Result<PurchaseOrderEntity>> CreateUnroutedStubAsync(
            Guid organisationId, Guid orderId, Stream fileStream, string filename, string contentType, CancellationToken ct)
            => PersistAsync(organisationId, supplierId: null, orderId, filename, ct);

        private async Task<Result<PurchaseOrderEntity>> PersistAsync(
            Guid orgId, Guid? supplierId, Guid orderId, string filename, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            await using var db = new ProcuLinkDbContext(_options);

            if (!await db.PurchaseOrders.AnyAsync(o => o.Id == orderId, ct))
            {
                db.PurchaseOrders.Add(new PurchaseOrderEntity
                {
                    Id = orderId, OrgId = orgId, SupplierId = supplierId,
                    PoNumber = "PO-STUB", Currency = "EUR", Status = OrderStatusConstants.Parsing,
                    OrderDate = DateOnly.FromDateTime(now),
                    SourceFileKey = $"{orgId}/{orderId}/{filename}",
                    CreatedAt = now, UpdatedAt = now,
                });
                await db.SaveChangesAsync(ct);
            }

            return Result<PurchaseOrderEntity>.Ok(new PurchaseOrderEntity
            {
                Id = orderId, OrgId = orgId, SupplierId = supplierId, Status = OrderStatusConstants.Parsing,
                PoNumber = "PO-STUB", Currency = "EUR", CreatedAt = now, UpdatedAt = now,
            });
        }
    }

    /// <summary>Body extractor that never yields an order — the attachment cells must not reach it.</summary>
    private sealed class NoOrderBodyExtractor : IEmailBodyOrderExtractor
    {
        public static readonly NoOrderBodyExtractor Instance = new();

        public Task<EmailBodyExtractionResult> ExtractAsync(string emailBody, CancellationToken ct)
            => Task.FromResult(new EmailBodyExtractionResult(
                Success: false, Confidence: 0, Order: null, FailureReason: "attachment cell — body never read"));
    }

    /// <summary>
    /// Body extractor that yields one order and NO supplier name, standing in for the OpenAI
    /// extractor on the prose-only path (BE #60). The point of the cell is that a body-extracted
    /// order with no resolvable supplier parks instead of being dropped.
    /// </summary>
    private sealed class OneOrderBodyExtractor : IEmailBodyOrderExtractor
    {
        public static readonly OneOrderBodyExtractor Instance = new();

        public Task<EmailBodyExtractionResult> ExtractAsync(string emailBody, CancellationToken ct)
            => Task.FromResult(new EmailBodyExtractionResult(
                Success: true,
                Confidence: 0.9,
                Order: new ExtractedOrder(
                    PoNumber: "PO-PROSE-1",
                    OrderDate: new DateTime(2026, 7, 26),
                    BuyerName: "Buyer Ltd",
                    Currency: "EUR",
                    Lines: new[] { new ExtractedOrderLine(1, "SKU-1", "A widget", 5m, "EA", 9.99m) }),
                FailureReason: null));
    }

    /// <summary>Hands every caller the same pre-built SFTP session over one file.</summary>
    private sealed class OneFileSftpFactory : ISftpClientFactory
    {
        private readonly string _remotePath;
        private readonly byte[] _content;

        public OneFileSftpFactory(string remotePath, byte[] content)
        {
            _remotePath = remotePath;
            _content = content;
        }

        public ISftpSession Connect(string host, int port, string username, string password)
            => new OneFileSftpSession(_remotePath, _content);
    }

    private sealed class OneFileSftpSession : ISftpSession
    {
        private readonly string _remotePath;
        private readonly byte[] _content;

        public OneFileSftpSession(string remotePath, byte[] content)
        {
            _remotePath = remotePath;
            _content = content;
        }

        public IEnumerable<string> ListFileNames(string remoteDirectory) => new[] { _remotePath };
        public MemoryStream DownloadFile(string remotePath) => new(_content);
        public Stream OpenRead(string remotePath) => new MemoryStream(_content);
        public void Dispose() { }
    }

    /// <summary>Hands every caller the same pre-built <see cref="IAmazonS3"/>.</summary>
    private sealed class SingleS3ClientFactory : IAmazonS3ClientFactory
    {
        private readonly IAmazonS3 _client;

        public SingleS3ClientFactory(IAmazonS3 client) => _client = client;

        public IAmazonS3 Create(string accessKeyId, string secretAccessKey, string region, string? serviceUrl)
            => _client;
    }
}
