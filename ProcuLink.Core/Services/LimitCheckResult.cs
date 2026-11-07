namespace ProcuLink.Core.Services;

/// <summary>
/// Result of a limit check. Distinguishes between pilot expiry and plan-limit exhaustion
/// so controllers can return the correct error code to the frontend.
/// </summary>
public record LimitCheckResult(
    bool   Allowed,
    bool   PilotExpired,  // true only for Pilot accounts past their window
    string Plan,
    int    Limit
)
{
    /// <summary>User-facing line a flow intake shows while a refused plan limit holds new documents back.</summary>
    public string FlowPausedMessage => PilotExpired
        ? "Paused: your trial has ended, so new documents aren't being accepted."
        : "Paused: you've reached your plan's document limit for this month.";
}
