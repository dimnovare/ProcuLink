namespace ProcuLink.Core.Documents;

public static class DocumentStatus
{
    public const string Received = "received";
    public const string Reading = "reading";
    public const string ReadFailed = "read_failed";
    public const string NeedsReview = "needs_review";
    public const string Ready = "ready";

    public static readonly IReadOnlyList<string> All = [Received, Reading, ReadFailed, NeedsReview, Ready];
}
