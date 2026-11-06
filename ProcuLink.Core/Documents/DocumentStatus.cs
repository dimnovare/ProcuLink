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

    public static readonly IReadOnlyList<string> All = [Received, Reading, ReadFailed, NeedsReview, Ready, Sending, Sent, SendFailed];
}
