using System.IO.Compression;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Catalog;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Catalog;
using ProcuLink.Infrastructure.Services.Ingress;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Tests.Services.Catalog;

/// <summary>
/// Pipeline tests for the real-distributor-feed additions (plan 2026-07-02): transparent ZIP
/// unwrap inside the pull path (Phase 1.4), per-source column mapping applied end-to-end
/// (Phase 2.4), and the <c>aes2fa</c> vendor fetcher through the fake-HTTP seam (Phase 6.6). REAL
/// guard, encryption, parser, and catalog sink; only the HTTP transport is faked.
/// </summary>
public class CatalogPullServiceRealFeedTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delivery:EncryptionKey"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
            ["Delivery:AllowPrivateNetworkTargets"] = "true",
        })
        .Build();

    private sealed record Harness(
        ProcuLinkDbContext Db, CatalogPullService Service, Guid OrgId, Guid SupplierId, Guid SourceId);

    private static async Task<Harness> BuildAsync(
        HttpClient httpClient, Action<SupplierCatalogSource> configureSource,
        IEnumerable<ICatalogVendorFetcher>? vendorFetchers = null)
    {
        var db = NewDb();
        var config = Config();
        var encryption = new DeliveryEncryptionService(config);
        var guard = new OutboundRequestGuard(config, NullLogger<OutboundRequestGuard>.Instance);
        var sink = new SupplierCatalogService(db);

        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_{orgId:N}", Name = "Feed Org",
            Slug = $"feed-{orgId:N}", CreatedAt = DateTime.UtcNow,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Feed Supplier", CreatedAt = DateTime.UtcNow });

        var source = new SupplierCatalogSource
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            Protocol = "https", Url = "https://supplier.example/catalog", HttpMethod = "GET",
            AuthMethod = "none", FileFormat = "auto", IsEnabled = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        configureSource(source);
        db.SupplierCatalogSources.Add(source);
        await db.SaveChangesAsync();

        var service = new TestableCatalogPullService(
            db, encryption, guard, new ThrowingSftpFactory(), new ThrowingFtpFactory(),
            new ThrowingHttpClientFactory(), sink, NullLogger<CatalogPullService>.Instance,
            httpClient, vendorFetchers);

        return new Harness(db, service, orgId, supplierId, source.Id);
    }

    private static byte[] ZipWith(string entryName, string content)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var e = z.CreateEntry(entryName);
            using var s = e.Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            s.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    // ── Phase 1.4: ZIP unwrap inside the pull path ────────────────────────────

    [Fact]
    public async Task Pull_HttpZipContainingCsv_UnwrappedAndParsed()
    {
        var zip = ZipWith("PRICE.TXT", "code,name,price\nA-1,Widget,12.50\nB-2,Gadget,9.00\n");
        var h = await BuildAsync(
            new HttpClient(new BytesHandler(zip, "application/zip")),
            s => s.FileFormat = "auto");

        var result = await h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);

        result.Status.Should().Be("ok");
        (result.Created + result.Updated).Should().Be(2);
    }

    [Fact]
    public async Task Pull_SameZipBytesTwice_SecondIsUnchanged()
    {
        var zip = ZipWith("PRICE.TXT", "code,name\nA-1,Widget\n");
        var h = await BuildAsync(new HttpClient(new BytesHandler(zip, "application/zip")), s => s.FileFormat = "auto");

        var first = await h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);
        first.Status.Should().Be("ok");

        var second = await h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);
        second.Status.Should().Be("unchanged"); // hash over the transported (zip) bytes is stable
    }

    // ── Phase 2.4: column mapping applied end-to-end (named-key JSON shape) ────

    [Fact]
    public async Task Pull_JsonWithColumnMapping_MapsNonAliasFields()
    {
        const string json = """[{"Id":923,"Name":"REDACTED-NAME","Price":73250.0},{"Id":930,"Name":"Other","Price":100.0}]""";
        var h = await BuildAsync(
            new HttpClient(new BytesHandler(Encoding.UTF8.GetBytes(json), "application/json")),
            s =>
            {
                s.FileFormat = "json";
                s.ColumnMappingJson = """{"Id":"code","Name":"name","Price":"price"}""";
            });

        var result = await h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);
        result.Status.Should().Be("ok");
        (result.Created + result.Updated).Should().Be(2);

        var products = await new SupplierCatalogService(h.Db).ListAsync(h.OrgId, h.SupplierId, null, null, CancellationToken.None);
        products.Should().Contain(p => p.Code == "923" && p.Price == 73250m);
    }

    [Fact]
    public async Task Pull_JsonWithoutMapping_NoCodeColumn_Fails()
    {
        const string json = """[{"Id":1,"Name":"X","Price":5}]""";
        var h = await BuildAsync(
            new HttpClient(new BytesHandler(Encoding.UTF8.GetBytes(json), "application/json")),
            s => s.FileFormat = "json"); // no mapping → Id/Name/Price don't alias to code

        var act = () => h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);
        await act.Should().ThrowAsync<CatalogSyncException>()
            .WithMessage(CatalogPullService.ErrNoCodeColumn);
    }

    // ── Phase 6.6: aes2fa vendor fetcher via the fake seam ────────────────────

    [Fact]
    public async Task Pull_Aes2faVendor_TokenPerPage_PaginatesAndFlattens()
    {
        // Fake vendor API: GenerateAccessToken → a token; GetProducts → 1 page of 2 items then end.
        var handler = new Aes2faVendorFakeHandler();
        var fetcher = new Aes2faSignedCatalogFetcher(NullLogger<Aes2faSignedCatalogFetcher>.Instance);

        var encryption = new DeliveryEncryptionService(Config());
        // customerId is a placeholder: the real one is the account number this org holds WITH the
        // distributor, which is an account identifier of the same class PR #181 scrubbed.
        var vendorCreds = """{"type":"aes2fa_signed","customerId":"CUST-TEST","consumerKey":"CK","consumerSecret":"CS","accessTokenKey":"01234567890123456789012345678901","currency":"EUR"}""";

        var h = await BuildAsync(
            new HttpClient(handler),
            s =>
            {
                s.Protocol = "aes2fa";
                s.Url = "https://vendor-api.example/api";
                s.FileFormat = "auto";
                s.AuthConfigEncrypted = encryption.Encrypt(
                    vendorCreds,
                    CredentialScope.ForSupplier(s.OrgId, CredentialPurpose.SupplierCatalogAuthConfig, s.Id));
                s.ColumnMappingJson = """{"SKU":"code","PriceExclVAT":"price"}""";
            },
            vendorFetchers: new[] { fetcher });

        var result = await h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);

        result.Status.Should().Be("ok");
        (result.Created + result.Updated).Should().Be(2);
        handler.TokenCalls.Should().BeGreaterThanOrEqualTo(1);

        var products = await new SupplierCatalogService(h.Db).ListAsync(h.OrgId, h.SupplierId, null, null, CancellationToken.None);
        products.Should().Contain(p => p.Code == "REDACTED-ITEM" && p.Price == 645.91m);
        products.Should().Contain(p => p.Code == "REDACTED-ITEM");
    }

    // ── test doubles ──────────────────────────────────────────────────────────

    private sealed class TestableCatalogPullService : CatalogPullService
    {
        private readonly HttpClient _client;
        public TestableCatalogPullService(
            ProcuLinkDbContext db, DeliveryEncryptionService encryption, OutboundRequestGuard guard,
            ISftpClientFactory sftp, IFtpFetchClientFactory ftp, IHttpClientFactory httpFactory,
            ISupplierCatalogService catalog, Microsoft.Extensions.Logging.ILogger<CatalogPullService> logger,
            HttpClient client, IEnumerable<ICatalogVendorFetcher>? vendorFetchers)
            : base(db, encryption, guard, sftp, ftp, httpFactory, catalog, logger, vendorFetchers)
        {
            _client = client;
        }

        internal override HttpClient CreateHttpClient() => _client;
    }

    private sealed class BytesHandler(byte[] body, string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>Minimal vendor API stub: signs in, then returns one product page then the end.</summary>
    private sealed class Aes2faVendorFakeHandler : HttpMessageHandler
    {
        public int TokenCalls { get; private set; }
        private int _productCalls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("GenerateAccessToken", StringComparison.OrdinalIgnoreCase))
            {
                TokenCalls++;
                // Any non-JSON, >40-char base64-ish body is accepted as a token by the fetcher.
                return Ok("\"" + new string('A', 200) + "\"", "text/plain");
            }
            if (path.Contains("GetProducts", StringComparison.OrdinalIgnoreCase))
            {
                _productCalls++;
                if (_productCalls == 1)
                {
                    // One page, two items, NextItemNo present → fetcher requests page 2.
                    return Ok("""
                    {"StatusCode":1,"Status":"OK","Message":[
                      {"SKU":"REDACTED-ITEM","Name":"REDACTED-PARTY","Price":{"PriceExclVAT":"645.91","Currency":"EUR"}},
                      {"SKU":"REDACTED-ITEM","Name":"DELL Monitor","Price":{"PriceExclVAT":"120.00","Currency":"EUR"}}
                    ],"NextItemNo":"REDACTED-ITEM"}
                    """, "application/json");
                }
                // Page 2 empty → terminates pagination.
                return Ok("""{"StatusCode":1,"Status":"OK","Message":[]}""", "application/json");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Ok(string body, string contentType) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType),
            });
    }

    private sealed class ThrowingSftpFactory : ISftpClientFactory
    {
        public ISftpSession Connect(
            string host, int port, string username, string password, SshHostKeyVerifier verifier)
            => throw new InvalidOperationException("SFTP factory must not be invoked.");
    }

    private sealed class ThrowingFtpFactory : IFtpFetchClientFactory
    {
        public IFtpFetchSession Connect(string host, int port, string username, string password, bool explicitTls)
            => throw new InvalidOperationException("FTP factory must not be invoked.");
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => throw new InvalidOperationException("CreateHttpClient is overridden.");
    }
}
