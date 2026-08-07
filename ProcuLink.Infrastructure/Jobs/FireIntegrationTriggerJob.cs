using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Hangfire;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Jobs;

/// <summary>
/// Hangfire job: delivers one integration event to one subscriber TargetUrl.
/// Signs with HMAC-SHA256 (X-ProcuLink-Signature: sha256=hex).
/// Deactivates subscription after 3 consecutive logical failures.
/// Idempotent: exits silently if subscription is inactive.
///
/// <para><b>At-least-once + dedupe (finding A4):</b> every delivery carries a stable
/// <c>X-ProcuLink-Delivery-Id</c> (and subscription-independent <c>X-ProcuLink-Event-Id</c>)
/// derived from the immutable job arguments, so a Hangfire retry re-POSTs the SAME id and a
/// subscriber can dedupe. A failure of the success-persist AFTER a 2xx is never recorded as a
/// failed delivery and never rethrown (the webhook was already delivered), so a transient DB error
/// can neither fabricate a false failure record nor trigger a duplicate POST.</para>
///
/// <para><b>Failure accounting (defer-list #5):</b> the persisted <c>FailureCount</c> tracks
/// LOGICAL (consecutive) delivery failures, NOT Hangfire's per-job retry attempts. Hangfire
/// re-runs the whole method on each <see cref="AutomaticRetryAttribute"/> retry; bumping
/// <c>FailureCount</c> on every run counted one failed webhook up to <c>Attempts + 1</c> times
/// and auto-deactivated the subscription after a SINGLE real failure. The count is now
/// incremented (and deactivation decided) only on the FINAL Hangfire attempt — once Hangfire
/// will no longer retry this delivery — so three genuinely separate failed deliveries are needed
/// to deactivate, as documented.</para>
/// </summary>
public class FireIntegrationTriggerJob
{
    /// <summary>Mirrors the <see cref="AutomaticRetryAttribute"/> <c>Attempts</c> on <see cref="ExecuteAsync"/>.</summary>
    internal const int MaxRetryAttempts = 3;

    /// <summary>Consecutive logical failures after which the subscription auto-deactivates.</summary>
    internal const int DeactivateAfterFailures = 3;

    private readonly ProcuLinkDbContext                     _db;
    private readonly IHttpClientFactory                     _http;
    private readonly DeliveryEncryptionService              _enc;
    private readonly OutboundRequestGuard                   _guard;
    private readonly ILogger<FireIntegrationTriggerJob>     _logger;

    public FireIntegrationTriggerJob(
        ProcuLinkDbContext                 db,
        IHttpClientFactory                 http,
        DeliveryEncryptionService          enc,
        OutboundRequestGuard               guard,
        ILogger<FireIntegrationTriggerJob> logger)
    {
        _db     = db;
        _http   = http;
        _enc    = enc;
        _guard  = guard;
        _logger = logger;
    }

    // Built lazily once from the guard's connect-time-revalidating handler and reused.
    private HttpClient? _guardedClient;

    /// <summary>
    /// Resolves the <see cref="HttpClient"/> used to fire the webhook. The default swaps in the
    /// guard's connect-time-revalidating <see cref="System.Net.Http.SocketsHttpHandler"/> so a
    /// DNS-rebind to a private/metadata IP after the up-front
    /// <see cref="OutboundRequestGuard.ValidateAsync"/> is still rejected at TCP connect.
    /// Tests override this to inject a fake transport.
    /// </summary>
    internal virtual HttpClient CreateSendClient()
    {
        if (_guardedClient is not null) return _guardedClient;

        var timeout = _http.CreateClient("delivery").Timeout;
        _guardedClient = new HttpClient(_guard.CreateGuardedHttpHandler(), disposeHandler: true)
        {
            Timeout = timeout,
        };
        return _guardedClient;
    }

    // The trailing PerformContext is INJECTED by Hangfire at execution time (it is not part of the
    // enqueued argument list — see Enqueue below). It carries the current "RetryCount" job
    // parameter that AutomaticRetry maintains, which is how we tell whether Hangfire will retry
    // this delivery again. Direct callers (tests) pass null → treated as the final attempt.
    [Queue("background")]
    [AutomaticRetry(Attempts = MaxRetryAttempts, DelaysInSeconds = new[] { 30, 120, 600 })]
    public Task ExecuteAsync(
        Guid subscriptionId, string payloadJson, CancellationToken ct, PerformContext? context = null) =>
        // Only the FINAL Hangfire attempt mutates the persisted consecutive-failure count, so a
        // single failed webhook (retried up to MaxRetryAttempts times by Hangfire) bumps the count
        // exactly once rather than once per retry.
        ExecuteCoreAsync(subscriptionId, payloadJson, IsFinalHangfireAttempt(context), ct);

    /// <summary>
    /// Testable core: the <paramref name="isFinalAttempt"/> flag is resolved from the Hangfire
    /// <see cref="PerformContext"/> by the public entrypoint; tests drive it directly to exercise
    /// both the non-final (no count bump) and final (count bump / deactivate) failure paths.
    /// </summary>
    internal async Task ExecuteCoreAsync(
        Guid subscriptionId, string payloadJson, bool isFinalAttempt, CancellationToken ct)
    {
        var sub = await _db.IntegrationSubscriptions
                           .Where(s => s.Id == subscriptionId)
                           .FirstOrDefaultAsync(ct);

        if (sub is null || !sub.IsActive)
        {
            _logger.LogInformation(
                "FireIntegrationTriggerJob: sub {SubId} not found or inactive — skipping.",
                subscriptionId);
            return;
        }

        // ── Stable delivery identity (finding A4) ─────────────────────────────
        // Derived from the IMMUTABLE job inputs (subscription id + event type + payload bytes),
        // so a Hangfire retry — which re-runs this method with the SAME arguments — re-POSTs the
        // SAME X-ProcuLink-Delivery-Id and a well-behaved subscriber can dedupe a duplicate that
        // a post-send persist failure + retry (or any at-least-once redelivery) may produce. The
        // event id is subscription-independent, so one event fanned out to many subscribers
        // shares a single X-ProcuLink-Event-Id.
        var deliveryId = DeterministicId(subscriptionId.ToString("N"), sub.EventType, payloadJson);
        var eventId    = DeterministicId(sub.EventType, payloadJson);

        string? sigHeader = null;
        if (!string.IsNullOrEmpty(sub.EncryptedSecret))
        {
            string secret;
            try
            {
                secret = _enc.Decrypt(sub.EncryptedSecret, CredentialScope.ForSupplier(
                    sub.OrganisationId, CredentialPurpose.OrgIntegrationWebhookSecret, subscriptionId));
            }
            catch (CredentialUnbindableException ex)
            {
                // A subscription that HAS a secret is a subscriber who asked for signed deliveries.
                // Sending unsigned because the secret would not decrypt is the failure this whole
                // change exists to remove. Same treatment as the SSRF guard below: record the
                // failure, apply the retry/deactivate flow, send nothing.
                _logger.LogError(ex,
                    "FireIntegrationTriggerJob: signing secret for sub {SubId} could not be decrypted ({Reason}) — not sending.",
                    subscriptionId, ex.Reason);
                await RecordFailureAsync(sub, isFinalAttempt, ct);
                throw new InvalidOperationException(
                    $"Webhook signing secret for subscription {subscriptionId} could not be decrypted.", ex);
            }

            var secretBytes = Encoding.UTF8.GetBytes(secret);
            var dataBytes   = Encoding.UTF8.GetBytes(payloadJson);
            using var hmac  = new HMACSHA256(secretBytes);
            sigHeader = $"sha256={Convert.ToHexString(hmac.ComputeHash(dataBytes)).ToLowerInvariant()}";
        }

        // ── SSRF guard — must pass before any outbound request ────────────────
        var guardResult = await _guard.ValidateAsync(sub.TargetUrl, ct);
        if (!guardResult.Allowed)
        {
            _logger.LogWarning(
                "FireIntegrationTriggerJob: SSRF guard blocked webhook to '{Url}' for sub {SubId}: {Reason}",
                sub.TargetUrl, subscriptionId, guardResult.Reason);
            // Treat as a delivery failure and apply the existing retry/deactivate flow.
            await RecordFailureAsync(sub, isFinalAttempt, ct);
            throw new InvalidOperationException(
                $"Webhook delivery blocked: {guardResult.Reason}");
        }

        // ── Fire the webhook ──────────────────────────────────────────────────
        // The critical distinction (finding A4): a SEND failure (connection reset / DNS / TLS /
        // client timeout / non-2xx) means the webhook was NEVER delivered — a genuine failure to
        // record and retry. A failure of the SUCCESS-PERSIST *after* a 2xx means the subscriber
        // ALREADY received the webhook — it must NOT be recorded as a failure and must NOT rethrow,
        // because a Hangfire retry would re-POST the identical bytes with no way for the subscriber
        // to know it is a duplicate (beyond the stable X-ProcuLink-Delivery-Id we now send).
        HttpResponseMessage response;
        try
        {
            var client = CreateSendClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, sub.TargetUrl)
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json"),
            };
            // sigHeader and the delivery/event ids are server-computed (hex / GUID) — inherently
            // CR/LF/NUL-safe. sub.EventType is a stored, tenant-influenced value flowing into a
            // header VALUE — validate it for CR/LF/NUL injection before adding, dropping (and
            // logging) on failure.
            if (sigHeader is not null)
                request.Headers.TryAddWithoutValidation("X-ProcuLink-Signature", sigHeader);
            request.Headers.TryAddWithoutValidation("X-ProcuLink-Delivery-Id", deliveryId.ToString());
            request.Headers.TryAddWithoutValidation("X-ProcuLink-Event-Id", eventId.ToString());
            if (!HttpHeaderGuard.TryAdd(request.Headers, "X-ProcuLink-Event", sub.EventType))
                _logger.LogWarning(
                    "Skipping X-ProcuLink-Event header for sub {SubId} (event type failed CR/LF validation).",
                    subscriptionId);

            response = await client.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown, not a subscriber failure — let Hangfire re-run without penalising the
            // subscription (recording/deactivating on a worker restart would be wrong).
            throw;
        }
        catch (Exception ex)
        {
            // The SEND ITSELF failed (connection refused / DNS / TLS / client-side timeout). Nothing
            // was delivered → a genuine delivery failure.
            await RecordFailureAsync(sub, isFinalAttempt, ct);
            throw new InvalidOperationException(
                $"Webhook send to {sub.TargetUrl} failed: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // Subscriber rejected the delivery (non-2xx) → a genuine delivery failure.
                await RecordFailureAsync(sub, isFinalAttempt, ct);
                throw new InvalidOperationException(
                    $"Delivery failed: HTTP {(int)response.StatusCode} from {sub.TargetUrl}");
            }
        }

        // ── Delivered (2xx) ───────────────────────────────────────────────────
        // Persist the success (reset the consecutive-failure streak). A transient error HERE must
        // NEVER be recorded as a failure and must NEVER rethrow: the subscriber already received the
        // webhook, so failing the job would make Hangfire re-POST identical bytes. Worst case the
        // streak stays stale until the next successful delivery clears it — a duplicate POST is the
        // far more customer-visible harm.
        try
        {
            sub.FailureCount = 0;
            sub.UpdatedAt    = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "FireIntegrationTriggerJob delivered to {Url}, status={Status}, delivery={DeliveryId}",
                sub.TargetUrl, response.StatusCode, deliveryId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "FireIntegrationTriggerJob: webhook to {Url} was DELIVERED (delivery={DeliveryId}) but the " +
                "success-persist failed; NOT recording a failure and NOT retrying, to avoid a duplicate POST.",
                sub.TargetUrl, deliveryId);
            // Swallow — a delivered webhook is a success regardless of our bookkeeping write.
        }
    }

    /// <summary>
    /// Deterministic UUIDv5-style id over the given immutable parts: SHA-256 of the joined inputs,
    /// first 16 bytes as a <see cref="Guid"/>. Identical inputs always yield the same id (so it is
    /// stable across Hangfire retries, which re-run with the same job arguments) while distinct
    /// events/subscriptions collide only with cryptographic improbability.
    /// </summary>
    private static Guid DeterministicId(params string[] parts)
    {
        var joined = string.Join('\n', parts);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(joined), hash);
        return new Guid(hash[..16]);
    }

    /// <summary>
    /// Records ONE logical failed delivery. On a non-final Hangfire attempt the persisted count is
    /// left untouched (the failure throws and Hangfire retries); only the final attempt increments
    /// the consecutive-failure count and, at the threshold, deactivates the subscription. This
    /// decouples the persisted count from Hangfire's retry count so one failed webhook = one bump.
    ///
    /// <para><b>Concurrency + idempotency (finding A4b):</b> the increment is a single atomic
    /// store-side statement (<c>FailureCount = FailureCount + 1</c>) rather than a
    /// load-modify-save, so concurrent failures from parallel workers cannot lose an update, and
    /// because it is one statement (and this method is called exactly once per failure path, with
    /// no catch re-invoking it) a transient persist error cannot double-count the same failure.
    /// Deactivation is a second conditional atomic update — it only flips an <em>active</em>
    /// subscription that has reached the threshold, so it never resurrects a subscription and never
    /// re-counts.</para>
    /// </summary>
    private async Task RecordFailureAsync(IntegrationSubscription sub, bool isFinalAttempt, CancellationToken ct)
    {
        if (!isFinalAttempt)
        {
            _logger.LogInformation(
                "FireIntegrationTriggerJob: sub {SubId} delivery failed on a retried attempt — " +
                "leaving FailureCount={Count} for Hangfire to retry.",
                sub.Id, sub.FailureCount);
            return;
        }

        if (_db.Database.IsRelational())
        {
            // Atomic, store-side increment (FailureCount = FailureCount + 1). A load-modify-save
            // bump loses concurrent increments from parallel Hangfire workers (silently under-
            // counting and resurrecting a should-be-dead subscription); the relative UPDATE does
            // not. Because it is a single statement — called exactly once per failure path, with no
            // catch re-invoking it — a transient persist error can never double-count one failure.
            await _db.IntegrationSubscriptions
                     .Where(s => s.Id == sub.Id)
                     .ExecuteUpdateAsync(set => set
                         .SetProperty(s => s.FailureCount, s => s.FailureCount + 1)
                         .SetProperty(s => s.UpdatedAt, DateTime.UtcNow), ct);

            // Deactivate atomically and idempotently — only an ACTIVE subscription that has reached
            // the threshold, so this never resurrects a subscription and never re-counts.
            var deactivated = await _db.IntegrationSubscriptions
                .Where(s => s.Id == sub.Id && s.IsActive && s.FailureCount >= DeactivateAfterFailures)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.IsActive, false), ct);

            if (deactivated > 0)
                _logger.LogWarning(
                    "FireIntegrationTriggerJob: deactivated sub {SubId} after reaching {Threshold} consecutive failures.",
                    sub.Id, DeactivateAfterFailures);
            return;
        }

        // Non-relational (the InMemory test provider cannot translate a relative ExecuteUpdate) —
        // emulate the same net effect through the change tracker. Tests are single-threaded, so the
        // lost-update atomicity the relational path guarantees is not needed here.
        sub.FailureCount++;
        sub.UpdatedAt = DateTime.UtcNow;
        if (sub.FailureCount >= DeactivateAfterFailures)
        {
            sub.IsActive = false;
            _logger.LogWarning(
                "FireIntegrationTriggerJob: deactivated sub {SubId} after {Count} consecutive failures.",
                sub.Id, sub.FailureCount);
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// True when Hangfire will NOT retry this delivery again (so the persisted failure count may be
    /// advanced exactly once for the logical failure). Reads the "RetryCount" job parameter that
    /// <see cref="AutomaticRetryAttribute"/> maintains: 0 on the first run, N on the Nth retry. The
    /// last allowed run is when RetryCount has reached the configured attempt cap. A null context
    /// (direct/test invocation, or a job recorded before this parameter existed) is treated as
    /// final so the failure is still recorded once.
    /// </summary>
    private static bool IsFinalHangfireAttempt(PerformContext? context)
    {
        if (context is null) return true;
        try
        {
            var retryCount = context.GetJobParameter<int>("RetryCount");
            return retryCount >= MaxRetryAttempts;
        }
        catch
        {
            // Missing/unparseable parameter — fail safe by recording the failure (final).
            return true;
        }
    }

    public static void Enqueue(IBackgroundJobClient jobs, Guid subscriptionId, string payloadJson)
    {
        // NOTE: the PerformContext arg is omitted here on purpose — Hangfire substitutes it at
        // execution time. Listing it would try to serialise a null into the job arguments.
        jobs.Enqueue<FireIntegrationTriggerJob>(
            j => j.ExecuteAsync(subscriptionId, payloadJson, CancellationToken.None, null!));
    }
}
