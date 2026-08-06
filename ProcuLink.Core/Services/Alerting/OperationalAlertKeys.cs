namespace ProcuLink.Core.Services.Alerting;

/// <summary>
/// Stable identifiers for each operator-alert condition. The key is the DE-DUPLICATION unit: the
/// alert sweep keeps one independent cooldown window per key, so a long-running incident on one
/// condition can never suppress the first notification of a different condition.
/// <para>
/// Keys are also sent to the sink (subject prefix / structured field), so they must stay stable —
/// an operator's mail filters and any downstream routing key off these strings.
/// </para>
/// </summary>
public static class OperationalAlertKeys
{
    /// <summary>No Hangfire worker has a recent heartbeat — background processing has stopped.</summary>
    public const string WorkerHeartbeatLost = "worker_heartbeat_lost";

    /// <summary>All-org dead-letter + failed-delivery backlog crossed its threshold.</summary>
    public const string DeadLetterBacklog = "dead_letter_backlog";

    /// <summary>Delivery attempts in the recent window are failing above the configured rate.</summary>
    public const string DeliveryFailureRate = "delivery_failure_rate";

    /// <summary>An enabled inbound pull channel has not recorded a successful poll recently.</summary>
    public const string PullChannelStalled = "pull_channel_stalled";

    /// <summary>One or more good-standing orgs have latched their monthly AI token cap.</summary>
    public const string AiTokenCapLatched = "ai_token_cap_latched";

    /// <summary>Every key, for tests and for documentation of the full alert surface.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        WorkerHeartbeatLost,
        DeadLetterBacklog,
        DeliveryFailureRate,
        PullChannelStalled,
        AiTokenCapLatched,
    };
}
