using System.Text.Json;

namespace ProcuLink.Core.Entities;

public class OutputTemplate
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>cXML | EDI | JSON | CSV | XML</summary>
    public string Format { get; set; } = string.Empty;
    public string Version { get; set; } = "v1.0";
    /// <summary>Format-specific config stored as JSONB.</summary>
    public JsonDocument? ConfigJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
}
