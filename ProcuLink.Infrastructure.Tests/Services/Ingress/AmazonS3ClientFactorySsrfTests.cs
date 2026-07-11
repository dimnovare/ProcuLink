using System.Net.Http;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Infrastructure.Services.Ingress;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Tests.Services.Ingress;

/// <summary>
/// SSRF connect-time (DNS-rebind TOCTOU) hardening for the S3 / Cloudflare R2 ingress path
/// (audit finding #6). The up-front <see cref="OutboundRequestGuard.ValidateAsync"/> on a
/// tenant-supplied <c>ServiceUrl</c> resolves + validates DNS once; the AWS SDK then
/// RE-RESOLVES the host at TCP connect. A low-TTL malicious DNS server can answer "public" at
/// validation time and <c>169.254.169.254</c> (cloud metadata) / an RFC-1918 host a moment
/// later at connect. These tests prove the production <see cref="AmazonS3ClientFactory"/> now
/// injects the guarded transport (<see cref="OutboundRequestGuard.CreateGuardedHttpHandler"/>
/// via a <see cref="GuardedAwsHttpClientFactory"/>) so the connect-time re-validation + IP-pin
/// covers S3 ingress exactly as it already covers HTTP delivery / webhooks — and that the real
/// AWS SDK routes its data-plane requests through it.
/// </summary>
public class AmazonS3ClientFactorySsrfTests
{
    private static OutboundRequestGuard StrictGuard() =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                { ["Delivery:AllowPrivateNetworkTargets"] = "false" })
                .Build(),
            NullLogger<OutboundRequestGuard>.Instance);

    // ── 1. Wiring: the production factory sets the guarded transport on the S3 config ────────

    [Fact]
    public void Create_WiresGuardedAwsHttpClientFactoryOntoConfig()
    {
        var factory = new AmazonS3ClientFactory(StrictGuard());

        using var client = factory.Create("ak", "sk", "us-east-1", serviceUrl: null);

        client.Config.HttpClientFactory.Should().BeOfType<GuardedAwsHttpClientFactory>(
            "the S3 client must send through the SSRF connect-time-revalidating transport");
    }

    [Fact]
    public void Create_WithCustomServiceUrl_AlsoWiresGuardedTransport()
    {
        // Cloudflare R2 / MinIO / arbitrary S3-compatible endpoint — the actual SSRF vector.
        var factory = new AmazonS3ClientFactory(StrictGuard());

        using var client = factory.Create(
            "ak", "sk", "auto", serviceUrl: "https://example-account.r2.cloudflarestorage.com");

        client.Config.HttpClientFactory.Should().BeOfType<GuardedAwsHttpClientFactory>();
    }

    // ── 2. The guarded transport blocks the metadata IP at TCP connect ──────────────────────

    [Fact]
    public async Task GuardedTransport_BlocksMetadataIpAtConnect()
    {
        var httpFactory = new GuardedAwsHttpClientFactory(StrictGuard());
        var http = httpFactory.CreateHttpClient(new AmazonS3Config());

        // 169.254.169.254 is the cloud-metadata endpoint — the classic SSRF target. The
        // ConnectCallback re-resolves + re-validates it and refuses to open the socket.
        var act = async () => await http.GetAsync("http://169.254.169.254/latest/meta-data/");

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Contain("SSRF guard");
    }

    // ── 3. The REAL AWS SDK routes a data-plane request through the guarded transport ───────

    [Fact]
    public async Task AwsSdk_ListObjects_ToMetadataServiceUrl_IsBlockedByGuardedTransport()
    {
        // Build the S3 client with the SAME guarded transport type the production factory wires,
        // point its ServiceUrl at the metadata endpoint, and issue a real ListObjectsV2. If the
        // SDK sends through the guarded ConnectCallback (it must), the connect is blocked before
        // any bytes leave the host. MaxErrorRetry = 0 keeps the failure fast + deterministic.
        var config = new AmazonS3Config
        {
            ServiceURL        = "http://169.254.169.254",
            ForcePathStyle    = true,
            HttpClientFactory = new GuardedAwsHttpClientFactory(StrictGuard()),
            MaxErrorRetry     = 0,
        };
        using var client = new AmazonS3Client("ak", "sk", config);

        var act = async () =>
            await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = "any-bucket" });

        var ex = await act.Should().ThrowAsync<Exception>();
        FlattenMessages(ex.Which).Should().Contain("SSRF guard",
            "the AWS SDK must route ListObjectsV2 through the guarded ConnectCallback, which blocks the metadata IP");
    }

    // ── 4. Escape hatch: with private targets allowed the transport does NOT range-block ─────

    [Fact]
    public async Task GuardedTransport_AllowPrivate_DoesNotRangeBlock()
    {
        var permissive = new OutboundRequestGuard(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                { ["Delivery:AllowPrivateNetworkTargets"] = "true" })
                .Build(),
            NullLogger<OutboundRequestGuard>.Instance);

        var http = new GuardedAwsHttpClientFactory(permissive).CreateHttpClient(new AmazonS3Config());
        http.Timeout = TimeSpan.FromSeconds(5);

        // Port 1 has no listener → the connect fails, but NOT with the SSRF "blocked" message.
        var act = async () => await http.GetAsync("http://127.0.0.1:1/");

        var ex = await act.Should().ThrowAsync<Exception>();
        FlattenMessages(ex.Which).Should().NotContain("SSRF guard",
            "the dev/test escape hatch must skip the range block");
    }

    private static string FlattenMessages(Exception ex)
    {
        var sb = new StringBuilder();
        void Walk(Exception? e)
        {
            while (e is not null)
            {
                sb.Append(e.Message).Append(" | ");
                if (e is AggregateException agg)
                    foreach (var inner in agg.InnerExceptions) Walk(inner);
                e = e.InnerException;
            }
        }
        Walk(ex);
        return sb.ToString();
    }
}
