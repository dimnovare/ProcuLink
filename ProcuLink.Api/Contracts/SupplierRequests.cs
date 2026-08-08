using System.ComponentModel.DataAnnotations;

namespace ProcuLink.Api.Contracts;

/// <param name="VatNumber">Optional VAT / tax number. Feeds supplier auto-detect's identity signal.</param>
/// <param name="RegistrationNumber">Optional company registration / registry code.</param>
/// <param name="EdiCode">Optional EDI routing id — GLN, ILN, Peppol participant id or scheme party code.</param>
/// <param name="PrimaryDomain">Optional email domain the supplier sends from, e.g. "acme.example".</param>
public record CreateSupplierRequest(
    [Required, MinLength(1), MaxLength(200)] string Name,
    [MaxLength(64)]  string? VatNumber = null,
    [MaxLength(64)]  string? RegistrationNumber = null,
    [MaxLength(64)]  string? EdiCode = null,
    [MaxLength(253)] string? PrimaryDomain = null);

/// <summary>
/// Body for PUT /api/suppliers/{id}. Despite the name this now updates supplier DETAILS, not just
/// the name.
/// <para><b>Identity fields are patch-style:</b> a null is "leave it as it is", NOT "clear it".
/// Clearing one means sending an empty string. The existing caller sends only <c>Name</c>, and a
/// PUT that nulled everything it was not told about would silently wipe an org's supplier identity
/// data on every rename.</para>
/// </summary>
public record RenameSupplierRequest(
    [Required, MinLength(1), MaxLength(200)] string Name,
    [MaxLength(64)]  string? VatNumber = null,
    [MaxLength(64)]  string? RegistrationNumber = null,
    [MaxLength(64)]  string? EdiCode = null,
    [MaxLength(253)] string? PrimaryDomain = null);

public record UpsertSupplierProfileRequest(
    [Required] string OutputFormat,
    [Required] string DestinationType,
    /// <summary>jsonb blob — arbitrary key/value (webhook URL, headers, etc.)</summary>
    string? DestinationConfig,
    List<string>? AcceptedFormats);
