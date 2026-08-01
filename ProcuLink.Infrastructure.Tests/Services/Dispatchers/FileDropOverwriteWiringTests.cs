using System.Text;
using System.Text.Json;
using FluentAssertions;
using FluentFTP;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Security;
using Renci.SshNet;

namespace ProcuLink.Infrastructure.Tests.Services.Dispatchers;

/// <summary>
/// WP-20 D1 — the checkbox is CONNECTED.
///
/// <para>
/// <c>FileDropOverwriteTests</c> proves two halves and not the join:
/// <c>OverwriteExistingFromConfig</c> is exercised as a pure function, and
/// <c>UploadCore</c>/<c>UploadCoreAsync</c> are exercised with the flag handed to them as a
/// parameter. Both stayed green when the expression that carries the operator's saved setting from
/// the config onto the live upload was replaced with a hardcoded <c>true</c> — 3961 tests passed
/// while an operator's "do not replace files on this supplier's server" became a no-op for real
/// purchase orders.
/// </para>
///
/// <para>
/// These tests start where the live path starts: a <see cref="SupplierDeliveryConfig"/> row holding
/// the JSON an operator's save actually writes, handed to the real public <c>DispatchAsync</c>. The
/// only thing substituted is the network transport.
/// </para>
/// </summary>
public class FileDropOverwriteWiringTests
{
    private const string RemoteDir  = "/inbound/orders";
    private const string FileName   = "PO-123-a1b2c3d4.xml";
    private const string RemotePath = RemoteDir + "/" + FileName;

    // ── SFTP ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sftp_DispatchAsync_WithOverwriteOffInTheSavedConfig_RefusesToReplace()
    {
        var session = new FakeSftpSession();
        session.Files[RemotePath] = "THE-SUPPLIERS-EXISTING-FILE"u8.ToArray();

        var result = await DispatchSftpAsync(session, overwriteExisting: false);

        result.Success.Should().BeFalse(
            "the operator turned replace-existing OFF for this supplier, and the live path must honour it");
        result.ErrorMessage.Should().Contain("already on the supplier's server");
        session.Uploads.Should().Be(0, "nothing may be written to the supplier when the setting says do not replace");
        Encoding.UTF8.GetString(session.Files[RemotePath]).Should().Be("THE-SUPPLIERS-EXISTING-FILE");
    }

    [Fact]
    public async Task Sftp_DispatchAsync_WithOverwriteOffInTheSavedConfig_TellsTheUploadItMayNotOverride()
    {
        // The refusal above is one of two ways the setting reaches the wire. This is the other:
        // when the path is FREE the upload still happens, and it must be told it may not override —
        // otherwise a file appearing between the check and the write is silently replaced.
        var session = new FakeSftpSession();

        var result = await DispatchSftpAsync(session, overwriteExisting: false);

        result.Success.Should().BeTrue();
        session.LastCanOverride.Should().BeFalse(
            "the saved setting must reach the upload call itself, not only the pre-write existence check");
    }

    [Fact]
    public async Task Sftp_DispatchAsync_WithNoSettingSaved_Overwrites()
    {
        // The compatibility half of the same wire: every SFTP connection configured before this
        // setting existed has no such key and must keep replacing its own earlier file.
        var session = new FakeSftpSession();
        session.Files[RemotePath] = "TRUNCATED-BY-A-CRASH"u8.ToArray();

        var result = await DispatchSftpAsync(session, overwriteExisting: null);

        result.Success.Should().BeTrue();
        session.LastCanOverride.Should().BeTrue();
        Encoding.UTF8.GetString(session.Files[RemotePath]).Should().Be("NEW-COMPLETE-DOCUMENT");
    }

    [Fact]
    public async Task Sftp_DispatchAsync_WithOverwriteOnInTheSavedConfig_Overwrites()
    {
        var session = new FakeSftpSession();
        session.Files[RemotePath] = "TRUNCATED-BY-A-CRASH"u8.ToArray();

        var result = await DispatchSftpAsync(session, overwriteExisting: true);

        result.Success.Should().BeTrue();
        session.LastCanOverride.Should().BeTrue();
        Encoding.UTF8.GetString(session.Files[RemotePath]).Should().Be("NEW-COMPLETE-DOCUMENT");
    }

    // ── FTPS ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ftps_DispatchAsync_WithOverwriteOffInTheSavedConfig_RefusesToReplace()
    {
        var session = new FakeFtpsServer();
        session.Files[RemotePath] = "THE-SUPPLIERS-EXISTING-FILE"u8.ToArray();

        var result = await DispatchFtpsAsync(session, overwriteExisting: false);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already on the supplier's server");
        session.LastExistsMode.Should().Be(FtpRemoteExists.Skip);
        Encoding.UTF8.GetString(session.Files[RemotePath]).Should().Be("THE-SUPPLIERS-EXISTING-FILE");
    }

    [Fact]
    public async Task Ftps_DispatchAsync_WithNoSettingSaved_Overwrites()
    {
        var session = new FakeFtpsServer();
        session.Files[RemotePath] = "TRUNCATED-BY-A-CRASH"u8.ToArray();

        var result = await DispatchFtpsAsync(session, overwriteExisting: null);

        result.Success.Should().BeTrue();
        session.LastExistsMode.Should().Be(FtpRemoteExists.Overwrite);
        Encoding.UTF8.GetString(session.Files[RemotePath]).Should().Be("NEW-COMPLETE-DOCUMENT");
    }

    [Fact]
    public async Task Ftps_DispatchAsync_WithOverwriteOnInTheSavedConfig_Overwrites()
    {
        var session = new FakeFtpsServer();
        session.Files[RemotePath] = "TRUNCATED-BY-A-CRASH"u8.ToArray();

        var result = await DispatchFtpsAsync(session, overwriteExisting: true);

        result.Success.Should().BeTrue();
        session.LastExistsMode.Should().Be(FtpRemoteExists.Overwrite);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static Task<ProcuLink.Core.Services.Delivery.DeliveryResult> DispatchSftpAsync(
        FakeSftpSession session, bool? overwriteExisting)
    {
        var dispatcher = new SftpDeliveryDispatcher(
            NullLogger<SftpDeliveryDispatcher>.Instance, AllowAllGuard(), _ => session);

        return dispatcher.DispatchAsync(
            "NEW-COMPLETE-DOCUMENT"u8.ToArray(),
            FileName,
            "application/xml",
            ConfigRow(DeliveryProtocolConstants.Sftp, 22, overwriteExisting),
            JsonSerializer.Serialize(new { username = "sftp-user", password = "secret" }),
            CancellationToken.None);
    }

    private static Task<ProcuLink.Core.Services.Delivery.DeliveryResult> DispatchFtpsAsync(
        FakeFtpsServer session, bool? overwriteExisting)
    {
        var dispatcher = new FtpsDeliveryDispatcher(
            NullLogger<FtpsDeliveryDispatcher>.Instance, AllowAllGuard(), () => session);

        return dispatcher.DispatchAsync(
            "NEW-COMPLETE-DOCUMENT"u8.ToArray(),
            FileName,
            "application/xml",
            ConfigRow(DeliveryProtocolConstants.Ftps, 21, overwriteExisting),
            JsonSerializer.Serialize(new { username = "ftps-user", password = "secret" }),
            CancellationToken.None);
    }

    /// <summary>
    /// The stored row, with the config JSON shaped exactly the way the delivery-config editor's save
    /// writes it — the setting arrives as a key inside <c>ConfigJson</c> and nowhere else.
    /// </summary>
    private static SupplierDeliveryConfig ConfigRow(string protocol, int port, bool? overwriteExisting)
    {
        var json = overwriteExisting is null
            ? $"{{\"host\":\"drop.supplier.example\",\"port\":{port},\"remotePath\":\"{RemoteDir}\"}}"
            : $"{{\"host\":\"drop.supplier.example\",\"port\":{port},\"remotePath\":\"{RemoteDir}\"," +
              $"\"overwriteExisting\":{(overwriteExisting.Value ? "true" : "false")}}}";

        return new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            Protocol = protocol,
            AutoDeliver = true,
            ConfigJson = json,
            EncryptedCredentials = string.Empty,
        };
    }

    // Fictional hostnames do not resolve; the SSRF guard's own behaviour is covered in
    // OutboundRequestGuardTests, so it is waved through here.
    private static OutboundRequestGuard AllowAllGuard()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:AllowPrivateNetworkTargets"] = "true",
            })
            .Build();
        return new OutboundRequestGuard(cfg, NullLogger<OutboundRequestGuard>.Instance);
    }

    private sealed class FakeSftpSession : SftpDeliveryDispatcher.ISftpUploadSession
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public int Uploads { get; private set; }
        public bool? LastCanOverride { get; private set; }

        public bool Exists(string path) => Files.ContainsKey(path);
        public void CreateDirectory(string path) { }

        public void UploadFile(Stream input, string path, bool canOverride)
        {
            LastCanOverride = canOverride;
            Uploads++;
            using var ms = new MemoryStream();
            input.CopyTo(ms);
            Files[path] = ms.ToArray();
        }
    }

    /// <summary>
    /// An FTPS server the way FluentFTP reports one: <c>Skip</c> mode over an occupied path is a
    /// <c>Skipped</c> transfer that writes nothing, anything else overwrites and succeeds.
    /// </summary>
    private sealed class FakeFtpsServer : FtpsDeliveryDispatcher.IFtpsUploadSession
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public FtpRemoteExists? LastExistsMode { get; private set; }

        public async Task<FtpStatus> UploadStream(
            Stream input, string remotePath, FtpRemoteExists existsMode,
            bool createRemoteDir, CancellationToken token)
        {
            LastExistsMode = existsMode;

            if (existsMode == FtpRemoteExists.Skip && Files.ContainsKey(remotePath))
                return FtpStatus.Skipped;

            using var ms = new MemoryStream();
            await input.CopyToAsync(ms, token);
            Files[remotePath] = ms.ToArray();
            return FtpStatus.Success;
        }
    }
}
