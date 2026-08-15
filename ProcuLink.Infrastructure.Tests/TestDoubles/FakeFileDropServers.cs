using FluentFTP;
using ProcuLink.Infrastructure.Services.Dispatchers;
using Renci.SshNet.Common;

namespace ProcuLink.Infrastructure.Tests.TestDoubles;

/// <summary>
/// In-memory stand-ins for the two file-drop servers, shared by every file-drop test rather than
/// copied per file.
///
/// <para>
/// Shared ON PURPOSE. What these have to model is not "a dictionary of files" but the exact
/// behaviours the atomic-write design turns on — that a plain SFTP rename REFUSES an occupied
/// destination, that the OpenSSH <c>posix-rename</c> extension is one a server may not have, and
/// that a transfer is visible at its target path before it is finished. A fake that quietly gets any
/// of those wrong makes the guarantee untestable, and four copies is four chances to get one wrong.
/// </para>
/// </summary>
internal sealed class FakeSftpServer : SftpDeliveryDispatcher.ISftpUploadSession
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    public List<string> CreatedDirectories { get; } = new();

    /// <summary>
    /// Whether this server offers the OpenSSH <c>posix-rename</c> extension. OpenSSH has since 4.8; plenty of
    /// appliance and mainframe SFTP servers do not, and the delete-then-rename fallback is only
    /// reachable with this off.
    /// </summary>
    public bool SupportsPosixRename { get; set; } = true;

    public int Uploads { get; private set; }
    public bool UploadStarted { get; private set; }
    public bool? LastCanOverride { get; private set; }
    public string? LastUploadPath { get; private set; }
    public List<(string From, string To, bool ReplaceExisting)> Renames { get; } = new();
    public List<string> Deletes { get; } = new();

    /// <summary>
    /// When set, the upload never finishes and never observes the token — a supplier server that
    /// completed the handshake and then stopped reading. The point of ignoring the token is that a
    /// deadline which only works against a cooperative transfer is not a deadline.
    /// </summary>
    public bool StallForever { get; set; }

    /// <summary>
    /// Runs when a transfer is half-written, so a test can look at the directory the way a
    /// supplier's poller would.
    /// </summary>
    public Func<Task>? MidTransfer { get; set; }

    /// <summary>Snapshot of what is readable at a path right now, or null if nothing is there.</summary>
    public byte[]? Read(string path) => Files.TryGetValue(path, out var bytes) ? bytes : null;

    public Task<bool> ExistsAsync(string path, CancellationToken ct) =>
        Task.FromResult(Files.ContainsKey(path) || CreatedDirectories.Contains(path));

    public Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        CreatedDirectories.Add(path);
        return Task.CompletedTask;
    }

    public async Task UploadFileAsync(Stream input, string path, bool canOverride, CancellationToken ct)
    {
        UploadStarted = true;
        LastUploadPath = path;
        LastCanOverride = canOverride;
        Uploads++;

        if (StallForever)
        {
            await new TaskCompletionSource().Task.ConfigureAwait(false);
        }

        if (!canOverride && Files.ContainsKey(path))
        {
            throw new SshException($"{path} already exists");
        }

        using var ms = new MemoryStream();
        await input.CopyToAsync(ms, ct).ConfigureAwait(false);
        var complete = ms.ToArray();

        // Half the bytes land, an observer gets to look, then the rest. This is the whole reason the
        // temporary name exists: a real STOR/SSH_FXP_WRITE makes the file readable from its first byte.
        Files[path] = complete[..(complete.Length / 2)];
        if (MidTransfer is not null)
        {
            await MidTransfer().ConfigureAwait(false);
        }

        Files[path] = complete;
    }

    public Task RenameAsync(string fromPath, string toPath, bool replaceExisting, CancellationToken ct)
    {
        Renames.Add((fromPath, toPath, replaceExisting));

        if (!Files.ContainsKey(fromPath))
        {
            throw new SftpPathNotFoundException($"{fromPath} not found");
        }

        if (replaceExisting)
        {
            // SSH.NET asks for the posix-rename extension here. A server without it answers
            // SSH_FX_OP_UNSUPPORTED, which surfaces as an SshException.
            if (!SupportsPosixRename)
            {
                throw new SshException("the posix-rename extension is not supported by this server");
            }
        }
        else if (Files.ContainsKey(toPath))
        {
            // Plain SSH_FXP_RENAME. SFTP v3 makes an existing target an error, and OpenSSH's
            // sftp-server implements the regular-file case with link(2) so it cannot clobber.
            throw new SshException($"{toPath} already exists");
        }

        Files[toPath] = Files[fromPath];
        _ = Files.Remove(fromPath);
        return Task.CompletedTask;
    }

    public Task DeleteFileAsync(string path, CancellationToken ct)
    {
        Deletes.Add(path);
        if (!Files.Remove(path))
        {
            throw new SftpPathNotFoundException($"{path} not found");
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// An FTPS server the way FluentFTP reports one. Same responsibilities as
/// <see cref="FakeSftpServer"/>: a transfer is readable at its target path before it finishes, and
/// a move declines rather than throws when the destination is occupied under
/// <see cref="FtpRemoteExists.Skip"/>.
/// </summary>
internal sealed class FakeFtpsServer : FtpsDeliveryDispatcher.IFtpsUploadSession
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public int Uploads { get; private set; }
    public bool UploadStarted { get; private set; }
    public string? LastUploadPath { get; private set; }
    public FtpRemoteExists? LastUploadExistsMode { get; private set; }
    public bool? LastCreateRemoteDir { get; private set; }
    public List<(string From, string To, FtpRemoteExists ExistsMode)> Moves { get; } = new();
    public List<string> Deletes { get; } = new();

    /// <summary>What the transfer reports. Success unless a test needs the failure mapping.</summary>
    public FtpStatus UploadStatus { get; set; } = FtpStatus.Success;

    /// <inheritdoc cref="FakeSftpServer.StallForever"/>
    public bool StallForever { get; set; }

    /// <inheritdoc cref="FakeSftpServer.MidTransfer"/>
    public Func<Task>? MidTransfer { get; set; }

    public byte[]? Read(string path) => Files.TryGetValue(path, out var bytes) ? bytes : null;

    public Task<bool> FileExists(string path, CancellationToken token) =>
        Task.FromResult(Files.ContainsKey(path));

    public async Task<FtpStatus> UploadStream(
        Stream input, string remotePath, FtpRemoteExists existsMode,
        bool createRemoteDir, CancellationToken token)
    {
        UploadStarted = true;
        LastUploadPath = remotePath;
        LastUploadExistsMode = existsMode;
        LastCreateRemoteDir = createRemoteDir;
        Uploads++;

        if (StallForever)
        {
            await new TaskCompletionSource().Task.ConfigureAwait(false);
        }

        if (existsMode == FtpRemoteExists.Skip && Files.ContainsKey(remotePath))
        {
            return FtpStatus.Skipped;
        }

        if (UploadStatus != FtpStatus.Success)
        {
            return UploadStatus;
        }

        using var ms = new MemoryStream();
        await input.CopyToAsync(ms, token).ConfigureAwait(false);
        var complete = ms.ToArray();

        Files[remotePath] = complete[..(complete.Length / 2)];
        if (MidTransfer is not null)
        {
            await MidTransfer().ConfigureAwait(false);
        }

        Files[remotePath] = complete;
        return FtpStatus.Success;
    }

    public Task<bool> MoveFile(
        string path, string dest, FtpRemoteExists existsMode, CancellationToken token)
    {
        Moves.Add((path, dest, existsMode));

        if (!Files.ContainsKey(path))
        {
            return Task.FromResult(false);
        }

        if (Files.ContainsKey(dest))
        {
            // FluentFTP checks the destination itself: Skip declines, Overwrite clears the way first.
            if (existsMode == FtpRemoteExists.Skip)
            {
                return Task.FromResult(false);
            }

            _ = Files.Remove(dest);
        }

        Files[dest] = Files[path];
        _ = Files.Remove(path);
        return Task.FromResult(true);
    }

    public Task DeleteFile(string path, CancellationToken token)
    {
        Deletes.Add(path);
        _ = Files.Remove(path);
        return Task.CompletedTask;
    }
}
