using System.Text;
using FluentAssertions;
using FluentFTP;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Tests.TestDoubles;

namespace ProcuLink.Infrastructure.Tests.Services.Dispatchers;

/// <summary>
/// B-14 — <i>"They imported half a purchase order."</i>
///
/// <para>
/// A file-drop delivery used to write straight to the name the supplier collects. Neither SFTP's
/// <c>SSH_FXP_WRITE</c> nor FTP's <c>STOR</c> makes a file appear all at once: it exists at its
/// final name from the first byte and grows, so a supplier polling that directory on a timer can
/// pick it up mid-transfer. The fix is to transfer under a temporary name and move the completed
/// file into place.
/// </para>
/// <para>
/// The observations below are taken from INSIDE the transfer, which is the only place the defect was
/// ever visible. A test that only checks the file is correct afterwards passes just as happily
/// against the broken version.
/// </para>
/// </summary>
public class FileDropAtomicWriteTests
{
    private const string OrderPath = "/inbound/PO-77-a1b2c3d4.xml";
    private const string Document  = "COMPLETE-PURCHASE-ORDER-DOCUMENT";

    // ── The temporary name itself ─────────────────────────────────────────────

    [Fact]
    public void ThePartialName_StaysInTheSameDirectory()
    {
        // A rename that crosses a directory can cross a mount point on the supplier's server, and
        // then it is a copy, which is not atomic and is the thing being avoided.
        SftpDeliveryDispatcher.GetDirectoryPath(SftpDeliveryDispatcher.PartialUploadPath(OrderPath))
            .Should().Be(SftpDeliveryDispatcher.GetDirectoryPath(OrderPath));
    }

    [Fact]
    public void ThePartialName_IsHiddenAndDoesNotEndInTheDocumentsExtension()
    {
        var partial = SftpDeliveryDispatcher.PartialUploadPath(OrderPath);

        partial.Should().NotBe(OrderPath);
        partial.Should().Contain("/.", "a leading dot keeps it out of a plain ls and most default globs");
        partial.Should().EndWith(SftpDeliveryDispatcher.PartialUploadSuffix,
            "an intake rule written as *.xml or *.csv must not match it");
        partial.Should().NotEndWith(".xml");
    }

    [Fact]
    public void ThePartialName_IsDeterministic()
    {
        // No timestamp, no random component: a crash-recovery re-drive reuses the one temporary
        // name instead of leaving a fresh orphan in the supplier's directory on every attempt.
        SftpDeliveryDispatcher.PartialUploadPath(OrderPath)
            .Should().Be(SftpDeliveryDispatcher.PartialUploadPath(OrderPath));
    }

    // ── SFTP ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sftp_NothingIsReadableAtTheOrdersNameUntilTheTransferIsComplete()
    {
        var server = new FakeSftpServer();
        byte[]? seenMidTransfer = null;
        var looked = false;

        server.MidTransfer = () =>
        {
            looked = true;
            seenMidTransfer = server.Read(OrderPath);
            return Task.CompletedTask;
        };

        var result = await UploadSftp(server, overwriteExisting: true);

        looked.Should().BeTrue("the observation must actually have been taken mid-transfer");
        seenMidTransfer.Should().BeNull(
            "a supplier polling the drop directory while we transfer must find nothing at the order's "
            + "name — finding a half-written file there is how half a purchase order gets imported");

        result.Success.Should().BeTrue();
        Encoding.UTF8.GetString(server.Files[OrderPath]).Should().Be(Document);
    }

    [Fact]
    public async Task Sftp_ARedeliveryLeavesTheEarlierFileIntactUntilTheNewOneIsComplete()
    {
        // The worse half of the same defect: overwriting in place truncates a file the supplier
        // already has, so a poller reads a document that got SHORTER.
        var server = new FakeSftpServer();
        server.Files[OrderPath] = "PREVIOUS-COMPLETE-DELIVERY"u8.ToArray();

        byte[]? seenMidTransfer = null;
        server.MidTransfer = () =>
        {
            seenMidTransfer = server.Read(OrderPath);
            return Task.CompletedTask;
        };

        var result = await UploadSftp(server, overwriteExisting: true);

        Encoding.UTF8.GetString(seenMidTransfer!).Should().Be("PREVIOUS-COMPLETE-DELIVERY",
            "the earlier delivery must stay whole and readable until the replacement is complete");

        result.Success.Should().BeTrue();
        Encoding.UTF8.GetString(server.Files[OrderPath]).Should().Be(Document);
    }

    [Fact]
    public async Task Sftp_TheTemporaryFileDoesNotSurviveASuccessfulDelivery()
    {
        var server = new FakeSftpServer();

        var result = await UploadSftp(server, overwriteExisting: true);

        result.Success.Should().BeTrue();
        server.Files.Keys.Should().BeEquivalentTo(new[] { OrderPath },
            "the move consumes the temporary file — a rename, not a copy");
    }

    [Fact]
    public async Task Sftp_AServerWithoutPosixRename_StillPublishes()
    {
        // The atomic replace is an EXTENSION, not core SFTP. OpenSSH has shipped posix-rename since
        // 4.8, but appliance and mainframe SFTP servers frequently have not, and plain
        // SSH_FXP_RENAME refuses an occupied destination — so without a fallback, every redelivery
        // to such a server would fail.
        var server = new FakeSftpServer { SupportsPosixRename = false };
        server.Files[OrderPath] = "PREVIOUS-COMPLETE-DELIVERY"u8.ToArray();

        var result = await UploadSftp(server, overwriteExisting: true);

        result.Success.Should().BeTrue(because: result.ErrorMessage);
        Encoding.UTF8.GetString(server.Files[OrderPath]).Should().Be(Document);
        server.Deletes.Should().Contain(OrderPath,
            "the destination has to be cleared before a plain rename can take it");
        server.Renames.Select(r => r.ReplaceExisting).Should().Equal(new[] { true, false },
            "the atomic form is tried first and only the refusal falls back");
    }

    [Fact]
    public async Task Sftp_APublishThatCannotHappen_IsAFailedDelivery_AndSaysWhy()
    {
        // A directory the account may write to but not rename within. The upload succeeds and the
        // supplier still gets nothing, which must never read as a success.
        var server = new FailToRenameSftpServer();

        var result = await UploadSftp(server, overwriteExisting: true);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("could not be renamed");
        result.ErrorMessage.Should().Contain("Nothing was delivered");
        server.Files.Should().NotContainKey(OrderPath, "nothing may appear at the name they collect");
    }

    [Fact]
    public async Task Sftp_AFailedPublish_TakesTheTemporaryFileWithIt()
    {
        var server = new FailToRenameSftpServer();

        _ = await UploadSftp(server, overwriteExisting: true);

        server.Files.Should().BeEmpty("an abandoned transfer must not be left in the supplier's directory");
    }

    // ── FTPS ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ftps_NothingIsReadableAtTheOrdersNameUntilTheTransferIsComplete()
    {
        var server = new FakeFtpsServer();
        byte[]? seenMidTransfer = null;
        var looked = false;

        server.MidTransfer = () =>
        {
            looked = true;
            seenMidTransfer = server.Read(OrderPath);
            return Task.CompletedTask;
        };

        var result = await UploadFtps(server, overwriteExisting: true);

        looked.Should().BeTrue("the observation must actually have been taken mid-transfer");
        seenMidTransfer.Should().BeNull();

        result.Success.Should().BeTrue();
        Encoding.UTF8.GetString(server.Files[OrderPath]).Should().Be(Document);
    }

    [Fact]
    public async Task Ftps_ARedeliveryLeavesTheEarlierFileIntactUntilTheNewOneIsComplete()
    {
        var server = new FakeFtpsServer();
        server.Files[OrderPath] = "PREVIOUS-COMPLETE-DELIVERY"u8.ToArray();

        byte[]? seenMidTransfer = null;
        server.MidTransfer = () =>
        {
            seenMidTransfer = server.Read(OrderPath);
            return Task.CompletedTask;
        };

        var result = await UploadFtps(server, overwriteExisting: true);

        Encoding.UTF8.GetString(seenMidTransfer!).Should().Be("PREVIOUS-COMPLETE-DELIVERY");
        result.Success.Should().BeTrue();
        Encoding.UTF8.GetString(server.Files[OrderPath]).Should().Be(Document);
    }

    [Fact]
    public async Task Ftps_TheTemporaryFileDoesNotSurviveASuccessfulDelivery()
    {
        var server = new FakeFtpsServer();

        var result = await UploadFtps(server, overwriteExisting: true);

        result.Success.Should().BeTrue();
        server.Files.Keys.Should().BeEquivalentTo(new[] { OrderPath });
    }

    [Fact]
    public async Task Ftps_APublishThatCannotHappen_IsAFailedDelivery_AndSaysWhy()
    {
        var server = new FailToMoveFtpsServer();

        var result = await UploadFtps(server, overwriteExisting: true);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("could not be renamed");
        server.Files.Should().NotContainKey(OrderPath);
        server.Files.Should().BeEmpty("an abandoned transfer must not be left in the supplier's directory");
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static Task<DeliveryResult> UploadSftp(
        SftpDeliveryDispatcher.ISftpUploadSession server, bool overwriteExisting) =>
        SftpDeliveryDispatcher.UploadCoreAsync(
            server, Encoding.UTF8.GetBytes(Document), OrderPath,
            makeDirectories: false, overwriteExisting, NullLogger.Instance, CancellationToken.None);

    private static Task<DeliveryResult> UploadFtps(
        FtpsDeliveryDispatcher.IFtpsUploadSession server, bool overwriteExisting) =>
        FtpsDeliveryDispatcher.UploadCoreAsync(
            server, Encoding.UTF8.GetBytes(Document), OrderPath,
            makeDirectories: false, overwriteExisting, CancellationToken.None);

    /// <summary>A directory the account can write to but not rename within.</summary>
    private sealed class FailToRenameSftpServer : SftpDeliveryDispatcher.ISftpUploadSession
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public Task<bool> ExistsAsync(string path, CancellationToken ct) =>
            Task.FromResult(Files.ContainsKey(path));

        public Task CreateDirectoryAsync(string path, CancellationToken ct) => Task.CompletedTask;

        public async Task UploadFileAsync(Stream input, string path, bool canOverride, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            await input.CopyToAsync(ms, ct);
            Files[path] = ms.ToArray();
        }

        public Task RenameAsync(string fromPath, string toPath, bool replaceExisting, CancellationToken ct) =>
            throw new Renci.SshNet.Common.SftpPermissionDeniedException("rename denied");

        public Task DeleteFileAsync(string path, CancellationToken ct)
        {
            _ = Files.Remove(path);
            return Task.CompletedTask;
        }
    }

    /// <summary>The FTPS shape of the same: the transfer lands, the move does not.</summary>
    private sealed class FailToMoveFtpsServer : FtpsDeliveryDispatcher.IFtpsUploadSession
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public Task<bool> FileExists(string path, CancellationToken token) =>
            Task.FromResult(Files.ContainsKey(path));

        public async Task<FtpStatus> UploadStream(
            Stream input, string remotePath, FtpRemoteExists existsMode,
            bool createRemoteDir, CancellationToken token)
        {
            using var ms = new MemoryStream();
            await input.CopyToAsync(ms, token);
            Files[remotePath] = ms.ToArray();
            return FtpStatus.Success;
        }

        public Task<bool> MoveFile(
            string path, string dest, FtpRemoteExists existsMode, CancellationToken token) =>
            Task.FromResult(false);

        public Task DeleteFile(string path, CancellationToken token)
        {
            _ = Files.Remove(path);
            return Task.CompletedTask;
        }
    }
}
