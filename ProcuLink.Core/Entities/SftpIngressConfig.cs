using System.ComponentModel.DataAnnotations.Schema;

namespace ProcuLink.Core.Entities;

/// <summary>
/// Stores per-organisation SFTP pull-ingress configuration.
/// Passwords are stored encrypted via <c>DeliveryEncryptionService</c>.
/// EF table: <c>sftp_ingress_configs</c>.
/// </summary>
[Table("sftp_ingress_configs")]
public class SftpIngressConfig
{
    public Guid Id { get; set; }

    /// <summary>Owning organisation.</summary>
    public Guid OrgId { get; set; }

    /// <summary>SFTP server hostname or IP address.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>SFTP port; default 22.</summary>
    public int Port { get; set; } = 22;

    /// <summary>SFTP username.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>AES-256-GCM ciphertext of the SFTP password (base64, same format as delivery credentials).</summary>
    public string EncryptedPassword { get; set; } = string.Empty;

    /// <summary>Remote directory to scan for files (e.g. <c>/incoming</c>).</summary>
    public string RemoteDirectory { get; set; } = string.Empty;

    /// <summary>Whether polling is active for this config.</summary>
    public bool IsEnabled { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
