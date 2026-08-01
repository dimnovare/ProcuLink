using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Tests.Services.Dispatchers;

/// <summary>
/// Drives <see cref="FtpsDeliveryDispatcher"/> through a REAL TLS handshake against an in-process
/// FTPS server that presents a self-signed certificate, and asserts on what the operator is told.
///
/// <para>
/// Why a real handshake rather than another <c>ShouldAcceptCertificate</c> unit test: the existing
/// tests exercise that pure helper only, so the wiring that consults it —
/// <c>ValidateAnyCertificate = false</c> plus the <c>ValidateCertificate</c> subscription — is
/// reachable by no test at all. Deleting either line, or flipping <c>ValidateAnyCertificate</c> to
/// <c>true</c> (which per FluentFTP's own documentation means the callback is never fired and every
/// certificate is accepted), leaves the whole existing suite green while every FTPS delivery silently
/// stops checking who it is talking to. These tests fail in that case, because a dispatcher that
/// stopped validating would complete the handshake and report a later, different failure.
/// </para>
///
/// <para>
/// The server is a loopback stub, not a container: it speaks just enough FTP to reach <c>AUTH TLS</c>
/// and then offers a certificate no CA signed. Live confirmation against a real pure-ftpd carrying a
/// self-signed certificate is recorded in <c>docs/ops/2026-08-01-wp38-delivery-channel-proof.md</c>.
/// </para>
/// </summary>
public class FtpsCertificateRejectionTests
{
    private static OutboundRequestGuard LoopbackGuard()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // 127.0.0.1 is a private target; without this the SSRF guard would block the
                // connection before TLS, and the test would prove nothing about certificates.
                ["Delivery:AllowPrivateNetworkTargets"] = "true",
            })
            .Build();
        return new OutboundRequestGuard(cfg, NullLogger<OutboundRequestGuard>.Instance);
    }

    [Fact]
    public async Task UntrustedCertificate_WithOverrideOff_TellsTheOperatorItWasTheCertificate()
    {
        using var server = SelfSignedFtpsServer.Start();

        var result = await DispatchAsync(server.Port, allowInvalidCertificate: null);

        result.Success.Should().BeFalse(
            "a certificate no CA signed must not be accepted when the operator has not opted in");

        // The whole point of the assertion: the operator must be able to act on this without a log.
        result.ErrorMessage.Should().NotBeNull();
        result.ErrorMessage!.Should().Contain("certificate",
            "the operator cannot fix a certificate problem they are not told about");
        result.ErrorMessage.Should().Contain("Allow invalid certificate",
            "the message must name the setting that resolves it");
        result.ErrorMessage.Should().NotBe("FTPS delivery failed before the upload could complete.",
            "the generic catch-all hides the only fact that makes this fixable");
    }

    [Fact]
    public async Task UntrustedCertificate_WithOverrideExplicitlyOff_IsStillRejected()
    {
        using var server = SelfSignedFtpsServer.Start();

        var result = await DispatchAsync(server.Port, allowInvalidCertificate: false);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(FtpsDeliveryDispatcher.DescribeTlsHandshakeFailure(false));
    }

    [Fact]
    public async Task UntrustedCertificate_WithOverrideOn_CompletesTheHandshakeAndFailsLater()
    {
        using var server = SelfSignedFtpsServer.Start();

        var result = await DispatchAsync(server.Port, allowInvalidCertificate: true);

        // The stub server hangs up once TLS is established — it never implements USER/PASS/STOR — so
        // the delivery still fails. What matters is that it no longer fails AT THE CERTIFICATE:
        // reaching a post-handshake failure is the observable difference between "we validated and
        // refused" and "we accepted this certificate". If validation were removed from the dispatcher,
        // the override-off tests above would land here too and stop distinguishing anything.
        result.Success.Should().BeFalse("the stub server implements no login, so the upload cannot succeed");
        result.ErrorMessage.Should().NotBe(FtpsDeliveryDispatcher.DescribeTlsHandshakeFailure(false),
            "with the override on, the certificate must no longer be the reported cause");
    }

    [Fact]
    public void TheTwoHandshakeMessagesDifferAndBothNameANextStep()
    {
        var off = FtpsDeliveryDispatcher.DescribeTlsHandshakeFailure(false);
        var on = FtpsDeliveryDispatcher.DescribeTlsHandshakeFailure(true);

        off.Should().NotBe(on, "the two situations need opposite next steps");
        off.Should().Contain("Allow invalid certificate");
        on.Should().Contain("TLS versions and ciphers",
            "with the override already on, pointing at the certificate sends the operator nowhere");
        on.Should().NotContain("turn on \"Allow invalid certificate\"",
            "it is already on — telling them to turn it on is the wrong next step");
    }

    private static async Task<Core.Services.Delivery.DeliveryResult> DispatchAsync(
        int port,
        bool? allowInvalidCertificate)
    {
        var overrideJson = allowInvalidCertificate is null
            ? string.Empty
            : $",\"allowInvalidCertificate\":{(allowInvalidCertificate.Value ? "true" : "false")}";

        var config = new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            Protocol = "ftps",
            ConfigJson =
                $"{{\"host\":\"127.0.0.1\",\"port\":{port},\"remotePath\":\"/\"," +
                $"\"makeDirectories\":false,\"timeoutSeconds\":10{overrideJson}}}",
        };

        var dispatcher = new FtpsDeliveryDispatcher(
            NullLogger<FtpsDeliveryDispatcher>.Instance,
            LoopbackGuard());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes("<PurchaseOrder><PoNumber>WP38</PoNumber></PurchaseOrder>"),
            "PO-WP38.xml",
            "application/xml",
            config,
            "{\"username\":\"plkuser\",\"password\":\"plkpass\"}",
            cts.Token);
    }

    /// <summary>
    /// A loopback FTP server that answers the greeting and <c>AUTH TLS</c>, then presents a
    /// self-signed certificate. Nothing beyond the handshake is implemented — everything this test
    /// class asserts is decided by then.
    /// </summary>
    private sealed class SelfSignedFtpsServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly X509Certificate2 _certificate;
        private readonly Task _loop;

        public int Port { get; }

        private SelfSignedFtpsServer(TcpListener listener, X509Certificate2 certificate)
        {
            _listener = listener;
            _certificate = certificate;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public static SelfSignedFtpsServer Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new SelfSignedFtpsServer(listener, CreateSelfSignedCertificate());
        }

        private static X509Certificate2 CreateSelfSignedCertificate()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());

            using var ephemeral = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

            // Round-tripping through PKCS#12 is what makes the private key usable by SslStream on
            // Windows; without it AuthenticateAsServerAsync fails for a reason unrelated to this test.
            return new X509Certificate2(ephemeral.Export(X509ContentType.Pfx));
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(ct); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { return; }

                _ = Task.Run(() => ServeAsync(client, ct), ct);
            }
        }

        private async Task ServeAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    await WriteLineAsync(stream, "220 wp38 self-signed test server", ct);

                    // Answer commands until AUTH arrives; FluentFTP sends AUTH TLS first in explicit mode.
                    for (var i = 0; i < 10; i++)
                    {
                        var line = await ReadLineAsync(stream, ct);
                        if (line is null) return;

                        if (line.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase))
                        {
                            await WriteLineAsync(stream, "234 AUTH TLS OK", ct);
                            break;
                        }

                        await WriteLineAsync(stream, "500 not implemented", ct);
                    }

                    await using var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    try
                    {
                        await ssl.AuthenticateAsServerAsync(
                            _certificate, clientCertificateRequired: false,
                            SslProtocols.None, checkCertificateRevocation: false);
                    }
                    catch
                    {
                        // Expected on the override-off runs: the client refused our certificate.
                        return;
                    }

                    // Handshake accepted (override-on run). Say nothing useful and hang up — the point
                    // of that case is only that the failure moved past the certificate.
                    await WriteLineAsync(ssl, "421 wp38 stub goes no further", ct);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
            {
                // Client-side teardown; the assertions live in the test, not here.
            }
        }

        private static async Task WriteLineAsync(Stream stream, string line, CancellationToken ct)
        {
            var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }

        private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var buffer = new byte[1];
            while (sb.Length < 512)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0) return sb.Length == 0 ? null : sb.ToString();
                if (buffer[0] == (byte)'\n') return sb.ToString().TrimEnd('\r');
                sb.Append((char)buffer[0]);
            }

            return sb.ToString();
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch (SocketException) { /* already down */ }
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { /* torn down */ }
            _certificate.Dispose();
            _cts.Dispose();
        }
    }
}
