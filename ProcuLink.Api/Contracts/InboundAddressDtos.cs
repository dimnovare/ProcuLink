namespace ProcuLink.Api.Contracts;

/// <summary>
/// One hosted inbound-email address, as shown to its owning organisation.
/// </summary>
/// <param name="Address">
/// The full mailbox a buyer sends to. NULL when the stored ciphertext could not be opened — the row
/// is still returned, because an address that cannot be displayed may still be accepting mail and
/// an operator who cannot see it cannot revoke it.
/// </param>
/// <param name="ExpiresAt">
/// When set, the address stops resolving at this instant. Legacy slug addresses carry one; minted
/// addresses do not.
/// </param>
public sealed record InboundAddressDto(
    Guid Id,
    string Kind,
    string Label,
    string? Address,
    string Prefix,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    DateTime? RevokedAt,
    DateTime? LastUsedAt);

/// <summary>The organisation's inbound addresses, newest first.</summary>
public sealed record InboundAddressListResponse(IReadOnlyList<InboundAddressDto> Addresses);
