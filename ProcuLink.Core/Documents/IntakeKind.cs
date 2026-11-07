namespace ProcuLink.Core.Documents;

/// <summary>The kinds of flow intake. A document's source is <c>"upload"</c> or one of these.</summary>
public static class IntakeKind
{
    public const string Email = "email";
    public const string Api = "api";
    public const string Imap = "imap";
    public const string Sftp = "sftp";
    public const string Ftp = "ftp";
    public const string Ftps = "ftps";
    public const string S3 = "s3";

    public static readonly string[] Pickups = [Imap, Sftp, Ftp, Ftps, S3];
}
