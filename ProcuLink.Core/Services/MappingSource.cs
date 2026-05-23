namespace ProcuLink.Core.Services;

/// <summary>
/// How a buyer→supplier item code mapping was created.
/// Stored as a lowercase string in the item_mappings.source column.
/// </summary>
public enum MappingSource
{
    Manual,    // User typed the code in the Resolve UI
    Imported,  // Loaded from an external file / bulk import
    Suggested  // Phase 4: AI-suggested, pending confirmation
}
