using System.Security.Cryptography;
using System.Text;

namespace ProcuLink.Core.Entities;

/// <summary>
/// What is left of a sent document a person removed, so the "already received" holds still see it: the file's SHA-256 and the
/// SHA-256 of its dedup key, unsalted. No file name and no raw key are stored, but a hash is not secrecy: anyone holding the file
/// can confirm it by its SHA-256, and a typical document number can be recovered from its hash by trying candidates.
/// Written only by a person's remove; a legal erase writes none, and an erase by date deletes those older than its cutoff.
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
