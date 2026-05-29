namespace ProcuLink.Core.Constants;

public static class DeliveryProtocolConstants
{
    public const string Http = "http";
    public const string Sftp = "sftp";
    public const string Ftp = "ftp";
    public const string Ftps = "ftps";
    public const string Smtp = "smtp";
    public const string ErpErply = "erp_erply";
    public const string ErpDirecto = "erp_directo";

    public static readonly string[] All =
    [
        Http,
        Sftp,
        Ftp,
        Ftps,
        Smtp,
        ErpErply,
        ErpDirecto
    ];

    public static string AllowedListForMessage => "http, sftp, ftp, ftps, smtp, erp_erply, or erp_directo";
}
