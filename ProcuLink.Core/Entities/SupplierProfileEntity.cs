using System.Text.Json;

namespace ProcuLink.Core.Entities;

/// <summary>
/// EF entity for supplier_profiles. Named SupplierProfileEntity to avoid
/// collision with ProcuLink.Core.Canonical.SupplierProfile used by Phase-0 code.
/// </summary>
public class SupplierProfileEntity
{
    public Guid Id { get; set; }
    public Guid SupplierId { get; set; }
    public Guid OrgId { get; set; }

    /// <summary>Allowed output formats: 'csv' or 'xml'.</summary>
    public List<string> AcceptedFormats { get; set; } = new();

    /// <summary>jsonb: arbitrary key/value required field definitions.</summary>
    public JsonDocument? RequiredFields { get; set; }

    public string OutputFormat { get; set; } = string.Empty;

    /// <summary>'webhook' or 'download'</summary>
    public string DestinationType { get; set; } = string.Empty;

    /// <summary>jsonb: webhook URL, headers, etc.</summary>
    public JsonDocument? DestinationConfig { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public Supplier Supplier { get; set; } = null!;
    public Organisation Organisation { get; set; } = null!;
}
