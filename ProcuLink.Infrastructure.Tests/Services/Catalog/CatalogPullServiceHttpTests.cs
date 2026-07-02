using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Catalog;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Catalog;
using ProcuLink.Infrastructure.Services.Ingress;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Tests.Services.Catalog;

/// <summary>
/// B6 gate for the HTTP(S) catalog pull branch (plan 2026-06-12 v2). REAL guard, encryption,
/// JSON parser, and catalog sink; the HTTP transport is the only fake. Proves the security
/// controls hold at the service level:
///  • SSRF up-front guard rejects a private URL BEFORE any fetch (recording handler untouched),
///  • a connect-time guard block (the redirect-to-private case) maps to a safe message and
///    NEVER imports attacker data,
///  • the production path uses the connect-time-revalidating guarded handler so a redirect to a
///    private IP is blocked at TCP connect (real handler, end-to-end, no fetch reaches a server),
///  • the response read is bounded (cap+1) so a lying/endless server can't exhaust memory,
///  • a JSON catalog body is parsed + alias-detected and really upserted,
///  • an OAuth token-fetch failure surfaces as a safe message and imports nothing.
/// </summary>
public class CatalogPullServiceHttpTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static IConfiguration Config(bool allowPrivate) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delivery:EncryptionKey"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
            ["Delivery:AllowPrivateNetworkTargets"] = allowPrivate ? "true" : "false",
        })
        .Build();

    private sealed record Harness(
        ProcuLinkDbContext Db,
        CatalogPullService Service,
        DeliveryEncryptionService Encryption,
        RecordingCatalogSink Sink,
        Guid OrgId,
        Guid SupplierId,
        Guid SourceId);

    private static async Task<Harness> BuildAsync(
        HttpClient httpClient,
        bool allowPrivate = true,
        string url = "https://supplier.example/catalog.json",
        string? authConfigJson = null,
        Action<SupplierCatalogSource>? mutateSource = null)
    {
        var db = NewDb();
        var config = Config(allowPrivate);
        var encryption = new DeliveryEncryptionService(config);
        var guard = new OutboundRequestGuard(config, NullLogger<OutboundRequestGuard>.Instance);
        var sink = new RecordingCatalogSink(new SupplierCatalogService(db));

        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_{orgId:N}",
            Name = "Http Org",
            Slug = $"http-{orgId:N}",
            CreatedAt = DateTime.UtcNow,
        });
        db.Suppliers.Add(new Supplier
        {
            Id = supplierId,
            OrgId = orgId,
            Name = "Http Supplier",
            CreatedAt = DateTime.UtcNow,
        });

        var source = new SupplierCatalogSource
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            Protocol = "https",
            Url = url,
            HttpMethod = "GET",
            AuthMethod = authConfigJson is null ? "none" : "configured",
            AuthConfigEncrypted = authConfigJson is null ? null : encryption.Encrypt(authConfigJson),
            FileFormat = "auto",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        mutateSource?.Invoke(source);
        db.SupplierCatalogSources.Add(source);
        await db.SaveChangesAsync();

        var service = new TestableCatalogPullService(
            db, encryption, guard,
            new ThrowingSftpFactory(), new ThrowingFtpFactory(),
            new ThrowingHttpClientFactory(), sink,
            NullLogger<CatalogPullService>.Instance, httpClient);

        return new Harness(db, service, encryption, sink, orgId, supplierId, source.Id);
    }

    // ── happy path: JSON catalog over http → parsed + upserted ────────────────

    [Fact]
    public async Task Pull_HttpJson_ParsesAliasesAndUpserts()
    {
        const string json = """
        { "products": [
            { "sku": "A-1", "description": "Widget", "unit_price": 9.50, "currency": "EUR" },
            { "sku": "B-2", "name": "Gadget", "price": "1.25" }
        ] }
        """;
        var handler = new StubHandler(HttpStatusCode.OK, json, "application/json");
        var h = await BuildAsync(new HttpClient(handler));

        var result = await h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);

        result.Status.Should().Be("ok");
        result.Created.Should().Be(2);
        handler.RequestCount.Should().Be(1);
        (await h.Db.SupplierProducts.CountAsync(p => p.OrgId == h.OrgId && p.SupplierId == h.SupplierId))
            .Should().Be(2);

        var source = await h.Db.SupplierCatalogSources.SingleAsync(s => s.Id == h.SourceId);
        source.LastSyncStatus.Should().Be("ok");
        source.LastSyncError.Should().BeNull();
    }

    [Fact]
    public async Task Pull_HttpCsvByContentType_Parses_WhenUrlHasNoExtension()
    {
        const string csv = "code,name,unit_price\nA-1,Widget,9.50\n";
        var handler = new StubHandler(HttpStatusCode.OK, csv, "text/csv");
        var h = await BuildAsync(new HttpClient(handler), url: "https://supplier.example/api/catalog");

        var result = await h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);

        result.Status.Should().Be("ok");
        result.Created.Should().Be(1);
    }

    // ── SSRF: up-front guard blocks a private URL BEFORE any fetch ─────────────

    [Fact]
    public async Task Pull_PrivateUrl_BlockedByGuard_BeforeAnyFetch()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]", "application/json");
        var h = await BuildAsync(new HttpClient(handler),
            allowPrivate: false, url: "http://169.254.169.254/latest/meta-data/");

        var act = () => h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);

        (await act.Should().ThrowAsync<CatalogSyncException>())
            .Which.Message.Should().Be(CatalogPullService.ErrUrlNotAllowed);
        handler.RequestCount.Should().Be(0, "the SSRF guard must reject the URL before any request is sent");
        h.Sink.UpsertCalls.Should().Be(0);
    }

    // ── redirect-to-private blocked at connect: real guarded handler, no fetch ─

    [Fact]
    public async Task Pull_RealGuardedHandler_LoopbackHost_BlockedAtConnect_NoImport()
    {
        // Drive the PRODUCTION connect-time-revalidating handler. The up-front guard is in
        // allowPrivate mode so the URL passes the scheme/range pre-check, but the connect-time
        // callback (range-validated even with the dev flag OFF) is built from a STRICT guard:
        // the host resolves to loopback and the socket is refused before any byte is exchanged.
        // This is the redirect-to-private / DNS-rebind class: nothing reaches a server.
        var strictConfig = Config(allowPrivate: false);
        var strictGuard = new OutboundRequestGuard(strictConfig, NullLogger<OutboundRequestGuard>.Instance);
        using var guardedClient = new HttpClient(strictGuard.CreateGuardedHttpHandler());

        // Up-front guard permissive so we actually reach the guarded connect callback.
        var h = await BuildAsync(guardedClient, allowPrivate: true, url: "http://localhost/catalog.json");

        var act = () => h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);

        // The connect-time block throws HttpRequestException → sanitized to a safe connect error.
        (await act.Should().ThrowAsync<CatalogSyncException>())
            .Which.Message.Should().Be(CatalogPullService.ErrConnectFailed);
        h.Sink.UpsertCalls.Should().Be(0, "a blocked connect must never import data");
    }

    // ── H2: oversized / lying server body aborts mid-read at cap+1 ─────────────

    [Fact]
    public async Task Pull_OversizeResponseBody_AbortsBounded_NoImport()
    {
        var handler = new StreamHandler(() => new EndlessZeroStream(), "text/csv");
        var h = await BuildAsync(new HttpClient(handler));

        var act = () => h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);

        (await act.Should().ThrowAsync<CatalogSyncException>())
            .Which.Message.Should().Be("Catalog file exceeds 256 MB.");
        h.Sink.UpsertCalls.Should().Be(0);
    }

    // ── non-2xx response → safe message, no import ────────────────────────────

    [Fact]
    public async Task Pull_Http500_MapsToSafeMessage_NoImport()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, "stack trace: secret-host failed", "text/plain");
        var h = await BuildAsync(new HttpClient(handler));

        var act = () => h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<CatalogSyncException>()).Which;
        thrown.Message.Should().Be(CatalogPullService.ErrHttpStatus);
        thrown.Message.Should().NotContain("secret-host");
        h.Sink.UpsertCalls.Should().Be(0);
    }

    // ── auth: bearer header is applied to the outgoing request ─────────────────

    [Fact]
    public async Task Pull_BearerAuth_AppliesAuthorizationHeader()
    {
        string? auth = null;
        var handler = new CapturingHandler(req => auth = req.Headers.Authorization?.ToString(),
            HttpStatusCode.OK, "[{\"code\":\"A-1\"}]", "application/json");
        var h = await BuildAsync(new HttpClient(handler),
            authConfigJson: "{\"type\":\"bearer\",\"token\":\"tok-xyz\"}");

        var result = await h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);

        result.Status.Should().Be("ok");
        auth.Should().Be("Bearer tok-xyz");
    }

    // ── OAuth token-fetch failure → safe message, no import ────────────────────

    [Fact]
    public async Task Pull_OAuthTokenFetch401_MapsToSafeMessage_NoImport()
    {
        var catalogFetched = false;
        var handler = new RoutingHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri.Contains("/oauth/token"))
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("bad secret") };
            catalogFetched = true;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{\"code\":\"A-1\"}]") };
        });
        var creds = "{\"type\":\"oauth2_client_credentials\",\"tokenUrl\":\"https://supplier.example/oauth/token\",\"clientId\":\"cid\",\"clientSecret\":\"secret\"}";
        var h = await BuildAsync(new HttpClient(handler), authConfigJson: creds);

        var act = () => h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<CatalogSyncException>()).Which;
        thrown.Message.Should().Be(CatalogPullService.ErrHttpAuthFailed);
        thrown.Message.Should().NotContain("secret");
        catalogFetched.Should().BeFalse("the catalog must not be fetched when auth could not be obtained");
        h.Sink.UpsertCalls.Should().Be(0);
    }

    // ── undecryptable auth-config → honest message, no fetch ───────────────────

    [Fact]
    public async Task Pull_UndecryptableAuthConfig_MapsToHonestMessage()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]", "application/json");
        var h = await BuildAsync(new HttpClient(handler),
            mutateSource: s => { s.AuthMethod = "bearer"; s.AuthConfigEncrypted = "bm90LWEtdmFsaWQtZW52ZWxvcGU="; });

        var act = () => h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);

        (await act.Should().ThrowAsync<CatalogSyncException>())
            .Which.Message.Should().Be(CatalogPullService.ErrAuthConfigUnreadable);
        handler.RequestCount.Should().Be(0);
    }

    // ── test-fetch over http reports honestly + writes nothing ────────────────

    [Fact]
    public async Task TestFetch_HttpJson_ReportsHonestly_WritesNothing()
    {
        const string json = """[ { "sku": "A-1", "description": "Widget", "warehouse_zone": "Z1" } ]""";
        var handler = new StubHandler(HttpStatusCode.OK, json, "application/json");
        var h = await BuildAsync(new HttpClient(handler));

        var result = await h.Service.TestFetchAsync(h.OrgId, h.SupplierId, CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.DetectedFormat.Should().Be("json");
        result.MappedFields.Should().Contain(new KeyValuePair<string, string>("sku", "code"));
        result.MappedFields.Should().Contain(new KeyValuePair<string, string>("description", "name"));
        result.UnmappedColumns.Should().Contain("warehouse_zone");
        result.ParsedRows.Should().Be(1);
        h.Sink.UpsertCalls.Should().Be(0);
        (await h.Db.SupplierProducts.CountAsync()).Should().Be(0);
    }

    // ── test doubles ──────────────────────────────────────────────────────────

    private sealed class TestableCatalogPullService : CatalogPullService
    {
        private readonly HttpClient _client;
        public TestableCatalogPullService(
            ProcuLinkDbContext db, DeliveryEncryptionService encryption, OutboundRequestGuard guard,
            ISftpClientFactory sftp, IFtpFetchClientFactory ftp, IHttpClientFactory httpFactory,
            ISupplierCatalogService catalog, Microsoft.Extensions.Logging.ILogger<CatalogPullService> logger,
            HttpClient client)
            : base(db, encryption, guard, sftp, ftp, httpFactory, catalog, logger)
        {
            _client = client;
        }

        internal override HttpClient CreateHttpClient() => _client;
    }

    private sealed class StubHandler(HttpStatusCode status, string body, string contentType) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType),
            });
        }
    }

    private sealed class CapturingHandler(Action<HttpRequestMessage> capture, HttpStatusCode status, string body, string contentType)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            capture(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType),
            });
        }
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(route(request));
    }

    private sealed class StreamHandler(Func<Stream> streamFactory, string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new StreamContent(streamFactory());
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class ThrowingSftpFactory : ISftpClientFactory
    {
        public ISftpSession Connect(string host, int port, string username, string password)
            => throw new InvalidOperationException("SFTP factory must not be invoked in http tests.");
    }

    private sealed class ThrowingFtpFactory : IFtpFetchClientFactory
    {
        public IFtpFetchSession Connect(string host, int port, string username, string password, bool explicitTls)
            => throw new InvalidOperationException("FTP factory must not be invoked in http tests.");
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => throw new InvalidOperationException("The named client factory must not be used — CreateHttpClient is overridden.");
    }

    private sealed class RecordingCatalogSink : ISupplierCatalogService
    {
        private readonly ISupplierCatalogService _inner;
        public int UpsertCalls { get; private set; }
        public RecordingCatalogSink(ISupplierCatalogService inner) => _inner = inner;

        public Task<IReadOnlyList<SupplierProduct>> ListAsync(Guid orgId, Guid supplierId, string? query, int? take, CancellationToken ct)
            => _inner.ListAsync(orgId, supplierId, query, take, ct);
        public Task<(int Created, int Updated)> UpsertManyAsync(Guid orgId, Guid supplierId, IEnumerable<SupplierProduct> products, CancellationToken ct)
        {
            UpsertCalls++;
            return _inner.UpsertManyAsync(orgId, supplierId, products, ct);
        }
        public Task<int> CountAsync(Guid orgId, Guid supplierId, CancellationToken ct) => _inner.CountAsync(orgId, supplierId, ct);
        public Task<int> DeleteAsync(Guid orgId, Guid supplierId, CancellationToken ct) => _inner.DeleteAsync(orgId, supplierId, ct);
    }

    private sealed class EndlessZeroStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => long.MaxValue;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) { Array.Clear(buffer, offset, count); return count; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
