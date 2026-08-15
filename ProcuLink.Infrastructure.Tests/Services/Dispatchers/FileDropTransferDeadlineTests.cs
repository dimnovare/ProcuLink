using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Security;
using ProcuLink.Infrastructure.Tests.TestDoubles;

namespace ProcuLink.Infrastructure.Tests.Services.Dispatchers;

/// <summary>
/// B-15 — <i>"One order sat 'sending' for hours."</i>
///
/// <para>
/// The SFTP dispatcher set <c>ConnectionInfo.Timeout</c>, which bounds the CONNECT and nothing else,
/// and left <c>SftpClient.OperationTimeout</c> at its default — negative one millisecond, which
/// SSH.NET documents as "an infinite timeout period". A supplier server that completed the handshake
/// and then stopped reading held the Hangfire job on that thread with no deadline at all.
/// </para>
///
/// <para><b>The stalled server here does not observe cancellation.</b> That is the entire design of
/// these tests. A transfer that politely returns when asked would pass against a token that is
/// merely handed down and never enforced — and "the token was passed" is exactly what was true
/// before, on the FTPS side, while the guarantee was still only as good as the library. What has to
/// hold is that the dispatcher gives up on its own.
/// </para>
/// <para>
/// Each stall test is paired with a control on the same configuration, because a deadline test that
/// passes because nothing ever ran is worth nothing: the control proves an ordinary transfer still
/// completes, and both assert the transfer was actually entered.
/// </para>
/// </summary>
public class FileDropTransferDeadlineTests
{
    private const int TimeoutSeconds = 2;

    /// <summary>
    /// How long a mutation that removes the bound is allowed to hang before the test calls it: far
    /// beyond the deadline, far short of forever. Without this an unbounded transfer would hang the
    /// suite instead of failing it, and a hang is not a red test.
    /// </summary>
    private static readonly TimeSpan HangBudget = TimeSpan.FromSeconds(20);

    // ── SFTP ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sftp_AStalledTransfer_EndsAtTheDeadline_NotNever()
    {
        var server = new FakeSftpServer { StallForever = true };
        var started = Stopwatch.StartNew();

        var result = await WithinHangBudget(DispatchSftp(server));

        started.Elapsed.Should().BeLessThan(HangBudget);
        server.UploadStarted.Should().BeTrue(
            "the deadline must be proven against a transfer that actually began — a timeout reached "
            + "before the upload starts proves the connect path, not the transfer");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain($"timed out after {TimeoutSeconds} seconds");
    }

    [Fact]
    public async Task Sftp_AnOrdinaryTransfer_CompletesOnTheSameConfiguration()
    {
        // The control. Without it, a deadline set to fire immediately would pass the test above.
        var server = new FakeSftpServer();

        var result = await WithinHangBudget(DispatchSftp(server));

        server.UploadStarted.Should().BeTrue();
        result.Success.Should().BeTrue(because: result.ErrorMessage);
    }

    [Fact]
    public async Task Sftp_ATimeoutSaysNothingPartialIsReadableAtTheSuppliersFileName()
    {
        // The claim is only true BECAUSE of the temporary-name write (B-14). If the transfer ever
        // goes back to targeting the supplier's file name directly, this sentence becomes a lie and
        // this test is where that is caught.
        var server = new FakeSftpServer { StallForever = true };

        var result = await WithinHangBudget(DispatchSftp(server));

        result.ErrorMessage.Should().Contain("Nothing incomplete is readable");
        server.LastUploadPath.Should().Be(
            SftpDeliveryDispatcher.PartialUploadPath($"{RemoteDir}/{FileName}"));
    }

    // ── FTPS ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ftps_AStalledTransfer_EndsAtTheDeadline_NotNever()
    {
        var server = new FakeFtpsServer { StallForever = true };
        var started = Stopwatch.StartNew();

        var result = await WithinHangBudget(DispatchFtps(server));

        started.Elapsed.Should().BeLessThan(HangBudget);
        server.UploadStarted.Should().BeTrue();

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain($"timed out after {TimeoutSeconds} seconds");
    }

    [Fact]
    public async Task Ftps_AnOrdinaryTransfer_CompletesOnTheSameConfiguration()
    {
        var server = new FakeFtpsServer();

        var result = await WithinHangBudget(DispatchFtps(server));

        server.UploadStarted.Should().BeTrue();
        result.Success.Should().BeTrue(because: result.ErrorMessage);
    }

    // ── The shared sentence ───────────────────────────────────────────────────

    [Theory]
    [InlineData("SFTP")]
    [InlineData("FTPS")]
    public void TheTimeoutMessage_NamesTheChannelTheOperatorConfigured_AndTheirOwnNumber(string channel)
    {
        var message = SftpDeliveryDispatcher.TransferTimedOut(channel, 45);

        message.Should().StartWith($"{channel} delivery timed out after 45 seconds");
        message.Should().Contain("raise the timeout on this connection",
            "the operator needs the one setting that changes the outcome");
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private const string RemoteDir = "/inbound/orders";
    private const string FileName  = "PO-123-a1b2c3d4.xml";

    /// <summary>
    /// Bounds the WAIT, not the dispatch. A dispatcher that has lost its deadline would otherwise
    /// hang this test forever instead of failing it, and a hung suite reads as infrastructure rather
    /// than as the regression it is.
    /// </summary>
    private static async Task<DeliveryResult> WithinHangBudget(Task<DeliveryResult> dispatch)
    {
        var finished = await Task.WhenAny(dispatch, Task.Delay(HangBudget));
        finished.Should().BeSameAs(dispatch,
            $"the dispatcher must give up on its own within {HangBudget.TotalSeconds:0} seconds; "
            + "it is the only thing standing between a stalled supplier server and an order stuck in "
            + "`delivering` indefinitely");
        return await dispatch;
    }

    private static Task<DeliveryResult> DispatchSftp(FakeSftpServer server) =>
        new SftpDeliveryDispatcher(
            NullLogger<SftpDeliveryDispatcher>.Instance, AllowAllGuard(), _ => server)
        .DispatchAsync(
            "PURCHASE-ORDER-DOCUMENT"u8.ToArray(), FileName, "application/xml",
            ConfigRow(DeliveryProtocolConstants.Sftp, 22),
            JsonSerializer.Serialize(new { username = "sftp-user", password = "secret" }),
            CancellationToken.None);

    private static Task<DeliveryResult> DispatchFtps(FakeFtpsServer server) =>
        new FtpsDeliveryDispatcher(
            NullLogger<FtpsDeliveryDispatcher>.Instance, AllowAllGuard(), () => server)
        .DispatchAsync(
            "PURCHASE-ORDER-DOCUMENT"u8.ToArray(), FileName, "application/xml",
            ConfigRow(DeliveryProtocolConstants.Ftps, 21),
            JsonSerializer.Serialize(new { username = "ftps-user", password = "secret" }),
            CancellationToken.None);

    private static SupplierDeliveryConfig ConfigRow(string protocol, int port) => new()
    {
        Id = Guid.NewGuid(),
        OrgId = Guid.NewGuid(),
        SupplierId = Guid.NewGuid(),
        Protocol = protocol,
        AutoDeliver = true,
        ConfigJson = $"{{\"host\":\"drop.supplier.example\",\"port\":{port}," +
                     $"\"remotePath\":\"{RemoteDir}\",\"timeoutSeconds\":{TimeoutSeconds}}}",
        EncryptedCredentials = string.Empty,
    };

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
}
