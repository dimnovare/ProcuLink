using System.Text;
using FluentAssertions;
using FluentFTP;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Tests.TestDoubles;

namespace ProcuLink.Infrastructure.Tests.Services.Dispatchers;

/// <summary>
/// WP-20 — the overwrite decision on the SFTP/FTPS live upload path.
///
/// <para>
/// These tests drive the REAL <c>UploadCoreAsync</c> through a fake session, not a re-implementation
/// of the decision: hardcoding the replace-existing argument, or dead-coding the pre-write existence
/// check, fails them. That was the point — an earlier attempt at this packet left ~20 new lines on
/// the live SFTP path with no coverage at all.
/// </para>
/// <para>
/// B-14 moved WHERE the decision is enforced without changing what it means. The upload no longer
/// targets the supplier's file name at all, so "may this replace an existing file?" is now a
/// property of the MOVE into place, and that is what these assert on. The pre-transfer refusal is
/// unchanged.
/// </para>
/// </summary>
public class FileDropOverwriteTests
{
    private const string OrderPath = "/in/PO-1-abcd1234.xml";

    // ── The default: overwrite ON ─────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"host\":\"sftp.vendor.test\",\"remotePath\":\"/in\"}")]
    [InlineData("{\"overwriteExisting\":true}")]
    [InlineData("{not-json")]
    public void ConfigsWithoutAnExplicitOptOut_Overwrite(string? configJson)
    {
        // Every SFTP/FTPS connection configured before this setting existed has no such key, and
        // must keep behaving exactly as it did — a nightly job that replaces a file it expects to
        // replace cannot start failing on deploy.
        SftpDeliveryDispatcher.OverwriteExistingFromConfig(configJson).Should().BeTrue();
    }

    [Theory]
    [InlineData("{\"overwriteExisting\":false}")]
    [InlineData("{\"host\":\"sftp.vendor.test\",\"overwriteExisting\":false}")]
    public void OnlyAnExplicitFalse_TurnsOverwriteOff(string configJson)
    {
        SftpDeliveryDispatcher.OverwriteExistingFromConfig(configJson).Should().BeFalse();
    }

    [Theory]
    [InlineData("{\"overwriteExisting\":\"false\"}")]   // a string, not a boolean
    [InlineData("{\"overwriteExisting\":null}")]
    [InlineData("{\"overwriteExisting\":0}")]
    public void ANonBooleanValue_IsNotAnOptOut(string configJson)
    {
        SftpDeliveryDispatcher.OverwriteExistingFromConfig(configJson).Should().BeTrue();
    }

    // ── SFTP upload path ──────────────────────────────────────────────────────

    [Fact]
    public async Task Sftp_OverwriteOn_ReplacesItsOwnEarlierFile()
    {
        var server = new FakeSftpServer();
        server.Files[OrderPath] = "TRUNCATED-BY-A-CRASH"u8.ToArray();

        var result = await Upload(server, "NEW-COMPLETE-DOCUMENT", OrderPath, overwriteExisting: true);

        result.Success.Should().BeTrue();
        Encoding.UTF8.GetString(server.Files[OrderPath]).Should().Be("NEW-COMPLETE-DOCUMENT",
            "a re-drive must be able to repair a partial upload of its own order");
        server.Renames.Should().ContainSingle().Which.ReplaceExisting.Should().BeTrue(
            "the move into place must be told it may replace the file");
    }

    [Fact]
    public async Task Sftp_OverwriteOff_RefusesRatherThanReplacing()
    {
        var server = new FakeSftpServer();
        server.Files[OrderPath] = "ALREADY-THERE"u8.ToArray();

        var result = await Upload(server, "NEW-DOCUMENT", OrderPath, overwriteExisting: false);

        result.Success.Should().BeFalse("a refused write is a failed delivery, never a silent success");
        result.ErrorMessage.Should().Contain("already on the supplier's server");
        server.Uploads.Should().Be(0, "nothing may be written when the operator said do not replace");
        Encoding.UTF8.GetString(server.Files[OrderPath]).Should().Be("ALREADY-THERE");
    }

    [Fact]
    public async Task Sftp_OverwriteOff_StillWritesWhenThePathIsFree()
    {
        var server = new FakeSftpServer();

        var result = await Upload(server, "NEW-DOCUMENT", OrderPath, overwriteExisting: false);

        result.Success.Should().BeTrue();
        server.Renames.Should().ContainSingle().Which.ReplaceExisting.Should().BeFalse(
            "the saved setting must reach the move itself, not only the pre-transfer existence check — "
            + "a plain SFTP rename refuses an occupied destination, which is what closes the race");
        Encoding.UTF8.GetString(server.Files[OrderPath]).Should().Be("NEW-DOCUMENT");
    }

    [Fact]
    public async Task Sftp_OverwriteOff_AFileAppearingDuringTheTransfer_IsNotReplaced()
    {
        // The race the pre-transfer check on its own cannot close: the path is free when we look,
        // and occupied by the time we publish. The rename is the only thing that decides this
        // atomically, and with overwrite off it must lose.
        var server = new FakeSftpServer();
        server.MidTransfer = () =>
        {
            server.Files[OrderPath] = "ARRIVED-WHILE-WE-WERE-UPLOADING"u8.ToArray();
            return Task.CompletedTask;
        };

        var result = await Upload(server, "NEW-DOCUMENT", OrderPath, overwriteExisting: false);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already on the supplier's server");
        Encoding.UTF8.GetString(server.Files[OrderPath]).Should().Be("ARRIVED-WHILE-WE-WERE-UPLOADING");
    }

    [Fact]
    public async Task Sftp_MakeDirectories_CreatesTheMissingTree()
    {
        var server = new FakeSftpServer();

        var result = await Upload(server, "DOC", "/in/orders/PO-1-abcd1234.xml",
                                  overwriteExisting: true, makeDirectories: true);

        result.Success.Should().BeTrue();
        server.CreatedDirectories.Should().Contain("/in/orders");
    }

    // ── FTPS upload path ──────────────────────────────────────────────────────

    [Fact]
    public async Task Ftps_OverwriteOn_MovesWithOverwriteMode()
    {
        var server = new FakeFtpsServer();
        server.Files[OrderPath] = "TRUNCATED-BY-A-CRASH"u8.ToArray();

        var result = await FtpsDeliveryDispatcher.UploadCoreAsync(
            server, "DOC"u8.ToArray(), OrderPath,
            makeDirectories: true, overwriteExisting: true, CancellationToken.None);

        result.Success.Should().BeTrue();
        server.Moves.Should().ContainSingle().Which.ExistsMode.Should().Be(FtpRemoteExists.Overwrite);
        server.LastCreateRemoteDir.Should().BeTrue();
        Encoding.UTF8.GetString(server.Files[OrderPath]).Should().Be("DOC");
    }

    [Fact]
    public async Task Ftps_OverwriteOff_MovesWithSkipMode()
    {
        var server = new FakeFtpsServer();

        await FtpsDeliveryDispatcher.UploadCoreAsync(
            server, "DOC"u8.ToArray(), OrderPath,
            makeDirectories: false, overwriteExisting: false, CancellationToken.None);

        server.Moves.Should().ContainSingle().Which.ExistsMode.Should().Be(FtpRemoteExists.Skip);
    }

    [Fact]
    public async Task Ftps_ADeclinedMove_IsAFailedDelivery_NotABenignNoOp()
    {
        // FluentFTP reports a declined move as a bare false, the same shape it used to report a
        // skipped transfer. A purchase order that was not sent is not a success — mapping either to
        // true would mark the order delivered.
        var server = new FakeFtpsServer();
        server.MidTransfer = () =>
        {
            server.Files[OrderPath] = "ARRIVED-WHILE-WE-WERE-UPLOADING"u8.ToArray();
            return Task.CompletedTask;
        };

        var result = await FtpsDeliveryDispatcher.UploadCoreAsync(
            server, "DOC"u8.ToArray(), OrderPath,
            makeDirectories: false, overwriteExisting: false, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already on the supplier's server");
        Encoding.UTF8.GetString(server.Files[OrderPath]).Should().Be("ARRIVED-WHILE-WE-WERE-UPLOADING");
    }

    [Fact]
    public async Task Ftps_AFailedUpload_StaysFailed()
    {
        var server = new FakeFtpsServer { UploadStatus = FtpStatus.Failed };

        var result = await FtpsDeliveryDispatcher.UploadCoreAsync(
            server, "DOC"u8.ToArray(), OrderPath,
            makeDirectories: false, overwriteExisting: true, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("FTPS upload did not complete successfully.");
        server.Moves.Should().BeEmpty("a transfer that did not finish must never be published");
    }

    [Theory]
    [InlineData(true,  FtpRemoteExists.Overwrite)]
    [InlineData(false, FtpRemoteExists.Skip)]
    public void Ftps_RemoteExistsMode_FollowsTheSetting(bool overwriteExisting, FtpRemoteExists expected)
    {
        FtpsDeliveryDispatcher.RemoteExistsModeFor(overwriteExisting).Should().Be(expected);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static Task<ProcuLink.Core.Services.Delivery.DeliveryResult> Upload(
        FakeSftpServer server, string body, string remotePath,
        bool overwriteExisting, bool makeDirectories = false) =>
        SftpDeliveryDispatcher.UploadCoreAsync(
            server, Encoding.UTF8.GetBytes(body), remotePath,
            makeDirectories, overwriteExisting, NullLogger.Instance, CancellationToken.None);
}
