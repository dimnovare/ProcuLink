namespace ProcuLink.Core.Services.Microsoft;

/// <summary>A OneDrive folder as Graph names it: the drive, the item, and the name people see.</summary>
public sealed record DriveItemRef(string DriveId, string ItemId, string Name);

/// <summary>A pasted link that cannot be used. <see cref="Unavailable"/>: Microsoft did not answer, so trying again may work.</summary>
public sealed class DriveLinkException(string message, bool unavailable = false) : Exception(message)
{
    public bool Unavailable { get; } = unavailable;
}

/// <summary>
/// Turns a pasted OneDrive sharing link into the folder it opens, with the organisation's Microsoft account (plan 30).
/// The link is never fetched itself, only encoded into a fixed Graph URL, and never stored or logged.
/// </summary>
public interface IMicrosoftDriveLinks
{
    /// <exception cref="DriveLinkException">The link, the account or the item cannot be used.</exception>
    Task<DriveItemRef> ResolveAsync(Guid orgId, Guid accountId, string link, CancellationToken ct);
}
