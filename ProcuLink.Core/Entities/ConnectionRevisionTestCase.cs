namespace ProcuLink.Core.Entities;

/// <summary>
/// A test-pack entry attached to a <see cref="SupplierConnectionRevision"/> (Group V1):
/// a sample source file plus its expected output artifact. V1 records the test pack as
/// first-class child rows (empty for backfilled rev-1); V2 ("Replay &amp; impact testing")
/// runs historical orders through a draft and diffs against the published revision.
/// </summary>
public class ConnectionRevisionTestCase
{
    public Guid Id { get; set; }
    public Guid RevisionId { get; set; }

    /// <summary>Human label for the sample case.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Storage key (R2/local) of the sample source file.</summary>
    public string? SampleSourceFileKey { get; set; }

    /// <summary>Storage key of the expected output artifact, if captured.</summary>
    public string? ExpectedOutputKey { get; set; }

    public DateTime CreatedAt { get; set; }

    public SupplierConnectionRevision Revision { get; set; } = null!;
}
