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
);
