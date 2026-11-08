namespace ProcuLink.Core.Entities;

/// <summary>An org table of "if the value is X, write Y", used by an output's Lookup step.</summary>
public class Lookup
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>A serialized <c>LookupEntry[]</c>.</summary>
    public string EntriesJson { get; set; } = "[]";
    /// <summary>A serialized <c>LookupCandidate[]</c>: the values this table may write.</summary>
    public string CandidatesJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
