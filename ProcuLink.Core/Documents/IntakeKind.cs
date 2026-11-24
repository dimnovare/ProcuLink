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
    /// <summary>An Outlook or Microsoft 365 mailbox, read through Microsoft Graph with a connected account (plan 30).</summary>
    public const string Outlook = "outlook";

    public static readonly string[] Pickups = [Imap, Sftp, Ftp, Ftps, S3, Outlook];

    /// <summary>Sources whose <c>SourceRef</c> is the sender's email address.</summary>
    public static readonly string[] Mail = [Email, Imap, Outlook];
}
