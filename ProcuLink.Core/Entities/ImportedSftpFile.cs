using System.ComponentModel.DataAnnotations.Schema;

namespace ProcuLink.Core.Entities;

/// <summary>
/// Tracks every remote file that has been successfully ingested via SFTP pull,
/// keyed by (OrgId, RemotePath) to prevent duplicate imports.
/// EF table: <c>imported_sftp_files</c>.
/// </summary>
[Table("imported_sftp_files")]
public class ImportedSftpFile
{
    public Guid Id { get; set; }

    /// <summary>Owning organisation.</summary>
    public Guid OrgId { get; set; }

    /// <summary>Full remote path as returned by the SFTP server (e.g. <c>/incoming/po-001.csv</c>).</summary>
    public string RemotePath { get; set; } = string.Empty;

    /// <summary>SHA-256 hex digest of the downloaded file bytes — used for content-level dedupe.</summary>
    public string FileHash { get; set; } = string.Empty;

    public DateTime ImportedAt { get; set; }
}
