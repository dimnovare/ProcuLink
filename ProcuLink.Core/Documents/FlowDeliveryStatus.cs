namespace ProcuLink.Core.Documents;

public static class FlowDeliveryStatus
{
    public const string Queued = "queued";
    public const string Delivering = "delivering";
    public const string Delivered = "delivered";
    public const string Stored = "stored";
    /// <summary>Failed but will be retried.</summary>
    public const string Failed = "failed";
    public const string DeadLetter = "dead_letter";

    /// <summary>A dead-lettered delivery whose output was deleted can never be retried, so it no longer decides the document's status.</summary>
    public static bool CountsTowardDocument(Guid? outputId, string status) => outputId is not null || status != DeadLetter;

    /// <summary>Sent when all are delivered/stored; sending while any is still in flight or retrying; otherwise send_failed.</summary>
    public static string DocumentStatusFor(IEnumerable<string> deliveryStatuses)
    {
        var s = deliveryStatuses.ToList();
        if (s.Count == 0) return DocumentStatus.SendFailed;
        if (s.All(x => x is Delivered or Stored)) return DocumentStatus.Sent;
        return s.Any(x => x is Queued or Delivering or Failed) ? DocumentStatus.Sending : DocumentStatus.SendFailed;
    }
}
