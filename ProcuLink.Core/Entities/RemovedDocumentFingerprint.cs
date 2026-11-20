using System.Security.Cryptography;
using System.Text;

namespace ProcuLink.Core.Entities;

/// <summary>
/// What is left of a sent document a person removed, so the "already received" holds still see it: the file's SHA-256 and the
/// SHA-256 of its dedup key. No content, no file name, no raw key. Written only by a person's remove; a legal erase writes none.
/// </summary>
public class RemovedDocumentFingerprint
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid FlowId { get; set; }
    public string Sha256 { get; set; } = "";
    public string? DedupKeyHash { get; set; }
    public DateTime DocumentCreatedAt { get; set; }
    public DateTime RemovedAt { get; set; }

    /// <summary>The stored form of a dedup key: lowercase SHA-256 hex.</summary>
    public static string HashOf(string dedupKey) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dedupKey))).ToLowerInvariant();
}
