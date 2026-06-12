using FluentAssertions;
using FluentFTP;
using ProcuLink.Infrastructure.Services.Ingress;

namespace ProcuLink.Infrastructure.Tests.Services.Ingress;

/// <summary>
/// H1 / M6 / H3 security regression gate for the catalog FTP fetch configuration.
/// <see cref="FluentFtpFetchClientFactory.BuildFtpConfig"/> is the pure helper every
/// catalog FTP/FTPS session is built from — if any of these assertions breaks, a
/// security control regressed:
///  • PASVEX (H1): passive data connection PINNED to the SSRF-validated control host —
///    the server-advertised PASV IP is ignored, closing the PASV-redirect SSRF.
///  • DataConnectionEncryption + ValidateAnyCertificate=false (M6): FTPS file content
///    never travels cleartext and there is NO invalid-certificate escape hatch.
///  • All four 30 s timeouts (H3): a stalling server cannot pin Worker threads.
/// </summary>
public class FluentFtpConfigTests
{
    [Fact]
    public void BuildFtpConfig_PlainFtp_UsesPasvex_AndAllTimeouts()
    {
        var config = FluentFtpFetchClientFactory.BuildFtpConfig(explicitTls: false);

        config.DataConnectionType.Should().Be(FtpDataConnectionType.PASVEX,
            "H1: the data connection must reuse the validated control host, never the server-advertised PASV IP");

        config.ConnectTimeout.Should().Be(30_000);
        config.ReadTimeout.Should().Be(30_000);
        config.DataConnectionConnectTimeout.Should().Be(30_000);
        config.DataConnectionReadTimeout.Should().Be(30_000);

        config.ValidateAnyCertificate.Should().BeFalse(
            "M6: no invalid-certificate escape hatch exists on the catalog fetch path");
    }

    [Fact]
    public void BuildFtpConfig_Ftps_EnforcesExplicitTls_AndDataChannelEncryption()
    {
        var config = FluentFtpFetchClientFactory.BuildFtpConfig(explicitTls: true);

        config.DataConnectionType.Should().Be(FtpDataConnectionType.PASVEX);
        config.EncryptionMode.Should().Be(FtpEncryptionMode.Explicit);
        config.DataConnectionEncryption.Should().BeTrue(
            "M6: catalog file content must never travel over a cleartext FTPS data channel");
        config.ValidateAnyCertificate.Should().BeFalse();

        config.ConnectTimeout.Should().Be(30_000);
        config.ReadTimeout.Should().Be(30_000);
        config.DataConnectionConnectTimeout.Should().Be(30_000);
        config.DataConnectionReadTimeout.Should().Be(30_000);
    }

    // ── H2 — bounded read helper ──────────────────────────────────────────────

    [Fact]
    public async Task BoundedRead_StreamWithinCap_CopiesFully()
    {
        var payload = new byte[1000];
        new Random(42).NextBytes(payload);

        using var result = await BoundedRead.CopyAsync(new MemoryStream(payload), maxBytes: 1000, CancellationToken.None);

        result.Length.Should().Be(1000);
        result.ToArray().Should().Equal(payload);
    }

    [Fact]
    public async Task BoundedRead_StreamOverCap_AbortsAtCapPlusOne()
    {
        // An "endless" stream simulating a lying/oversized server: the copy must abort
        // after reading at most cap+1 bytes, never materializing the whole stream (H2).
        var endless = new EndlessZeroStream();

        var act = () => BoundedRead.CopyAsync(endless, maxBytes: 4096, CancellationToken.None);

        await act.Should().ThrowAsync<ProcuLink.Core.Services.Catalog.CatalogFileTooLargeException>();
        endless.TotalRead.Should().BeLessThanOrEqualTo(4097, "at most cap+1 bytes may ever be read");
    }

    /// <summary>Infinite zero stream that counts how many bytes were actually read.</summary>
    private sealed class EndlessZeroStream : Stream
    {
        public long TotalRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => long.MaxValue;
        public override long Position { get => TotalRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            TotalRead += count;
            return count;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
