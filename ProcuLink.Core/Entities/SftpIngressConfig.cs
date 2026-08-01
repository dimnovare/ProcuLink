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

    /// <summary>Supplier used when importing files from this assisted pull source.</summary>
    public Guid? DefaultSupplierId { get; set; }

    /// <summary>Whether polling is active for this config.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// The SSH host-key fingerprint(s) this poller trusts, newline-separated, in OpenSSH's
    /// <c>SHA256:…</c> form — the same string <c>ssh-keygen -lf</c> prints, so an operator can
    /// compare it against what the supplier tells them. Null/empty means nothing is pinned yet:
    /// the next successful poll records what the server presented (trust-on-first-use) and every
    /// poll after that is verified against it.
    ///
    /// <para>
    /// NOT a secret, and deliberately not encrypted: it is the digest of a PUBLIC key, and the whole
    /// point is that a human can read it back. Parsed by
    /// <c>ProcuLink.Core.Services.Security.SshHostKeyPolicy.Parse</c>; a SET rather than a value
    /// because a supplier behind a load balancer legitimately answers with more than one host key.
    /// </para>
    ///
    /// <para>
    /// Clearing this field is the deliberate re-trust path after a supplier genuinely rebuilds their
    /// server: the next poll pins whatever it then finds.
    /// </para>
    /// </summary>
    public string? HostKeyFingerprints { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
