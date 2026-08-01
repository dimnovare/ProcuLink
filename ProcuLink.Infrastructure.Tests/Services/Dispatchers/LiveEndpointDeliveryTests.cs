using System.Net.Http;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Security;
using ProcuLink.TestSupport;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services.Dispatchers;

/// <summary>
/// LIVE real-endpoint delivery test-fires — these construct the REAL dispatchers
/// (real HttpClient / MailKit SMTP / SSH.NET) and fire them at REAL external
/// endpoints, then the caller verifies receipt out-of-band. They close the gap
/// between the mocked unit tests and "does this actually work against a real
/// supplier endpoint?".
///
/// Gated behind PROCULINK_LIVE_ENDPOINT_TESTS=1 so the normal suite/CI skips the
/// real network calls. Endpoints + creds come from env vars (never committed):
///
///   PROCULINK_LIVE_ENDPOINT_TESTS=1
///   PROCULINK_LIVE_HTTP_BASE=https://&lt;worker&gt;.workers.dev   (OAuth2 token + /po receiver)
///   PROCULINK_LIVE_SMTP_HOST / _PORT / _USER / _PASS / _FROM / _TO
///   PROCULINK_LIVE_SFTP_HOST / _PORT / _USER / _PASS / _DIR
///
///   dotnet test ProcuLink.Infrastructure.Tests --filter "Category=LiveEndpoint"
/// </summary>
[Trait("Category", "LiveEndpoint")]
public class LiveEndpointDeliveryTests
{
    private static string Env(string k) => Environment.GetEnvironmentVariable(k) ?? "";

    // A real factory — returns a live HttpClient (no mocked handler).
    private sealed class RealHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient();
    }

    // Public hosts are allowed by default; AllowPrivateNetworkTargets also lets
    // localhost containers through for the SFTP/SMTP live tests.
    private static OutboundRequestGuard Guard()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:AllowPrivateNetworkTargets"] = "true",
            })
            .Build();
        return new OutboundRequestGuard(cfg, NullLogger<OutboundRequestGuard>.Instance);
    }

    private static SupplierDeliveryConfig HttpConfig(string url) => new()
    {
        Id = Guid.NewGuid(),
        OrgId = Guid.NewGuid(),
        SupplierId = Guid.NewGuid(),
        Protocol = "http",
        AutoDeliver = false,
        ConfigJson = JsonSerializer.Serialize(new { url, method = "POST", timeoutSeconds = 30 }),
        EncryptedCredentials = string.Empty,
    };

    // ── HTTP + OAuth2 client-credentials, fired at a real public Worker ───────
    [EnvironmentGatedFact(
        "requires a live HTTP endpoint that issues OAuth2 client-credentials tokens",
        LiveTestEnvironment.EndpointOptIn, "PROCULINK_LIVE_HTTP_BASE")]
    public async Task Live_Http_OAuth2_RealTokenFetchAndBearerDelivery()
    {
        var baseUrl = Env("PROCULINK_LIVE_HTTP_BASE").TrimEnd('/');

        var dispatcher = new HttpDeliveryDispatcher(
            new RealHttpClientFactory(), Guard(), NullLogger<HttpDeliveryDispatcher>.Instance);

        var config = HttpConfig($"{baseUrl}/po");
        var creds = JsonSerializer.Serialize(new
        {
            type = "oauth2_client_credentials",
            tokenUrl = $"{baseUrl}/token",
            clientId = "proculink-test-client",
            clientSecret = "test-secret-9f3a2b",
            scope = "orders.write",
        });
        var payload = Encoding.UTF8.GetBytes(
            "<cXML><Request><OrderRequest po=\"PO-LIVE-OAUTH-001\"/></Request></cXML>");

        var result = await dispatcher.DispatchAsync(
            payload, "PO-LIVE-OAUTH-001.xml", "application/xml", config, creds, default);

        result.Success.Should().BeTrue(because: $"the real OAuth2 delivery should succeed; error: {result.ErrorMessage}");
        result.ResponseCode.Should().Be(200);
    }

    // ── Plain HTTP POST (no auth), fired at a real public Worker ──────────────
    [EnvironmentGatedFact(
        "requires a live HTTP endpoint that accepts an unauthenticated POST",
        LiveTestEnvironment.EndpointOptIn, "PROCULINK_LIVE_HTTP_BASE")]
    public async Task Live_Http_PlainPost_RealDelivery()
    {
        var baseUrl = Env("PROCULINK_LIVE_HTTP_BASE").TrimEnd('/');

        var dispatcher = new HttpDeliveryDispatcher(
            new RealHttpClientFactory(), Guard(), NullLogger<HttpDeliveryDispatcher>.Instance);

        var config = HttpConfig($"{baseUrl}/po-plain");
        var creds = JsonSerializer.Serialize(new { type = "none" });

        var result = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes("po_number,date\r\nPO-LIVE-PLAIN-002,2026-06-05"),
            "PO-LIVE-PLAIN-002.csv", "text/csv", config, creds, default);

        result.Success.Should().BeTrue(because: $"the real plain HTTP delivery should succeed; error: {result.ErrorMessage}");
        result.ResponseCode.Should().Be(200);
    }

    // ── SMTP delivery, fired at a real mailbox (Ethereal) ─────────────────────
    [EnvironmentGatedFact(
        "requires a live SMTP relay and a mailbox to deliver into",
        LiveTestEnvironment.EndpointOptIn,
        "PROCULINK_LIVE_SMTP_HOST", "PROCULINK_LIVE_SMTP_FROM", "PROCULINK_LIVE_SMTP_TO")]
    public async Task Live_Smtp_RealSendToMailbox()
    {
        var host = Env("PROCULINK_LIVE_SMTP_HOST");

        var dispatcher = new SmtpDeliveryDispatcher(NullLogger<SmtpDeliveryDispatcher>.Instance, Guard());

        var config = new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            Protocol = "smtp",
            AutoDeliver = false,
            ConfigJson = JsonSerializer.Serialize(new
            {
                host,
                port = int.TryParse(Env("PROCULINK_LIVE_SMTP_PORT"), out var p) ? p : 587,
                useSsl = Env("PROCULINK_LIVE_SMTP_SSL") == "1",
                fromAddress = Env("PROCULINK_LIVE_SMTP_FROM"),
                toAddresses = new[] { Env("PROCULINK_LIVE_SMTP_TO") },
                subjectTemplate = "ProcuLink live PO {poNumber}",
                timeoutSeconds = 30,
            }),
            EncryptedCredentials = string.Empty,
        };
        var creds = JsonSerializer.Serialize(new
        {
            username = Env("PROCULINK_LIVE_SMTP_USER"),
            password = Env("PROCULINK_LIVE_SMTP_PASS"),
        });
        var payload = Encoding.UTF8.GetBytes(
            "<cXML><Request><OrderRequest po=\"PO-LIVE-SMTP-003\"/></Request></cXML>");

        var result = await dispatcher.DispatchAsync(
            payload, "PO-LIVE-SMTP-003.xml", "application/xml", config, creds, default);

        result.Success.Should().BeTrue(because: $"the real SMTP send should be accepted by the relay; error: {result.ErrorMessage}");
    }

    // ── SFTP delivery, fired at a real SFTP server ────────────────────────────
    [EnvironmentGatedFact(
        "requires a live SFTP server to upload into",
        LiveTestEnvironment.EndpointOptIn,
        "PROCULINK_LIVE_SFTP_HOST", "PROCULINK_LIVE_SFTP_USER", "PROCULINK_LIVE_SFTP_PASS")]
    public async Task Live_Sftp_RealUpload()
    {
        var host = Env("PROCULINK_LIVE_SFTP_HOST");

        var dispatcher = new SftpDeliveryDispatcher(NullLogger<SftpDeliveryDispatcher>.Instance, Guard());

        var config = new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            Protocol = "sftp",
            AutoDeliver = false,
            ConfigJson = JsonSerializer.Serialize(new
            {
                host,
                port = int.TryParse(Env("PROCULINK_LIVE_SFTP_PORT"), out var p) ? p : 22,
                remotePath = Env("PROCULINK_LIVE_SFTP_DIR"),
                makeDirectories = true,
                timeoutSeconds = 30,
            }),
            EncryptedCredentials = string.Empty,
        };
        var creds = JsonSerializer.Serialize(new
        {
            username = Env("PROCULINK_LIVE_SFTP_USER"),
            password = Env("PROCULINK_LIVE_SFTP_PASS"),
        });
        var payload = Encoding.UTF8.GetBytes(
            "<cXML><Request><OrderRequest po=\"PO-LIVE-SFTP-004\"/></Request></cXML>");

        var result = await dispatcher.DispatchAsync(
            payload, "PO-LIVE-SFTP-004.xml", "application/xml", config, creds, default);

        result.Success.Should().BeTrue(because: $"the real SFTP upload should succeed; error: {result.ErrorMessage}");
    }

    // ── SSH host-key verification, against a real SSH server (WP-38) ──────────
    //
    // These are the only tests that exercise the ONE step no fake can reach: the SSH.NET
    // HostKeyReceived subscription itself, raised by a real key exchange with a real server. The
    // unit tests prove the decision; these prove it is consulted.
    //
    // Reproduce in about a minute (docs/ops/2026-08-01-wp38-delivery-channel-proof.md §7):
    //
    //   docker run -d --name wp38-sftp -p 2222:22 atmoz/sftp:alpine plkuser:plkpass:::upload
    //   docker exec wp38-sftp ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
    //
    //   PROCULINK_LIVE_ENDPOINT_TESTS=1
    //   PROCULINK_LIVE_SFTP_HOST=127.0.0.1  PROCULINK_LIVE_SFTP_PORT=2222
    //   PROCULINK_LIVE_SFTP_USER=plkuser    PROCULINK_LIVE_SFTP_PASS=plkpass
    //   PROCULINK_LIVE_SFTP_DIR=/upload
    //   PROCULINK_LIVE_SFTP_FINGERPRINT=SHA256:<the ssh-keygen value above>

    private static SupplierDeliveryConfig LiveSftpConfig(string? hostKeyFingerprint)
    {
        var settings = new Dictionary<string, object?>
        {
            ["host"] = Env("PROCULINK_LIVE_SFTP_HOST"),
            ["port"] = int.TryParse(Env("PROCULINK_LIVE_SFTP_PORT"), out var p) ? p : 22,
            ["remotePath"] = Env("PROCULINK_LIVE_SFTP_DIR"),
            ["makeDirectories"] = true,
            ["timeoutSeconds"] = 30,
        };
        if (hostKeyFingerprint is not null)
            settings["hostKeyFingerprints"] = new[] { hostKeyFingerprint };

        return new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            Protocol = "sftp",
            AutoDeliver = false,
            ConfigJson = JsonSerializer.Serialize(settings),
            EncryptedCredentials = string.Empty,
        };
    }

    private static string LiveSftpCreds() => JsonSerializer.Serialize(new
    {
        username = Env("PROCULINK_LIVE_SFTP_USER"),
        password = Env("PROCULINK_LIVE_SFTP_PASS"),
    });

    private static readonly byte[] LivePayload =
        Encoding.UTF8.GetBytes("<cXML><Request><OrderRequest po=\"PO-LIVE-HOSTKEY\"/></Request></cXML>");

    /// <summary>
    /// Trust-on-first-use against a real server, cross-checked against a fingerprint this code did
    /// not compute. <c>PROCULINK_LIVE_SFTP_FINGERPRINT</c> comes from OpenSSH's own
    /// <c>ssh-keygen -lf</c>, so agreement proves the recorded value is the string an operator will
    /// see in their own terminal — the only thing that makes the re-trust decision verifiable.
    /// </summary>
    [EnvironmentGatedFact(
        "requires a live SFTP server and its ssh-keygen fingerprint",
        LiveTestEnvironment.EndpointOptIn,
        "PROCULINK_LIVE_SFTP_HOST", "PROCULINK_LIVE_SFTP_USER", "PROCULINK_LIVE_SFTP_PASS",
        "PROCULINK_LIVE_SFTP_FINGERPRINT")]
    public async Task Live_Sftp_HostKey_RecordedOnFirstUse_MatchesSshKeygen()
    {
        var dispatcher = new SftpDeliveryDispatcher(NullLogger<SftpDeliveryDispatcher>.Instance, Guard());

        var result = await dispatcher.DispatchAsync(
            LivePayload, "PO-LIVE-HOSTKEY-TOFU.xml", "application/xml",
            LiveSftpConfig(hostKeyFingerprint: null), LiveSftpCreds(), default);

        result.Success.Should().BeTrue(because: $"error: {result.ErrorMessage}");
        result.LearnedHostKeyFingerprint.Should().Be(
            Env("PROCULINK_LIVE_SFTP_FINGERPRINT"),
            "the recorded fingerprint must be byte-identical to what ssh-keygen prints for this server");
    }

    /// <summary>
    /// The exact experiment the proof run performed, with the opposite outcome. Host, port, username
    /// and password are unchanged; only the identity the connection expects differs. Before this
    /// existed the upload succeeded and the password was handed to whoever answered.
    /// </summary>
    [EnvironmentGatedFact(
        "requires a live SFTP server to be refused by",
        LiveTestEnvironment.EndpointOptIn,
        "PROCULINK_LIVE_SFTP_HOST", "PROCULINK_LIVE_SFTP_USER", "PROCULINK_LIVE_SFTP_PASS",
        "PROCULINK_LIVE_SFTP_FINGERPRINT")]
    public async Task Live_Sftp_ChangedHostKey_IsRefusedWithAnActionableMessage()
    {
        const string someoneElse = "SHA256:ai1X2iIAsJtHWuquGw8cQxn5DUD55PDciTIy6PfdAmw";
        var dispatcher = new SftpDeliveryDispatcher(NullLogger<SftpDeliveryDispatcher>.Instance, Guard());

        var result = await dispatcher.DispatchAsync(
            LivePayload, "PO-LIVE-HOSTKEY-REFUSED.xml", "application/xml",
            LiveSftpConfig(someoneElse), LiveSftpCreds(), default);

        result.Success.Should().BeFalse("a server whose identity does not match the pin must not receive the order");

        // The library's own refusal is "Key exchange negotiation failed." — WP-38's acceptance
        // criterion is an ACTIONABLE message, so ours must name both fingerprints and the next step.
        result.ErrorMessage.Should().NotBeNull();
        result.ErrorMessage!.Should().Contain(Env("PROCULINK_LIVE_SFTP_FINGERPRINT"));
        result.ErrorMessage.Should().Contain(someoneElse);
        result.ErrorMessage.Should().NotContain("Key exchange negotiation failed");

        result.LearnedHostKeyFingerprint.Should().BeNull(
            "a refused key must never be recorded — that would re-pin the very server just refused");
    }

    [EnvironmentGatedFact(
        "requires a live SFTP server with a known fingerprint",
        LiveTestEnvironment.EndpointOptIn,
        "PROCULINK_LIVE_SFTP_HOST", "PROCULINK_LIVE_SFTP_USER", "PROCULINK_LIVE_SFTP_PASS",
        "PROCULINK_LIVE_SFTP_FINGERPRINT")]
    public async Task Live_Sftp_PinnedHostKey_StillUploads()
    {
        var dispatcher = new SftpDeliveryDispatcher(NullLogger<SftpDeliveryDispatcher>.Instance, Guard());

        var result = await dispatcher.DispatchAsync(
            LivePayload, "PO-LIVE-HOSTKEY-PINNED.xml", "application/xml",
            LiveSftpConfig(Env("PROCULINK_LIVE_SFTP_FINGERPRINT")), LiveSftpCreds(), default);

        result.Success.Should().BeTrue(
            because: $"verification must not break the connections it protects; error: {result.ErrorMessage}");
        result.LearnedHostKeyFingerprint.Should().BeNull("an already-pinned key teaches nothing new");
    }
}
