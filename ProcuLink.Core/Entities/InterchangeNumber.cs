namespace ProcuLink.Core.Entities;

/// <summary>
/// One handed-out interchange control number: the identity id, wrapped, goes into a sent X12 / EDIFACT envelope.
/// Global, not per organisation: orgs without partner IDs share one sender ID, so per-org counters would repeat a number under it.
/// </summary>
public class InterchangeNumber
{
    public long Id { get; set; }
    public DateTime CreatedAt { get; set; }
}
