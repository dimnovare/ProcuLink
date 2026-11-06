namespace ProcuLink.Core.Documents;

public static class DocumentStatus
{
    public const string Received = "received";
    public const string Reading = "reading";
    public const string ReadFailed = "read_failed";
    public const string NeedsReview = "needs_review";
    public const string Ready = "ready";
    public const string Sending = "sending";
    public const string Sent = "sent";
    public const string SendFailed = "send_failed";

    /// <summary>A read document a person may edit or send. A duplicate is never read, so it is neither.</summary>
    public static bool IsEditable(string status, string? statusReason) =>
        status is NeedsReview or Ready or SendFailed && statusReason != "duplicate_file";

    public static readonly IReadOnlyList<string> All = [Received, Reading, ReadFailed, NeedsReview, Ready, Sending, Sent, SendFailed];
}
