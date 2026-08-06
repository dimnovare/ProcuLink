using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

// ════════════════════════════════════════════════════════════════════════════
//  ONE serious end-to-end test of the core product loop:
//
//      upload PO → parse → validate → map → transform → deliver (HTTP) →
//      record DeliveryAttempt → PO Passport shows the proof.
//
//  Why this shape:
//   • The production pipeline is ASYNC via three Hangfire jobs
//     (ParseOrderJob → TransformOrderJob → DeliverOrderJob). There is no
//     Hangfire server in a test, so we DRIVE the pipeline deterministically by
//     invoking the SAME service methods those jobs call, in the same order.
//   • OrderService.ParseStoredFileAsync uses ExecuteUpdateAsync, which the EF
//     InMemory provider cannot translate. So we run against a REAL throwaway
//     Postgres spun up by Testcontainers (fully isolated — never proculink_dev).
//     If Docker is unavailable the whole class is skipped (see the Docker guard
//     in InitializeAsync + SkipUnlessDockerAttribute) so the suite stays green.
//   • Delivery uses the REAL DeliveryService. Only the leaf HTTP dispatcher is
//     faked (CapturingHttpDispatcher, Protocol = "http") so we capture the bytes
//     actually delivered and return 200 — every status transition, the
//     DeliveryAttempt row, the ACK timestamp, and the integration trigger all run
//     for real. File storage is an in-memory IFileStorageService so the artifact
//     round-trips through the real upload→download path. AI mapping is the real
//     no-op service (no API key configured).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// In-memory <see cref="IFileStorageService"/>. Real upload/download semantics —
/// the artifact written by TransformAsync is the exact byte buffer the delivery
/// path downloads and hands to the dispatcher.
/// </summary>
public sealed class InMemoryFileStorage : IFileStorageService
{
    private readonly Dictionary<string, byte[]> _store = new();
    private readonly object _gate = new();

    public Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        content.CopyTo(ms);
        lock (_gate) { _store[key] = ms.ToArray(); }
        return Task.FromResult(key);
    }

    public Task<Stream> DownloadAsync(string key, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_store.TryGetValue(key, out var bytes))
                throw new FileNotFoundException($"No object stored under key '{key}'.");
            return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
    }

    public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) =>
        Task.FromResult($"https://test-storage.local/{key}");

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        lock (_gate) { _store.Remove(key); }
        return Task.CompletedTask;
    }

    public bool TryGet(string key, out byte[] bytes)
    {
        lock (_gate) { return _store.TryGetValue(key, out bytes!); }
    }
}

/// <summary>
/// Fake HTTP delivery dispatcher (Protocol = "http"). Captures the payload the
/// real <see cref="DeliveryService"/> hands it and returns a 200 success, exactly
/// as a happy-path supplier endpoint would. Everything upstream of the wire stays
/// real.
/// </summary>
public sealed class CapturingHttpDispatcher : IDeliveryDispatcher
{
    public string Protocol => "http";

    public byte[]? CapturedContent { get; private set; }
    public string? CapturedFileName { get; private set; }
    public string? CapturedContentType { get; private set; }
    public string? CapturedDestinationConfig { get; private set; }
    public int CallCount { get; private set; }

    public Task<DeliveryResult> DispatchAsync(
        byte[] content,
        string fileName,
        string contentType,
        SupplierDeliveryConfig config,
        string decryptedCredentials,
        CancellationToken ct, string? idempotencyKey = null)
    {
        CallCount++;
        CapturedContent = content;
        CapturedFileName = fileName;
        CapturedContentType = contentType;
        CapturedDestinationConfig = config.ConfigJson;

        // 200 OK with a small ACK body — mirrors a supplier that accepted the PO.
        return Task.FromResult(new DeliveryResult(
            Success: true,
            ErrorMessage: null,
            ResponseCode: 200,
            ResponseBody: "{\"status\":\"accepted\"}"));
    }

    public string CapturedText =>
        CapturedContent is null ? string.Empty : Encoding.UTF8.GetString(CapturedContent);
}

/// <summary>
/// No-op <see cref="IIntegrationTriggerService"/>. The real implementation queries
/// the scoped DbContext, and both OrderService and DeliveryService invoke it
/// FIRE-AND-FORGET (<c>_ = _integrationTrigger.EnqueueAsync(...)</c>). In this test
/// each pipeline step runs in its own DI scope that is disposed immediately after
/// the awaited call returns — so an in-flight fire-and-forget query would race the
/// DbContext disposal and corrupt the Npgsql connection. Wave-4 trigger firing is
/// orthogonal to the core parse→transform→deliver loop under test (no subscriptions
/// are configured), so a no-op is behaviourally identical here and removes the race.
/// </summary>
public sealed class NoopIntegrationTriggerService : IIntegrationTriggerService
{
    public Task EnqueueAsync(Guid organisationId, string eventType, object payload, CancellationToken ct)
        => Task.CompletedTask;
}

// ── Test auth handler (clones the proven TenancyIsolationTests pattern) ───────

public sealed class E2ETestAuthOptions : AuthenticationSchemeOptions
{
    public string OrgId { get; set; } = "org_E2E";
    public string OrgSlug { get; set; } = "org-e2e";
    public string Sub { get; set; } = "user_E2E";
}

public sealed class E2ETestAuthHandler : AuthenticationHandler<E2ETestAuthOptions>
{
    public E2ETestAuthHandler(
        IOptionsMonitor<E2ETestAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim("sub", Options.Sub),
            new Claim("org_id", Options.OrgId),
            new Claim("org_slug", Options.OrgSlug),
            new Claim("azp", "http://localhost:3000"),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}

/// <summary>
/// WebApplicationFactory wired for a REAL Postgres (Testcontainers) end-to-end run:
///  - Npgsql points at the throwaway container.
///  - Hangfire → in-memory (no Postgres job storage, no background server).
///  - Auth → single controllable test scheme.
///  - IFileStorageService → in-memory fake.
///  - A CapturingHttpDispatcher is appended to the IDeliveryDispatcher set so the
///    real DeliveryService resolves protocol "http" to it.
/// </summary>
public sealed class E2EPipelineFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public InMemoryFileStorage FileStorage { get; } = new();
    public CapturingHttpDispatcher HttpDispatcher { get; } = new();

    public E2EPipelineFactory(string connectionString) => _connectionString = connectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            const string testEncKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAE=";
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _connectionString,
                ["Delivery:EncryptionKey"] = testEncKey,
                ["Clerk:Authority"] = "https://test.clerk.accounts.dev",
                ["Sentry:Dsn"] = "",
                ["Storage:R2AccessKeyId"] = "",
                ["Frontend:Url"] = "http://localhost:3000",
                // No "Ai:OpenAI:ApiKey" → the real AI mapping service no-ops.
            });
        });

        builder.ConfigureServices(services =>
        {
            // ── Hangfire Postgres → InMemory (no background job server needed) ──
            var hangfireDescriptors = services
                .Where(d => d.ServiceType.FullName?.StartsWith("Hangfire") == true)
                .ToList();
            foreach (var d in hangfireDescriptors)
                services.Remove(d);

            services.AddHangfire(cfg => cfg
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseInMemoryStorage());

            // ── Auth → single test scheme ──────────────────────────────────────
            var authDescriptors = services
                .Where(d => d.ServiceType.FullName?.Contains("Authentication") == true ||
                            d.ServiceType.FullName?.Contains("JwtBearer") == true)
                .ToList();
            foreach (var d in authDescriptors)
                services.Remove(d);

            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            })
            .AddScheme<E2ETestAuthOptions, E2ETestAuthHandler>("Test", _ => { });

            // ── File storage → in-memory fake (shared instance) ────────────────
            var storageDescriptors = services
                .Where(d => d.ServiceType == typeof(IFileStorageService))
                .ToList();
            foreach (var d in storageDescriptors)
                services.Remove(d);
            services.AddSingleton<IFileStorageService>(FileStorage);

            // ── Delivery dispatcher → append capturing HTTP dispatcher ─────────
            // DeliveryService consumes IEnumerable<IDeliveryDispatcher> and keys by
            // Protocol. Registering ours (singleton, shared instance) makes the real
            // DeliveryService route protocol "http" deliveries through it. If a real
            // HttpDeliveryDispatcher is also registered, ToDictionary throws on the
            // duplicate key — so remove any existing "http" dispatcher first.
            var existingHttp = services
                .Where(d => d.ServiceType == typeof(IDeliveryDispatcher)
                         && d.ImplementationType?.Name == "HttpDeliveryDispatcher")
                .ToList();
            foreach (var d in existingHttp)
                services.Remove(d);
            services.AddSingleton<IDeliveryDispatcher>(HttpDispatcher);

            // ── Integration trigger → no-op (avoid fire-and-forget DbContext race) ─
            var triggerDescriptors = services
                .Where(d => d.ServiceType == typeof(IIntegrationTriggerService))
                .ToList();
            foreach (var d in triggerDescriptors)
                services.Remove(d);
            services.AddSingleton<IIntegrationTriggerService, NoopIntegrationTriggerService>();
        });
    }

    public IServiceScope NewScope() => Services.CreateScope();
}

/// <summary>
/// One-time Docker reachability probe. xUnit v2 has no dynamic <c>Assert.Skip</c>,
/// so a custom <see cref="DockerRequiredFactAttribute"/> reads this and statically
/// marks the test skipped when Docker is unreachable — keeping the suite green on
/// machines/CI without Docker instead of erroring on container start.
/// Availability requires an actual engine response (non-empty server version), not
/// just a zero exit code — a wedged Docker Desktop engine can exit 0 while every
/// container start would fail.
/// </summary>
internal static class DockerProbe
{
    public static readonly string? UnavailableReason = Detect();

    private static string? Detect()
    {
        try
        {
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "info --format \"{{.ServerVersion}}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null)
                return "Docker CLI could not be started.";

            // Drain both pipes concurrently so WaitForExit cannot deadlock on a full buffer.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(15_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return "Docker probe timed out.";
            }

            if (proc.ExitCode != 0)
                return $"`docker info` exited {proc.ExitCode} — Docker daemon not reachable.";

            // A wedged Docker Desktop engine can exit 0 with no server version, so an
            // exit-code check alone reports "available" and every container start fails.
            var serverVersion = stdoutTask.GetAwaiter().GetResult().Trim();
            if (serverVersion.Length == 0)
            {
                var stderr = stderrTask.GetAwaiter().GetResult().Trim();
                return stderr.Length > 0
                    ? $"`docker info` returned no server version — engine not responding ({stderr})."
                    : "`docker info` returned no server version — engine not responding.";
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"Docker CLI unavailable: {ex.Message}";
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that statically skips when Docker is unreachable.
/// </summary>
public sealed class DockerRequiredFactAttribute : FactAttribute
{
    public DockerRequiredFactAttribute()
    {
        if (DockerProbe.UnavailableReason is { } reason)
            Skip = $"Requires Docker/Testcontainers — {reason}";
    }
}

/// <summary>
/// <see cref="DockerRequiredFactAttribute"/>'s Theory twin — statically skips every case when
/// Docker is unreachable.
/// </summary>
public sealed class DockerRequiredTheoryAttribute : TheoryAttribute
{
    public DockerRequiredTheoryAttribute()
    {
        if (DockerProbe.UnavailableReason is { } reason)
            Skip = $"Requires Docker/Testcontainers — {reason}";
    }
}

[Collection("postgres-container")]
public sealed class EndToEndPipelineTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private E2EPipelineFactory? _factory;

    public async Task InitializeAsync()
    {
        // When Docker is unreachable the single test is skipped by the attribute;
        // do not ask the shared container for a database.
        if (DockerProbe.UnavailableReason is not null)
            return;

        // The schema arrives WITH the database: PostgresContainerFixture migrates a template
        // once and clones it. That still satisfies the reason this class migrated by hand —
        // Program.cs kicks off a fire-and-forget MigrateAsync the moment the host starts (the
        // first time factory.Services is touched), PostgreSQL enum `CREATE TYPE` is not
        // protected by EF's migration history advisory lock, so two concurrent MigrateAsync
        // calls race and one dies with 23505 on pg_type. The schema exists before the host is
        // ever built, so the background MigrateAsync finds nothing to apply (no concurrent DDL).
        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_e2e");

        // Disable connection pooling for the whole run. With pooling on, that background
        // migration connection is multiplexed onto the same pooled physical connection the test
        // scopes use, and the interleaving corrupts the Npgsql protocol ("BindComplete while
        // expecting ReadyForQueryMessage"). With Pooling=false every DbContext gets its own
        // dedicated connection that is opened/closed per use, so the background task can never
        // share — and thus never corrupt — a connection with the test. Test-only; production
        // config is untouched.
        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_databaseConnectionString)
        {
            Pooling = false,
        }.ConnectionString;

        _factory = new E2EPipelineFactory(connectionString);
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  THE end-to-end test
    // ════════════════════════════════════════════════════════════════════════

    [DockerRequiredFact]
    public async Task FullPipeline_UploadParseValidateMapTransformDeliver_ProducesPassportWithDeliveryProof()
    {
        var factory = _factory!;
        var http = factory.HttpDispatcher;
        var ct = CancellationToken.None;

        // Stable identifiers for the run.
        var clerkOrgId = "org_E2E_PIPELINE";
        var supplierName = "Northwind Trading OÜ";
        const string deliveryUrl = "https://supplier.example/inbound/orders";

        // ── 1. Seed org + supplier + delivery config (HTTP, AutoDeliver) +
        //       a PO mapping template. Done directly in the DB to isolate the
        //       test from unrelated controller surface. ───────────────────────
        Guid orgGuid, supplierId;
        using (var scope = factory.NewScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            var now = DateTime.UtcNow;

            var org = new Organisation
            {
                Id = Guid.NewGuid(),
                ClerkOrgId = clerkOrgId,
                Name = "E2E Buyer Co",
                Slug = "e2e-buyer-co",
                Plan = "operations",          // a paid plan → CanProcessOrders true, generous limit
                AccountStatus = "active",
                CreatedAt = now,
                TrialStartedAt = now,
                TrialEndsAt = now.AddDays(14),
            };
            orgGuid = org.Id;

            var supplier = new Supplier
            {
                Id = Guid.NewGuid(),
                OrgId = org.Id,
                Name = supplierName,
                CreatedAt = now,
            };
            supplierId = supplier.Id;

            // HTTP delivery config with AutoDeliver = true so the post-transform
            // dispatch (requireAutoDeliver: true) actually fires, like the real job.
            var deliveryConfig = new SupplierDeliveryConfig
            {
                Id = Guid.NewGuid(),
                OrgId = org.Id,
                SupplierId = supplier.Id,
                Protocol = "http",
                AutoDeliver = true,
                ConfigJson = JsonSerializer.Serialize(new { url = deliveryUrl }),
                EncryptedCredentials = string.Empty, // no creds needed for the fake dispatcher
                CreatedAt = now,
                UpdatedAt = now,
            };

            // PO mapping template: maps the buyer CSV's columns onto the canonical
            // PO model. Exercises the real mapping engine (Group D) on parse.
            var mappingConfig = new PoMappingConfig
            {
                HasHeaderRecord = true,
                Separator = ",",
                Header = new Dictionary<string, FieldMappingEntry>
                {
                    ["PoNumber"] = new() { ExternalField = "order_ref" },
                    ["Currency"] = new() { ExternalField = "ccy" },
                    ["BuyerName"] = new() { FixedValue = "E2E Buyer Co" },
                },
                Lines = new Dictionary<string, FieldMappingEntry>
                {
                    ["LineNumber"] = new() { ExternalField = "row" },
                    ["BuyerItemCode"] = new() { ExternalField = "sku" },
                    ["Description"] = new() { ExternalField = "name" },
                    ["Quantity"] = new() { ExternalField = "qty" },
                    ["Unit"] = new() { ExternalField = "uom" },
                    ["UnitPrice"] = new() { ExternalField = "price" },
                },
            };

            var poMapping = new SupplierPoMapping
            {
                Id = Guid.NewGuid(),
                OrgId = org.Id,
                SupplierId = supplier.Id,
                ConfigJson = JsonSerializer.Serialize(mappingConfig),
                CreatedAt = now,
                UpdatedAt = now,
            };

            // Pre-seed item mappings so every line auto-resolves deterministically
            // (no AI, no manual resolve needed) → order reaches "ready" after parse.
            db.ItemMappings.AddRange(
                new ItemMapping
                {
                    Id = Guid.NewGuid(),
                    OrgId = org.Id,
                    SupplierId = supplier.Id,
                    BuyerItemCode = "BUYER-AAA",
                    SupplierItemCode = "SUP-AAA",
                    Source = "manual",
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                new ItemMapping
                {
                    Id = Guid.NewGuid(),
                    OrgId = org.Id,
                    SupplierId = supplier.Id,
                    BuyerItemCode = "BUYER-BBB",
                    SupplierItemCode = "SUP-BBB",
                    Source = "manual",
                    CreatedAt = now,
                    UpdatedAt = now,
                });

            db.Organisations.Add(org);
            db.Suppliers.Add(supplier);
            db.SupplierDeliveryConfigs.Add(deliveryConfig);
            db.SupplierPoMappings.Add(poMapping);
            await db.SaveChangesAsync(ct);
        }

        // The CSV a buyer would upload — columns are the supplier-specific names the
        // mapping template above translates. Two lines, both pre-mapped.
        const string csv =
            "order_ref,ccy,row,sku,name,qty,uom,price\r\n" +
            "PO-E2E-7788,EUR,1,BUYER-AAA,Widget Alpha,10,EA,2.50\r\n" +
            "PO-E2E-7788,EUR,2,BUYER-BBB,Gadget Beta,4,EA,5.00\r\n";

        Guid orderId;

        // ── 2. UPLOAD → create stub (status "parsing"). Mirrors the upload
        //       controller's call into OrderService.CreateStubAsync. ──────────
        using (var scope = factory.NewScope())
        {
            var orders = scope.ServiceProvider.GetRequiredService<IOrderService>();
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
            var stub = await orders.CreateStubAsync(
                orgGuid, supplierId, stream, "po-e2e.csv", "text/csv", ct);

            Assert.True(stub.IsSuccess, $"CreateStubAsync failed: {stub.Error}");
            orderId = stub.Value!.Id;
            Assert.Equal(OrderStatusConstants.Parsing, stub.Value!.Status);
        }

        // ── 3. PARSE → canonical order, lines materialised, auto-resolved.
        //       This is exactly what ParseOrderJob.ExecuteAsync drives. ────────
        using (var scope = factory.NewScope())
        {
            var orders = scope.ServiceProvider.GetRequiredService<IOrderService>();
            var parsed = await orders.ParseStoredFileAsync(orgGuid, orderId, ct);
            Assert.True(parsed.IsSuccess, $"ParseStoredFileAsync failed: {parsed.Error}");
        }

        // ── 4. VALIDATE the parse + mapping result by reloading the order. ────
        //       (All lines must be resolved → status "ready", NeedsReview false.)
        using (var scope = factory.NewScope())
        {
            var orders = scope.ServiceProvider.GetRequiredService<IOrderService>();
            var got = await orders.GetByIdAsync(orgGuid, orderId, ct);
            Assert.True(got.IsSuccess);
            var order = got.Value!;

            // Header mapping worked: PoNumber + currency came from the buyer CSV.
            Assert.Equal("PO-E2E-7788", order.PoNumber);
            Assert.Equal("EUR", order.Currency);

            // Two lines, both deterministically mapped buyer→supplier code.
            Assert.Equal(2, order.Lines.Count);
            Assert.All(order.Lines, l => Assert.False(l.NeedsReview));
            var l1 = order.Lines.Single(l => l.LineNumber == 1);
            var l2 = order.Lines.Single(l => l.LineNumber == 2);
            Assert.Equal("BUYER-AAA", l1.BuyerItemCode);
            Assert.Equal("SUP-AAA", l1.SupplierItemCode);
            Assert.Equal(10m, l1.Quantity);
            Assert.Equal(2.50m, l1.UnitPrice);
            Assert.Equal("BUYER-BBB", l2.BuyerItemCode);
            Assert.Equal("SUP-BBB", l2.SupplierItemCode);

            // Fully resolved → ready to transform.
            Assert.Equal(OrderStatusConstants.Ready, order.Status);
        }

        // ── 5. TRANSFORM → supplier-ready CSV artifact (OutboundArtifact row +
        //       status ready_to_deliver). Mirrors TransformOrderJob. ──────────
        Guid artifactId;
        using (var scope = factory.NewScope())
        {
            var orders = scope.ServiceProvider.GetRequiredService<IOrderService>();
            var transformed = await orders.TransformAsync(orgGuid, orderId, OutputFormat.Csv, ct);
            Assert.True(transformed.IsSuccess, $"TransformAsync failed: {transformed.Error}");
            artifactId = transformed.Value!.ArtifactId;
            Assert.Equal("csv", transformed.Value!.Format);

            var got = await orders.GetByIdAsync(orgGuid, orderId, ct);
            Assert.Equal(OrderStatusConstants.ReadyToDeliver, got.Value!.Status);
            Assert.Single(got.Value!.OutboundArtifacts);
        }

        // ── 6. DELIVER → real DeliveryService dispatches the artifact to the
        //       fake HTTP receiver (requireAutoDeliver: true, exactly as
        //       DeliverOrderJob.Enqueue → ExecuteAsync does). ─────────────────
        using (var scope = factory.NewScope())
        {
            var delivery = scope.ServiceProvider.GetRequiredService<IDeliveryService>();
            var result = await delivery.DispatchArtifactAsync(
                orgGuid, orderId, artifactId, requireAutoDeliver: true, ct);

            Assert.True(result.Success, $"Delivery failed: {result.ErrorMessage}");
            Assert.Equal(200, result.ResponseCode);
        }

        // ── 6a. The fake receiver got the transformed payload. ────────────────
        Assert.Equal(1, http.CallCount);
        Assert.NotNull(http.CapturedContent);
        Assert.Equal("text/csv", http.CapturedContentType);
        Assert.Equal("PO-E2E-7788.csv", http.CapturedFileName);     // DeliveryService names it from PoNumber
        Assert.Contains(deliveryUrl, http.CapturedDestinationConfig);

        var deliveredText = http.CapturedText;
        // The supplier-ready CSV header + both mapped SUPPLIER codes are present.
        Assert.Contains("SupplierItemCode,Description,Quantity,Unit,UnitPrice,LineTotal", deliveredText);
        Assert.Contains("SUP-AAA", deliveredText);
        Assert.Contains("SUP-BBB", deliveredText);
        Assert.Contains("Widget Alpha", deliveredText);
        // LineTotal for line 1 = 10 * 2.50 = 25.00 — proves real transform math.
        Assert.Contains("25.00", deliveredText);

        // The delivered bytes are exactly what is in storage under the artifact key.
        using (var scope = factory.NewScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            var artifact = await db.OutboundArtifacts
                .AsNoTracking()
                .SingleAsync(a => a.Id == artifactId, ct);
            Assert.True(factory.FileStorage.TryGet(artifact.FileKey, out var stored));
            Assert.Equal(stored, http.CapturedContent);
        }

        // ── 7. A DeliveryAttempt row persisted: delivered/success, right channel
        //       + destination, ACK stamped. ──────────────────────────────────
        using (var scope = factory.NewScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();

            var attempts = await db.DeliveryAttempts
                .AsNoTracking()
                .Where(a => a.OrderId == orderId && a.OrgId == orgGuid)
                .ToListAsync(ct);

            var attempt = Assert.Single(attempts);
            Assert.Equal("success", attempt.Status);
            Assert.Equal("http", attempt.Channel);
            Assert.Equal(deliveryUrl, attempt.Destination);
            Assert.Equal(200, attempt.ResponseCode);
            Assert.Equal(1, attempt.AttemptNumber);
            Assert.NotNull(attempt.AcknowledgedAt);           // ACK round-trip recorded
            Assert.Null(attempt.RejectionReason);

            // Order reached the terminal delivered state.
            var order = await db.PurchaseOrders.AsNoTracking()
                .SingleAsync(o => o.Id == orderId, ct);
            Assert.Equal(OrderStatusConstants.Delivered, order.Status);
        }

        // ── 8. PO PASSPORT shows the proof: delivery attempt + output artifact +
        //       supplier "acknowledged" response + timeline of the pipeline. ───
        using (var scope = factory.NewScope())
        {
            var passportSvc = scope.ServiceProvider.GetRequiredService<IPassportService>();
            var pr = await passportSvc.GetAsync(orgGuid, orderId, ct);
            Assert.True(pr.IsSuccess, $"Passport failed: {pr.Error}");
            var p = pr.Value!;

            // Header / canonical roll-up.
            Assert.Equal(orderId, p.Order.OrderId);
            Assert.Equal("PO-E2E-7788", p.Order.PoNumber);
            Assert.Equal(supplierName, p.Order.SupplierName);
            Assert.Equal(OrderStatusConstants.Delivered, p.FinalStatus);
            Assert.Equal(2, p.Canonical.LineCount);
            Assert.Equal(45.00m, p.Canonical.TotalValue);   // 10*2.5 + 4*5
            Assert.Equal(14m, p.Canonical.TotalQuantity);   // 10 + 4

            // Source artifact reference (csv) — referenced, not embedded.
            Assert.Equal("csv", p.SourceArtifact!.DetectedFormat);
            Assert.NotNull(p.SourceArtifact.StorageKey);

            // Mapping decisions: both lines deterministic buyer→supplier.
            Assert.Equal(2, p.MappingDecisions.Count);
            Assert.All(p.MappingDecisions, m => Assert.Equal("deterministic", m.Source));
            Assert.Contains(p.MappingDecisions, m => m.SupplierItemCode == "SUP-AAA");
            Assert.Contains(p.MappingDecisions, m => m.SupplierItemCode == "SUP-BBB");

            // Output artifact reference matches the transformed artifact.
            Assert.NotNull(p.OutputArtifact);
            Assert.Equal(artifactId, p.OutputArtifact!.ArtifactId);
            Assert.Equal("csv", p.OutputArtifact.Format);

            // Delivery proof: exactly one attempt, delivered to the supplier URL.
            var pAttempt = Assert.Single(p.DeliveryAttempts);
            Assert.Equal("success", pAttempt.Status);
            Assert.Equal("http", pAttempt.Channel);
            Assert.Equal(deliveryUrl, pAttempt.Destination);
            Assert.Equal(200, pAttempt.ResponseCode);
            Assert.NotNull(pAttempt.AcknowledgedAt);

            // Supplier response derived as acknowledged.
            Assert.NotNull(p.SupplierResponse);
            Assert.Equal("acknowledged", p.SupplierResponse!.Outcome);
            Assert.Equal(200, p.SupplierResponse.ResponseCode);

            // Supplier delivery profile surfaced (http protocol).
            Assert.NotNull(p.SupplierProfile);
            Assert.Equal("http", p.SupplierProfile!.Protocol);

            // Timeline reconstructed from audit events covers the pipeline stages.
            var actions = p.Timeline.Select(t => t.Action).ToList();
            Assert.Contains("Created", actions);
            Assert.Contains("Parsed", actions);
            Assert.Contains("Transformed", actions);
        }
    }

    [DockerRequiredFact]
    public async Task ReviewResolveTransformDeliver_UnmappedLine_BlocksThenSavesMappingAndDelivers()
    {
        var factory = _factory!;
        var http = factory.HttpDispatcher;
        var ct = CancellationToken.None;

        var orgGuid = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        const string supplierName = "Acme Components";
        const string deliveryUrl = "https://supplier.example/review-path";

        using (var scope = factory.NewScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            var now = DateTime.UtcNow;

            db.Organisations.Add(new Organisation
            {
                Id = orgGuid,
                ClerkOrgId = "org_REVIEW_PATH",
                Name = "Review Path Buyer",
                Slug = "review-path-buyer",
                Plan = "operations",
                AccountStatus = "active",
                CreatedAt = now,
                TrialStartedAt = now,
                TrialEndsAt = now.AddDays(14),
            });

            db.Suppliers.Add(new Supplier
            {
                Id = supplierId,
                OrgId = orgGuid,
                Name = supplierName,
                CreatedAt = now,
            });

            db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
            {
                Id = Guid.NewGuid(),
                OrgId = orgGuid,
                SupplierId = supplierId,
                Protocol = "http",
                AutoDeliver = true,
                ConfigJson = JsonSerializer.Serialize(new { url = deliveryUrl }),
                EncryptedCredentials = string.Empty,
                CreatedAt = now,
                UpdatedAt = now,
            });

            var mappingConfig = new PoMappingConfig
            {
                HasHeaderRecord = true,
                Separator = ",",
                Header = new Dictionary<string, FieldMappingEntry>
                {
                    ["PoNumber"] = new() { ExternalField = "order_ref" },
                    ["Currency"] = new() { ExternalField = "ccy" },
                    ["BuyerName"] = new() { FixedValue = "Review Path Buyer" },
                },
                Lines = new Dictionary<string, FieldMappingEntry>
                {
                    ["LineNumber"] = new() { ExternalField = "row" },
                    ["BuyerItemCode"] = new() { ExternalField = "sku" },
                    ["Description"] = new() { ExternalField = "name" },
                    ["Quantity"] = new() { ExternalField = "qty" },
                    ["Unit"] = new() { ExternalField = "uom" },
                    ["UnitPrice"] = new() { ExternalField = "price" },
                },
            };

            db.SupplierPoMappings.Add(new SupplierPoMapping
            {
                Id = Guid.NewGuid(),
                OrgId = orgGuid,
                SupplierId = supplierId,
                ConfigJson = JsonSerializer.Serialize(mappingConfig),
                CreatedAt = now,
                UpdatedAt = now,
            });

            await db.SaveChangesAsync(ct);
        }

        const string csv =
            "order_ref,ccy,row,sku,name,qty,uom,price\r\n" +
            "PO-REVIEW-100,EUR,1,BUYER-UNMAPPED,Review Needed Widget,2,EA,9.99\r\n";

        Guid orderId;
        using (var scope = factory.NewScope())
        {
            var orders = scope.ServiceProvider.GetRequiredService<IOrderService>();
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
            var stub = await orders.CreateStubAsync(
                orgGuid, supplierId, stream, "review-needed.csv", "text/csv", ct);

            Assert.True(stub.IsSuccess, $"CreateStubAsync failed: {stub.Error}");
            orderId = stub.Value!.Id;
        }

        using (var scope = factory.NewScope())
        {
            var orders = scope.ServiceProvider.GetRequiredService<IOrderService>();
            var parsed = await orders.ParseStoredFileAsync(orgGuid, orderId, ct);

            Assert.True(parsed.IsSuccess, $"ParseStoredFileAsync failed: {parsed.Error}");
            Assert.Equal(OrderStatusConstants.PendingReview, parsed.Value!.Entity.Status);
            var line = Assert.Single(parsed.Value!.Entity.Lines);
            Assert.Equal("BUYER-UNMAPPED", line.BuyerItemCode);
            Assert.True(line.NeedsReview);
            Assert.True(string.IsNullOrWhiteSpace(line.SupplierItemCode));
        }

        using (var scope = factory.NewScope())
        {
            var orders = scope.ServiceProvider.GetRequiredService<IOrderService>();
            var blocked = await orders.TransformAsync(orgGuid, orderId, OutputFormat.Csv, ct);

            Assert.False(blocked.IsSuccess);
            Assert.Contains("Resolve all lines before transforming", blocked.Error);
        }

        using (var scope = factory.NewScope())
        {
            var orders = scope.ServiceProvider.GetRequiredService<IOrderService>();
            var resolved = await orders.ResolveAsync(
                orgGuid,
                orderId,
                new[] { new LineResolution(1, "SUP-REVIEW-100") },
                saveMappings: true,
                ct);

            Assert.True(resolved.IsSuccess, $"ResolveAsync failed: {resolved.Error}");
            Assert.Equal(OrderStatusConstants.Ready, resolved.Value!.Status);
            var line = Assert.Single(resolved.Value.Lines);
            Assert.False(line.NeedsReview);
            Assert.Equal("SUP-REVIEW-100", line.SupplierItemCode);
        }

        using (var scope = factory.NewScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            var saved = await db.ItemMappings.AsNoTracking()
                .SingleOrDefaultAsync(m =>
                    m.OrgId == orgGuid
                    && m.SupplierId == supplierId
                    && m.BuyerItemCode == "BUYER-UNMAPPED", ct);

            Assert.NotNull(saved);
            Assert.Equal("SUP-REVIEW-100", saved!.SupplierItemCode);
            Assert.Equal("manual", saved.Source);
        }

        Guid artifactId;
        using (var scope = factory.NewScope())
        {
            var orders = scope.ServiceProvider.GetRequiredService<IOrderService>();
            var transformed = await orders.TransformAsync(orgGuid, orderId, OutputFormat.Csv, ct);

            Assert.True(transformed.IsSuccess, $"TransformAsync failed: {transformed.Error}");
            artifactId = transformed.Value!.ArtifactId;
        }

        using (var scope = factory.NewScope())
        {
            var delivery = scope.ServiceProvider.GetRequiredService<IDeliveryService>();
            var result = await delivery.DispatchArtifactAsync(
                orgGuid, orderId, artifactId, requireAutoDeliver: true, ct);

            Assert.True(result.Success, $"Delivery failed: {result.ErrorMessage}");
            Assert.Equal(200, result.ResponseCode);
        }

        Assert.Equal(1, http.CallCount);
        Assert.Equal("PO-REVIEW-100.csv", http.CapturedFileName);
        Assert.Contains("SUP-REVIEW-100", http.CapturedText);
        Assert.Contains("Review Needed Widget", http.CapturedText);

        using (var scope = factory.NewScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            var order = await db.PurchaseOrders.AsNoTracking()
                .SingleAsync(o => o.Id == orderId && o.OrgId == orgGuid, ct);

            Assert.Equal(OrderStatusConstants.Delivered, order.Status);

            var attempts = await db.DeliveryAttempts.AsNoTracking()
                .Where(a => a.OrderId == orderId && a.OrgId == orgGuid)
                .ToListAsync(ct);

            var attempt = Assert.Single(attempts);
            Assert.Equal("success", attempt.Status);
            Assert.Equal(deliveryUrl, attempt.Destination);
        }
    }

    [DockerRequiredFact]
    public async Task ParseStoredFileAsync_UblXml_RoutesToUblParserEvenWhenCxmlRegisteredFirst()
    {
        var factory = _factory!;
        var ct = CancellationToken.None;
        var orgGuid = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        using (var scope = factory.NewScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            var now = DateTime.UtcNow;

            db.Organisations.Add(new Organisation
            {
                Id = orgGuid,
                ClerkOrgId = "org_UBL_XML",
                Name = "UBL XML Test Org",
                Slug = "ubl-xml-test-org",
                Plan = "operations",
                AccountStatus = "active",
                CreatedAt = now,
            });

            db.Suppliers.Add(new Supplier
            {
                Id = supplierId,
                OrgId = orgGuid,
                Name = "Globex Supplier",
                CreatedAt = now,
            });

            await db.SaveChangesAsync(ct);
        }

        const string ublXml = """
            <Order xmlns="urn:oasis:names:specification:ubl:schema:xsd:Order-2"
                   xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"
                   xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2">
              <cbc:ID>PO-UBL-001</cbc:ID>
              <cbc:IssueDate>2026-06-01</cbc:IssueDate>
              <cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
              <cac:BuyerCustomerParty>
                <cac:Party>
                  <cac:PartyName><cbc:Name>Acme Buyer Ltd</cbc:Name></cac:PartyName>
                </cac:Party>
              </cac:BuyerCustomerParty>
              <cac:SellerSupplierParty>
                <cac:Party>
                  <cac:PartyName><cbc:Name>Globex Supplier</cbc:Name></cac:PartyName>
                </cac:Party>
              </cac:SellerSupplierParty>
              <cac:OrderLine>
                <cac:LineItem>
                  <cbc:ID>1</cbc:ID>
                  <cbc:Quantity unitCode="EA">3</cbc:Quantity>
                  <cac:Price><cbc:PriceAmount currencyID="EUR">12.50</cbc:PriceAmount></cac:Price>
                  <cac:Item>
                    <cbc:Name>Router bracket</cbc:Name>
                    <cac:SellersItemIdentification><cbc:ID>SUP-UBL-001</cbc:ID></cac:SellersItemIdentification>
                  </cac:Item>
                </cac:LineItem>
              </cac:OrderLine>
            </Order>
            """;

        Guid orderId;
        using (var scope = factory.NewScope())
        {
            var orders = scope.ServiceProvider.GetRequiredService<IOrderService>();
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ublXml));
            var stub = await orders.CreateStubAsync(
                orgGuid, supplierId, stream, "buyer-order.xml", "application/xml", ct);

            Assert.True(stub.IsSuccess, $"CreateStubAsync failed: {stub.Error}");
            orderId = stub.Value!.Id;
        }

        using (var scope = factory.NewScope())
        {
            var orders = scope.ServiceProvider.GetRequiredService<IOrderService>();
            var parsed = await orders.ParseStoredFileAsync(orgGuid, orderId, ct);

            Assert.True(parsed.IsSuccess, $"ParseStoredFileAsync failed: {parsed.Error}");
            Assert.Equal("PO-UBL-001", parsed.Value!.Entity.PoNumber);
            Assert.Equal("EUR", parsed.Value!.Entity.Currency);
            Assert.Single(parsed.Value!.Entity.Lines);
            Assert.Equal("ubl", parsed.Value!.DetectedFormat);
        }
    }

    /// <summary>
    /// V5 regression guard (the round-trip the unit tests lacked). The header field
    /// <see cref="PurchaseOrderEntity.RequestedDeliveryDate"/> was originally EF-Ignored and "rode
    /// canonical_json", so in production it was ALWAYS null at transform time: the async ingest
    /// persisted typed columns only via <c>ExecuteUpdateAsync</c> (no canonical rewrite) and the
    /// transform reloaded the entity fresh.
    ///
    /// This drives the REAL ingest path — <c>CreateStubAsync</c> → <c>ParseStoredFileAsync</c>
    /// (which uses <c>ExecuteUpdateAsync</c>, untranslatable by the InMemory provider, hence real
    /// Postgres here) — on a SAP IDoc ORDERS05 carrying <c>E1EDK03 IDDAT=012 DATUM=20260525</c>, then
    /// RELOADS the order from a fresh DbContext scope and asserts the header requested delivery date
    /// came back as the non-null parsed value. Pre-fix this read back null.
    /// </summary>
    [DockerRequiredFact]
    public async Task ParseStoredFileAsync_Idoc_PersistsHeaderRequestedDeliveryDate()
    {
        var factory = _factory!;
        var ct = CancellationToken.None;
        var orgGuid = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        using (var scope = factory.NewScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            var now = DateTime.UtcNow;

            db.Organisations.Add(new Organisation
            {
                Id = orgGuid,
                ClerkOrgId = "org_IDOC_RDD",
                Name = "IDoc RDD Test Org",
                Slug = "idoc-rdd-test-org",
                Plan = "operations",
                AccountStatus = "active",
                CreatedAt = now,
            });

            db.Suppliers.Add(new Supplier
            {
                Id = supplierId,
                OrgId = orgGuid,
                Name = "Initech Supplier",
                CreatedAt = now,
            });

            await db.SaveChangesAsync(ct);
        }

        // Minimal SAP IDoc ORDERS05 carrying a header requested delivery date
        // (E1EDK03 IDDAT=012 DATUM=20260525) and one orderable line.
        const string idocXml = """
            <ORDERS05>
              <IDOC BEGIN="1">
                <E1EDK01><CURCY>EUR</CURCY><BELNR>4501450099</BELNR></E1EDK01>
                <E1EDK02><QUALF>001</QUALF><BELNR>PO-IDOC-RDD-1</BELNR><DATUM>20260601</DATUM></E1EDK02>
                <E1EDK03><IDDAT>012</IDDAT><DATUM>20260525</DATUM></E1EDK03>
                <E1EDP01>
                  <POSEX>00010</POSEX><MENGE>50.000</MENGE><MENEE>EA</MENEE><VPREI>1.05</VPREI><NETWR>52.5</NETWR>
                  <E1EDP19><QUALF>002</QUALF><IDTNR>BUY-IDOC-1</IDTNR></E1EDP19>
                  <E1EDP19><QUALF>001</QUALF><KTEXT>Network bracket</KTEXT></E1EDP19>
                </E1EDP01>
                <E1EDS01><SUMME>52.5</SUMME><SUNIT>EUR</SUNIT></E1EDS01>
              </IDOC>
            </ORDERS05>
            """;

        Guid orderId;
        using (var scope = factory.NewScope())
        {
            var orders = scope.ServiceProvider.GetRequiredService<IOrderService>();
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(idocXml));
            var stub = await orders.CreateStubAsync(
                orgGuid, supplierId, stream, "idoc-order.xml", "application/xml", ct);

            Assert.True(stub.IsSuccess, $"CreateStubAsync failed: {stub.Error}");
            orderId = stub.Value!.Id;
        }

        // Drive the previously-broken async path (uses ExecuteUpdateAsync).
        using (var scope = factory.NewScope())
        {
            var orders = scope.ServiceProvider.GetRequiredService<IOrderService>();
            var parsed = await orders.ParseStoredFileAsync(orgGuid, orderId, ct);

            Assert.True(parsed.IsSuccess, $"ParseStoredFileAsync failed: {parsed.Error}");
            Assert.Equal("PO-IDOC-RDD-1", parsed.Value!.Entity.PoNumber);
            // The in-memory entity reflects the parsed value...
            Assert.Equal(new DateOnly(2026, 5, 25), parsed.Value!.Entity.RequestedDeliveryDate);
        }

        // ...and — the actual regression — a FRESH reload from the DB has it too,
        // proving ExecuteUpdateAsync persisted the column (pre-fix this was null).
        using (var scope = factory.NewScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            var reloaded = await db.PurchaseOrders.AsNoTracking()
                .SingleAsync(o => o.Id == orderId && o.OrgId == orgGuid, ct);

            Assert.Equal(new DateOnly(2026, 5, 25), reloaded.RequestedDeliveryDate);
        }
    }

    /// <summary>
    /// Phase 1 Task 8 — for structured formats (here CSV) the async parse path must persist the
    /// FULL source-token set into <c>source_captures.tokens_json</c>, not just the canonically
    /// mapped fields. Before the fix, <c>ParseStoredFileAsync</c> called
    /// <c>UpsertSourceCaptureAsync(..., tokens: null, ...)</c> for non-PDF formats, so a source
    /// column that maps to NO canonical field (here <c>internal_note</c>) was simply discarded.
    ///
    /// This drives the REAL ingest path (<c>CreateStubAsync</c> → <c>ParseStoredFileAsync</c>,
    /// which uses <c>ExecuteUpdateAsync</c>/<c>ExecuteDeleteAsync</c> — hence real Postgres). No
    /// PO mapping template is seeded, so the default <c>CsvOrderParser</c> runs: it maps
    /// po/sku/description/qty/price but knows nothing about <c>internal_note</c>. We then RELOAD
    /// the order's <see cref="SourceCapture"/> from a fresh DbContext scope and assert the
    /// unmapped cell's verbatim value survived in the persisted jsonb token bag.
    /// </summary>
    [DockerRequiredFact]
    public async Task ParseStoredFileAsync_Csv_PersistsFullSourceTokens_IncludingUnmappedColumn()
    {
        var factory = _factory!;
        var ct = CancellationToken.None;
        var orgGuid = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        using (var scope = factory.NewScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            var now = DateTime.UtcNow;

            db.Organisations.Add(new Organisation
            {
                Id = orgGuid,
                ClerkOrgId = "org_FULL_TOKENS",
                Name = "Full Tokens Test Org",
                Slug = "full-tokens-test-org",
                Plan = "operations",
                AccountStatus = "active",
                CreatedAt = now,
            });

            db.Suppliers.Add(new Supplier
            {
                Id = supplierId,
                OrgId = orgGuid,
                Name = "Token Supplier",
                CreatedAt = now,
            });

            await db.SaveChangesAsync(ct);
        }

        // The last column — internal_note — maps to NO canonical field, so the parsed order
        // never carries it. Only the full-token capture preserves "TOP-SECRET-NOTE-9001".
        const string unmappedValue = "TOP-SECRET-NOTE-9001";
        const string csv =
            "po_number,sku,description,qty,price,internal_note\r\n" +
            $"PO-TOKENS-1,BUY-1,Widget,3,4.25,{unmappedValue}\r\n";

        Guid orderId;
        using (var scope = factory.NewScope())
        {
            var orders = scope.ServiceProvider.GetRequiredService<IOrderService>();
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
            var stub = await orders.CreateStubAsync(
                orgGuid, supplierId, stream, "tokens.csv", "text/csv", ct);

            Assert.True(stub.IsSuccess, $"CreateStubAsync failed: {stub.Error}");
            orderId = stub.Value!.Id;
        }

        // Drive the real async parse path (ExecuteUpdateAsync + the new tokeniser wiring).
        using (var scope = factory.NewScope())
        {
            var orders = scope.ServiceProvider.GetRequiredService<IOrderService>();
            var parsed = await orders.ParseStoredFileAsync(orgGuid, orderId, ct);
            Assert.True(parsed.IsSuccess, $"ParseStoredFileAsync failed: {parsed.Error}");
        }

        // Reload the SourceCapture from a FRESH context: the unmapped cell value must be present
        // in the persisted jsonb token bag (and the header label too), proving the full token set
        // — not just the canonically mapped fields — was captured.
        using (var scope = factory.NewScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            var capture = await db.SourceCaptures.AsNoTracking()
                .SingleAsync(s => s.OrderId == orderId && s.OrgId == orgGuid, ct);

            Assert.NotNull(capture.TokensJson);
            var raw = capture.TokensJson!.RootElement.GetRawText();

            // The unmapped column's VALUE survived (this is the whole point — pre-fix it was dropped).
            Assert.Contains(unmappedValue, raw);
            // The unmapped column's header label survived too (full token set, not just mapped fields).
            Assert.Contains("internal_note", raw);

            // Sanity: this really is the token bag (cell ids), not a raw_fields fallback bag.
            using var bag = System.Text.Json.JsonDocument.Parse(raw);
            Assert.Contains(
                bag.RootElement.EnumerateArray(),
                t => t.TryGetProperty("value", out var v)
                     && v.GetString() == unmappedValue);
            Assert.Contains(
                bag.RootElement.EnumerateArray(),
                t => t.TryGetProperty("id", out var id)
                     && id.GetString() is { } s && s.StartsWith("cell:"));
        }
    }
}
