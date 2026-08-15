using System.Text.Json;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Services.Dispatchers;

/// <summary>
/// SFTP delivery dispatcher — uploads the artifact to the configured remote
/// directory via SSH/SFTP using SSH.NET. Supports password and private-key auth
/// (with optional key passphrase). Mirrors the HttpDeliveryDispatcher contract:
/// never throws, always returns a DeliveryResult with a humanised error message.
/// </summary>
public sealed class SftpDeliveryDispatcher : IDeliveryDispatcher
{
    private readonly ILogger<SftpDeliveryDispatcher> _logger;
    private readonly OutboundRequestGuard _guard;

    /// <summary>
    /// Test seam — how a connected upload session is obtained. Null in production (the only PUBLIC
    /// constructor leaves it null, and Microsoft DI only ever sees public constructors), where the
    /// session comes from a real SSH.NET connect.
    ///
    /// <para>
    /// It exists because the step that carries the operator's <c>overwriteExisting</c> setting OUT
    /// OF THE SAVED CONFIG and onto the upload is a single expression inside
    /// <see cref="DispatchAsync"/>. Without a seam that expression cannot be reached by any test —
    /// <see cref="OverwriteExistingFromConfig"/> is provable as a pure function and
    /// <see cref="UploadCoreAsync"/> is provable with an injected bool, and BOTH can be green while the
    /// wire between them is cut. Replacing that expression with a hardcoded <c>true</c> — the
    /// operator's OFF setting silently ignored on the live path for real purchase orders — left the
    /// entire suite passing. It does not any more.
    /// </para>
    /// <para>
    /// The seam substitutes the SESSION only, never the upload. There is exactly ONE
    /// <see cref="UploadCoreAsync"/> call in this class, and the connect step it skips
    /// (<see cref="TryConnect"/>) carries no part of the overwrite decision — so a test taking this
    /// branch exercises the same expression production does. An earlier shape had a second,
    /// production-only forward of the flag, which no test could reach and which therefore survived
    /// the same one-token mutation.
    /// </para>
    /// </summary>
    private readonly Func<ConnectionInfo, ISftpUploadSession>? _sessionFactory;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Protocol => DeliveryProtocolConstants.Sftp;

    // Safe in the sense ResendSafety means, and ONLY that sense: a re-send after an unknown outcome
    // cannot DUPLICATE at the supplier. The remote path is a deterministic function of the order, so
    // a re-send either replaces its own file (overwriteExisting on, the default) or refuses outright
    // (overwriteExisting off) — neither produces a second copy.
    //
    // It does NOT mean the re-send completes. With overwriteExisting off it refuses, which would
    // walk the order into dead-letter while the supplier may already hold the document, so
    // DeliveryService parks that combination for a human rather than re-driving it
    // (CannotRepairItsOwnFile). See OverwriteExistingFromConfig for the trade-off the off setting makes.
    public ResendSafety ResendSafety => ResendSafety.Safe;

    // No HTTP status codes exist on this channel at all — every DeliveryResult it returns carries a
    // null ResponseCode, so the classification never reaches its 400 branch and there is no supplier
    // reason to capture. Declared explicitly rather than inherited: the whole point of the capability
    // is that a dispatcher states what it can see, and "nothing, because there is nothing to see" is
    // an answer, not an omission.
    public bool CapturesSupplierResponseBody => false;

    public SftpDeliveryDispatcher(ILogger<SftpDeliveryDispatcher> logger, OutboundRequestGuard guard)
        : this(logger, guard, sessionFactory: null)
    {
    }

    internal SftpDeliveryDispatcher(
        ILogger<SftpDeliveryDispatcher> logger,
        OutboundRequestGuard guard,
        Func<ConnectionInfo, ISftpUploadSession>? sessionFactory)
    {
        _logger = logger;
        _guard = guard;
        _sessionFactory = sessionFactory;
    }

    public async Task<DeliveryResult> DispatchAsync(
        byte[] content,
        string fileName,
        string contentType,
        SupplierDeliveryConfig config,
        string decryptedCredentials,
        CancellationToken ct,
        string? idempotencyKey = null,
        bool isTestFire = false)
    {
        // isTestFire is deliberately unused: a file drop has no covering message to reword. What a
        // test leaves behind here is a FILE, and the honest handling of that is the operator-facing
        // disclosure in the UI plus RefusedTestFileExists below — not a change to the upload.
        // A3 idempotency: SFTP is already idempotent by construction. The remote filename is a
        // deterministic function of the ORDER (PO number + order id — see DeliveryService.BuildFileName),
        // so a crash-recovery re-upload targets the same path rather than creating a second file —
        // no supplier idempotency key is needed (idempotencyKey is intentionally unused here).

        // Hoisted out of the try so the timeout message below can name the number the operator set.
        var timeoutSeconds = DefaultTimeoutSeconds;

        try
        {
            var cfg = JsonSerializer.Deserialize<SftpConfig>(config.ConfigJson, JsonOpts);
            if (cfg is null || string.IsNullOrWhiteSpace(cfg.Host))
                return new DeliveryResult(false, "SFTP delivery configuration is invalid — host is required.");

            var creds = string.IsNullOrEmpty(decryptedCredentials)
                ? null
                : JsonSerializer.Deserialize<SftpCredentials>(decryptedCredentials, JsonOpts);

            if (creds is null || string.IsNullOrWhiteSpace(creds.Username))
                return new DeliveryResult(false, "SFTP delivery credentials are missing — username is required.");

            var port = cfg.Port > 0 ? cfg.Port : 22;
            var remoteDir = NormaliseRemoteDir(cfg.RemotePath);
            var remotePath = $"{remoteDir.TrimEnd('/')}/{SanitiseFileName(fileName)}";

            var connectionInfo = BuildConnectionInfo(cfg.Host, port, creds);
            timeoutSeconds = cfg.TimeoutSeconds is > 0 ? cfg.TimeoutSeconds!.Value : DefaultTimeoutSeconds;

            // Bounds the CONNECT, and only the connect. It is why an unreachable host fails in
            // 30 seconds rather than never — but it says nothing at all about what happens once a
            // server has answered, which is the gap the deadline below closes.
            connectionInfo.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

            // ── The transfer deadline ────────────────────────────────────────────────────────
            // Until this existed the upload had NO deadline of any kind: ConnectionInfo.Timeout
            // above stops at the connect, and SSH.NET's own SftpClient.OperationTimeout defaults
            // to -1ms — "an infinite timeout period", in the library's own words. A supplier
            // server that completed the handshake and then stopped reading held the Hangfire job
            // on that thread indefinitely, and the order sat in `delivering` for hours.
            //
            // Shape mirrors FtpsDeliveryDispatcher: one linked source covering the whole dispatch,
            // so the caller's own cancellation and our deadline arrive by the same route. The
            // connect is bounded separately (ConnectionInfo.Timeout) because SSH.NET's Connect()
            // is synchronous and observes no token, so the honest worst case for the pair is
            // connect ≤ timeout, transfer ≤ timeout, rather than one shared budget.
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var deadline = linkedCts.Token;

            // ── SSRF guard — re-validated IMMEDIATELY before connecting to shrink the
            // DNS-rebinding TOCTOU window. SSH.NET reconnects by hostname (re-resolving) and
            // pinning the IP would break host-key/hostname semantics, so the tightest available
            // mitigation is to re-resolve+validate right before dispatch.
            var guardResult = await _guard.ValidateHostAsync(cfg.Host, port, deadline);
            if (!guardResult.Allowed)
                return new DeliveryResult(false, $"SFTP delivery blocked: {guardResult.Reason}");

            var fakeSession = _sessionFactory?.Invoke(connectionInfo);

            // SSH.NET's connect is synchronous — wrap in Task.Run so we honour the CancellationToken.
            return await Task.Run(
                async () =>
                {
                    // Where the session comes from is the ONLY thing that differs between production
                    // and a test: production connects, a test hands one over. Everything after that
                    // — including the operator's overwriteExisting setting — travels through the
                    // SINGLE UploadCoreAsync call below. Deliberately not two calls: while there were
                    // two, the production one was reachable by no test, so hardcoding ITS
                    // overwriteExisting argument to true left the whole suite green while an
                    // operator's OFF setting became a no-op for real purchase orders. Mirrors
                    // FtpsDeliveryDispatcher, which has always had one call site.
                    SftpClient? client = null;
                    ISftpUploadSession session;
                    SshHostKeyVerifier? verifier = null;

                    if (fakeSession is null)
                    {
                        // Who this server claims to be is now part of the decision. Until this
                        // existed, flipping the server's host key between two deliveries — host,
                        // port, username and password unchanged — produced the same Success: True,
                        // no warning and no log line, and handed the supplier's password to the new
                        // identity along with the purchase order.
                        verifier = new SshHostKeyVerifier(
                            "SFTP delivery", DeliveryHostKeyConfig.Read(config.ConfigJson));

                        client = new SftpClient(connectionInfo);

                        // Belt and braces on the deadline, at the layer below it. UploadCoreAsync
                        // enforces the deadline itself and does not depend on this — but this is
                        // the library's OWN bound on every individual SFTP request, and leaving it
                        // at its -1ms "infinite" default is what let a stalled server park a thread
                        // for hours. Two independent mechanisms, because the outer one abandons a
                        // blocked request while this one ends it.
                        client.OperationTimeout = TimeSpan.FromSeconds(timeoutSeconds);

                        verifier.Attach(client);

                        // Belt and braces: SSH.NET aborts on CanTrust=false — proven live on the
                        // pinned 2024.2.0 — but that is a property of the library, and this is the
                        // one place where trusting it silently would put a purchase order and the
                        // supplier's password on an unverified server. A connect that SUCCEEDS
                        // despite our refusal is still a refusal.
                        var connectFailure = TryConnect(client, verifier)
                            ?? (verifier.Rejection is null ? null : new DeliveryResult(false, verifier.Rejection.Message));

                        if (connectFailure is not null)
                        {
                            client.Dispose();
                            // Stamped even on failure: an authentication error AFTER a completed key
                            // exchange means the server's identity WAS cryptographically
                            // established, so it is a genuine first-use observation and pinning it
                            // protects the retry. A refused key never reaches here as something to
                            // learn — LearnedFingerprint is null for a rejection by construction.
                            return Stamp(connectFailure, verifier);
                        }

                        session = new SshNetUploadSession(client);
                    }
                    else
                    {
                        session = fakeSession;
                    }

                    try
                    {
                        return Stamp(
                            await UploadCoreAsync(
                                session, content, remotePath, cfg.MakeDirectories,
                                // THE wire between the operator's saved setting and the live upload,
                                // read from the config row the operator actually edited. Covered
                                // end-to-end by FileDropOverwriteWiringTests — hardcoding this to true
                                // must not be able to pass, and now cannot.
                                OverwriteExistingFromConfig(config.ConfigJson),
                                _logger, deadline),
                            verifier);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Our deadline, not the caller's cancellation. Reported here rather than at
                        // the outer catch so the sentence can say the transfer stalled — and so a
                        // host key learned during the connect is still recorded, which is the whole
                        // point of Stamp and would be thrown away by letting this escape.
                        return Stamp(new DeliveryResult(false, TransferTimedOut("SFTP", timeoutSeconds)), verifier);
                    }
                    finally
                    {
                        if (client is not null)
                        {
                            try { client.Disconnect(); } catch { /* swallow */ }
                            client.Dispose();
                        }
                    }
                },
                ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The deadline fired before the session existed — during the SSRF re-resolve, or
            // between Task.Run being handed the token and its delegate starting.
            return new DeliveryResult(false, TransferTimedOut("SFTP", timeoutSeconds));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "SFTP delivery config or credentials JSON malformed.");
            return new DeliveryResult(false, "SFTP delivery configuration could not be parsed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SFTP delivery failed unexpectedly.");
            return new DeliveryResult(false, "SFTP delivery failed before the upload could complete.");
        }
    }

    /// <summary>
    /// Carries a first-use host-key observation out of the transport and onto the result, so
    /// <c>DeliveryService</c> — which owns the config row and the transaction — can record it.
    /// Null verifier (the test session seam) and nothing-new-to-learn both leave the result alone.
    /// </summary>
    private static DeliveryResult Stamp(DeliveryResult result, SshHostKeyVerifier? verifier) =>
        verifier?.LearnedFingerprint is { } fingerprint
            ? result with { LearnedHostKeyFingerprint = fingerprint }
            : result;

    /// <summary>
    /// Opens the SSH connection, mapping the connect failures an operator can actually fix to their
    /// own sentence. Returns null on success — transport only, so it carries no part of the upload
    /// decision.
    /// </summary>
    private static DeliveryResult? TryConnect(SftpClient client, SshHostKeyVerifier verifier)
    {
        try
        {
            client.Connect();
            return null;
        }
        catch (Exception) when (verifier.Rejection is not null)
        {
            // The host key is why we are here, whatever the library called the exception. SSH.NET
            // reports a subscriber-refused key as SshConnectionException("Key exchange negotiation
            // failed.") — verified live — which names neither the cause nor a next step and reads
            // identically to an algorithm mismatch. Filtering on OUR OWN rejection rather than on
            // the exception type means a library upgrade that renames or re-wraps it cannot quietly
            // reinstate the useless message.
            return new DeliveryResult(false, verifier.Rejection.Message);
        }
        catch (SshAuthenticationException)
        {
            return new DeliveryResult(false, "SFTP authentication failed — check the username, password, or private key.");
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            return new DeliveryResult(false, $"SFTP connection failed: {ex.Message}");
        }
        catch (SshOperationTimeoutException)
        {
            return new DeliveryResult(false, "SFTP connection timed out.");
        }
    }

    /// <summary>
    /// Everything that happens on a connected SFTP session: the overwrite decision, directory
    /// creation, the upload, the atomic move into place, and the error mapping. Split out from the
    /// transport so it can be exercised against a fake session — the overwrite behaviour is a
    /// live-path decision about real purchase orders and must be covered by a test that fails when
    /// it changes.
    ///
    /// <para><b>Nothing is ever written directly to the name the supplier reads.</b> The bytes go to
    /// <see cref="PartialUploadPath"/> in the same remote directory, and only a completed upload is
    /// moved onto the real name. Before this, a supplier polling the drop directory could — and did —
    /// collect a file mid-transfer and import half a purchase order: nothing about a plain SFTP write
    /// makes it appear all-at-once, so the file exists at its final name from the first byte.
    /// </para>
    /// <para>
    /// Every remote call is bounded by <paramref name="ct"/>, which carries the delivery deadline.
    /// <c>WaitAsync</c> rather than trusting the token to the call: it returns on the deadline even
    /// if the underlying request ignores cancellation entirely, which is the difference between a
    /// bound and a request to please stop. An abandoned request is ended by the client disposal in
    /// <see cref="DispatchAsync"/>'s finally.
    /// </para>
    /// </summary>
    internal static async Task<DeliveryResult> UploadCoreAsync(
        ISftpUploadSession session,
        byte[] content,
        string remotePath,
        bool makeDirectories,
        bool overwriteExisting,
        ILogger logger,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var partialPath = PartialUploadPath(remotePath);

        if (makeDirectories)
        {
            await EnsureRemoteDirectoryExistsAsync(session, GetDirectoryPath(remotePath), ct);
        }

        // Explicit refusal rather than a silent replace, when the operator has turned overwrite off.
        // Reported as a normal failed delivery (retry/dead-letter as usual), never as a success.
        // Kept ahead of the transfer so a refusal costs no bytes; the move below refuses again, and
        // atomically, which is what closes the gap between this check and the publish.
        if (!overwriteExisting && await session.ExistsAsync(remotePath, ct).WaitAsync(ct))
        {
            return new DeliveryResult(false, RefusedBecauseFileExists(remotePath));
        }

        using var ms = new MemoryStream(content);
        try
        {
            // canOverride is TRUE here regardless of the operator's setting, and that is not the
            // wire being cut: this is OUR scratch name, not the supplier's file. A partial left by
            // a crashed earlier attempt must be repairable, and refusing to replace our own litter
            // would wedge the supplier permanently. The operator's setting governs the destination,
            // and it does so in PublishAsync below.
            await session.UploadFileAsync(ms, partialPath, canOverride: true, ct).WaitAsync(ct);
        }
        catch (SftpPathNotFoundException)
        {
            return new DeliveryResult(false, $"SFTP remote directory '{GetDirectoryPath(remotePath)}' does not exist. Set makeDirectories=true to auto-create.");
        }
        catch (SftpPermissionDeniedException)
        {
            return new DeliveryResult(false, $"SFTP permission denied writing to '{remotePath}'.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SFTP upload failed after connection.");
            return new DeliveryResult(false, "SFTP upload failed after a successful connection.");
        }

        try
        {
            await PublishAsync(session, partialPath, remotePath, overwriteExisting, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await TryDeleteAsync(session, partialPath, ct);

            // With overwrite off, a rename that refuses because something is at the destination is
            // the SAME refusal as the pre-transfer check — just decided by the server, atomically,
            // on a file that appeared while we were uploading. It has to read the same to an operator.
            if (!overwriteExisting && await ExistsQuietlyAsync(session, remotePath, ct))
            {
                return new DeliveryResult(false, RefusedBecauseFileExists(remotePath));
            }

            logger.LogWarning(ex, "SFTP upload completed but could not be moved into place.");
            return new DeliveryResult(false, CouldNotPublish(remotePath));
        }

        return new DeliveryResult(true, null);
    }

    /// <summary>
    /// Moves a completed upload from its temporary name onto the name the supplier reads.
    ///
    /// <para><b>Verified per protocol, because SFTP rename is not POSIX rename.</b> SFTP v3 —
    /// what essentially every server speaks — makes an existing target an ERROR for
    /// <c>SSH_FXP_RENAME</c>, and OpenSSH's sftp-server implements the regular-file case with
    /// <c>link(2)</c> + <c>unlink(2)</c> precisely so it cannot clobber. So "write to temp, then
    /// rename" is NOT one behaviour here; it is two, and which one is correct is exactly the
    /// operator's <c>overwriteExisting</c> setting:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>Overwrite off</b> — plain rename, which the server refuses if anything
    /// is at the destination. This is strictly stronger than the old <c>canOverride: false</c> on
    /// the write: the check and the publish are now the same operation, so a file appearing between
    /// them cannot be replaced.</description></item>
    /// <item><description><b>Overwrite on</b> — the OpenSSH <c>posix-rename</c> SFTP extension (its
    /// wire name carries OpenSSH's vendor suffix), which replaces
    /// atomically. OpenSSH has carried the extension since 4.8 (2008), so the common case has no
    /// window at all. A server without it falls back to delete-then-rename, which is NOT atomic —
    /// but its window exposes an ABSENT path, never a half-written one, and an absent file is
    /// something a poller already has to cope with. That is the whole property being bought.
    /// </description></item>
    /// </list>
    /// <para>
    /// The fallback can delete the destination and then fail to rename, leaving the supplier without
    /// the earlier copy. That copy is this same order's own document by construction (the path is a
    /// deterministic function of the order), the delivery is reported failed, and a retry re-uploads
    /// it — so the trade is a temporarily missing file against a permanently half-written one.
    /// </para>
    /// </summary>
    private static async Task PublishAsync(
        ISftpUploadSession session, string fromPath, string toPath, bool overwriteExisting, CancellationToken ct)
    {
        if (!overwriteExisting)
        {
            await session.RenameAsync(fromPath, toPath, replaceExisting: false, ct).WaitAsync(ct);
            return;
        }

        try
        {
            await session.RenameAsync(fromPath, toPath, replaceExisting: true, ct).WaitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No posix-rename extension on this server. Plain rename would fail with the
            // destination occupied, so it has to be cleared first.
            await TryDeleteAsync(session, toPath, ct);
            await session.RenameAsync(fromPath, toPath, replaceExisting: false, ct).WaitAsync(ct);
        }
    }

    /// <summary>
    /// The temporary name a transfer is written under, always in the SAME remote directory as its
    /// destination — a rename across directories can cross a mount point on the supplier's server
    /// and stop being a rename at all.
    ///
    /// <para>
    /// Leading dot and a suffix after the real extension, so it is hidden from a plain <c>ls</c> and
    /// misses an intake glob written as <c>*.xml</c> or <c>*.csv</c>. Deterministic — no timestamp,
    /// no random component — so a crash-recovery re-drive reuses the one temporary name instead of
    /// leaving a new orphan in the supplier's directory on every attempt.
    /// </para>
    /// </summary>
    internal static string PartialUploadPath(string remotePath)
    {
        var lastSlash = remotePath.LastIndexOf('/');
        var dir = lastSlash < 0 ? string.Empty : remotePath[..(lastSlash + 1)];
        var name = lastSlash < 0 ? remotePath : remotePath[(lastSlash + 1)..];
        return $"{dir}.{name}{PartialUploadSuffix}";
    }

    internal const string PartialUploadSuffix = ".proculink-part";

    internal const int DefaultTimeoutSeconds = 30;

    /// <summary>
    /// What an operator reads when the deadline fires. Shared by both file-drop dispatchers so the
    /// two wordings cannot drift, and deliberately specific about what was left behind: the claim
    /// that nothing incomplete is readable at the supplier's file name is only true BECAUSE of the
    /// temporary-name write above, and the two must move together.
    /// </summary>
    internal static string TransferTimedOut(string channel, int timeoutSeconds) =>
        $"{channel} delivery timed out after {timeoutSeconds} seconds — the server answered but did " +
        "not finish the transfer in time. Nothing incomplete is readable at the file name the " +
        "supplier collects: an upload is written under a temporary name and only moved onto the real " +
        "name once it is complete. If this supplier's server is simply slow, raise the timeout on " +
        "this connection and send again.";

    /// <summary>
    /// The upload landed but the move onto the supplier's file name did not. Distinct from an upload
    /// failure because the remedy is different — this is almost always a directory the account may
    /// write to but not rename within.
    /// </summary>
    internal static string CouldNotPublish(string remotePath) =>
        $"Nothing was delivered. The upload reached the supplier's server but could not be renamed to " +
        $"'{remotePath}', so nothing appeared at the name they collect. This usually means the account " +
        "may create files in that directory but not rename or replace them — ask the supplier to allow " +
        "rename there. Sending again is safe: an unfinished upload sits under a temporary name and is " +
        "overwritten by the next attempt, never delivered.";

    /// <summary>
    /// Best-effort removal of our own temporary file. Never turns a delivery outcome into a
    /// different one — the caller has already decided the outcome, and a drop directory the account
    /// cannot delete from is not a delivery failure.
    /// </summary>
    private static async Task TryDeleteAsync(ISftpUploadSession session, string path, CancellationToken ct)
    {
        try { await session.DeleteFileAsync(path, ct).WaitAsync(ct); }
        catch (OperationCanceledException) { /* deadline already fired; the client disposal cleans up */ }
        catch (Exception) { /* leftover temp is litter, not a delivery outcome */ }
    }

    /// <summary>
    /// An existence probe used only to CHOOSE A SENTENCE after a failure has already been decided.
    /// A probe that throws must not be able to change the outcome, so it answers false.
    /// </summary>
    private static async Task<bool> ExistsQuietlyAsync(
        ISftpUploadSession session, string path, CancellationToken ct)
    {
        try { return await session.ExistsAsync(path, ct).WaitAsync(ct); }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// What an operator reads when overwrite is off and something is already at the path. Shared by
    /// both file-drop dispatchers so the two wordings cannot drift.
    ///
    /// <para>
    /// Two situations, two sentences, because one sentence cannot be true of both. A PURCHASE ORDER
    /// writes to a path that is a deterministic function of the order, so whatever is sitting on it
    /// is that order's own document: the message must not say "remove or rename the file there" —
    /// renaming it and re-sending manufactures the second copy at the supplier that
    /// <see cref="ResendSafety.Safe"/> promises cannot happen, and deleting it throws away a
    /// purchase order the supplier may already be working from. A TEST FIRE
    /// (<see cref="DeliveryTestArtifact"/>) has no order behind it at all, so every clause about
    /// "this order" would be a fabrication — and there, deleting the file IS a legitimate remedy,
    /// because it is our own previous test and not the customer's document.
    /// </para>
    /// </summary>
    internal static string RefusedBecauseFileExists(string remotePath) =>
        DeliveryTestArtifact.IsAtPath(remotePath)
            ? RefusedTestFileExists(remotePath)
            : RefusedOrderFileExists(remotePath);

    private static string RefusedOrderFileExists(string remotePath) =>
        $"Nothing was sent. A file named '{remotePath}' is already on the supplier's server and this " +
        "connection is set not to replace existing files. That remote path belongs to this order and " +
        "no other, so the file there is almost certainly this same purchase order already delivered " +
        "— check with the supplier before sending it again. To let a repeat send replace its own " +
        "file, turn \"replace existing files\" back on for this supplier.";

    /// <summary>
    /// The same refusal, told truthfully about a connection test. No order exists, so nothing here
    /// may suggest one was or was not delivered: this is the previous test's own file, and the
    /// connection behaving exactly as configured.
    /// </summary>
    private static string RefusedTestFileExists(string remotePath) =>
        $"The connection worked, but nothing was written: a file named '{remotePath}' is already on " +
        "the supplier's server — left by an earlier test — and this connection is set not to replace " +
        "existing files. No purchase order is involved, and none was affected. Delete that test file " +
        "on the server to run the test again, or turn \"replace existing files\" back on for this " +
        "supplier.";

    /// <summary>
    /// Whether an upload may replace a file already at the remote path. Absent key ⇒ TRUE, which is
    /// exactly what every SFTP connection did before this setting existed, so no configured supplier
    /// changes behaviour on deploy.
    ///
    /// <para>
    /// True is also the safe default on the reliability axis, not merely the compatible one.
    /// Delivery here is at-least-once: a crash between the network write and the outcome commit
    /// leaves a re-drive that must be able to REPAIR its own possibly-truncated file. With overwrite
    /// off that re-drive refuses instead, and the supplier keeps a partial document until a human
    /// clears it. The clobber this packet fixes — two DIFFERENT orders sharing a PO number — is
    /// solved by the filename carrying the order id, not by this flag; the flag exists for operators
    /// whose remote directory must be strictly append-only and who accept that trade.
    /// </para>
    /// </summary>
    internal static bool OverwriteExistingFromConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return true;

        try
        {
            using var doc = JsonDocument.Parse(configJson);
            return !doc.RootElement.TryGetProperty("overwriteExisting", out var flag)
                || flag.ValueKind switch
                {
                    JsonValueKind.False => false,
                    JsonValueKind.True  => true,
                    // Anything else (null, a string, a number) is not an operator saying "no".
                    _ => true,
                };
        }
        catch (JsonException)
        {
            // Malformed config is reported by the caller's own parse; never silently flip to refuse.
            return true;
        }
    }

    private static ConnectionInfo BuildConnectionInfo(string host, int port, SftpCredentials creds)
    {
        // Prefer key-based auth if a private key is configured; fall back to password.
        if (!string.IsNullOrWhiteSpace(creds.PrivateKey))
        {
            using var keyStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(creds.PrivateKey));
            var keyFile = string.IsNullOrEmpty(creds.PrivateKeyPassphrase)
                ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, creds.PrivateKeyPassphrase);
            return new ConnectionInfo(host, port, creds.Username, new PrivateKeyAuthenticationMethod(creds.Username, keyFile));
        }

        if (!string.IsNullOrWhiteSpace(creds.Password))
        {
            return new ConnectionInfo(host, port, creds.Username, new PasswordAuthenticationMethod(creds.Username, creds.Password));
        }

        // Shouldn't reach here because the calling code validates credentials, but guard anyway.
        throw new InvalidOperationException("SFTP credentials must include either a password or a private key.");
    }

    private static async Task EnsureRemoteDirectoryExistsAsync(
        ISftpUploadSession session, string dirPath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(dirPath) || dirPath == "/" || dirPath == ".") return;
        if (await session.ExistsAsync(dirPath, ct).WaitAsync(ct)) return;

        var parent = GetDirectoryPath(dirPath);
        if (!string.IsNullOrEmpty(parent) && parent != "/" && parent != dirPath)
            await EnsureRemoteDirectoryExistsAsync(session, parent, ct);

        try { await session.CreateDirectoryAsync(dirPath, ct).WaitAsync(ct); }
        catch (Renci.SshNet.Common.SftpPathNotFoundException) { /* race or parent missing */ }
        catch (Renci.SshNet.Common.SshException) { /* already exists or permission */ }
    }

    // internal static (not private) so the pure path/filename logic can be unit-tested
    // directly via InternalsVisibleTo without standing up a live SFTP server.
    internal static string NormaliseRemoteDir(string? remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath)) return ".";
        var trimmed = remotePath.Replace('\\', '/').Trim();
        return trimmed.StartsWith('/') ? trimmed : $"./{trimmed}";
    }

    internal static string SanitiseFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "delivery.bin";
        var safe = new string(fileName.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').ToArray());
        return safe.Trim('_').Length > 0 ? safe : "delivery.bin";
    }

    internal static string GetDirectoryPath(string remotePath)
    {
        var lastSlash = remotePath.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : remotePath[..lastSlash];
    }

    // ── Config + credentials POCOs ────────────────────────────────────────────

    private sealed record SftpConfig(
        string Host,
        int Port,
        string? RemotePath,
        bool MakeDirectories,
        int? TimeoutSeconds);

    // ── Upload seam ───────────────────────────────────────────────────────────

    /// <summary>
    /// The SSH.NET calls the upload actually makes. Exists so <see cref="UploadCoreAsync"/> — which
    /// owns the overwrite decision and the move-into-place for real purchase orders — is testable
    /// without an SSH server.
    ///
    /// <para>
    /// Every member is asynchronous and takes the deadline token. That is not decoration: the
    /// synchronous SSH.NET surface this used to sit on cannot be interrupted at all, so a bound on
    /// the transfer was not expressible against it.
    /// </para>
    /// </summary>
    internal interface ISftpUploadSession
    {
        Task<bool> ExistsAsync(string path, CancellationToken ct);
        Task CreateDirectoryAsync(string path, CancellationToken ct);
        Task UploadFileAsync(Stream input, string path, bool canOverride, CancellationToken ct);

        /// <param name="replaceExisting">
        /// True asks for an ATOMIC replace (the OpenSSH <c>posix-rename</c> extension); false asks for the
        /// plain <c>SSH_FXP_RENAME</c>, which the server refuses if the destination is occupied.
        /// Not a hint — the false case is how "do not replace existing files" is enforced without a
        /// check-then-write race.
        /// </param>
        Task RenameAsync(string fromPath, string toPath, bool replaceExisting, CancellationToken ct);

        Task DeleteFileAsync(string path, CancellationToken ct);
    }

    private sealed class SshNetUploadSession : ISftpUploadSession
    {
        private readonly SftpClient _client;
        public SshNetUploadSession(SftpClient client) => _client = client;

        public Task<bool> ExistsAsync(string path, CancellationToken ct) =>
            _client.ExistsAsync(path, ct);

        public Task CreateDirectoryAsync(string path, CancellationToken ct) =>
            _client.CreateDirectoryAsync(path, ct);

        public Task UploadFileAsync(Stream input, string path, bool canOverride, CancellationToken ct) =>
            // Positional, and the null progress reporter is cast rather than named: the five-argument
            // overload is the only one carrying canOverride, and SSH.NET ships no parameter names for
            // it, so naming an argument here does not compile.
            _client.UploadFileAsync(input, path, canOverride, (IProgress<UploadFileProgressReport>?)null, ct);

        // SSH.NET exposes no async posix-rename — RenameFileAsync is the plain rename only — so the
        // atomic form goes through the thread pool. It is still bounded: the caller's WaitAsync
        // returns on the deadline and SftpClient.OperationTimeout ends the blocked request.
        public Task RenameAsync(string fromPath, string toPath, bool replaceExisting, CancellationToken ct) =>
            replaceExisting
                ? Task.Run(() => _client.RenameFile(fromPath, toPath, isPosix: true), ct)
                : _client.RenameFileAsync(fromPath, toPath, ct);

        public Task DeleteFileAsync(string path, CancellationToken ct) =>
            _client.DeleteFileAsync(path, ct);
    }

    private sealed record SftpCredentials(
        string Username,
        string? Password,
        string? PrivateKey,
        string? PrivateKeyPassphrase);
}

