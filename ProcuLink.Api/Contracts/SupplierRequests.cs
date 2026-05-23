using System.ComponentModel.DataAnnotations;

namespace ProcuLink.Api.Contracts;

public record CreateSupplierRequest(
    [Required, MinLength(1), MaxLength(200)] string Name);

public record RenameSupplierRequest(
    [Required, MinLength(1), MaxLength(200)] string Name);

public record UpsertSupplierProfileRequest(
    [Required] string OutputFormat,
    [Required] string DestinationType,
    /// <summary>jsonb blob — arbitrary key/value (webhook URL, headers, etc.)</summary>
    string? DestinationConfig,
    List<string>? AcceptedFormats);
