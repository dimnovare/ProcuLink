using System.Text;
using FluentAssertions;
using FluentFTP;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Infrastructure.Services.Dispatchers;

namespace ProcuLink.Infrastructure.Tests.Services.Dispatchers;

/// <summary>
/// WP-20 — the overwrite decision on the SFTP/FTPS live upload path.
///
/// <para>
/// These tests drive the REAL <c>UploadCore</c> / <c>UploadCoreAsync</c> through a fake session, not
/// a re-implementation of the decision: hardcoding <c>canOverride: true</c> or
/// <c>FtpRemoteExists.Overwrite</c> back at the call site, or dead-coding the pre-write existence
/// check, fails them. That was the point — an earlier attempt at this packet left ~20 new lines on
/// the live SFTP path with no coverage at all.
/// </para>
/// </summary>
public class FileDropOverwriteTests
{
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
    public void Sftp_OverwriteOn_ReplacesItsOwnEarlierFile()
    {
        var session = new FakeSftpSession();
        session.Files["/in/PO-1-abcd1234.xml"] = "TRUNCATED-BY-A-CRASH"u8.ToArray();

        var result = Upload(session, "NEW-COMPLETE-DOCUMENT", "/in/PO-1-abcd1234.xml", overwriteExisting: true);

        result.Success.Should().BeTrue();
        session.LastCanOverride.Should().BeTrue("the upload must be told it may replace the file");
        Encoding.UTF8.GetString(session.Files["/in/PO-1-abcd1234.xml"]).Should().Be("NEW-COMPLETE-DOCUMENT",
            "a re-drive must be able to repair a partial upload of its own order");
    }

    [Fact]
    public void Sftp_OverwriteOff_RefusesRatherThanReplacing()
    {
        var session = new FakeSftpSession();
        session.Files["/in/PO-1-abcd1234.xml"] = "ALREADY-THERE"u8.ToArray();

        var result = Upload(session, "NEW-DOCUMENT", "/in/PO-1-abcd1234.xml", overwriteExisting: false);

        result.Success.Should().BeFalse("a refused write is a failed delivery, never a silent success");
        result.ErrorMessage.Should().Contain("already on the supplier's server");
        session.Uploads.Should().Be(0, "nothing may be written when the operator said do not replace");
        Encoding.UTF8.GetString(session.Files["/in/PO-1-abcd1234.xml"]).Should().Be("ALREADY-THERE");
    }

    [Fact]
    public void Sftp_OverwriteOff_StillWritesWhenThePathIsFree()
    {
        var session = new FakeSftpSession();

        var result = Upload(session, "NEW-DOCUMENT", "/in/PO-1-abcd1234.xml", overwriteExisting: false);

        result.Success.Should().BeTrue();
        session.LastCanOverride.Should().BeFalse();
        Encoding.UTF8.GetString(session.Files["/in/PO-1-abcd1234.xml"]).Should().Be("NEW-DOCUMENT");
    }

    [Fact]
    public void Sftp_MakeDirectories_CreatesTheMissingTree()
    {
        var session = new FakeSftpSession();

        var result = Upload(session, "DOC", "/in/orders/PO-1-abcd1234.xml",
                            overwriteExisting: true, makeDirectories: true);

        result.Success.Should().BeTrue();
        session.CreatedDirectories.Should().Contain("/in/orders");
    }

    // ── FTPS upload path ──────────────────────────────────────────────────────

    [Fact]
    public async Task Ftps_OverwriteOn_UsesOverwriteMode()
    {
        var session = new FakeFtpsSession(FtpStatus.Success);

        var result = await FtpsDeliveryDispatcher.UploadCoreAsync(
            session, "DOC"u8.ToArray(), "/in/PO-1-abcd1234.xml",
            makeDirectories: true, overwriteExisting: true, CancellationToken.None);

        result.Success.Should().BeTrue();
        session.LastExistsMode.Should().Be(FtpRemoteExists.Overwrite);
        session.LastCreateRemoteDir.Should().BeTrue();
    }

    [Fact]
    public async Task Ftps_OverwriteOff_UsesSkipMode()
    {
        var session = new FakeFtpsSession(FtpStatus.Success);

        await FtpsDeliveryDispatcher.UploadCoreAsync(
            session, "DOC"u8.ToArray(), "/in/PO-1-abcd1234.xml",
            makeDirectories: false, overwriteExisting: false, CancellationToken.None);

        session.LastExistsMode.Should().Be(FtpRemoteExists.Skip);
    }

    [Fact]
    public async Task Ftps_ASkippedUpload_IsAFailedDelivery_NotABenignNoOp()
    {
        // FluentFTP calls a skipped transfer a success-ish outcome. A purchase order that was not
        // sent is not a success — mapping Skipped to true would mark the order delivered.
        var session = new FakeFtpsSession(FtpStatus.Skipped);

        var result = await FtpsDeliveryDispatcher.UploadCoreAsync(
            session, "DOC"u8.ToArray(), "/in/PO-1-abcd1234.xml",
            makeDirectories: false, overwriteExisting: false, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already on the supplier's server");
    }

    [Fact]
    public async Task Ftps_AFailedUpload_StaysFailed()
    {
        var session = new FakeFtpsSession(FtpStatus.Failed);

        var result = await FtpsDeliveryDispatcher.UploadCoreAsync(
            session, "DOC"u8.ToArray(), "/in/PO-1-abcd1234.xml",
            makeDirectories: false, overwriteExisting: true, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("FTPS upload did not complete successfully.");
    }

    [Theory]
    [InlineData(true,  FtpRemoteExists.Overwrite)]
    [InlineData(false, FtpRemoteExists.Skip)]
    public void Ftps_RemoteExistsMode_FollowsTheSetting(bool overwriteExisting, FtpRemoteExists expected)
    {
        FtpsDeliveryDispatcher.RemoteExistsModeFor(overwriteExisting).Should().Be(expected);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private static ProcuLink.Core.Services.Delivery.DeliveryResult Upload(
        FakeSftpSession session, string body, string remotePath,
        bool overwriteExisting, bool makeDirectories = false) =>
        SftpDeliveryDispatcher.UploadCore(
            session, Encoding.UTF8.GetBytes(body), remotePath,
            makeDirectories, overwriteExisting, NullLogger.Instance, CancellationToken.None);

    private sealed class FakeSftpSession : SftpDeliveryDispatcher.ISftpUploadSession
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public List<string> CreatedDirectories { get; } = new();
        public int Uploads { get; private set; }
        public bool? LastCanOverride { get; private set; }

        public bool Exists(string path) =>
            Files.ContainsKey(path) || CreatedDirectories.Contains(path);

        public void CreateDirectory(string path) => CreatedDirectories.Add(path);

        public void UploadFile(Stream input, string path, bool canOverride)
        {
            LastCanOverride = canOverride;
            Uploads++;
            using var ms = new MemoryStream();
            input.CopyTo(ms);
            Files[path] = ms.ToArray();
        }
    }

    private sealed class FakeFtpsSession : FtpsDeliveryDispatcher.IFtpsUploadSession
    {
        private readonly FtpStatus _status;
        public FakeFtpsSession(FtpStatus status) => _status = status;

        public FtpRemoteExists? LastExistsMode { get; private set; }
        public bool? LastCreateRemoteDir { get; private set; }

        public Task<FtpStatus> UploadStream(
            Stream input, string remotePath, FtpRemoteExists existsMode,
            bool createRemoteDir, CancellationToken token)
        {
            LastExistsMode = existsMode;
            LastCreateRemoteDir = createRemoteDir;
            return Task.FromResult(_status);
        }
    }
}
