namespace ProcuLink.Core.Services;

/// <summary>
/// Removes an erased workspace's sign-in accounts at the sign-in provider (Clerk): each member who
/// belongs to no other organisation and is not platform staff, then the organisation itself.
/// Best-effort and never throws: every failure is named in <see cref="SignInRemoval.Failed"/>.
/// </summary>
public interface ISignInAccountRemover
{
    Task<SignInRemoval> RemoveAsync(string clerkOrgId, CancellationToken ct);
}

/// <summary><see cref="Step"/> is one of <see cref="SignInSteps"/>; <see cref="Failed"/> names user ids, "members" or "organization".</summary>
public sealed record SignInRemoval(string Step, int AccountsRemoved, IReadOnlyList<string> Failed);

public static class SignInSteps
{
    public const string Done = "done", Failed = "failed", SkippedNoKey = "skipped_no_key", NotRequested = "not_requested";
}
